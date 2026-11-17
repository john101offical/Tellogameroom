using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Dashboard.Services;

public enum ConnectionState
{
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>
/// The live connection to <c>/hubs/dashboard</c>. It reconnects forever with backoff and, after every reconnect,
/// subscribes again and delivers a fresh snapshot, so the grid can never stay stale.
/// </summary>
public sealed class Realtime(Session session) : IAsyncDisposable
{
    private HubConnection? _hub;
    private CancellationTokenSource? _cts;
    private Guid? _branchId;

    public ConnectionState State { get; private set; } = ConnectionState.Connecting;

    public event Action<ConnectionState>? StateChanged;

    public event Action<BranchStateDto>? Snapshot;

    public event Action<MachineStateDto>? MachineChanged;

    public event Action<Guid, Guid>? MachineRemoved;

    public event Action<Guid>? BranchChanged;

    public void Start()
    {
        if (_hub is not null || session.Server is null)
        {
            return;
        }

        var validation = TelloHttp.CreateValidationCallback(session.Settings.PinnedCertificates);
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(session.Server, ProtocolInfo.DashboardHubPath.TrimStart('/')), options =>
            {
                options.AccessTokenProvider = () => session.GetAccessTokenAsync();
                options.HttpMessageHandlerFactory = _ => TelloHttp.CreateHandler(session.Settings.PinnedCertificates);
                if (validation is not null)
                {
                    options.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = validation;
                }
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        _hub.On<MachineStateDto>(nameof(IDashboardClient.MachineChanged), m => MachineChanged?.Invoke(m));
        _hub.On<Guid, Guid>(nameof(IDashboardClient.MachineRemoved), (b, m) => MachineRemoved?.Invoke(b, m));
        _hub.On<Guid>(nameof(IDashboardClient.BranchChanged), b => BranchChanged?.Invoke(b));
        _hub.Reconnecting += _ =>
        {
            Set(ConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ => ResubscribeAsync();

        _cts = new CancellationTokenSource();
        var hub = _hub;
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(hub, token));
    }

    /// <summary>Switches the branch whose machines are streamed.</summary>
    public async Task SubscribeAsync(Guid branchId)
    {
        var previous = _branchId;
        _branchId = branchId;
        if (_hub?.State == HubConnectionState.Connected)
        {
            if (previous is { } old && old != branchId)
            {
                await _hub.InvokeAsync(nameof(IDashboardHub.Unsubscribe), old);
            }

            await ResubscribeAsync();
        }
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }

        _branchId = null;
        Set(ConnectionState.Connecting);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task RunAsync(HubConnection hub, CancellationToken ct)
    {
        long attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (hub.State == HubConnectionState.Disconnected)
                {
                    Set(attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting);
                    await hub.StartAsync(ct);
                    attempt = 0;
                    await ResubscribeAsync();
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning("Dashboard hub connection attempt {Attempt} failed: {Message}", attempt, ex.Message);
                Set(ConnectionState.Reconnecting);
                try
                {
                    await Task.Delay(Backoff.Delay(attempt++), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ResubscribeAsync()
    {
        if (_hub is null || _branchId is not { } branchId)
        {
            Set(_hub?.State == HubConnectionState.Connected ? ConnectionState.Connected : State);
            return;
        }

        try
        {
            var snapshot = await _hub.InvokeAsync<BranchStateDto>(nameof(IDashboardHub.Subscribe), branchId);
            Set(ConnectionState.Connected);
            Snapshot?.Invoke(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning("Subscribe failed: {Message}", ex.Message);
        }
    }

    private void Set(ConnectionState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }
    }

    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Backoff.Delay(retryContext.PreviousRetryCount);
    }
}
