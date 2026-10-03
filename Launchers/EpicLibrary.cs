using System.IO;
using System.Text;
using System.Text.Json;
using GameShelf.Models;
using Microsoft.Win32;

namespace GameShelf.Launchers;

/// <summary>The three names Epic knows a game by.</summary>
internal sealed record EpicIdentity(string AppName, string Namespace, string ItemId, string Title);

/// <summary>A game of the user's Epic library, as listed by <see cref="EpicClient"/>.</summary>
internal sealed record OwnedEpicGame(
    string AppName,
    string Namespace,
    string ItemId,
    string Title,
    string? Developer,
    string? Description,
    Dictionary<string, string> Images);

/// <summary>
/// Builds the Epic games of the shelf. The owned games come from the launcher itself (<see cref="EpicClient"/>); what
/// is installed comes from the manifests (.item files) the launcher keeps on this PC, and the artwork of installed
/// games also from its catalog cache. Without the owned list, only the installed games are shown.
/// </summary>
internal static class EpicLibrary
{
    // Image types of Epic's catalog: the portrait box art, the logo, and the wide key art.
    public const string BoxArt = "DieselGameBoxTall", LogoArt = "DieselGameBoxLogo", WideArt = "DieselGameBox";

    /// <summary>The address that opens the launcher on its library page.</summary>
    public const string LibraryUri = "com.epicgames.launcher://store/library";

    const string DefaultDataPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\";
    const uint EpicIdFlag = 0x80000000;

    // What the catalog says about each game, by the id GameShelf gave it. Filled in by Scan.
    static readonly Dictionary<uint, CatalogEntry> Entries = new();
    static readonly Dictionary<uint, EpicIdentity> Identities = new();

    /// <summary>What Epic calls a game: needed to install it.</summary>
    public static EpicIdentity? IdentityOf(uint gameId) => Identities.GetValueOrDefault(gameId);

    /// <summary>The launcher's data folder (from the registry, else the default). Settable for the tests.</summary>
    public static string DataPath { get; internal set; } = Locate();

    /// <summary>True for the ids GameShelf makes up for Epic games, which never collide with Steam's (below 2^31).</summary>
    public static bool IsEpicId(uint id) => (id & EpicIdFlag) != 0;

