using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameShelf.Services;

/// <summary>A cover found for a game: the picture itself, already downloaded, and what it is called at its source.</summary>
internal sealed record CoverCandidate(string Title, byte[] Image);

/// <summary>A console and the folder of libretro's thumbnail server that holds its box art; no folder means "look in Steam".</summary>
internal sealed record ConsoleChoice(string Label, string? Folder);

/// <summary>
/// Finds covers for a game added by hand, without any account or key: Steam's public store search for PC games, and
/// libretro's thumbnail server (box art of the games of old and current consoles, named like the game's ROM) for the
/// consoles. The picture is downloaded here so that a cover that does not exist is never offered.
/// </summary>
internal static partial class CoverSearch
{
    public static readonly IReadOnlyList<ConsoleChoice> Consoles = new ConsoleChoice[]
    {
        new("PC", null),
        new("Nintendo Switch / other", null),
        new("Nintendo 64", "Nintendo - Nintendo 64"),
        new("GameCube", "Nintendo - GameCube"),
        new("Wii", "Nintendo - Wii"),
        new("Wii U", "Nintendo - Wii U"),
        new("Super Nintendo", "Nintendo - Super Nintendo Entertainment System"),
        new("Nintendo (NES)", "Nintendo - Nintendo Entertainment System"),
        new("Game Boy", "Nintendo - Game Boy"),
        new("Game Boy Color", "Nintendo - Game Boy Color"),
        new("Game Boy Advance", "Nintendo - Game Boy Advance"),
        new("Nintendo DS", "Nintendo - Nintendo DS"),
        new("Nintendo 3DS", "Nintendo - Nintendo 3DS"),
        new("PlayStation", "Sony - PlayStation"),
        new("PlayStation 2", "Sony - PlayStation 2"),
        new("PlayStation 3", "Sony - PlayStation 3"),
        new("PSP", "Sony - PlayStation Portable"),
        new("PS Vita", "Sony - PlayStation Vita"),
        new("Mega Drive / Genesis", "Sega - Mega Drive - Genesis"),
        new("Sega Saturn", "Sega - Saturn"),
        new("Dreamcast", "Sega - Dreamcast"),
        new("Xbox", "Microsoft - Xbox"),
        new("Xbox 360", "Microsoft - Xbox 360"),
    };

    const int MaxResults = 6;
    const string LibretroRoot = "https://thumbnails.libretro.com/";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The covers that match <paramref name="name"/> for the console (up to six), or none when offline.</summary>
    public static async Task<List<CoverCandidate>> SearchAsync(string name, ConsoleChoice console)
    {
        try
        {
            var found = console.Folder is null ? await SteamAsync(name) : await LibretroAsync(name, console.Folder);
            var downloads = await Task.WhenAll(found.Select(async hit =>
            {
                try
                {
                    return new CoverCandidate(hit.Title, await Http.GetByteArrayAsync(hit.Url));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    return null; // this one has no picture
                }
            }));
            return downloads.OfType<CoverCandidate>().ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new List<CoverCandidate>();
        }
    }

    static async Task<List<(string Title, string Url)>> SteamAsync(string name)
    {
        var json = await Http.GetStringAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=us");
        using var document = JsonDocument.Parse(json);
        var hits = new List<(string, string)>();
        if (document.RootElement.TryGetProperty("items", out var items))
            foreach (var item in items.EnumerateArray().Take(MaxResults))
                if (item.TryGetProperty("id", out var id) && item.TryGetProperty("name", out var title))
                    hits.Add((title.GetString() ?? "", $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id.GetInt64()}/library_600x900.jpg"));
        return hits;
    }

    static async Task<List<(string Title, string Url)>> LibretroAsync(string name, string folder)
    {
        var names = await LibretroIndexAsync(folder);
        return Rank(names, name, MaxResults)
            .Select(file => (file, $"{LibretroRoot}{Uri.EscapeDataString(folder)}/Named_Boxarts/{Uri.EscapeDataString(file + ".png")}"))
            .ToList();
    }

    /// <summary>The names of the box art of one console. Kept for a month in the app data folder: the list is large.</summary>
    static async Task<List<string>> LibretroIndexAsync(string folder)
    {
        var cacheName = $"libretro/{folder}.txt";
        var cache = AppData.PathOf(cacheName);
        if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(30))
            return File.ReadAllLines(cache).ToList();

        var html = await Http.GetStringAsync($"{LibretroRoot}{Uri.EscapeDataString(folder)}/Named_Boxarts/");
        var names = ParseIndex(html);
        if (names.Count > 0) AppData.WriteText(cacheName, string.Join('\n', names));
        return names;
    }

    /// <summary>The picture names of an Apache directory listing, decoded and without ".png".</summary>
    internal static List<string> ParseIndex(string html) =>
        PngLink().Matches(html).Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).Distinct().ToList();

    /// <summary>
    /// The names that match <paramref name="query"/>, best first: the same title, then one that starts with it, then
    /// one that contains it, then one that has all its words. Among equals the USA, then European, release comes first.
    /// </summary>
    internal static List<string> Rank(IEnumerable<string> names, string query, int take)
    {
        var wanted = Platforms.Key(query);
        if (wanted.Length == 0) return new List<string>();
        var words = Words().Matches(query).Select(m => Platforms.Key(m.Value)).Where(w => w.Length > 0).ToArray();

        return names
            .Select(file =>
            {
                var key = Platforms.Key(Tags().Replace(file, ""));
                int score = key == wanted ? 0 : key.StartsWith(wanted, StringComparison.Ordinal) ? 1
                    : key.Contains(wanted, StringComparison.Ordinal) ? 2 : words.All(key.Contains) ? 3 : 9;
                int region = file.Contains("(USA", StringComparison.Ordinal) ? 0 : file.Contains("(Europe", StringComparison.Ordinal) ? 1 : 2;
                return (file, score, region);
            })
            .Where(x => x.score < 9)
            .OrderBy(x => x.score).ThenBy(x => x.region).ThenBy(x => x.file.Length)
            .Select(x => x.file)
            .Take(take)
            .ToList();
    }

    [GeneratedRegex(@"href=""([^""?/][^""]*)\.png""")]
    private static partial Regex PngLink();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
