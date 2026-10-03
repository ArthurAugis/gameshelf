using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameShelf.Views;

/// <summary>
/// Shows GameShelf's dialogs (confirmations, the install choice, text input, the Epic sign-in) over the main window
/// instead of in windows of their own: the page changes, nothing new opens on the desktop. <see cref="ShowModal"/>
/// returns once the dialog is closed, like <c>Window.ShowDialog</c>, so callers read the answer right after it.
/// </summary>
internal static class DialogHost
{
    sealed record Entry(FrameworkElement Dialog, Panel Layer, Grid Backdrop, DispatcherFrame Frame, IInputElement? PreviousFocus);

    static readonly List<Entry> Open = new();

    /// <summary>True while a dialog is up: the shelf and the game page ignore keys and controller buttons then.</summary>
    public static bool IsOpen => Open.Count > 0;

    /// <summary>Puts <paramref name="dialog"/> over the main window, dims what is behind it, and waits until it is closed.</summary>
    public static void ShowModal(Window owner, FrameworkElement dialog)
    {
        var window = owner as MainWindow ?? Application.Current.MainWindow as MainWindow
            ?? throw new InvalidOperationException("The main window is not open.");
        var layer = window.DialogLayer;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1b)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3a, 0x35, 0x2f)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            // In a short window a tall dialog scrolls instead of being cut off.
            Child = new ScrollViewer
            {
                Content = dialog,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        // The backdrop takes the mouse, so nothing behind the dialog can be clicked.
        var backdrop = new Grid { Background = new SolidColorBrush(Color.FromArgb(175, 0, 0, 0)), Children = { card } };

        var entry = new Entry(dialog, layer, backdrop, new DispatcherFrame(), Keyboard.FocusedElement);
        Open.Add(entry);
        layer.Children.Add(backdrop);
        layer.Visibility = Visibility.Visible;

        // Focus inside the dialog, so Enter reaches its default button.
        _ = dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
        Dispatcher.PushFrame(entry.Frame);
    }

    /// <summary>Takes the dialog off the window and lets <see cref="ShowModal"/> return.</summary>
    public static void Close(FrameworkElement dialog)
    {
        var entry = Open.Find(open => ReferenceEquals(open.Dialog, dialog));
        if (entry is null) return;

        Open.Remove(entry);
        if (entry.Dialog.Parent is ScrollViewer holder) holder.Content = null; // the dialog goes back to having no parent
        entry.Backdrop.Children.Clear();
        entry.Layer.Children.Remove(entry.Backdrop);
        if (Open.Count == 0) entry.Layer.Visibility = Visibility.Collapsed;
        entry.Frame.Continue = false;
        if (entry.PreviousFocus is not null) Keyboard.Focus(entry.PreviousFocus);
    }
}
