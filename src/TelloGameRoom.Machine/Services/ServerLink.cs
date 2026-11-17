using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Machine.Services;

public enum LinkStatus
{
    Connecting,
    Connected,
    Offline,

    /// <summary>The server rejected this machine's token: it was deleted, disabled or re-paired.</summary>
    Rejected,
}

/// <summary>
/// The connection to the server: REST for pairing and uploads, SignalR for live state. It reconnects forever with
/// exponential backoff and resynchronizes after every reconnect; the server's answer always replaces local state.
/// </summary>
public sealed class ServerLink : IAsyncDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);
    private readonly MachineStore _store;
    private readonly CancellationTokenSource _shutdown = new();
    private HubConnection? _hub;
    private ApiClient? _api;
    private Task? _loop;
    private CancellationTokenSource? _connection;

    public ServerLink(MachineStore store) => _store = store;

    public static string AppVersion { get; } =
        typeof(ServerLink).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public LinkStatus Status { get; private set; } = LinkStatus.Connecting;

    public event Action<LinkStatus>? StatusChanged;

    public event Action<MachineSessionState>? StateReceived;

    public event Action<MachineConfigDto>? ConfigReceived;

    public event Action<MachineMessageDto>? MessageReceived;

    public event Action<MachineCommand>? CommandReceived;

    public event Action<string>? ScreenshotRequested;

    /// <summary>Exchanges a pairing code for this PC's identity and stores it.</summary>
    public async Task PairAsync(Uri server, string code, string[] pins, CancellationToken ct)
    {
        using var http = new HttpClient(TelloHttp.CreateHandler(pins)) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(20) };
        var api = new ApiClient(http);
        var paired = await api.PostAsync<PairMachineResponse>("/machines/pair",
            new PairMachineRequest(code, Environment.MachineName, Environment.OSVersion.VersionString, AppVersion), ct);
        _store.SaveIdentity(server.ToString(), paired, pins);
    }

    /// <summary>Starts (or restarts) the connection for the stored identity.</summary>
    public void Start(MachineIdentityFile identity)
    {
        // Re-pairing replaces the identity: drop the previous connection before opening a new one.
        _connection?.Cancel();
        if (_hub is { } previous)
        {
            _ = previous.DisposeAsync().AsTask();
        }

        _connection = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        SetStatus(LinkStatus.Connecting);

        var token = MachineStore.Unprotect(identity);
        if (token is null)
        {
            Log.Error("The stored machine token cannot be decrypted on this computer");
            SetStatus(LinkStatus.Rejected);
            return;
        }

        var server = new Uri(identity.ServerUrl);
        var authorization = $"{ProtocolInfo.MachineAuthScheme} {token}";
        _api = new ApiClient(new HttpClient(TelloHttp.CreateHandler(identity.PinnedCertificates)) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) })
        {
            AuthorizationProvider = _ => Task.FromResult<string?>(authorization),
        };

        var validation = TelloHttp.CreateValidationCallback(identity.PinnedCertificates);
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server, $"{ProtocolInfo.MachineHubPath.TrimStart('/')}?appVersion={Uri.EscapeDataString(AppVersion)}"), options =>
            {
                options.Headers["Authorization"] = authorization;
                options.HttpMessageHandlerFactory = _ => TelloHttp.CreateHandler(identity.PinnedCertificates);
                if (validation is not null)
                {
                    options.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = validation;
                }
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        _hub.On<MachineSessionState>(nameof(IMachineClient.SessionStateChanged), s => StateReceived?.Invoke(s));
        _hub.On<MachineConfigDto>(nameof(IMachineClient.ConfigChanged), c => ConfigReceived?.Invoke(c));
        _hub.On<MachineMessageDto>(nameof(IMachineClient.ShowMessage), m => MessageReceived?.Invoke(m));
        _hub.On<MachineCommand>(nameof(IMachineClient.ExecuteCommand), c => CommandReceived?.Invoke(c));
        _hub.On<string>(nameof(IMachineClient.CaptureScreenshot), id => ScreenshotRequested?.Invoke(id));
        _hub.Reconnecting += _ =>
        {
            SetStatus(LinkStatus.Offline);
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ => OnConnectedAsync();

        var hub = _hub;
        var cancel = _connection.Token;
        _loop = Task.Run(() => RunAsync(hub, cancel));
    }

    public async Task UploadScreenshotAsync(string requestId, byte[] jpeg)
    {
        if (_api is not null)
        {
            await _api.SendBytesAsync<object>(HttpMethod.Post, $"/machines/me/screenshots/{Uri.EscapeDataString(requestId)}", jpeg, "image/jpeg", _shutdown.Token);
        }
    }

    /// <summary>Downloads the shop logo into the local cache; returns false when there is none.</summary>
    public async Task<bool> DownloadLogoAsync()
    {
        if (_api is null)
        {
            return false;
        }

        try
        {
            await File.WriteAllBytesAsync(_store.LogoPath, await _api.GetBytesAsync("/settings/shop/logo", _shutdown.Token), _shutdown.Token);
            return true;
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            File.Delete(_store.LogoPath);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }

        if (_loop is not null)
        {
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _connection?.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunAsync(HubConnection hub, CancellationToken ct)
    {
        long attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (hub.State == HubConnectionState.Disconnected)
                {
                    SetStatus(Status == LinkStatus.Rejected ? LinkStatus.Rejected : attempt == 0 ? LinkStatus.Connecting : LinkStatus.Offline);
                    await hub.StartAsync(ct);
                    attempt = 0;
                    await OnConnectedAsync();
                }

                await Task.Delay(SyncInterval, ct);
                if (hub.State == HubConnectionState.Connected)
                {
                    // Periodic resync corrects any drift and recovers from a push that was lost.
                    StateReceived?.Invoke(await hub.InvokeAsync<MachineSessionState>(nameof(IMachineHub.Sync), ct));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Log.Warning("The server rejected this machine's credentials");
                SetStatus(LinkStatus.Rejected);
                await DelayAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (Exception ex)
            {
                Log.Warning("Connection attempt {Attempt} failed: {Message}", attempt, ex.Message);
                SetStatus(LinkStatus.Offline);
                await DelayAsync(Backoff.Delay(attempt++), ct);
            }
        }
    }

    private async Task OnConnectedAsync()
    {
        if (_hub is null)
        {
            return;
        }

        try
        {
            ConfigReceived?.Invoke(await _hub.InvokeAsync<MachineConfigDto>(nameof(IMachineHub.GetConfig), _shutdown.Token));
            StateReceived?.Invoke(await _hub.InvokeAsync<MachineSessionState>(nameof(IMachineHub.Sync), _shutdown.Token));
            SetStatus(LinkStatus.Connected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Resync after connect failed");
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void SetStatus(LinkStatus status)
    {
        if (Status != status)
        {
            Status = status;
            StatusChanged?.Invoke(status);
        }
    }

    /// <summary>Never gives up: 1 s, 2 s, 4 s ... capped at 30 s.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Backoff.Delay(retryContext.PreviousRetryCount);
    }
}
