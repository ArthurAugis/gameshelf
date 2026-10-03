using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GameShelf.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace GameShelf.Launchers;

/// <summary>
/// Builds the GOG games of the shelf from GOG Galaxy's own database (galaxy-2.0.db): the games the account owns,
/// installed or not, with their artwork, play time and install folder. Nothing is asked of GOG's servers and no
/// sign-in is needed here: Galaxy keeps the library up to date, GameShelf only reads a copy of the file.
/// </summary>
internal static class GogLibrary
{
    const string DefaultDatabase = @"C:\ProgramData\GOG.com\Galaxy\storage\galaxy-2.0.db";
    const uint GogIdFlag = 0x80000000;

    // Galaxy's names for the artwork, as keys of its "originalImages" piece.
    public const string CoverArt = "verticalCover", WideArt = "background";

    sealed record Entry(string ProductId, string? Description, string? Developer, string? Genres, string? ReleaseDate, Dictionary<string, string> Images);

    // What Galaxy says about each game, by the id GameShelf gave it. Filled in by Scan.
    static readonly Dictionary<uint, Entry> Entries = new();

    /// <summary>Galaxy's database (from the registry, else the default). Settable for the tests.</summary>
    public static string DatabasePath { get; internal set; } = Locate();

    /// <summary>True when GOG Galaxy has a library file on this PC.</summary>
    public static bool IsGalaxyInstalled => File.Exists(DatabasePath);

    /// <summary>True for the ids Scan gave to GOG games. They share the top bit with Epic's, so ask this first.</summary>
    public static bool IsGogId(uint id) => Entries.ContainsKey(id);

