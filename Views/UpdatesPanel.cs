using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameShelf.Services;
using GameShelf.Theming;

namespace GameShelf.Views;

/// <summary>The games that have an update waiting, with a button to start each one (or all of them), over the shelf.</summary>
internal sealed class UpdatesPanel : StackPanel
{
    readonly List<Func<Task>> starters = new();
    readonly Button updateAll;

    UpdatesPanel(IReadOnlyList<PendingUpdate> updates, string? note)
    {
        Width = 580;
        Margin = new Thickness(28);

        Children.Add(new TextBlock { Text = Loc.T("Updates"), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        Children.Add(new TextBlock
        {
            Text = updates.Count == 0 ? Loc.T("Everything is up to date.") : Loc.Count(updates.Count, "{0} game has an update.", "{0} games have an update."),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x91, 0x83)),
            FontSize = 13.5,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var list = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var update in updates) list.Children.Add(CreateRow(update));
        if (updates.Count > 0) Children.Add(new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        if (note is not null)
            Children.Add(new TextBlock
            {
                Text = note,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xc9, 0x8a)),
                FontSize = 12.5,
                Margin = new Thickness(0, 14, 0, 0),
            });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        updateAll = new Button
        {
            Content = Loc.T("Update all"),
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Height = 40,
            MinWidth = 130,
            Margin = new Thickness(0, 0, 10, 0),
            Visibility = updates.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        updateAll.Click += async (_, _) =>
        {
            updateAll.IsEnabled = false;
            foreach (var start in starters.ToList()) await start();
        };
        var close = new Button
        {
            Content = Loc.T("Close"),
            Style = (Style)Application.Current.FindResource("SecondaryButton"),
            Height = 40,
            MinWidth = 110,
            IsCancel = true,
            IsDefault = updates.Count == 0,
        };
        close.Click += (_, _) => DialogHost.Close(this);
        buttons.Children.Add(updateAll);
        buttons.Children.Add(close);
        Children.Add(buttons);
    }

    /// <summary>Shows the panel. <paramref name="note"/> is a line under the list, for what could not be checked.</summary>
    public static void Show(Window owner, IReadOnlyList<PendingUpdate> updates, string? note = null) =>
        DialogHost.ShowModal(owner, new UpdatesPanel(updates, note));

    Grid CreateRow(PendingUpdate update)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = PlatformLogos.Create(update.Game.Launcher, 16);
        logo.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(logo);

        var name = new TextBlock
        {
            Text = update.Game.Name,
            Foreground = Brushes.White,
            FontSize = 14.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = update.Game.Name,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var size = new TextBlock
        {
            Text = update.Bytes > 0 ? DisplayFormat.Size(update.Bytes) : "",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x91, 0x83)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0),
        };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);

        var button = new Button
        {
            Content = Loc.T("Update"),
            Style = (Style)Application.Current.FindResource("SecondaryButton"),
            Height = 32,
            MinWidth = 96,
        };
        async Task Start()
        {
            if (!button.IsEnabled) return;
            button.IsEnabled = false;
            button.Content = Loc.T("Starting...");
            button.Content = await PendingUpdates.StartAsync(update.Game) ? Loc.T("Started") : Loc.T("Failed");
        }
        button.Click += async (_, _) => await Start();
        starters.Add(Start);
        Grid.SetColumn(button, 3);
        row.Children.Add(button);
        return row;
    }
}
