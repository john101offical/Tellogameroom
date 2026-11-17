using System.Windows.Controls;

namespace TelloGameRoom.Dashboard.Views;

public partial class ConnectView : UserControl
{
    public ConnectView()
    {
        InitializeComponent();
        Loaded += (_, _) => ServerBox.Focus();
    }
}
