using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TelloGameRoom.Dashboard.ViewModels;

namespace TelloGameRoom.Dashboard.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
        Loaded += (_, _) => ShopBox.Focus();
        KeyDown += (_, e) =>
        {
            // Enter moves the wizard forward, like the primary button.
            if (e.Key == Key.Enter && DataContext is SetupViewModel vm)
            {
                e.Handled = true;
                if (vm.IsStep1)
                {
                    OnNext(this, new RoutedEventArgs());
                }
                else
                {
                    OnFinish(this, new RoutedEventArgs());
                }
            }
        };
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm)
        {
            vm.NextCommand.Execute(null);
            if (vm.IsStep2)
            {
                Dispatcher.BeginInvoke(() => OwnerBox.Focus());
            }
        }
    }

    private async void OnFinish(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupViewModel vm || !FinishButton.IsEnabled)
        {
            return;
        }

        FinishButton.IsEnabled = false;
        try
        {
            await vm.FinishAsync(PasswordBox.Password, ConfirmBox.Password);
        }
        finally
        {
            FinishButton.IsEnabled = true;
        }
    }
}
