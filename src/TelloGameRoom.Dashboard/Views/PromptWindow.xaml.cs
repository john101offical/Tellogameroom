using System.Windows;
using Wpf.Ui.Controls;

namespace TelloGameRoom.Dashboard.Views;

/// <summary>One item of a prompt's drop-down.</summary>
public sealed record PromptChoice(string Label, object Value);

/// <summary>
/// Describes a small modal: a confirmation, a text or secret prompt, a pick-one list, or a combination. One window
/// covers all of the Dashboard's simple questions so they look and behave the same everywhere.
/// </summary>
public sealed class PromptOptions
{
    public required string Title { get; init; }

    public string? Message { get; init; }

    public string ConfirmText { get; init; } = Loc.T("Common_Ok");

    /// <summary>Paints the confirm button red for destructive actions.</summary>
    public bool Danger { get; init; }

    public bool ShowCancel { get; init; } = true;

    public string? BigText { get; init; }

    public string? InputLabel { get; init; }

    public string? InputPlaceholder { get; init; }

    public string Input { get; set; } = string.Empty;

    public bool InputRequired { get; init; }

    public bool IsPassword { get; init; }

    public int MaxLength { get; init; } = 300;

    public string? SecondInputLabel { get; init; }

    public string SecondInput { get; set; } = string.Empty;

    public IReadOnlyList<string>? QuickTexts { get; init; }

    public string? ChoiceLabel { get; init; }

    public IReadOnlyList<PromptChoice>? Choices { get; init; }

    public PromptChoice? SelectedChoice { get; set; }

    /// <summary>Extra check before closing; return the message to show, or null when the answer is fine.</summary>
    public Func<PromptOptions, string?>? Validate { get; init; }

    public bool HasChoices => Choices is { Count: > 0 };

    public bool HasQuickTexts => QuickTexts is { Count: > 0 };
}

public partial class PromptWindow : FluentWindow
{
    private readonly PromptOptions _options;

    public PromptWindow(PromptOptions options)
    {
        _options = options;
        DataContext = options;
        InitializeComponent();
        Title = options.Title;
        Bar.Title = options.Title;
        if (options.Danger)
        {
            ConfirmButton.Appearance = ControlAppearance.Danger;
        }

        Loaded += (_, _) =>
        {
            if (options.IsPassword)
            {
                SecretBox.Focus();
            }
            else if (options.InputLabel is not null)
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }
            else if (options.HasChoices)
            {
                ChoiceBox.Focus();
            }
            else
            {
                ConfirmButton.Focus();
            }
        };
    }

    /// <summary>Shows the prompt over the main window. Returns the options with the answers filled in, or null when cancelled.</summary>
    public static PromptOptions? Ask(PromptOptions options)
    {
        var window = new PromptWindow(options) { Owner = Application.Current.MainWindow is { IsVisible: true } main ? main : null };
        return window.ShowDialog() == true ? options : null;
    }

    /// <summary>A yes/no question.</summary>
    public static bool Confirm(string title, string message, string confirmText, bool danger = false) =>
        Ask(new PromptOptions { Title = title, Message = message, ConfirmText = confirmText, Danger = danger }) is not null;

    /// <summary>A message with only an OK button.</summary>
    public static void Info(string title, string message) =>
        Ask(new PromptOptions { Title = title, Message = message, ShowCancel = false });

    private void OnQuickText(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string text })
        {
            InputBox.Text = text;
            InputBox.Focus();
            InputBox.CaretIndex = text.Length;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_options.IsPassword)
        {
            _options.Input = SecretBox.Password;
        }

        string? error = null;
        if (_options.InputRequired && string.IsNullOrWhiteSpace(_options.Input))
        {
            error = Loc.T("Common_Required");
        }
        else if (_options.HasChoices && _options.SelectedChoice is null)
        {
            error = Loc.T("Common_Required");
        }
        else
        {
            error = _options.Validate?.Invoke(_options);
        }

        if (error is not null)
        {
            ErrorLabel.Text = error;
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }
}
