using TelloGameRoom.Dashboard.ViewModels;
using Wpf.Ui.Controls;

namespace TelloGameRoom.Dashboard.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(RootViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
