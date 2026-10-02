using System.IO;

namespace GameShelf.Services;

/// <summary>Per-user storage for settings and caches, under %LOCALAPPDATA%\GameShelf.</summary>
internal static class AppData
{
    static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameShelf");

    /// <summary>Full path of a file or folder inside the app data folder.</summary>
    public static string PathOf(string name) => Path.Combine(Root, name);

    public static bool Exists(string name) => File.Exists(PathOf(name));

    /// <summary>File content, or null if the file does not exist.</summary>
    public static string? ReadText(string name) => Exists(name) ? File.ReadAllText(PathOf(name)) : null;

    /// <summary>Writes the file, creating any missing folder (the name may include sub-folders).</summary>
    public static void WriteText(string name, string text)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
