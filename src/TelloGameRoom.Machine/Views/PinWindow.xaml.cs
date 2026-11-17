using System.Windows;
using TelloGameRoom.Machine.ViewModels;

namespace TelloGameRoom.Machine.Views;

/// <summary>Asks for the admin PIN before the Machine app may be closed.</summary>
public partial class PinWindow : Window
{
    private readonly MainViewModel _viewModel;

    public PinWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Activate();
            PinBox.Focus();
            if (!_viewModel.HasAdminPin)
            {
                ShowError(Loc.T("Pin_NotSet"));
            }
        };
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var error = _viewModel.TryExit(PinBox.Password);
        PinBox.Clear();
        if (error is null)
        {
            DialogResult = true;
        }
        else
        {
            ShowError(error);
            PinBox.Focus();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
