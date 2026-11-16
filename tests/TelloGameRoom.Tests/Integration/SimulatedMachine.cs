using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Tests.Integration;

/// <summary>Builds SignalR connections that talk to the in-process test server.</summary>
public static class TestHubs
{
    public static HubConnection Build(TelloFactory factory, string path, Action<HttpConnectionOptions> configure) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, path), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                configure(o);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    public static HubConnection Dashboard(TelloFactory factory, string accessToken) =>
        Build(factory, ProtocolInfo.DashboardHubPath, o => o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken));

    /// <summary>Polls until the condition holds or the timeout passes.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }
}

/// <summary>A stand-in for a Machine app: connects with a machine token and records everything the server pushes.</summary>
public sealed class SimulatedMachine : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public SimulatedMachine(TelloFactory factory, PairMachineResponse identity)
    {
        Identity = identity;
        _connection = TestHubs.Build(factory, ProtocolInfo.MachineHubPath,
            o => o.Headers["Authorization"] = $"{ProtocolInfo.MachineAuthScheme} {identity.Token}");
        _connection.On<MachineSessionState>(nameof(IMachineClient.SessionStateChanged), s => States.Enqueue(s));
        _connection.On<MachineMessageDto>(nameof(IMachineClient.ShowMessage), m => Messages.Enqueue(m));
        _connection.On<MachineCommand>(nameof(IMachineClient.ExecuteCommand), c => Commands.Enqueue(c));
        _connection.On<MachineConfigDto>(nameof(IMachineClient.ConfigChanged), c => Configs.Enqueue(c));
        _connection.On<string>(nameof(IMachineClient.CaptureScreenshot), id => ScreenshotRequests.Enqueue(id));
    }

    public PairMachineResponse Identity { get; }

    public Guid Id => Identity.MachineId;

    public ConcurrentQueue<MachineSessionState> States { get; } = new();

    public ConcurrentQueue<MachineMessageDto> Messages { get; } = new();

    public ConcurrentQueue<MachineCommand> Commands { get; } = new();

    public ConcurrentQueue<MachineConfigDto> Configs { get; } = new();

    public ConcurrentQueue<string> ScreenshotRequests { get; } = new();

    public MachineSessionState? Latest => States.LastOrDefault();

    public Task ConnectAsync() => _connection.StartAsync();

    public Task DisconnectAsync() => _connection.StopAsync();

    public Task<MachineSessionState> SyncAsync() => _connection.InvokeAsync<MachineSessionState>(nameof(IMachineHub.Sync));

    public Task<MachineConfigDto> GetConfigAsync() => _connection.InvokeAsync<MachineConfigDto>(nameof(IMachineHub.GetConfig));

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
