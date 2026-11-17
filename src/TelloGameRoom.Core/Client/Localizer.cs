using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace TelloGameRoom.Core.Client;

/// <summary>
/// Looks up UI strings from .resx resources and can switch language at runtime. UI bindings target the indexer and are
/// refreshed through <see cref="INotifyPropertyChanged"/> when the language changes.
/// </summary>
public sealed class Localizer(ResourceManager resources) : INotifyPropertyChanged
{
    public static readonly IReadOnlyList<string> SupportedLanguages = ["en", "es"];

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture { get; private set; } = Resolve(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public string Language => Culture.TwoLetterISOLanguageName;

    /// <summary>The string for a key in the current language; the key itself when it is missing, so gaps are visible.</summary>
    public string this[string key] => resources.GetString(key, Culture) ?? key;

    public string Format(string key, params object?[] args) => string.Format(Culture, this[key], args);

    public bool Has(string key) => resources.GetString(key, Culture) is not null;

    public void SetLanguage(string? language)
    {
        var culture = Resolve(language);
        if (culture.Name == Culture.Name)
        {
            return;
        }

        Culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    /// <summary>Spanish uses Colombian formatting (1.000,00); everything else falls back to English.</summary>
    private static CultureInfo Resolve(string? language) =>
        CultureInfo.GetCultureInfo(language is not null && language.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "es-CO" : "en-US");
}
