using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TelloGameRoom.Machine.Interop;
using TelloGameRoom.Machine.ViewModels;

namespace TelloGameRoom.Machine.Views;

/// <summary>
/// The floating timer shown during a session. It never takes the keyboard focus (so it cannot pull a game out of
/// focus), stays out of Alt+Tab, and re-asserts its top-most position so borderless-fullscreen games do not cover it.
/// Exclusive-fullscreen games draw over every window; that is a Windows limitation.
/// </summary>
public partial class TimerWidget : Window
{
    public TimerWidget(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
        Loaded += (_, _) =>
        {
            // Top-right corner of the primary screen by default; the customer can drag it elsewhere.
            Left = SystemParameters.WorkArea.Right - ActualWidth - 16;
            Top = SystemParameters.WorkArea.Top + 16;
        };
    }

    /// <summary>Raised when staff choose "Administrator access" from the widget's menu.</summary>
    public event Action? AdminRequested;

    public void Reassert()
    {
        if (IsVisible)
        {
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnAdmin(object sender, RoutedEventArgs e) => AdminRequested?.Invoke();
}
