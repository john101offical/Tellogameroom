using System.Windows;
using TelloGameRoom.Dashboard.ViewModels;
using Wpf.Ui.Controls;

namespace TelloGameRoom.Dashboard.Views;

/// <summary>Start session, add time or remove time: presets, a custom amount and the total in large type.</summary>
public partial class TimeDialog : FluentWindow
{
    public TimeDialog(TimeDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Title = viewModel.Title;
        viewModel.Confirmed += () => DialogResult = true;
        Loaded += (_, _) => (viewModel.IsRemove ? FirstPreset : HourPreset).Focus();
    }

    /// <summary>Shows the dialog over the main window; null when cancelled.</summary>
    public static TimeDialogResult? Ask(TimeDialogViewModel viewModel)
    {
        var dialog = new TimeDialog(viewModel) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? viewModel.Result : null;
    }
}
