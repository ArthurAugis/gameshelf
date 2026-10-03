using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using GameShelf.Models;
using GameShelf.Services;

namespace GameShelf.Launchers;

/// <summary>A program the user added to the shelf by hand (a game outside any launcher, an emulator and its game).</summary>
internal sealed record ManualEntry(string Id, string Name, string Exe, string? Arguments, string Console, double Minutes, DateTime? LastPlayed);

/// <summary>
/// The games added by hand, kept in <c>manual-games.json</c>, each with its cover in the cover folder. A game starts
/// by running its program; the play time is how long that program stays open.
/// </summary>
internal static class ManualGames
{
    const string FileName = "manual-games.json";
    const uint ManualIdFlag = 0x80000000;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    static readonly Dictionary<uint, string> EntryIds = new(); // game id -> entry id. Filled in by Scan.

    /// <summary>The file the games are kept in. Settable for the tests.</summary>
    public static string StoragePath { get; internal set; } = AppData.PathOf(FileName);

    /// <summary>Raised when a game was added or removed, from any thread: the shelf reads the list again.</summary>
    public static event Action? Changed;

    /// <summary>True for the ids Scan gave to the games added by hand. They share the top bit with Epic's, so ask this first.</summary>
    public static bool IsManualId(uint id) => EntryIds.ContainsKey(id);

    /// <summary>A stable id for a game: its entry id hashed (FNV-1a) with the top bit set.</summary>
    public static uint IdOf(string entryId)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes("manual:" + entryId)) hash = unchecked((hash ^ b) * 16777619);
        return hash | ManualIdFlag;
    }

    public static string CoverPath(string entryId) => Path.Combine(Path.GetDirectoryName(StoragePath)!, "covers", $"manual_{entryId}.jpg");

    /// <summary>What the game page shows under "genres" and what the details say: the console, if it is one.</summary>
    public static GameDetails? DetailsOf(uint gameId) =>
        EntryIds.ContainsKey(gameId) ? new GameDetails(null, Array.Empty<string>(), null, null, null) : null;

    public static List<Game> Scan()
    {
        var games = new List<Game>();
        lock (Gate)
        {
            EntryIds.Clear();
            foreach (var entry in Load())
            {
                var cover = CoverPath(entry.Id);
                var game = new Game(IdOf(entry.Id), entry.Name)
                {
                    Launcher = Launcher.Manual,
                    Installed = File.Exists(entry.Exe),
                    InstallDir = Path.GetDirectoryName(entry.Exe),
                    Cover = File.Exists(cover) ? cover : null,
                    PlayTime = TimeSpan.FromMinutes(entry.Minutes),
                    LastPlayed = entry.LastPlayed,
                    Console = entry.Console is { Length: > 0 } and not "PC" ? entry.Console : null,
                };
                EntryIds[game.AppId] = entry.Id;
                games.Add(game);
            }
        }
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Adds a game with its cover (a JPEG), and tells the shelf.</summary>
    public static void Add(string name, string exe, string? arguments, string console, byte[] coverJpeg)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CoverPath(id))!);
            File.WriteAllBytes(CoverPath(id), coverJpeg);
            var entries = Load();
            entries.Add(new ManualEntry(id, name, exe, string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim(), console, 0, null));
            Save(entries);
        }
        Changed?.Invoke();
    }

    /// <summary>What was entered for the game, to fill the edit window. Null if it is not one added by hand.</summary>
    public static ManualEntry? EntryOf(Game game)
    {
        lock (Gate) return EntryIds.TryGetValue(game.AppId, out var id) ? Load().FirstOrDefault(entry => entry.Id == id) : null;
    }

    /// <summary>Changes the game's program, name or console, and its cover when <paramref name="coverJpeg"/> is given. Play time is kept.</summary>
    public static void Edit(Game game, string name, string exe, string? arguments, string console, byte[]? coverJpeg)
    {
        lock (Gate)
        {
            if (!EntryIds.TryGetValue(game.AppId, out var id)) return;
            if (coverJpeg is not null) File.WriteAllBytes(CoverPath(id), coverJpeg);
            Update(id, entry => entry with { Name = name, Exe = exe, Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim(), Console = console });
        }
        Changed?.Invoke();
    }

    /// <summary>Takes the game off the shelf. The program and its files are not touched.</summary>
    public static void Remove(Game game)
    {
        lock (Gate)
        {
            if (!EntryIds.TryGetValue(game.AppId, out var id)) return;
            Save(Load().Where(entry => entry.Id != id).ToList());
            try
            {
                File.Delete(CoverPath(id));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A cover left behind is harmless.
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Starts the game's program, in its own folder, and adds the time it stays open to the play time.</summary>
    public static bool Play(Game game)
    {
        ManualEntry? entry;
        lock (Gate) entry = Load().FirstOrDefault(e => EntryIds.TryGetValue(game.AppId, out var id) && id == e.Id);
        if (entry is null) return false;

        var start = new ProcessStartInfo(entry.Exe)
        {
            UseShellExecute = true, // runs shortcuts and scripts too
            WorkingDirectory = Path.GetDirectoryName(entry.Exe) ?? "",
            Arguments = entry.Arguments ?? "",
        };
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }

        var startedAt = DateTime.Now;
        game.LastPlayed = startedAt;
        Update(entry.Id, e => e with { LastPlayed = startedAt });
        if (process is not null)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                Update(entry.Id, e => e with { Minutes = e.Minutes + (DateTime.Now - startedAt).TotalMinutes });
                process.Dispose();
            };
        }
        return true;
    }

    static void Update(string entryId, Func<ManualEntry, ManualEntry> change)
    {
        lock (Gate) Save(Load().Select(entry => entry.Id == entryId ? change(entry) : entry).ToList());
    }

    static List<ManualEntry> Load()
    {
        try
        {
            if (!File.Exists(StoragePath)) return new List<ManualEntry>();
            return JsonSerializer.Deserialize<List<ManualEntry>>(File.ReadAllText(StoragePath)) ?? new List<ManualEntry>();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new List<ManualEntry>();
        }
    }

    static void Save(List<ManualEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        File.WriteAllText(StoragePath, JsonSerializer.Serialize(entries, Indented));
    }
}
