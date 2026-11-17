using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TelloGameRoom.Dashboard.Services;

/// <summary>Switches between the dark and light theme and keeps the brand accent colour in both.</summary>
public sealed class ThemeService
{
    private static readonly Color Brand = Color.FromRgb(0x7C, 0x5C, 0xFF);

    public void Apply(string? theme)
    {
        var target = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(target, WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.Apply(Brand, target, false, false);
    }
}
