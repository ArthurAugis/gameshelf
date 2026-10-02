using System.Globalization;
using System.IO;
using GameShelf.Models;
using Microsoft.Win32;

namespace GameShelf.Steam;

/// <summary>Locates Steam on this PC and lists the games it knows about.</summary>
internal static class SteamLibrary
{
    /// <summary>File names of the artwork Steam keeps in appcache\librarycache\&lt;appid&gt;.</summary>
    public const string CoverFile = "library_600x900.jpg";
    public const string CapsuleFile = "library_capsule.jpg"; // the portrait cover, in Steam's newer cache layout
    public const string LogoFile = "logo.png";
    public const string HeroFile = "library_hero.jpg";

    const string DefaultSteamPath = @"C:\Program Files (x86)\Steam";

    // StateFlags of an appmanifest: bit 4 = fully installed. Installing, or uninstalling, have other values.
    const int FullyInstalledFlag = 4;

    /// <summary>
    /// Steam's install folder, from the registry (default location if the key is missing). Settable so the tests
    /// can point it at a made-up Steam folder.
    /// </summary>
    public static string SteamPath { get; internal set; } = Locate();

    /// <summary>Path of an artwork file Steam already cached for an app, or null.</summary>
    public static string? LocalArt(uint appId, string fileName)
    {
        var folder = Path.Combine(SteamPath, "appcache", "librarycache", appId.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(folder)) return null;

        // Recent Steam clients keep the art of many games in sub-folders named by a hash, and call the portrait
        // cover "library_capsule.jpg" there.
        var names = fileName == CoverFile ? new[] { CoverFile, CapsuleFile } : new[] { fileName };
        foreach (var name in names)
        {
            var direct = Path.Combine(folder, name);
            if (File.Exists(direct)) return direct;
            if (Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault() is { } nested) return nested;
        }
        return null;
    }

    /// <summary>The game as installed on disk, or null if it is not (fully) installed.</summary>
    public static Game? ReadInstalledGame(uint appId)
    {
        foreach (var folder in LibraryFolders())
        {
            var steamApps = Path.Combine(folder, "steamapps");
            var manifest = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
            if (File.Exists(manifest) && ReadManifest(manifest, steamApps) is { } game) return game;
        }
        return null;
    }

    /// <summary>
    /// Play time and last play date from Steam's localconfig.vdf. When several accounts used this PC the
    /// highest values win. Null if the game was never played.
    /// </summary>
    public static PlayStats? ReadPlayStats(uint appId) => ReadAllPlayStats().GetValueOrDefault(appId);

    /// <summary>Play stats of every game that was ever played, in one read of the localconfig files.</summary>
    public static Dictionary<uint, PlayStats> ReadAllPlayStats()
    {
        var minutes = new Dictionary<uint, long>();
        var lastPlayed = new Dictionary<uint, long>();

        var userData = Path.Combine(SteamPath, "userdata");
        if (Directory.Exists(userData))
            foreach (var user in Directory.GetDirectories(userData))
            {
                var localConfig = Path.Combine(user, "config", "localconfig.vdf");
                if (!File.Exists(localConfig)) continue;

                var apps = Vdf.ParseText(File.ReadAllText(localConfig))
                    .Node("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                if (apps is null) continue;

                foreach (var (key, value) in apps)
                {
                    if (!uint.TryParse(key, out var appId) || value is not Kv app) continue;
                    if (long.TryParse(app.Str("Playtime"), out var playtime))
                        minutes[appId] = Math.Max(minutes.GetValueOrDefault(appId), playtime);
                    if (long.TryParse(app.Str("LastPlayed"), out var last))
                        lastPlayed[appId] = Math.Max(lastPlayed.GetValueOrDefault(appId), last);
                }
            }

        return minutes.Keys.Union(lastPlayed.Keys)
            .Select(appId => (appId, minutes: minutes.GetValueOrDefault(appId), last: lastPlayed.GetValueOrDefault(appId)))
            .Where(s => s.minutes > 0 || s.last > 0)
            .ToDictionary(
                s => s.appId,
                s => new PlayStats(
                    TimeSpan.FromMinutes(s.minutes),
                    s.last > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.last).LocalDateTime : null));
    }

