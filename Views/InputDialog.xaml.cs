using System.Windows;
using System.Windows.Controls;
using GameShelf.Services;

namespace GameShelf.Views;

/// <summary>GameShelf's own panel to type one line of text, with the same look as <see cref="MessageDialog"/>.</summary>
internal sealed partial class InputDialog : UserControl
{
    readonly Func<string, string?> validate;
    string? result;

    InputDialog(string heading, string confirmText, string initialText, Func<string, string?> validate, int maxLength)
    {
        InitializeComponent();
        Loc.Apply(this);

        this.validate = validate;
        HeadingText.Text = heading;
        ConfirmButton.Content = confirmText;
        Input.MaxLength = maxLength;
        Input.Text = initialText;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>
    /// Asks for a line of text. <paramref name="validate"/> returns why the text is not acceptable (shown under
    /// the field), or null if it is. Returns the trimmed text, or null if the user cancelled.
    /// </summary>
    public static string? Ask(
        Window owner, string heading, string confirmText, Func<string, string?> validate, string initialText = "",
        int maxLength = 30)
    {
        var dialog = new InputDialog(heading, confirmText, initialText, validate, maxLength);
        DialogHost.ShowModal(owner, dialog);
        return dialog.result;
    }

    void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var text = Input.Text.Trim();
        if (validate(text) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        result = text;
        DialogHost.Close(this);
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => DialogHost.Close(this);
}
