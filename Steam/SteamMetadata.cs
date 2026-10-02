using System.IO;
using GameShelf.Models;

namespace GameShelf.Steam;

/// <summary>
/// Fills in what the shelf can search and filter on, from files Steam keeps on this PC: genres, features, release
/// date and Metacritic score from appinfo.vdf; play time and last played date from localconfig.vdf.
/// </summary>
internal static class SteamMetadata
{
    // Steam's store genre ids (appinfo.vdf, common/genres). Ids not listed here are left out.
    static readonly Dictionary<int, string> GenreNames = new()
    {
        [1] = "Action", [2] = "Strategy", [3] = "RPG", [4] = "Casual", [9] = "Racing", [18] = "Sports",
        [23] = "Indie", [25] = "Adventure", [28] = "Simulation", [29] = "Massively Multiplayer",
        [37] = "Free to Play", [70] = "Early Access",
        [51] = "Animation & Modeling", [52] = "Audio Production", [53] = "Design & Illustration", [54] = "Education",
        [55] = "Photo Editing", [56] = "Software Training", [57] = "Utilities", [58] = "Video Production",
        [59] = "Web Publishing", [60] = "Game Development",
    };

    // Steam's store category ids (appinfo.vdf, common/category), grouped into a few features.
    static readonly Dictionary<int, string> FeatureNames = new()
    {
        [2] = "Single-player",
        [1] = "Multiplayer", [36] = "Multiplayer", [37] = "Multiplayer", [47] = "Multiplayer", [49] = "Multiplayer",
        [9] = "Co-op", [38] = "Co-op", [39] = "Co-op", [48] = "Co-op",
        [18] = "Controller support", [28] = "Controller support",
    };

    /// <summary>The features the filter offers, in display order.</summary>
    public static readonly string[] AllFeatures = { "Single-player", "Multiplayer", "Co-op", "Controller support" };

    public static void Fill(IReadOnlyCollection<Game> games)
    {
        var playStats = SteamLibrary.ReadAllPlayStats();
        var info = ReadAppInfo(games);

        foreach (var game in games)
        {
            if (playStats.TryGetValue(game.AppId, out var stats))
            {
                game.PlayTime = stats.PlayTime;
                game.LastPlayed = stats.LastPlayed;
            }

            if (!info.TryGetValue(game.AppId, out var app) || app.Node("common") is not { } common) continue;

            game.Genres = common.Node("genres")?.Values.OfType<string>()
                .Select(id => int.TryParse(id, out var genreId) && GenreNames.TryGetValue(genreId, out var name) ? name : null)
                .OfType<string>().Distinct().ToList() ?? new List<string>();

            // Categories are keys like "category_2" (single-player), each set to "1".
            game.Features = common.Node("category")?.Keys
                .Select(FeatureOfCategoryKey)
                .OfType<string>().ToHashSet() ?? new HashSet<string>();

            if (long.TryParse(common.Str("steam_release_date"), out var released) && released > 0)
                game.ReleaseDate = DateTimeOffset.FromUnixTimeSeconds(released).LocalDateTime;
            if (int.TryParse(common.Str("metacritic_score"), out var score) && score > 0)
                game.Metacritic = score;
        }
    }

    static string? FeatureOfCategoryKey(string key)
    {
        const string prefix = "category_";
        return key.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(key[prefix.Length..], out var id)
            && FeatureNames.TryGetValue(id, out var feature)
            ? feature
            : null;
    }

    static Dictionary<uint, Kv> ReadAppInfo(IReadOnlyCollection<Game> games)
    {
        var path = Path.Combine(SteamLibrary.SteamPath, "appcache", "appinfo.vdf");
        try
        {
            return File.Exists(path)
                ? Vdf.ParseAppInfo(path, games.Select(g => g.AppId).ToHashSet())
                : new Dictionary<uint, Kv>();
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException or EndOfStreamException)
        {
            return new Dictionary<uint, Kv>(); // unreadable or a newer format: search then works on names only
        }
    }
}
