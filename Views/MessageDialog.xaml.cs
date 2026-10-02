using System.Windows;
using System.Windows.Controls;
using GameShelf.Services;

namespace GameShelf.Views;

/// <summary>GameShelf's own message and confirmation panel, shown over the main window instead of the grey system message box.</summary>
internal sealed partial class MessageDialog : UserControl
{
    bool confirmed;

    MessageDialog(string heading, string body, string confirmText, string? cancelText, bool destructive)
    {
        InitializeComponent();

        // The caller's texts are already translated; the buttons' texts are translated here.
        HeadingText.Text = heading;
        BodyText.Text = body;
        ConfirmButton.Content = confirmText;
        if (destructive) ConfirmButton.Style = (Style)FindResource("DangerButton");
        if (cancelText is null) CancelButton.Visibility = Visibility.Collapsed;
        else CancelButton.Content = cancelText;
    }

    /// <summary>Asks a yes/no question. True if the user confirmed.</summary>
    public static bool Confirm(Window owner, string heading, string body, string confirmText,
        string? cancelText = null, bool destructive = false)
    {
        cancelText ??= Loc.T("Cancel");
        var dialog = new MessageDialog(heading, body, confirmText, cancelText, destructive);
        DialogHost.ShowModal(owner, dialog);
        return dialog.confirmed;
    }

    /// <summary>Tells the user something, with a single OK button.</summary>
    public static void Info(Window owner, string heading, string body) =>
        DialogHost.ShowModal(owner, new MessageDialog(heading, body, Loc.T("OK"), cancelText: null, destructive: false));

    void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        confirmed = true;
        DialogHost.Close(this);
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => DialogHost.Close(this);
}
