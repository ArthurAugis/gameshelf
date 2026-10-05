using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameShelf.Services;

namespace GameShelf.Views;

/// <summary>Every release with what changed in it, newest first, over the shelf. The running version is marked.</summary>
internal sealed class ChangelogPanel : StackPanel
{
    static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x9a, 0x91, 0x83));
    static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a));

    ChangelogPanel(IReadOnlyList<ChangelogEntry> entries, string current)
    {
        Width = 560;
        Margin = new Thickness(28);

        Children.Add(new TextBlock { Text = Loc.T("Changelog"), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        Children.Add(new TextBlock
        {
            Text = Loc.T("You are using version {0}.", current),
            Foreground = Grey,
            FontSize = 13.5,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var list = new StackPanel { Margin = new Thickness(0, 18, 8, 0) };
        foreach (var entry in entries) list.Children.Add(CreateRelease(entry, entry.Version == current));
        Children.Add(new ScrollViewer { Content = list, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var close = new Button
        {
            Content = Loc.T("Close"),
            Style = (Style)Application.Current.FindResource("SecondaryButton"),
            Height = 40,
            MinWidth = 110,
            IsCancel = true,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0),
        };
        close.Click += (_, _) => DialogHost.Close(this);
        Children.Add(close);
    }

    static StackPanel CreateRelease(ChangelogEntry entry, bool isCurrent)
    {
        var release = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new TextBlock { Text = "v" + entry.Version, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Gold });
        if (entry.Date.Length > 0)
            heading.Children.Add(new TextBlock { Text = entry.Date, FontSize = 12.5, Foreground = Grey, Margin = new Thickness(10, 3, 0, 0) });
        if (isCurrent)
            heading.Children.Add(new TextBlock { Text = Loc.T("Current"), FontSize = 12.5, Foreground = Brushes.LimeGreen, Margin = new Thickness(10, 3, 0, 0) });
        release.Children.Add(heading);

        foreach (var change in entry.Changes)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = "•", Foreground = Grey, FontSize = 13.5 });
            var text = new TextBlock { Text = change, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xcf, 0xc8, 0xbb)), FontSize = 13.5 };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            release.Children.Add(row);
        }
        return release;
    }

    public static void Show(Window owner) =>
        DialogHost.ShowModal(owner, new ChangelogPanel(Changelog.Load(), UpdateChecker.CurrentVersion.ToString(3)));
}
