using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace TelloGameRoom.Dashboard.Views;

/// <summary>Shows a remote screenshot and lets the owner save it.</summary>
public partial class ImageWindow : FluentWindow
{
    private readonly byte[] _jpeg;

    public ImageWindow(string title, byte[] jpeg)
    {
        _jpeg = jpeg;
        InitializeComponent();
        Title = title;
        Bar.Title = title;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(jpeg);
        image.EndInit();
        image.Freeze();
        Picture.Source = image;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JPEG|*.jpg", FileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.jpg" };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllBytes(dialog.FileName, _jpeg);
        }
    }
}
