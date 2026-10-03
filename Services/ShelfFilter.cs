using System.Globalization;
using System.Text;
using GameShelf.Models;

namespace GameShelf.Services;

internal enum StatusFilter { All, Installed, NotInstalled }

internal enum PlayFilter { Any, Played, NeverPlayed }

internal enum SortMode { Name, RecentlyPlayed, PlayTime, ReleaseDate, SizeOnDisk, Rating }

/// <summary>What is searched, filtered and how the shelf is sorted.</summary>
internal sealed class ShelfFilter
{
    public string Query { get; set; } = "";

    public StatusFilter Status { get; set; }

    public PlayFilter Play { get; set; }

    public SortMode Sort { get; set; }

    /// <summary>How the shelf is split into titled groups. Like the sort, it is not a filter and not cleared.</summary>
    public GroupMode Group { get; set; }

    /// <summary>Only games of this collection, or all games when null.</summary>
    public string? Collection { get; set; }

    /// <summary>A game matches if it comes from one of these launchers (by display name). Empty: any launcher.</summary>
    public HashSet<string> Launchers { get; } = new();

    /// <summary>A game matches if it has any of these genres.</summary>
    public HashSet<string> Genres { get; } = new();

    /// <summary>A game matches if it has all of these features.</summary>
    public HashSet<string> Features { get; } = new();

    /// <summary>Number of active filter choices (the search text and the sort are not counted).</summary>
    public int ActiveCount =>
        (Status != StatusFilter.All ? 1 : 0) + (Play != PlayFilter.Any ? 1 : 0) + (Collection is not null ? 1 : 0)
        + Launchers.Count + Genres.Count + Features.Count;

    public bool IsActive => ActiveCount > 0 || Query.Trim().Length > 0;

    /// <summary>Resets the search and every filter. The sort order is kept.</summary>
    public void Clear()
    {
        Query = "";
        Status = StatusFilter.All;
        Play = PlayFilter.Any;
        Collection = null;
        Launchers.Clear();
        Genres.Clear();
        Features.Clear();
    }

    public bool Matches(Game game)
    {
        if (Status == StatusFilter.Installed && !game.Installed) return false;
        if (Status == StatusFilter.NotInstalled && game.Installed) return false;

        if (Launchers.Count > 0 && !game.OwnedOn.Any(launcher => Launchers.Contains(game.LabelOf(launcher)))) return false;

        // Only Steam records play time: for the other launchers "played" and "never played" are unknown.
        if (Play != PlayFilter.Any && game.Launcher == Launcher.Epic) return false; // Epic's launcher keeps no play time
        if (Play == PlayFilter.Played && !game.HasBeenPlayed) return false;
        if (Play == PlayFilter.NeverPlayed && game.HasBeenPlayed) return false;

        if (Collection is not null && !GameCollections.Contains(Collection, game.KeyId)) return false;
        if (Genres.Count > 0 && !game.Genres.Any(Genres.Contains)) return false;
        if (Features.Count > 0 && !Features.All(game.Features.Contains)) return false;

        // Every word of the search must appear in the name or in a genre.
        var words = Normalize(Query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var text = Normalize($"{game.Name} {string.Join(' ', game.Genres)}");
        return words.All(text.Contains);
    }

    /// <summary>Sorts by the chosen mode (most recent, longest, newest, biggest, best first), then by name.</summary>
    public List<T> Order<T>(IEnumerable<T> items, Func<T, Game> game)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<T> ordered = Sort switch
        {
            SortMode.RecentlyPlayed => items.OrderByDescending(i => game(i).LastPlayed ?? DateTime.MinValue),
            SortMode.PlayTime => items.OrderByDescending(i => game(i).PlayTime),
            SortMode.ReleaseDate => items.OrderByDescending(i => game(i).ReleaseDate ?? DateTime.MinValue),
            SortMode.SizeOnDisk => items.OrderByDescending(i => game(i).SizeOnDisk),
            SortMode.Rating => items.OrderByDescending(i => game(i).Metacritic ?? -1),
            _ => items.OrderBy(i => game(i).Name, byName),
        };
        return ordered.ThenBy(i => game(i).Name, byName).ToList();
    }

    /// <summary>Lower case without accents, so "pokemon" finds "Pokémon".</summary>
    static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }
}
