namespace GameShelf.Services;

/// <summary>
/// Games the user chose to hide from the shelf (for example after a refund). Persisted, one app id per line.
/// </summary>
internal static class HiddenGames
{
    const string FileName = "hidden.txt";

    static readonly HashSet<uint> Ids = Load();

    public static bool Contains(uint appId) => Ids.Contains(appId);

    /// <summary>Hides the game, or shows it again if it was hidden.</summary>
    public static void Toggle(uint appId)
    {
        if (!Ids.Remove(appId)) Ids.Add(appId);
        AppData.WriteText(FileName, string.Join('\n', Ids));
    }

    static HashSet<uint> Load() =>
        (AppData.ReadText(FileName) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => uint.TryParse(line, out var id) ? id : 0)
            .Where(id => id != 0)
            .ToHashSet();
}
