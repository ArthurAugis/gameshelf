using System.Text.Json;

namespace GameShelf.Services;

/// <summary>
/// The user's own named groups of games ("Favorites", "To finish"...). A game can be in several of them.
/// Persisted in collections.json: collection name to app ids, in the order the collections are shown.
/// </summary>
internal static class GameCollections
{
    const string FileName = "collections.json";
    const int MaxNameLength = 30;

    static readonly string[] DefaultNames = { "Favorites", "To finish", "Co-op" };
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly List<string> OrderedNames = new();
    static readonly Dictionary<string, HashSet<uint>> Members = new(StringComparer.OrdinalIgnoreCase);

    static GameCollections() => Load();

    /// <summary>Raised after any change to the collections or to what they contain.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<string> Names => OrderedNames;

    public static bool Contains(string name, uint appId) => Members.TryGetValue(name, out var ids) && ids.Contains(appId);

    public static int Count(string name) => Members.TryGetValue(name, out var ids) ? ids.Count : 0;

    /// <summary>The collections a game is in, in display order.</summary>
    public static IEnumerable<string> Of(uint appId) => OrderedNames.Where(name => Members[name].Contains(appId));

    /// <summary>Adds the game. False if it was already in the collection.</summary>
    public static bool Add(string name, uint appId)
    {
        if (!Members.TryGetValue(name, out var ids) || !ids.Add(appId)) return false;
        Save();
        return true;
    }

    /// <summary>Adds the game, or removes it if it was already in the collection.</summary>
    public static void Toggle(string name, uint appId)
    {
        if (!Members.TryGetValue(name, out var ids)) return;
        if (!ids.Remove(appId)) ids.Add(appId);
        Save();
    }

    /// <summary>Why this name cannot be used for a new collection, or null if it can.</summary>
    public static string? Validate(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return Loc.T("Enter a name.");
        if (name.Length > MaxNameLength) return Loc.T("Use at most {0} characters.", MaxNameLength);
        return Members.ContainsKey(name) ? Loc.T("A collection with this name already exists.") : null;
    }

    public static bool Create(string name)
    {
        name = name.Trim();
        if (Validate(name) is not null) return false;
        OrderedNames.Add(name);
        Members[name] = new HashSet<uint>();
        Save();
        return true;
    }

    /// <summary>Removes the collection. The games stay in the library.</summary>
    public static void Delete(string name)
    {
        if (!Members.Remove(name)) return;
        OrderedNames.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    static void Save()
    {
        var data = OrderedNames.ToDictionary(name => name, name => Members[name].Order().ToList());
        AppData.WriteText(FileName, JsonSerializer.Serialize(data, JsonOptions));
        Changed?.Invoke();
    }

    static void Load()
    {
        Dictionary<string, List<uint>>? saved = null;
        try
        {
            if (AppData.ReadText(FileName) is { } json) saved = JsonSerializer.Deserialize<Dictionary<string, List<uint>>>(json);
        }
        catch (JsonException)
        {
            // Unreadable file: start again with the default collections.
        }

        saved ??= DefaultNames.ToDictionary(name => name, _ => new List<uint>());
        foreach (var (name, ids) in saved)
        {
            OrderedNames.Add(name);
            Members[name] = ids.ToHashSet();
        }
    }
}
