using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TelloGameRoom.Machine.Interop;
using TelloGameRoom.Machine.ViewModels;

namespace TelloGameRoom.Machine.Views;

/// <summary>
/// The fullscreen lock screen. It covers every monitor, stays on top, refuses to close, and takes the focus back
/// whenever something else grabs it.
/// </summary>
public partial class LockWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _pinOpen;

    public LockWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        CoverAllScreens();
        SystemParameters.StaticPropertyChanged += (_, _) => CoverAllScreens();
        Loaded += (_, _) => FocusFirstField();
        IsVisibleChanged += (_, _) => FocusFirstField();
    }

    /// <summary>Set by the app once the admin PIN was accepted or Windows is shutting down.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Called every second while the lock is required: stay on top and keep the keyboard focus.</summary>
    public void Reassert()
    {
        if (!IsVisible || _pinOpen)
        {
            return;
        }

        Topmost = true;
        var handle = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE);
        if (!IsActive)
        {
            Activate();
            Native.SetForegroundWindow(handle);
        }
    }

    public void ShowPinDialog()
    {
        if (_pinOpen)
        {
            return;
        }

        if (_viewModel.CanExitWithoutPin)
        {
            _viewModel.ExitUnpaired();
            return;
        }

        _pinOpen = true;
        try
        {
            new PinWindow(_viewModel) { Owner = IsVisible ? this : null }.ShowDialog();
        }
        finally
        {
            _pinOpen = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 and "close window" requests are ignored; only an authorized exit gets through.
        e.Cancel = !AllowClose;
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Hidden staff shortcut: Ctrl+Shift+F12 asks for the admin PIN.
        if (e.Key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            ShowPinDialog();
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_viewModel.IsLockScreenRequired && !_pinOpen)
        {
            Dispatcher.BeginInvoke(Reassert);
        }
    }

    private void CoverAllScreens()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // The primary monitor always starts at (0,0) in virtual-screen coordinates.
        PrimaryArea.Margin = new Thickness(-SystemParameters.VirtualScreenLeft, -SystemParameters.VirtualScreenTop, 0, 0);
        PrimaryArea.Width = SystemParameters.PrimaryScreenWidth;
        PrimaryArea.Height = SystemParameters.PrimaryScreenHeight;
    }

    private void FocusFirstField()
    {
        if (IsVisible && _viewModel.Screen == MachineScreen.Pairing)
        {
            (string.IsNullOrEmpty(_viewModel.PairServer) ? ServerBox : CodeBox).Focus();
        }
    }
}
