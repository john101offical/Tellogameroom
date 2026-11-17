using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using TelloGameRoom.Machine.Services;
using TelloGameRoom.Machine.ViewModels;
using TelloGameRoom.Machine.Views;

namespace TelloGameRoom.Machine;

/// <summary>
/// Composition root of the Machine app. It wires the services together and switches between the two faces of the
/// app: the fullscreen lock screen, and the floating timer shown while a session runs.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private ServerLink? _link;
    private MainViewModel? _viewModel;
    private LockWindow? _lockWindow;
    private TimerWidget? _widget;
    private OverlayWindow? _overlay;
    private DispatcherTimer? _reassertTimer;
    private readonly KeyboardBlocker _keyboard = new();
    private readonly GuardClient _guard = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per Windows session. The guard may try to start a second copy; it simply exits.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\TelloGameRoom.Machine", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        var store = new MachineStore();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(store.LogDirectory, "machine-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                shared: true, formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        Log.Information("Machine app {Version} starting", ServerLink.AppVersion);

        DispatcherUnhandledException += (_, args) =>
        {
            // Never let a UI glitch take the lock screen down.
            Log.Error(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _link = new ServerLink(store);
        _viewModel = new MainViewModel(store, _link);
        _lockWindow = new LockWindow(_viewModel);
        _widget = new TimerWidget(_viewModel);
        _overlay = new OverlayWindow(_viewModel);
        _widget.AdminRequested += () => _lockWindow.ShowPinDialog();

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.ExitAuthorized += ExitForMaintenance;
        _viewModel.Start();
        ApplyScreen();

        _reassertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reassertTimer.Tick += (_, _) =>
        {
            if (_viewModel.IsLockScreenRequired)
            {
                _lockWindow.Reassert();
            }
            else
            {
                _widget.Reassert();
                _overlay.Reassert();
            }
        };
        _reassertTimer.Start();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is logging off or shutting down: let it.
        if (_lockWindow is not null)
        {
            _lockWindow.AllowClose = true;
        }

        _viewModel?.Stop();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboard.Dispose();
        _viewModel?.Stop();
        _link?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        Log.CloseAndFlush();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Screen) or nameof(MainViewModel.MessageText) or nameof(MainViewModel.WarningText))
        {
            ApplyScreen();
        }
    }

    /// <summary>Shows the windows that belong to the current state and arms or disarms the lock-down.</summary>
    private void ApplyScreen()
    {
        if (_viewModel is null || _lockWindow is null || _widget is null || _overlay is null)
        {
            return;
        }

        if (_viewModel.IsLockScreenRequired)
        {
            _widget.Hide();
            _overlay.Hide();
            if (!_lockWindow.IsVisible)
            {
                _lockWindow.Show();
                _lockWindow.Activate();
            }

            _keyboard.Enable();
            _guard.ReportLocked(true);
        }
        else
        {
            _keyboard.Disable();
            _guard.ReportLocked(false);
            _lockWindow.Hide();
            _widget.Show();

            // During a session, messages and the low-time warning appear in a small overlay instead.
            if (_viewModel.MessageText is not null || _viewModel.WarningText is not null)
            {
                _overlay.Show();
            }
            else
            {
                _overlay.Hide();
            }
        }
    }

    private void ExitForMaintenance()
    {
        _guard.EnterMaintenance();
        _guard.ReportLocked(false);
        _keyboard.Disable();
        if (_lockWindow is not null)
        {
            _lockWindow.AllowClose = true;
        }

        // Give the guard a moment to read the message before the process disappears.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Shutdown();
        };
        timer.Start();
    }
}
