using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TelloGameRoom.Dashboard.ViewModels;

namespace TelloGameRoom.Dashboard.Views;

public partial class MachinesView : UserControl
{
    public MachinesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MachinesViewModel vm)
            {
                vm.Context.Machines.CollectionChanged += OnMachinesChanged;
                UpdateEmptyState(vm);
            }
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is MachinesViewModel vm)
            {
                vm.Context.Machines.CollectionChanged -= OnMachinesChanged;
            }
        };
    }

    private void OnMachinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MachinesViewModel vm)
        {
            UpdateEmptyState(vm);
        }
    }

    private void UpdateEmptyState(MachinesViewModel vm) =>
        EmptyState.Visibility = vm.Context.Machines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "..." button opens the same menu as a right-click on the card.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DependencyObject? current = sender as DependencyObject;
        while (current is not null and not Button { ContextMenu: not null })
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is Button { ContextMenu: { } menu } card && !ReferenceEquals(current, sender))
        {
            menu.PlacementTarget = card;
            menu.IsOpen = true;
        }
    }
}
