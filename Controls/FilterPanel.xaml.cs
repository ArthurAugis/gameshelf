using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GameShelf.Services;

namespace GameShelf.Controls;

/// <summary>The filter popup: sort order, grouping, status, played, features and genres, as toggle chips.</summary>
internal sealed partial class FilterPanel : UserControl
{
    ShelfFilter filter = new();
    IReadOnlyList<(string Name, int Count)> genres = Array.Empty<(string, int)>();
    IReadOnlyList<string> features = Array.Empty<string>();
    IReadOnlyList<string> launchers = Array.Empty<string>();

    public FilterPanel()
    {
        InitializeComponent();
        Loc.Apply(this);
    }

    /// <summary>Raised when the user changes a choice.</summary>
    public event Action? Changed;

    /// <summary>
    /// Shows the choices of <paramref name="filter"/>, with the genres, features and launchers that exist in the
    /// library. The launcher choice only shows when there is more than one launcher.
    /// </summary>
    public void Bind(ShelfFilter filter, IReadOnlyList<(string Name, int Count)> genres, IReadOnlyList<string> features,
        IReadOnlyList<string> launchers)
    {
        this.filter = filter;
        this.genres = genres;
        this.features = features;
        this.launchers = launchers;
        Refresh();
    }

    /// <summary>Redraws every chip from the filter (after it was changed from outside).</summary>
    public void Refresh()
    {
        BuildSingleChoice(SortChips, filter.Sort, value => filter.Sort = value, new[]
        {
            (Loc.T("Name"), SortMode.Name), (Loc.T("Recently played"), SortMode.RecentlyPlayed),
            (Loc.T("Play time"), SortMode.PlayTime), (Loc.T("Release date"), SortMode.ReleaseDate),
            (Loc.T("Size on disk"), SortMode.SizeOnDisk), (Loc.T("Rating"), SortMode.Rating),
        });
        BuildSingleChoice(GroupChips, filter.Group, value => filter.Group = value, new[]
        {
            (Loc.T("None"), GroupMode.None), (Loc.T("Genre"), GroupMode.Genre), (Loc.T("Status"), GroupMode.Status),
            (Loc.T("Year"), GroupMode.Year), (Loc.T("Collection"), GroupMode.Collection),
            (Loc.T("Launcher"), GroupMode.Launcher),
        });
        BuildSingleChoice(StatusChips, filter.Status, value => filter.Status = value, new[]
        {
            (Loc.T("All"), StatusFilter.All), (Loc.T("Installed"), StatusFilter.Installed),
            (Loc.T("Not installed"), StatusFilter.NotInstalled),
        });
        BuildSingleChoice(PlayChips, filter.Play, value => filter.Play = value, new[]
        {
            (Loc.T("Any"), PlayFilter.Any), (Loc.T("Played"), PlayFilter.Played), (Loc.T("Never played"), PlayFilter.NeverPlayed),
        });

        // The English names stay the values the filter works with: only the text on the chip is translated.
        BuildMultiChoice(LauncherChips, launchers.Select(l => (l, l)), filter.Launchers);
        LauncherSection.Visibility = launchers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BuildMultiChoice(FeatureChips, features.Select(f => (Loc.T(f), f)), filter.Features);
        BuildMultiChoice(GenreChips, genres.Select(g => ($"{Loc.T(g.Name)}  {g.Count}", g.Name)), filter.Genres);
        FeatureSection.Visibility = features.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GenreSection.Visibility = genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowResultCount(int shown, int total) =>
        ResultText.Text = shown == total ? Loc.T("{0} games", total) : Loc.T("{0} of {1} games", shown, total);

    /// <summary>One chip per option, one selected at a time.</summary>
    void BuildSingleChoice<T>(WrapPanel panel, T current, Action<T> select, (string Text, T Value)[] options)
        where T : struct, Enum
    {
        panel.Children.Clear();
        var chips = new List<(ToggleButton Chip, T Value)>();
        foreach (var (text, value) in options)
        {
            var chip = CreateChip(text);
            chip.IsChecked = EqualityComparer<T>.Default.Equals(current, value);
            chip.Click += (_, _) =>
            {
                select(value);
                foreach (var (other, otherValue) in chips) other.IsChecked = EqualityComparer<T>.Default.Equals(otherValue, value);
                Changed?.Invoke();
            };
            chips.Add((chip, value));
            panel.Children.Add(chip);
        }
    }

    /// <summary>One chip per option, any number selected: the selected values are kept in <paramref name="selected"/>.</summary>
    void BuildMultiChoice(WrapPanel panel, IEnumerable<(string Text, string Value)> options, HashSet<string> selected)
    {
        panel.Children.Clear();
        foreach (var (text, value) in options)
        {
            var chip = CreateChip(text);
            chip.IsChecked = selected.Contains(value);
            chip.Click += (_, _) =>
            {
                if (chip.IsChecked == true) selected.Add(value);
                else selected.Remove(value);
                Changed?.Invoke();
            };
            panel.Children.Add(chip);
        }
    }

    ToggleButton CreateChip(string text) =>
        new() { Content = text, Style = (Style)FindResource("ChipToggle") };

    void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        filter.Clear();
        Refresh();
        Changed?.Invoke();
    }
}