    /// <summary>
    /// Builds the game list. <paramref name="library"/> is the exact list asked from the running Steam client
    /// (see <see cref="SteamClient"/>). Without it, the list comes from local history only, which also contains
    /// refunded games and games from people who left the family.
    /// </summary>
    public static List<Game> Scan(Dictionary<uint, string>? library = null)
    {
        var installed = FindInstalled();
        var games = library is not null ? FromClientLibrary(library, installed) : FromLocalHistory(installed);
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string Locate()
    {
        var registryPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        return registryPath is not null && Directory.Exists(registryPath)
            ? Path.GetFullPath(registryPath)
            : DefaultSteamPath;
    }

    /// <summary>Every Steam library folder: Steam's own plus the ones listed in libraryfolders.vdf.</summary>
    static readonly System.Buffers.SearchValues<char> PathSeparators = System.Buffers.SearchValues.Create(@"\/");

    /// <summary>
    /// Deletes what Steam leaves behind when a download is cancelled: the app manifest, the staged files
    /// (<c>steamapps\downloading\id</c>) and the install folder. Only for a game that was never fully installed: a
    /// game whose manifest says it is installed is left alone. True if something was deleted. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when Steam still holds a file.
    /// </summary>
    public static bool DeletePartialInstall(uint appId)
    {
        bool deleted = false;
        foreach (var folder in LibraryFolders())
        {
            var steamApps = Path.Combine(folder, "steamapps");
            var manifest = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
            string? installDir = null;
            if (File.Exists(manifest))
            {
                var state = Vdf.ParseText(File.ReadAllText(manifest)).Node("AppState");
                if (state is not null && int.TryParse(state.Str("StateFlags"), out var flags) && (flags & FullyInstalledFlag) != 0) continue;
                installDir = state?.Str("installdir");
            }

            foreach (var staged in new[] { "downloading", "temp" })
            {
                var path = Path.Combine(steamApps, staged, appId.ToString(CultureInfo.InvariantCulture));
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    deleted = true;
                }
            }
            // The install folder is a single name inside steamapps\common: never a path that could point elsewhere.
            if (!string.IsNullOrWhiteSpace(installDir) && installDir.AsSpan().IndexOfAny(PathSeparators) < 0 && installDir is not ("." or ".."))
            {
                var path = Path.Combine(steamApps, "common", installDir);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    deleted = true;
                }
            }
            if (File.Exists(manifest))
            {
                File.Delete(manifest);
                deleted = true;
            }
        }
        return deleted;
    }

    // Steam's "update required" bit in an appmanifest's StateFlags.
    const int UpdateRequiredFlag = 2;

    /// <summary>
    /// The installed games Steam has an update for, with the bytes to download, read from the app manifests of every
    /// library folder: a fully installed game whose manifest says an update is required.
    /// </summary>
    public static Dictionary<uint, long> ReadPendingUpdates()
    {
        var pending = new Dictionary<uint, long>();
        foreach (var folder in LibraryFolders())
        {
            var steamApps = Path.Combine(folder, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var state = Vdf.ParseText(File.ReadAllText(manifest)).Node("AppState");
                    if (state is null || !uint.TryParse(state.Str("appid"), out var appId)) continue;
                    if (!int.TryParse(state.Str("StateFlags"), out var flags)) continue;
                    if ((flags & FullyInstalledFlag) == 0 || (flags & UpdateRequiredFlag) == 0) continue;
                    pending[appId] = long.TryParse(state.Str("BytesToDownload"), out var bytes) ? bytes : 0;
                }
                catch (IOException)
                {
                    // Steam is writing the file right now: the next look will see it.
                }
            }
        }
        return pending;
    }

    static List<string> LibraryFolders()
    {
        var folders = new List<string> { SteamPath };
        var listFile = Path.Combine(SteamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(listFile) && Vdf.ParseText(File.ReadAllText(listFile)).Node("libraryfolders") is { } node)
            folders.AddRange(node.Values.OfType<Kv>().Select(f => f.Str("path")).OfType<string>());
        return folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Installed games, read from the appmanifest files of every Steam library folder.</summary>
    static Dictionary<uint, Game> FindInstalled()
    {
        var installed = new Dictionary<uint, Game>();
        foreach (var folder in LibraryFolders())
        {
            var steamApps = Path.Combine(folder, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var manifest in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
                if (ReadManifest(manifest, steamApps) is { } game) installed[game.AppId] = game;
        }
        return installed;
    }

    static Game? ReadManifest(string manifestPath, string steamApps)
    {
        Kv? state;
        try
        {
            state = Vdf.ParseText(File.ReadAllText(manifestPath)).Node("AppState");
        }
        catch (IOException)
        {
            return null; // Steam is writing the file right now
        }

        if (state is null || !uint.TryParse(state.Str("appid"), out var appId)) return null;
        if (!int.TryParse(state.Str("StateFlags"), out var flags) || (flags & FullyInstalledFlag) == 0) return null;

        _ = long.TryParse(state.Str("SizeOnDisk"), out var size); // a missing size shows as 0
        var installDir = state.Str("installdir");
        return new Game(appId, state.Str("name") ?? $"App {appId}")
        {
            Installed = true,
            SizeOnDisk = size,
            InstallDir = installDir is null ? null : Path.Combine(steamApps, "common", installDir),
            Cover = LocalArt(appId, CoverFile),
        };
    }

    static IEnumerable<Game> FromClientLibrary(Dictionary<uint, string> library, Dictionary<uint, Game> installed) =>
        library.Select(entry => installed.TryGetValue(entry.Key, out var game) ? game : NotInstalled(entry.Key, entry.Value));

    static IEnumerable<Game> FromLocalHistory(Dictionary<uint, Game> installed)
    {
        var appIds = new HashSet<uint>(installed.Keys);
        appIds.UnionWith(ReadLocalHistoryIds());
        var info = Vdf.ParseAppInfo(Path.Combine(SteamPath, "appcache", "appinfo.vdf"), appIds);

        foreach (var appId in appIds)
        {
            info.TryGetValue(appId, out var app);
            var type = app?.Str("common", "type")?.ToLowerInvariant();

            if (installed.TryGetValue(appId, out var game))
            {
                if (type != "tool") yield return game;
            }
            else if (type is "game" or "application" or "demo" or "beta" && app?.Str("common", "name") is { } name)
            {
                yield return NotInstalled(appId, name);
            }
        }
    }

    /// <summary>
    /// App ids Steam has any local trace of, across all local accounts: achievement cache files
    /// and the apps listed in localconfig.vdf (the ones that were played).
    /// </summary>
    static IEnumerable<uint> ReadLocalHistoryIds()
    {
        var userData = Path.Combine(SteamPath, "userdata");
        if (!Directory.Exists(userData)) yield break;

        foreach (var user in Directory.GetDirectories(userData))
        {
            var config = Path.Combine(user, "config");

            var cacheFolder = Path.Combine(config, "librarycache");
            if (Directory.Exists(cacheFolder))
                foreach (var file in Directory.GetFiles(cacheFolder, "*.json"))
                    if (uint.TryParse(Path.GetFileNameWithoutExtension(file), out var cachedId)) yield return cachedId;

            var localConfig = Path.Combine(config, "localconfig.vdf");
            if (File.Exists(localConfig) && Vdf.ParseText(File.ReadAllText(localConfig))
                    .Node("UserLocalConfigStore", "Software", "Valve", "Steam", "apps") is { } played)
                foreach (var key in played.Keys)
                    if (uint.TryParse(key, out var playedId)) yield return playedId;
        }
    }

    static Game NotInstalled(uint appId, string name) => new(appId, name) { Cover = LocalArt(appId, CoverFile) };
}
