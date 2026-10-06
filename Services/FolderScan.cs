using System.IO;
using System.Text.RegularExpressions;

namespace GameShelf.Services;

/// <summary>A game found in a folder: what to start (a program and its arguments), its name and its console.</summary>
internal sealed record FoundGame(string Name, string Exe, string? Arguments, string Console);

/// <param name="Games">One per game found.</param>
/// <param name="RomsWithoutEmulator">ROM files seen while looking for PC games: they need an emulator to start.</param>
internal sealed record ScanResult(IReadOnlyList<FoundGame> Games, int RomsWithoutEmulator);

/// <summary>
/// Looks through a folder for games to put on the shelf. With an emulator, every ROM or ISO it can run is a game
/// (the arguments are written for it, the console is guessed from the file, then from the folder names); without
/// one, every sub-folder is a PC game and its program is guessed, and shortcuts are games too.
/// </summary>
internal static partial class FolderScan
{
    const int MaxDepth = 4, MaxFiles = 5000;
    const string UnknownConsole = "Nintendo Switch / other"; // the "no particular console" choice of the Add a game list

    // Extensions that name one console, whatever the folder is called.
    static readonly Dictionary<string, string> ConsoleByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".nes"] = "Nintendo (NES)", [".sfc"] = "Super Nintendo", [".smc"] = "Super Nintendo",
        [".gb"] = "Game Boy", [".gbc"] = "Game Boy Color", [".gba"] = "Game Boy Advance",
        [".nds"] = "Nintendo DS", [".3ds"] = "Nintendo 3DS", [".cia"] = "Nintendo 3DS",
        [".n64"] = "Nintendo 64", [".z64"] = "Nintendo 64", [".v64"] = "Nintendo 64",
        [".md"] = "Mega Drive / Genesis", [".gen"] = "Mega Drive / Genesis", [".smd"] = "Mega Drive / Genesis",
        [".gcm"] = "GameCube", [".gcz"] = "GameCube", [".wbfs"] = "Wii", [".wad"] = "Wii",
        [".wua"] = "Wii U", [".wux"] = "Wii U", [".rpx"] = "Wii U", [".xci"] = UnknownConsole, [".nsp"] = UnknownConsole,
    };

    // Disc images and the like: several consoles use them, so the folder names (or the emulator) say which.
    static readonly HashSet<string> SharedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".iso", ".chd", ".cue", ".bin", ".rvz", ".ciso", ".img", ".pbp", ".cso" };

    // Words of a folder name that tell the console, the most specific first ("wii u" before "wii").
    static readonly (string Alias, string Console)[] ConsoleByFolder =
    {
        ("wii u", "Wii U"), ("wiiu", "Wii U"), ("wii", "Wii"),
        ("gamecube", "GameCube"), ("game cube", "GameCube"), ("ngc", "GameCube"), ("gc", "GameCube"),
        ("nintendo 64", "Nintendo 64"), ("n64", "Nintendo 64"),
        ("super nintendo", "Super Nintendo"), ("super famicom", "Super Nintendo"), ("snes", "Super Nintendo"), ("sfc", "Super Nintendo"),
        ("nes", "Nintendo (NES)"), ("famicom", "Nintendo (NES)"),
        ("game boy advance", "Game Boy Advance"), ("gba", "Game Boy Advance"),
        ("game boy color", "Game Boy Color"), ("gbc", "Game Boy Color"),
        ("game boy", "Game Boy"), ("gameboy", "Game Boy"), ("gb", "Game Boy"),
        ("3ds", "Nintendo 3DS"), ("nintendo ds", "Nintendo DS"), ("nds", "Nintendo DS"), ("ds", "Nintendo DS"),
        ("playstation 3", "PlayStation 3"), ("ps3", "PlayStation 3"),
        ("playstation 2", "PlayStation 2"), ("ps2", "PlayStation 2"),
        ("playstation portable", "PSP"), ("psp", "PSP"),
        ("ps vita", "PS Vita"), ("vita", "PS Vita"),
        ("playstation", "PlayStation"), ("psx", "PlayStation"), ("psone", "PlayStation"), ("ps1", "PlayStation"),
        ("mega drive", "Mega Drive / Genesis"), ("megadrive", "Mega Drive / Genesis"), ("genesis", "Mega Drive / Genesis"),
        ("saturn", "Sega Saturn"), ("dreamcast", "Dreamcast"), ("dc", "Dreamcast"),
        ("xbox 360", "Xbox 360"), ("xbox360", "Xbox 360"), ("xbox", "Xbox"),
    };

    // Programs in a game's folder that are not the game.
    static readonly string[] NotTheGame =
        { "unins", "setup", "install", "redist", "dxsetup", "crash", "update", "helper", "dotnet", "directx", "oalinst", "config" };

    static readonly string[] NotTheGameFolders = { "redist", "_commonredist", "commonredist", "directx", "__installer", "support" };

    public static ScanResult Scan(string folder, string? emulatorExe)
    {
        if (!Directory.Exists(folder)) return new ScanResult(Array.Empty<FoundGame>(), 0);
        return emulatorExe is null ? ScanPcGames(folder) : ScanRoms(folder, emulatorExe);
    }

    /// <summary>The title of a game from its file name: no "(USA)" or "[!]" tags, underscores as spaces.</summary>
    internal static string TitleOf(string fileNameWithoutExtension)
    {
        var title = Spaces().Replace(Tags().Replace(fileNameWithoutExtension, "").Replace('_', ' '), " ").Trim();
        return title.Length > 0 ? title : fileNameWithoutExtension;
    }

    /// <summary>The console a folder name points to ("PS2", "Nintendo - Wii U", "roms_snes"), or null.</summary>
    internal static string? ConsoleOfFolder(string folderName)
    {
        var padded = " " + string.Join(' ', Words().Matches(folderName.ToLowerInvariant()).Select(m => m.Value)) + " ";
        return ConsoleByFolder.FirstOrDefault(x => padded.Contains(" " + x.Alias + " ", StringComparison.Ordinal)).Console;
    }

    static ScanResult ScanRoms(string folder, string emulatorExe)
    {
        var preset = Emulators.Detect(emulatorExe);
        var patterns = preset?.Extensions.Split(';') ?? Array.Empty<string>();
        var games = new List<FoundGame>();
        var discsWithCue = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in FilesUnder(folder))
        {
            var extension = Path.GetExtension(file);
            bool wanted = preset is not null
                ? patterns.Any(p => p.StartsWith("*.", StringComparison.Ordinal)
                    ? extension.Equals(p[1..], StringComparison.OrdinalIgnoreCase)
                    : Path.GetFileName(file).Equals(p, StringComparison.OrdinalIgnoreCase))
                : ConsoleByExtension.ContainsKey(extension) || SharedExtensions.Contains(extension);
            if (!wanted) continue;

            // A disc image is a .cue sheet and its .bin tracks: only the sheet starts the game.
            var directory = Path.GetDirectoryName(file) ?? folder;
            if (extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(file).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase))
            {
                if (!discsWithCue.TryGetValue(directory, out var hasCue))
                    discsWithCue[directory] = hasCue = Directory.EnumerateFiles(directory, "*.cue").Any();
                if (hasCue) continue;
            }

            games.Add(new FoundGame(TitleOf(NameSource(file, folder)), emulatorExe, preset?.ArgumentsFor(file) ?? $"\"{file}\"",
                ConsoleOf(file, folder, preset)));
        }
        return new ScanResult(games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), 0);
    }

    static ScanResult ScanPcGames(string folder)
    {
        var games = new List<FoundGame>();

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(file);
            if (extension is not (".lnk" or ".url" or ".bat") || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;

            // A shortcut is read for what it starts; one to something that has no file path is started as it is.
            var target = extension == ".lnk" ? ShortcutFile.Resolve(file) : null;
            games.Add(target is not null && File.Exists(target.Target)
                ? new FoundGame(name, target.Target, target.Arguments.Length > 0 ? target.Arguments : null, "PC")
                : new FoundGame(name, file, null, "PC"));
        }

        foreach (var directory in Directory.EnumerateDirectories(folder))
            if (ProgramOf(directory) is { } exe) games.Add(new FoundGame(Path.GetFileName(directory), exe, null, "PC"));

        // The chosen folder may be a single game's own folder.
        if (games.Count == 0 && ProgramOf(folder) is { } own) games.Add(new FoundGame(Path.GetFileName(folder.TrimEnd('\\', '/')), own, null, "PC"));

        int roms = FilesUnder(folder).Count(f => ConsoleByExtension.ContainsKey(Path.GetExtension(f)));
        return new ScanResult(games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), roms);
    }

    /// <summary>
    /// The program of a game folder: the one named like the folder, else the biggest. Installers, redistributables,
    /// crash reporters and the like are left out. Null when there is none.
    /// </summary>
    static string? ProgramOf(string directory)
    {
        var candidates = new List<(string Path, long Size)>();
        try
        {
            foreach (var exe in Directory.EnumerateFiles(directory, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }))
            {
                var relative = Path.GetRelativePath(directory, exe).ToLowerInvariant();
                var fileName = Path.GetFileName(relative);
                var folders = relative.Split('\\', '/')[..^1];
                if (NotTheGame.Any(n => fileName.Contains(n, StringComparison.Ordinal)) || folders.Any(f => NotTheGameFolders.Contains(f))) continue;
                candidates.Add((exe, new FileInfo(exe).Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (candidates.Count == 0) return null;

        var key = Platforms.Key(Path.GetFileName(directory.TrimEnd('\\', '/')));
        var named = candidates.Where(c => key.Length >= 3 && Platforms.Key(Path.GetFileNameWithoutExtension(c.Path)) is { Length: >= 3 } n
            && (n.Contains(key, StringComparison.Ordinal) || key.Contains(n, StringComparison.Ordinal))).ToList();
        return (named.Count > 0 ? named : candidates).OrderByDescending(c => c.Size).First().Path;
    }

    static string ConsoleOf(string file, string root, EmulatorPreset? preset)
    {
        if (preset?.Console is { } fixedConsole) return fixedConsole;
        var extension = Path.GetExtension(file);
        if (ConsoleByExtension.TryGetValue(extension, out var byExtension)) return byExtension;

        // The folders from the game's own up to the chosen one: "Roms\PS2\Some Game" says PlayStation 2.
        for (var directory = Path.GetDirectoryName(file); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (ConsoleOfFolder(Path.GetFileName(directory)) is { } byFolder) return byFolder;
            if (string.Equals(directory.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) break;
        }

        // Dolphin runs GameCube and Wii discs: a GameCube disc is at most 1.4 GB, a Wii one is bigger.
        if (preset?.Name == "Dolphin") return extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length < 1_500_000_000 ? "GameCube" : "Wii";
        return UnknownConsole;
    }

    /// <summary>The file name a game is called after: a PS3 game is its EBOOT.BIN, deep in the game's own folder.</summary>
    static string NameSource(string file, string root)
    {
        if (!Path.GetFileName(file).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(file);
        for (var directory = Path.GetDirectoryName(file); directory is not null && directory.Length > root.TrimEnd('\\', '/').Length; directory = Path.GetDirectoryName(directory))
            if (Path.GetFileName(directory).ToUpperInvariant() is not ("USRDIR" or "PS3_GAME")) return Path.GetFileName(directory);
        return "EBOOT";
    }

    static IEnumerable<string> FilesUnder(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = MaxDepth, IgnoreInaccessible = true })
                .Take(MaxFiles).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
