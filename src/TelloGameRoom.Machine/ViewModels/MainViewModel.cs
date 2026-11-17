using System.Diagnostics;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Core.Sessions;
using TelloGameRoom.Machine.Services;

namespace TelloGameRoom.Machine.ViewModels;

public enum MachineScreen
{
    /// <summary>Not registered yet: show the pairing form.</summary>
    Pairing,
    Locked,
    Paused,

    /// <summary>A session is running: the lock screen is hidden and the floating timer is shown.</summary>
    Active,

    /// <summary>The server no longer accepts this machine.</summary>
    Rejected,
}

/// <summary>
/// All the state the Machine app shows. It owns the <see cref="MachineSessionTracker"/>, feeds it with what the
/// server sends, and ticks it four times a second on the UI thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxPinAttempts = 5;
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(10);

    private readonly MachineStore _store;
    private readonly ServerLink _link;
    private readonly Stopwatch _monotonic = Stopwatch.StartNew();
    private readonly MachineSessionTracker _tracker;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private MachineConfigDto? _config;
    private TimeSpan _messageShownAt;
    private TimeSpan _lastPersist;
    private bool _warningShownForThisSession;
    private int _pinFailures;
    private TimeSpan _pinLockedUntil;
    private bool _forceLocked;

    [ObservableProperty]
    private MachineScreen _screen = MachineScreen.Locked;

    [ObservableProperty]
    private string _shopName = "TelloGameRoom";

    [ObservableProperty]
    private string _branchName = string.Empty;

    [ObservableProperty]
    private string _machineName = Environment.MachineName;

    [ObservableProperty]
    private string _monogram = "T";

    [ObservableProperty]
    private ImageSource? _logo;

    [ObservableProperty]
    private string _remainingText = "00:00";

    [ObservableProperty]
    private string _clockText = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    [ObservableProperty]
    private LinkStatus _linkStatus = LinkStatus.Connecting;

    [ObservableProperty]
    private string _connectionText = string.Empty;

    [ObservableProperty]
    private bool _isOffline;

    [ObservableProperty]
    private bool _isLowTime;

    [ObservableProperty]
    private string? _messageText;

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private string _pairServer = string.Empty;

    [ObservableProperty]
    private string _pairCode = string.Empty;

    [ObservableProperty]
    private string _pairFingerprint = string.Empty;

    [ObservableProperty]
    private string? _pairError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    private bool _isPairing;

    public MainViewModel(MachineStore store, ServerLink link)
    {
        _store = store;
        _link = link;
        _dispatcher = Application.Current.Dispatcher;
        _tracker = new MachineSessionTracker(() => _monotonic.Elapsed);
        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();

        _link.StatusChanged += s => OnUi(() => ApplyLinkStatus(s));
        _link.StateReceived += s => OnUi(() => ApplyServerState(s));
        _link.ConfigReceived += c => OnUi(() => ApplyConfig(c, fromServer: true));
        _link.MessageReceived += m => OnUi(() => ShowMessage(m.Text));
        _link.CommandReceived += c => OnUi(() => Execute(c));
        _link.ScreenshotRequested += id => _ = CaptureAsync(id);
        Loc.Instance.PropertyChanged += (_, _) => OnUi(RefreshTexts);
    }

    /// <summary>Raised after the correct admin PIN was entered: the app should close.</summary>
    public event Action? ExitAuthorized;

    public bool HasAdminPin => !string.IsNullOrEmpty(_config?.AdminPinHash);

    /// <summary>
    /// A PC that has never been paired cannot know the shop's admin PIN, and it is not selling anything, so it may be
    /// closed without one. Once paired, the PIN (cached locally) is always required.
    /// </summary>
    public bool CanExitWithoutPin => Screen == MachineScreen.Pairing && !HasAdminPin && _store.LoadIdentity() is null;

    /// <summary>Closes a PC that was never paired.</summary>
    public void ExitUnpaired()
    {
        if (CanExitWithoutPin)
        {
            Log.Information("Closed from the pairing screen of a PC that was never paired");
            ExitAuthorized?.Invoke();
        }
    }

    /// <summary>True while the lock screen must cover the desktop.</summary>
    public bool IsLockScreenRequired => Screen != MachineScreen.Active;

    public void Start()
    {
        var preset = _store.LoadPreset();
        PairServer = preset?.ServerUrl ?? string.Empty;
        PairFingerprint = string.Join(" ", preset?.PinnedCertificates ?? []);

        var identity = _store.LoadIdentity();
        if (identity is null)
        {
            Screen = MachineScreen.Pairing;
        }
        else
        {
            MachineName = identity.MachineName;
            if (_store.LoadConfig() is { } cached)
            {
                ApplyConfig(cached, fromServer: false);
            }

            // If the app was killed or the PC rebooted mid-session, carry on from the saved state until the server answers.
            if (_store.LoadSession() is { } saved)
            {
                _tracker.Restore(saved, DateTime.UtcNow);
            }

            _link.Start(identity);
        }

        ApplyLinkStatus(_link.Status);
        _timer.Start();
        Tick();
    }

    public void Stop()
    {
        _timer.Stop();
        Persist();
    }

    [RelayCommand(CanExecute = nameof(CanPair))]
    private async Task PairAsync()
    {
        PairError = null;
        var server = TelloHttp.NormalizeServerUrl(PairServer);
        if (server is null)
        {
            PairError = Loc.T("Pair_ErrorAddress");
            return;
        }

        var pins = PairFingerprint.Split([' ', ',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TelloHttp.NormalizeThumbprint).Where(p => p.Length == 64).ToArray();

        IsPairing = true;
        try
        {
            await _link.PairAsync(server, PairCode, pins, CancellationToken.None);
            var identity = _store.LoadIdentity()!;
            MachineName = identity.MachineName;
            PairCode = string.Empty;
            Screen = MachineScreen.Locked;
            _link.Start(identity);
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.InvalidPairingCode)
        {
            PairError = Loc.T("Pair_ErrorCode");
        }
        catch (ApiException ex) when (ex.IsNetwork)
        {
            PairError = Loc.T("Pair_ErrorNetwork");
        }
        catch (ApiException ex)
        {
            PairError = Loc.T("Pair_ErrorGeneric", ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException { InnerException: AuthenticationException } or AuthenticationException)
        {
            PairError = Loc.T("Pair_ErrorCertificate");
        }
        finally
        {
            IsPairing = false;
        }
    }

    private bool CanPair() => !IsPairing;

    /// <summary>From the "not registered" screen: forget the old identity and show the pairing form.</summary>
    [RelayCommand]
    private void PairAgain()
    {
        var identity = _store.LoadIdentity();
        if (identity is not null && string.IsNullOrEmpty(PairServer))
        {
            PairServer = identity.ServerUrl;
            PairFingerprint = string.Join(" ", identity.PinnedCertificates);
        }

        _store.ClearIdentity();
        Screen = MachineScreen.Pairing;
    }

    [RelayCommand]
    private void DismissMessage() => MessageText = null;

    [RelayCommand]
    private void DismissWarning()
    {
        _tracker.AcknowledgeWarning();
        WarningText = null;
    }

    /// <summary>Checks the admin PIN. Five wrong attempts cost a one-minute wait.</summary>
    /// <returns>Null when accepted, otherwise the message to show.</returns>
    public string? TryExit(string pin)
    {
        if (!HasAdminPin)
        {
            return Loc.T("Pin_NotSet");
        }

        var now = _monotonic.Elapsed;
        if (now < _pinLockedUntil)
        {
            return Loc.T("Pin_Throttled", (int)Math.Ceiling((_pinLockedUntil - now).TotalSeconds));
        }

        if (PinHasher.Verify(pin, _config!.AdminPinHash))
        {
            _pinFailures = 0;
            Log.Information("Admin PIN accepted; closing on request");
            ExitAuthorized?.Invoke();
            return null;
        }

        Log.Warning("Wrong admin PIN entered");
        if (++_pinFailures >= MaxPinAttempts)
        {
            _pinFailures = 0;
            _pinLockedUntil = now + TimeSpan.FromSeconds(60);
            return Loc.T("Pin_Throttled", 60);
        }

        return Loc.T("Pin_Wrong");
    }

    partial void OnScreenChanged(MachineScreen value)
    {
        OnPropertyChanged(nameof(IsLockScreenRequired));
        RefreshTexts();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("t", Loc.Instance.Culture);
        DateText = now.ToString("dddd, d MMMM", Loc.Instance.Culture);

        if (Screen == MachineScreen.Pairing)
        {
            return;
        }

        var view = _tracker.Tick();
        RemainingText = TimeText.Clock(view.RemainingSeconds);
        IsLowTime = view.WarningActive || (view.State == MachineLockState.Active && view.RemainingSeconds <= 60);

        var target = _link.Status == LinkStatus.Rejected ? MachineScreen.Rejected
            : view.State == MachineLockState.Active && !_forceLocked ? MachineScreen.Active
            : view.State == MachineLockState.Paused ? MachineScreen.Paused
            : MachineScreen.Locked;
        if (target != Screen)
        {
            Screen = target;
            Persist();
            if (target != MachineScreen.Active)
            {
                WarningText = null;
            }
        }

        if (view.WarningActive)
        {
            WarningText = Loc.T("Warn_Body", TimeText.Clock(view.RemainingSeconds));
            if (!_warningShownForThisSession)
            {
                _warningShownForThisSession = true;
                SystemSounds.Exclamation.Play();
            }
        }
        else
        {
            _warningShownForThisSession = false;
            if (view.State != MachineLockState.Active)
            {
                WarningText = null;
            }
        }

        if (MessageText is not null && _monotonic.Elapsed - _messageShownAt > MessageDuration)
        {
            MessageText = null;
        }

        if (_monotonic.Elapsed - _lastPersist > PersistInterval)
        {
            Persist();
        }
    }

    private void ApplyServerState(MachineSessionState state)
    {
        if (_tracker.ApplyServerState(state))
        {
            // Any word from the server ends a locally forced lock: the server decides.
            _forceLocked = false;
            Persist();
            Tick();
        }
    }

    private void ApplyConfig(MachineConfigDto config, bool fromServer)
    {
        var logoChanged = _config?.LogoVersion != config.LogoVersion;
        _config = config;
        ShopName = string.IsNullOrWhiteSpace(config.ShopName) ? "TelloGameRoom" : config.ShopName;
        BranchName = config.BranchName;
        MachineName = config.MachineName;
        Monogram = ShopName[..1].ToUpperInvariant();
        Loc.Instance.SetLanguage(config.Language);
        OnPropertyChanged(nameof(HasAdminPin));

        if (fromServer)
        {
            _store.SaveConfig(config);
            if (logoChanged || (config.LogoVersion is not null && Logo is null))
            {
                _ = RefreshLogoAsync(config.LogoVersion is not null);
            }
        }
        else
        {
            LoadLogoFromCache();
        }
    }

    private async Task RefreshLogoAsync(bool hasLogo)
    {
        try
        {
            if (hasLogo)
            {
                await _link.DownloadLogoAsync();
            }
            else
            {
                File.Delete(_store.LogoPath);
            }
        }
        catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException)
        {
            Log.Warning("Could not refresh the logo: {Message}", ex.Message);
        }

        OnUi(LoadLogoFromCache);
    }

    private void LoadLogoFromCache()
    {
        try
        {
            if (!File.Exists(_store.LogoPath))
            {
                Logo = null;
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(File.ReadAllBytes(_store.LogoPath));
            image.EndInit();
            image.Freeze();
            Logo = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Logo = null;
        }
    }

    private void ApplyLinkStatus(LinkStatus status)
    {
        LinkStatus = status;
        IsOffline = status is LinkStatus.Offline or LinkStatus.Rejected;
        RefreshTexts();
    }

    private void RefreshTexts() => ConnectionText = Screen == MachineScreen.Pairing
        ? Loc.T("Conn_NotPaired")
        : LinkStatus switch
    {
        LinkStatus.Connected => Loc.T("Conn_Connected"),
        LinkStatus.Connecting => Loc.T("Conn_Connecting"),
        _ => Loc.T("Conn_Offline"),
    };

    private void ShowMessage(string text)
    {
        MessageText = text;
        _messageShownAt = _monotonic.Elapsed;
        SystemSounds.Asterisk.Play();
    }

    private void Execute(MachineCommand command)
    {
        Log.Information("Remote command received: {Command}", command);
        switch (command)
        {
            case MachineCommand.Lock:
                // Show the lock screen right away; the server's next state (normally "paused") takes over from there.
                _forceLocked = true;
                Tick();
                break;
            case MachineCommand.Restart:
                ShowMessage(Loc.T("Cmd_Restart"));
                Persist();
                PowerCommands.Restart();
                break;
            case MachineCommand.Shutdown:
                ShowMessage(Loc.T("Cmd_Shutdown"));
                Persist();
                PowerCommands.Shutdown();
                break;
        }
    }

    private async Task CaptureAsync(string requestId)
    {
        try
        {
            var jpeg = await Task.Run(ScreenCapture.CaptureJpeg);
            await _link.UploadScreenshotAsync(requestId, jpeg);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Screenshot failed");
        }
    }

    private void Persist()
    {
        _lastPersist = _monotonic.Elapsed;
        _store.SaveSession(_tracker.Snapshot(DateTime.UtcNow));
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
