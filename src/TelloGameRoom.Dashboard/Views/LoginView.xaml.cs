using System.Windows;
using System.Windows.Controls;
using TelloGameRoom.Dashboard.ViewModels;

namespace TelloGameRoom.Dashboard.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Returning staff only need to type their password.
            if (DataContext is LoginViewModel { Username.Length: > 0 })
            {
                PasswordBox.Focus();
            }
            else
            {
                UserBox.Focus();
            }
        };
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LoginViewModel vm)
        {
            return;
        }

        SignInButton.IsEnabled = false;
        try
        {
            await vm.SignInAsync(PasswordBox.Password);
        }
        finally
        {
            SignInButton.IsEnabled = true;
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
    }
}
