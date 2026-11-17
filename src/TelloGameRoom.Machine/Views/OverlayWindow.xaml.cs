using System.Windows;
using System.Windows.Interop;
using TelloGameRoom.Machine.Interop;
using TelloGameRoom.Machine.ViewModels;

namespace TelloGameRoom.Machine.Views;

/// <summary>Shows staff messages and the low-time warning over whatever the customer is doing, without stealing focus.</summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) => Place();
    }

    public void Reassert()
    {
        if (IsVisible)
        {
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private void Place()
    {
        Left = SystemParameters.WorkArea.Left + ((SystemParameters.WorkArea.Width - ActualWidth) / 2);
        Top = SystemParameters.WorkArea.Top + 24;
    }
}