    /// <summary>A stable id for an Epic game: its app name hashed (FNV-1a) with the top bit set.</summary>
    public static uint IdOf(string appName)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(appName)) hash = unchecked((hash ^ b) * 16777619);
        return hash | EpicIdFlag;
    }

    /// <summary>
    /// The Epic games: every game of <paramref name="owned"/> (installed or not), plus the games installed on this
    /// PC that the list does not have (the list was not available).
    /// </summary>
    public static List<Game> Scan(IReadOnlyList<OwnedEpicGame>? owned = null)
    {
        Entries.Clear();
        Identities.Clear();
        var installed = ReadManifests();
        var catalog = installed.Count > 0 ? ReadCatalog() : new Dictionary<string, CatalogEntry>();
        var games = new List<Game>();

        foreach (var game in owned ?? Array.Empty<OwnedEpicGame>())
        {
            installed.Remove(game.AppName, out var manifest);
            games.Add(CreateGame(game.AppName, game.Title, game.Namespace, game.ItemId, manifest,
                new CatalogEntry(game.Description, game.Developer, game.Images)));
        }
        foreach (var manifest in installed.Values)
        {
            games.Add(CreateGame(manifest.AppName, manifest.Name, manifest.CatalogNamespace, manifest.CatalogItemId, manifest,
                catalog.GetValueOrDefault(manifest.CatalogItemId) ?? CatalogEntry.Empty));
        }
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>True when the Epic Games Launcher seems to be installed on this PC.</summary>
    public static bool IsLauncherInstalled => Directory.Exists(DataPath);

    /// <summary>Address of one of the game's images in Epic's catalog, or null if it has none of that type.</summary>
    public static string? ArtUrl(uint gameId, string imageType) =>
        Entries.TryGetValue(gameId, out var entry) && entry.Images.TryGetValue(imageType, out var url) ? url : null;

    /// <summary>What the catalog knows about the game, as store details. Null for a game that is not an Epic game.</summary>
    public static GameDetails? DetailsOf(uint gameId) =>
        Entries.TryGetValue(gameId, out var entry)
            ? new GameDetails(entry.Description, Array.Empty<string>(), entry.Developer, entry.Developer, ReleaseDate: null)
            : null;

    static Game CreateGame(string appName, string title, string catalogNamespace, string itemId, Manifest? manifest, CatalogEntry entry)
    {
        var game = new Game(IdOf(appName), title)
        {
            Launcher = Launcher.Epic,
            Installed = manifest is not null,
            SizeOnDisk = manifest?.Size ?? 0,
            InstallDir = manifest?.InstallLocation,
            LaunchUri = AppUri(catalogNamespace, itemId, appName, "launch&silent=true"),
            InstallUri = AppUri(catalogNamespace, itemId, appName, "install"),
        };
        Entries[game.AppId] = entry;
        Identities[game.AppId] = new EpicIdentity(appName, catalogNamespace, itemId, title);
        return game;
    }

    /// <summary>An address the launcher understands: start the game, or open its install window.</summary>
    static string AppUri(string catalogNamespace, string itemId, string appName, string action) =>
        $"com.epicgames.launcher://apps/{catalogNamespace}%3A{itemId}%3A{appName}?action={action}";

    static string Locate()
    {
        foreach (var key in new[] { @"SOFTWARE\Epic Games\EpicGamesLauncher", @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher" })
        {
            if (Registry.LocalMachine.OpenSubKey(key)?.GetValue("AppDataPath") is string path && Directory.Exists(path))
                return path;
        }
        return DefaultDataPath;
    }

    /// <summary>The installed games, by app name. Add-ons, unfinished installs and engine versions are left out.</summary>
    static Dictionary<string, Manifest> ReadManifests()
    {
        var result = new Dictionary<string, Manifest>();
        var folder = Path.Combine(DataPath, "Manifests");
        if (!Directory.Exists(folder)) return result;

        foreach (var file in Directory.GetFiles(folder, "*.item"))
            if (ReadManifest(file) is { } manifest) result[manifest.AppName] = manifest;
        return result;
    }

    /// <summary>
    /// Removes a game's files and its manifest, which is all the launcher does to uninstall (its own confirmation
    /// box cannot be answered from outside). Only a folder holding the launcher's .egstore data is deleted. False if
    /// nothing was found or a file is in use; the launcher keeps showing the game until it is restarted.
    /// </summary>
    public static bool RemoveInstall(string appName)
    {
        var folder = Path.Combine(DataPath, "Manifests");
        if (!Directory.Exists(folder)) return false;
        foreach (var file in Directory.GetFiles(folder, "*.item", SearchOption.AllDirectories)) // a download in progress may be filed in a sub-folder
        {
            try
            {
                string? location;
                using (var document = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    if (Text(document.RootElement, "AppName") != appName) continue;
                    location = Text(document.RootElement, "InstallLocation");
                }
                if (location is { Length: > 0 } && Directory.Exists(Path.Combine(location, ".egstore"))) Directory.Delete(location, true);
                File.Delete(file);
                return true;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    static Manifest? ReadManifest(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var appName = Text(root, "AppName");
            var name = Text(root, "DisplayName");
            if (appName is null || name is null) return null;

            bool incomplete = root.TryGetProperty("bIsIncompleteInstall", out var flag) && flag.ValueKind == JsonValueKind.True;
            // An add-on names the game it belongs to; so do engine versions and plugins, which are not games.
            var mainGame = Text(root, "MainGameAppName");
            bool addOn = !string.IsNullOrEmpty(mainGame) && mainGame != appName;
            bool notAGame = root.TryGetProperty("AppCategories", out var categories)
                && categories.ValueKind == JsonValueKind.Array
                && categories.GetArrayLength() > 0
                && !categories.EnumerateArray().Any(c => c.GetString() == "games");
            if (incomplete || addOn || notAGame) return null;

            return new Manifest(
                appName, name,
                Text(root, "CatalogNamespace") ?? "", Text(root, "CatalogItemId") ?? "",
                Text(root, "InstallLocation"),
                root.TryGetProperty("InstallSize", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // the launcher is writing the file right now, or it is not a manifest
        }
    }

    /// <summary>
    /// The launcher's catalog cache (Data\Catalog\catcache.bin): a base64 text holding a JSON list of the items it
    /// has seen, with their images. Keyed by catalog item id.
    /// </summary>
    static Dictionary<string, CatalogEntry> ReadCatalog()
    {
        var result = new Dictionary<string, CatalogEntry>();
        var file = Path.Combine(DataPath, "Catalog", "catcache.bin");
        if (!File.Exists(file)) return result;
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(File.ReadAllText(file).Trim()));
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (Text(item, "id") is not { } id) continue;
                var images = new Dictionary<string, string>();
                if (item.TryGetProperty("keyImages", out var keyImages) && keyImages.ValueKind == JsonValueKind.Array)
                    foreach (var image in keyImages.EnumerateArray())
                        if (Text(image, "type") is { } type && Text(image, "url") is { } url) images[type] = url;
                result[id] = new CatalogEntry(Text(item, "description"), Text(item, "developer"), images);
            }
        }
        catch (Exception e) when (e is IOException or FormatException or JsonException or InvalidOperationException)
        {
            // An unreadable cache only costs the artwork: the games are still listed from their manifests.
        }
        return result;
    }

    static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    sealed record Manifest(string AppName, string Name, string CatalogNamespace, string CatalogItemId, string? InstallLocation, long Size);

    sealed record CatalogEntry(string? Description, string? Developer, IReadOnlyDictionary<string, string> Images)
    {
        public static readonly CatalogEntry Empty = new(null, null, new Dictionary<string, string>());
    }
}
