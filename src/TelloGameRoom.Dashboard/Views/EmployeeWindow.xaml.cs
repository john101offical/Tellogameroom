using System.Windows;
using TelloGameRoom.Dashboard.ViewModels;
using Wpf.Ui.Controls;

namespace TelloGameRoom.Dashboard.Views;

/// <summary>Add or edit a staff account.</summary>
public partial class EmployeeWindow : FluentWindow
{
    private readonly EmployeeEditorViewModel _viewModel;

    public EmployeeWindow(EmployeeEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Title = viewModel.Title;
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <returns>True when the employee was saved.</returns>
    public static bool Show(EmployeeEditorViewModel viewModel) =>
        new EmployeeWindow(viewModel) { Owner = Application.Current.MainWindow }.ShowDialog() == true;

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.DisplayName) || string.IsNullOrWhiteSpace(_viewModel.Username))
        {
            _viewModel.Error = Loc.T("Common_Required");
            return;
        }

        if (_viewModel.IsNew && PasswordBox.Password.Length < 8)
        {
            _viewModel.Error = Loc.T("Setup_PasswordRule");
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            _viewModel.Error = _viewModel.Save is null ? null : await _viewModel.Save(_viewModel, PasswordBox.Password);
            if (_viewModel.Error is null)
            {
                DialogResult = true;
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
