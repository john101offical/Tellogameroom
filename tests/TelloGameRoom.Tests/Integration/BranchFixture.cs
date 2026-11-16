using System.Net.Http.Json;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Tests.Integration;

/// <summary>A set-up shop with an authenticated owner, shared by the session, remote, shift and report tests.</summary>
public abstract class BranchTestBase : IAsyncLifetime
{
    protected const string Api = ProtocolInfo.ApiPrefix;

    protected TelloFactory Factory { get; private set; } = new() { AccessTokenMinutes = 60 * 24 * 30 };

    protected HttpClient Owner { get; private set; } = null!;

    protected AuthResponse OwnerAuth { get; private set; } = null!;

    protected Guid BranchId { get; private set; }

    protected string B => $"/branches/{BranchId}";

    public virtual async Task InitializeAsync()
    {
        Owner = Factory.CreateClient();
        OwnerAuth = await Factory.SetupAsync(Owner);
        TelloFactory.Authorize(Owner, OwnerAuth.AccessToken);
        BranchId = OwnerAuth.User.Branches[0].Id;
    }

    public virtual Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }

    protected async Task<PairMachineResponse> PairAsync(string name, TelloFactory? factory = null, HttpClient? owner = null)
    {
        factory ??= Factory;
        var created = await factory.PostAsync(owner ?? Owner, $"{B}/machines/pairing-codes", new CreatePairingCodeRequest(name));
        created.EnsureSuccessStatusCode();
        var code = (await created.Content.ReadFromJsonAsync<PairingCodeResponse>(TelloJson.Options))!;
        var paired = await factory.PostAsync(factory.CreateClient(), "/machines/pair", new PairMachineRequest(code.Code, name));
        paired.EnsureSuccessStatusCode();
        return (await paired.Content.ReadFromJsonAsync<PairMachineResponse>(TelloJson.Options))!;
    }

    protected async Task<SimulatedMachine> ConnectMachineAsync(string name)
    {
        var machine = new SimulatedMachine(Factory, await PairAsync(name));
        await machine.ConnectAsync();
        return machine;
    }

    protected async Task<HttpClient> EmployeeClientAsync(string username = "maria", bool clockIn = true)
    {
        await Factory.CreateEmployeeAsync(username, username + "-password-1", BranchId);
        var client = Factory.CreateClient();
        TelloFactory.Authorize(client, (await Factory.LoginAsync(client, username, username + "-password-1")).AccessToken);
        if (clockIn)
        {
            (await Factory.PostAsync(client, $"{B}/shifts/open", new OpenShiftRequest(0))).EnsureSuccessStatusCode();
        }

        return client;
    }

    protected Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string url, T body) => Factory.PostAsync(client, url, body);

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<T>(TelloJson.Options))!;
    }

    protected async Task<MachineStateDto> StartAsync(Guid machineId, long seconds = 3600, HttpClient? client = null, bool allowOffline = true) =>
        await ReadAsync<MachineStateDto>(await PostAsync(client ?? Owner, $"{B}/machines/{machineId}/session/start",
            new StartSessionRequest(seconds, AllowOffline: allowOffline)));

    protected async Task<BranchStateDto> GetStateAsync(HttpClient? client = null) =>
        (await (client ?? Owner).GetFromJsonAsync<BranchStateDto>($"{Api}{B}/state", TelloJson.Options))!;
}