    /// <summary>A stable id for a GOG game: its product id hashed (FNV-1a) with the top bit set.</summary>
    public static uint IdOf(string productId)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes("gog:" + productId)) hash = unchecked((hash ^ b) * 16777619);
        return hash | GogIdFlag;
    }

    /// <summary>Address of one of the game's images ("verticalCover", "background"), as a JPEG, or null.</summary>
    public static string? ArtUrl(uint gameId, string imageKey) =>
        Entries.TryGetValue(gameId, out var entry) && entry.Images.TryGetValue(imageKey, out var url) ? url : null;

    /// <summary>What Galaxy knows about the game, as store details. Null for a game that is not a GOG game.</summary>
    public static GameDetails? DetailsOf(uint gameId) =>
        Entries.TryGetValue(gameId, out var entry)
            ? new GameDetails(entry.Description, entry.Genres is null ? Array.Empty<string>() : entry.Genres.Split(", "),
                entry.Developer, entry.Developer, entry.ReleaseDate)
            : null;

    /// <summary>The games of the library, or none when Galaxy is not installed, not signed in, or busy writing its file.</summary>
    public static List<Game> Scan()
    {
        Entries.Clear();
        if (!IsGalaxyInstalled) return new List<Game>();

        var work = Path.Combine(Path.GetTempPath(), "GameShelfGog-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Galaxy keeps its file open and in WAL mode: read a copy of the file and of its log.
            Directory.CreateDirectory(work);
            var copy = Path.Combine(work, "galaxy.db");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(DatabasePath + suffix)) File.Copy(DatabasePath + suffix, copy + suffix, overwrite: true);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
            connection.Open();
            return Read(connection);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new List<Game>(); // Galaxy is writing the file right now, or it is an unknown version
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The temporary copy stays until the system cleans the folder.
            }
        }
    }

    static List<Game> Read(SqliteConnection db)
    {
        var owned = Rows(db,
            "SELECT lr.releaseKey FROM LibraryReleases lr JOIN LicensedReleases ls ON ls.libraryId = lr.id " +
            "WHERE ls.isOwned = 1 AND lr.releaseKey LIKE 'gog\\_%' ESCAPE '\\'",
            row => row.GetString(0)).ToHashSet();

        var pieces = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (releaseKey, type, value) in Rows(db,
            "SELECT p.releaseKey, t.type, p.value FROM GamePieces p JOIN GamePieceTypes t ON t.id = p.gamePieceTypeId " +
            "WHERE t.type IN ('title', 'originalTitle', 'originalImages', 'meta', 'originalMeta', 'summary')",
            row => (row.GetString(0), row.GetString(1), row.IsDBNull(2) ? "" : row.GetString(2))))
        {
            if (!owned.Contains(releaseKey)) continue;
            if (!pieces.TryGetValue(releaseKey, out var byType)) pieces[releaseKey] = byType = new Dictionary<string, string>();
            byType.TryAdd(type, value);
        }

        var installed = Rows(db, "SELECT productId, installationPath FROM InstalledBaseProducts",
            row => (Id: row.GetValue(0).ToString() ?? "", Path: row.IsDBNull(1) ? null : row.GetString(1)))
            .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Path);
        var minutes = Rows(db, "SELECT releaseKey, minutesInGame FROM GameTimes",
            row => (Key: row.GetString(0), Minutes: row.IsDBNull(1) ? 0L : row.GetInt64(1)))
            .GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Max(r => r.Minutes));
        var played = Rows(db, "SELECT gameReleaseKey, lastPlayedDate FROM LastPlayedDates",
            row => (Key: row.GetString(0), Date: row.IsDBNull(1) ? null : row.GetValue(1).ToString()))
            .GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Select(r => r.Date).FirstOrDefault());
        var sizes = Rows(db, "SELECT gameReleaseKey, diskSize FROM DiskSizes",
            row => (Key: row.GetString(0), Size: row.IsDBNull(1) ? 0L : row.GetInt64(1)))
            .GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Max(r => r.Size));

        var games = new List<Game>();
        foreach (var (releaseKey, byType) in pieces)
        {
            var title = TextOf(byType, "title", "title") ?? TextOf(byType, "originalTitle", "title");
            if (title is null) continue; // not a game of its own (an add-on without a page)

            var productId = releaseKey["gog_".Length..];
            var meta = byType.GetValueOrDefault("meta") ?? byType.GetValueOrDefault("originalMeta");
            installed.TryGetValue(productId, out var installPath);
            var game = new Game(IdOf(productId), title)
            {
                Launcher = Launcher.Gog,
                Installed = installed.ContainsKey(productId),
                InstallDir = installPath,
                SizeOnDisk = sizes.GetValueOrDefault(releaseKey),
                PlayTime = TimeSpan.FromMinutes(minutes.GetValueOrDefault(releaseKey)),
                LastPlayed = played.GetValueOrDefault(releaseKey) is { } date
                    && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                    ? when.ToLocalTime() : null,
                InstallUri = $"goggalaxy://openGameView/{productId}",
            };
            var genres = ListOf(meta, "genres");
            game.Genres = genres;
            game.ReleaseDate = UnixDate(meta);
            Entries[game.AppId] = new Entry(productId, TextOf(byType, "summary", "summary"),
                ListOf(meta, "developers").FirstOrDefault(), genres.Count > 0 ? string.Join(", ", genres) : null,
                game.ReleaseDate?.ToString("d MMM yyyy", CultureInfo.CurrentCulture), ImagesOf(byType.GetValueOrDefault("originalImages")));
            games.Add(game);
        }
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Starts the game through Galaxy, which runs it with its overlay and tracks the play time.</summary>
    public static bool Play(Game game)
    {
        if (!Entries.TryGetValue(game.AppId, out var entry) || GalaxyClient() is not { } client) return false;
        var arguments = $"/command=runGame /gameId={entry.ProductId}" + (game.InstallDir is null ? "" : $" /path=\"{game.InstallDir}\"");
        Process.Start(new ProcessStartInfo(client, arguments) { UseShellExecute = false });
        return true;
    }

    /// <summary>
    /// Starts the game's own uninstaller (GOG installs bring one, which asks for confirmation itself). False when it
    /// has none, and Galaxy's page of the game is the way left.
    /// </summary>
    public static bool Uninstall(Game game)
    {
        if (game.InstallDir is not { } folder || !Directory.Exists(folder)) return false;
        var uninstaller = Directory.GetFiles(folder, "unins*.exe").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (uninstaller is null) return false;
        Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true, WorkingDirectory = folder });
        return true;
    }

    static string? GalaxyClient()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", @"SOFTWARE\GOG.com\GalaxyClient\paths" })
            if (Registry.LocalMachine.OpenSubKey(key)?.GetValue("client") is string folder
                && Path.Combine(folder, "GalaxyClient.exe") is var exe && File.Exists(exe))
                return exe;
        return null;
    }

    static string Locate()
    {
        // The storage folder is named in Galaxy's config.json; the default holds on a standard install.
        var config = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(DefaultDatabase))!, "config.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(config));
            if (document.RootElement.TryGetProperty("storagePath", out var path) && path.GetString() is { Length: > 0 } storage)
                return Path.Combine(storage, "galaxy-2.0.db");
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // No config: the default location.
        }
        return DefaultDatabase;
    }

    static List<T> Rows<T>(SqliteConnection db, string sql, Func<SqliteDataReader, T> read)
    {
        var rows = new List<T>();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(read(reader));
        }
        catch (SqliteException)
        {
            // A table this version of Galaxy does not have: that part of the information is missing.
        }
        return rows;
    }

    /// <summary>A text out of a piece's JSON, e.g. {"title":"..."}.</summary>
    static string? TextOf(Dictionary<string, string> pieces, string type, string property)
    {
        if (!pieces.TryGetValue(type, out var json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static List<string> ListOf(string? metaJson, string property)
    {
        var result = new List<string>();
        if (metaJson is null) return result;
        try
        {
            using var document = JsonDocument.Parse(metaJson);
            if (document.RootElement.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
                result.AddRange(list.EnumerateArray().Select(item => item.GetString()).OfType<string>());
        }
        catch (JsonException)
        {
            // No usable metadata.
        }
        return result;
    }

    static DateTime? UnixDate(string? metaJson)
    {
        if (metaJson is null) return null;
        try
        {
            using var document = JsonDocument.Parse(metaJson);
            return document.RootElement.TryGetProperty("releaseDate", out var seconds) && seconds.TryGetInt64(out var value) && value > 0
                ? DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// The artwork addresses. Galaxy lists WebP files, which WPF cannot read; the same picture is served as JPEG
    /// under the same name.
    /// </summary>
    static Dictionary<string, string> ImagesOf(string? json)
    {
        var images = new Dictionary<string, string>();
        if (json is null) return images;
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.GetString() is { Length: > 0 } url) images[property.Name] = url.Replace(".webp?", ".jpg?", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            // No artwork.
        }
        return images;
    }
}
