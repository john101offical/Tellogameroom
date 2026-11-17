using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;
using TelloGameRoom.Core.Client;

namespace TelloGameRoom.Machine;

/// <summary>Access to localized strings from code: <c>Loc.T("Key")</c>.</summary>
public static class Loc
{
    public static Localizer Instance { get; } =
        new(new ResourceManager("TelloGameRoom.Machine.Resources.Strings", typeof(Loc).Assembly));

    public static string T(string key) => Instance[key];

    public static string T(string key, params object?[] args) => Instance.Format(key, args);
}

/// <summary>Localized string in XAML: <c>Text="{l:Tr Lock_PayAtCounter}"</c>. Updates live when the language changes.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
