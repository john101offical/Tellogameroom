using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data.Entities;

namespace TelloGameRoom.Tests.Integration;

public sealed class MachinePairingTests : IAsyncLifetime
{
    private const string Api = ProtocolInfo.ApiPrefix;
    private readonly TelloFactory _factory = new();
    private HttpClient _owner = null!;
    private Guid _branchId;

    public async Task InitializeAsync()
    {
        _owner = _factory.CreateClient();
        var auth = await _factory.SetupAsync(_owner);
        TelloFactory.Authorize(_owner, auth.AccessToken);
        _branchId = auth.User.Branches[0].Id;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<PairingCodeResponse> CreateCodeAsync(string name)
    {
        var response = await _factory.PostAsync(_owner, $"/branches/{_branchId}/machines/pairing-codes", new CreatePairingCodeRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingCodeResponse>(TelloJson.Options))!;
    }

    private async Task<PairMachineResponse> PairAsync(string code)
    {
        var response = await _factory.PostAsync(_factory.CreateClient(), "/machines/pair", new PairMachineRequest(code, "DESKTOP-01", "Windows 11", "0.1.0"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairMachineResponse>(TelloJson.Options))!;
    }

    private HttpClient MachineClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ProtocolInfo.MachineAuthScheme, token);
        return client;
    }

    [Fact]
    public async Task Machine_pairs_with_a_code_and_authenticates_with_its_own_token()
    {
        var code = await CreateCodeAsync("PC-01");
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code.Code);

        // Typed in lower case without the dash still works.
        var paired = await PairAsync(code.Code.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant());
        Assert.Equal("PC-01", paired.MachineName);
        Assert.Equal("Tello Test Shop", paired.ShopName);
        Assert.Equal(_branchId, paired.BranchId);

        var me = await MachineClient(paired.Token).GetFromJsonAsync<MachineIdentity>($"{Api}/machines/me", TelloJson.Options);
        Assert.Equal(paired.MachineId, me!.MachineId);

        // The secret is stored hashed, never in clear.
        var secret = paired.Token.Split('.')[1];
        Assert.False(await _factory.WithDbAsync(db => db.Machines.AnyAsync(m => m.TokenHash.Contains(secret))));

        var list = await _owner.GetFromJsonAsync<List<MachineSummary>>($"{Api}/branches/{_branchId}/machines", TelloJson.Options);
        Assert.Equal("PC-01", Assert.Single(list!).Name);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.MachinePaired)));
    }

    [Fact]
    public async Task Pairing_code_is_single_use()
    {
        var code = await CreateCodeAsync("PC-01");
        await PairAsync(code.Code);
        var second = await _factory.PostAsync(_factory.CreateClient(), "/machines/pair", new PairMachineRequest(code.Code));
        await HttpAssert.ProblemAsync(second, HttpStatusCode.BadRequest, ErrorCodes.InvalidPairingCode);
    }

    [Fact]
    public async Task Racing_machines_cannot_both_use_one_code()
    {
        var code = await CreateCodeAsync("PC-01");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            _factory.PostAsync(_factory.CreateClient(), "/machines/pair", new PairMachineRequest(code.Code))));
        Assert.Equal(1, attempts.Count(r => r.IsSuccessStatusCode));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Machines.CountAsync()));
    }

    [Fact]
    public async Task Expired_and_wrong_codes_are_rejected()
    {
        var code = await CreateCodeAsync("PC-01");
        var wrong = await _factory.PostAsync(_factory.CreateClient(), "/machines/pair", new PairMachineRequest("AAAA-AAAA"));
        await HttpAssert.ProblemAsync(wrong, HttpStatusCode.BadRequest, ErrorCodes.InvalidPairingCode);

        _factory.Clock.Advance(TimeSpan.FromMinutes(16));
        var expired = await _factory.PostAsync(_factory.CreateClient(), "/machines/pair", new PairMachineRequest(code.Code));
        await HttpAssert.ProblemAsync(expired, HttpStatusCode.BadRequest, ErrorCodes.InvalidPairingCode);
    }

    [Fact]
    public async Task Machine_names_are_unique_per_branch()
    {
        await PairAsync((await CreateCodeAsync("PC-01")).Code);
        var duplicate = await _factory.PostAsync(_owner, $"/branches/{_branchId}/machines/pairing-codes", new CreatePairingCodeRequest("PC-01"));
        await HttpAssert.ProblemAsync(duplicate, HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    [Fact]
    public async Task Bad_machine_credentials_are_rejected()
    {
        var paired = await PairAsync((await CreateCodeAsync("PC-01")).Code);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"{Api}/machines/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MachineClient($"{paired.MachineId}.wrong-secret").GetAsync($"{Api}/machines/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MachineClient("garbage").GetAsync($"{Api}/machines/me")).StatusCode);

        // A disabled machine is locked out at once.
        await _factory.WithDbAsync(db => db.Machines.ExecuteUpdateAsync(s => s.SetProperty(m => m.IsEnabled, false)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await MachineClient(paired.Token).GetAsync($"{Api}/machines/me")).StatusCode);
    }

    [Fact]
    public async Task Machine_token_cannot_call_staff_endpoints_and_staff_token_cannot_pose_as_a_machine()
    {
        var paired = await PairAsync((await CreateCodeAsync("PC-01")).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MachineClient(paired.Token).GetAsync($"{Api}/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MachineClient(paired.Token).GetAsync($"{Api}/branches/{_branchId}/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _owner.GetAsync($"{Api}/machines/me")).StatusCode);
    }

    [Fact]
    public async Task Employee_can_view_machines_but_cannot_create_pairing_codes()
    {
        await _factory.CreateEmployeeAsync("maria", "maria-password-1", _branchId);
        var employee = _factory.CreateClient();
        TelloFactory.Authorize(employee, (await _factory.LoginAsync(employee, "maria", "maria-password-1")).AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync($"{Api}/branches/{_branchId}/machines")).StatusCode);
        var forbidden = await _factory.PostAsync(employee, $"/branches/{_branchId}/machines/pairing-codes", new CreatePairingCodeRequest("PC-99"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Employee_cannot_see_a_branch_they_are_not_assigned_to()
    {
        var otherBranch = await _factory.WithDbAsync(async db =>
        {
            var branch = new Branch { Id = Guid.CreateVersion7(), Name = "North", CreatedAtUtc = DateTime.UtcNow };
            db.Branches.Add(branch);
            await db.SaveChangesAsync();
            return branch.Id;
        });
        await _factory.CreateEmployeeAsync("maria", "maria-password-1", _branchId);
        var employee = _factory.CreateClient();
        var auth = await _factory.LoginAsync(employee, "maria", "maria-password-1");
        TelloFactory.Authorize(employee, auth.AccessToken);

        Assert.Equal("Main", Assert.Single(auth.User.Branches).Name);
        Assert.Equal(HttpStatusCode.NotFound, (await employee.GetAsync($"{Api}/branches/{otherBranch}/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync($"{Api}/branches/{otherBranch}/machines")).StatusCode);
    }
}
