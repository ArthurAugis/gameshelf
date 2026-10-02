using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameShelf.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum PlayStatus { None, Playing, Finished, Dropped }

/// <summary>The user's own status, rating (0 = none, up to 5 stars) and comment for one game.</summary>
internal sealed record GameNote(PlayStatus Status, int Rating, string Comment)
{
    public static readonly GameNote Empty = new(PlayStatus.None, 0, "");

    public bool IsEmpty => Status == PlayStatus.None && Rating == 0 && Comment.Length == 0;
}

/// <summary>Personal notes, kept in notes.json: app id to <see cref="GameNote"/>.</summary>
internal static class GameNotes
{
    const string FileName = "notes.json";
    public const int MaxRating = 5;

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly Dictionary<uint, GameNote> Notes = Load();

    public static GameNote Get(uint appId) => Notes.GetValueOrDefault(appId) ?? GameNote.Empty;

    /// <summary>Saves the note. An empty note removes the entry.</summary>
    public static void Set(uint appId, GameNote note)
    {
        if (note.IsEmpty) Notes.Remove(appId);
        else Notes[appId] = note;
        AppData.WriteText(FileName, JsonSerializer.Serialize(Notes, JsonOptions));
    }

    static Dictionary<uint, GameNote> Load()
    {
        try
        {
            if (AppData.ReadText(FileName) is { } json)
                return JsonSerializer.Deserialize<Dictionary<uint, GameNote>>(json) ?? new Dictionary<uint, GameNote>();
        }
        catch (JsonException)
        {
            // Unreadable file: start with no notes.
        }
        return new Dictionary<uint, GameNote>();
    }
}
