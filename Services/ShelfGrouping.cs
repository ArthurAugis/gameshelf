using System.Globalization;
using GameShelf.Models;

namespace GameShelf.Services;

internal enum GroupMode { None, Genre, Status, Year, Collection, Launcher }

/// <summary>A titled part of the shelf. <see cref="Collection"/> is set when the group stands for a collection.</summary>
internal sealed record ShelfGroup<T>(string Title, List<T> Items, string? Collection = null);

/// <summary>Splits the (already sorted) shelf into titled groups. Each group keeps the order of its items.</summary>
internal static class ShelfGrouping
{
    const string OtherGenre = "Other", UnknownYear = "Unknown", NoCollection = "Not in a collection";

    // Store tags that say how a game is sold rather than what it is: a game's group only falls back to them.
    static readonly HashSet<string> NotAKind = new() { "Indie", "Free to Play", "Early Access", "Massively Multiplayer" };

    /// <param name="mode">How to group.</param>
    /// <param name="ordered">The items, already sorted.</param>
    /// <param name="game">The game an item stands for.</param>
    /// <param name="keepEmptyCollections">
    /// In collection mode, also return the collections that have no game, so games can be dropped on them.
    /// </param>
    public static List<ShelfGroup<T>> Group<T>(
        GroupMode mode, List<T> ordered, Func<T, Game> game, bool keepEmptyCollections)
    {
        if (mode == GroupMode.None) return new List<ShelfGroup<T>> { new("", ordered) };

        var groups = new Dictionary<string, List<T>>();
        foreach (var item in ordered)
        {
            foreach (var key in KeysOf(mode, game(item)))
            {
                if (!groups.TryGetValue(key, out var items)) groups[key] = items = new List<T>();
                items.Add(item);
            }
        }

        var result = new List<ShelfGroup<T>>();
        if (mode == GroupMode.Collection)
        {
            foreach (var name in GameCollections.Names)
            {
                if (groups.TryGetValue(name, out var items)) result.Add(new ShelfGroup<T>(name, items, name));
                else if (keepEmptyCollections) result.Add(new ShelfGroup<T>(name, new List<T>(), name));
            }
            if (groups.TryGetValue(NoCollection, out var rest)) result.Add(new ShelfGroup<T>(NoCollection, rest));
            return result;
        }

        foreach (var title in OrderedTitles(mode, groups.Keys))
            result.Add(new ShelfGroup<T>(title, groups[title]));
        return result;
    }

    /// <summary>Alphabetical (newest first for years), with the "unknown" group last.</summary>
    static IEnumerable<string> OrderedTitles(GroupMode mode, IEnumerable<string> titles)
    {
        var known = titles.OrderBy(title => title is OtherGenre or UnknownYear);
        return mode == GroupMode.Year
            ? known.ThenByDescending(title => title, StringComparer.Ordinal)
            : known.ThenBy(title => title, StringComparer.CurrentCultureIgnoreCase);
    }

    static IEnumerable<string> KeysOf(GroupMode mode, Game game) => mode switch
    {
        GroupMode.Genre => new[] { MainGenre(game) },
        GroupMode.Status => new[] { game.Installed ? "Installed" : "Not installed" },
        GroupMode.Launcher => new[] { string.Join(" + ", game.OwnedOn.Select(game.LabelOf)) },
        GroupMode.Year => new[] { game.ReleaseDate?.Year.ToString(CultureInfo.InvariantCulture) ?? UnknownYear },
        _ => CollectionsOf(game),
    };

    static string MainGenre(Game game)
    {
        if (game.Genres.Count == 0) return OtherGenre;
        return game.Genres.FirstOrDefault(genre => !NotAKind.Contains(genre)) ?? game.Genres[0];
    }

    static IEnumerable<string> CollectionsOf(Game game)
    {
        var names = GameCollections.Of(game.KeyId).ToList();
        return names.Count > 0 ? names : new[] { NoCollection };
    }
}
