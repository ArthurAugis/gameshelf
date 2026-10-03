namespace GameShelf.Models;

/// <summary>Where a game comes from.</summary>
internal enum Launcher { Steam, Epic, Gog }

internal static class LauncherExtensions
{
    /// <summary>The launcher's name as shown to the user. Brand names are not translated.</summary>
    public static string DisplayName(this Launcher launcher) => launcher switch
    {
        Launcher.Epic => "Epic Games",
        Launcher.Gog => "GOG Galaxy",
        _ => "Steam",
    };
}

/// <summary>A game (or software, demo, playtest) shown on the shelf.</summary>
internal sealed record Game(uint AppId, string Name)
{
    /// <summary>
    /// <see cref="AppId"/> is Steam's own id for a Steam game; for a game of another launcher it is an id GameShelf
    /// makes up (see EpicLibrary), so that collections, notes and hiding work the same for every launcher.
    /// </summary>
    public Launcher Launcher { get; init; }

    /// <summary>Every launcher the user owns this game on (see Platforms.Match): this game's own launcher, and the others that list the same title.</summary>
    public IReadOnlyList<Launcher> Platforms { get; set; } = Array.Empty<Launcher>();

    uint? keyId;

    /// <summary>
    /// The id notes, collections and hiding are kept under. The Steam and Epic copies of one title share it, so the
    /// status you gave the game and its collections are the same whichever platform you open it from.
    /// </summary>
    public uint KeyId
    {
        get => keyId ?? AppId;
        set => keyId = value;
    }

    /// <summary>True for the copies of a title that the shelf shows through another launcher's entry (see Platforms.Match).</summary>
    public bool IsAlternate { get; set; }

    /// <summary>Every entry of the same title across launchers, this one included; empty when the title is on one launcher only.</summary>
    public IReadOnlyList<Game> Siblings { get; set; } = Array.Empty<Game>();

    /// <summary>The launchers to show a logo for: at least the game's own.</summary>
    public IReadOnlyList<Launcher> OwnedOn => Platforms.Count > 0 ? Platforms : new[] { Launcher };

    /// <summary>The address that starts a game of another launcher (Steam games are started through Steam).</summary>
    public string? LaunchUri { get; init; }

    /// <summary>The address that opens the install window of a game of another launcher.</summary>
    public string? InstallUri { get; init; }

    /// <summary>Fully installed. Changes when the user installs or uninstalls while GameShelf is open.</summary>
    public bool Installed { get; set; }

    public long SizeOnDisk { get; set; }

    public string? InstallDir { get; set; }

    /// <summary>Portrait cover (600x900) on disk. Filled in once resolved, null if none exists.</summary>
    public string? Cover { get; set; }

    /// <summary>Transparent title logo on disk. Filled in once resolved, null if none exists.</summary>
    public string? Logo { get; set; }

    // Used to search and filter the shelf (see SteamMetadata). Empty when Steam has no information.

    public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

    /// <summary>"Single-player", "Multiplayer", "Co-op", "Controller support".</summary>
    public IReadOnlyCollection<string> Features { get; set; } = Array.Empty<string>();

    public DateTime? ReleaseDate { get; set; }

    public int? Metacritic { get; set; }

    public TimeSpan PlayTime { get; set; }

    public DateTime? LastPlayed { get; set; }

    public bool HasBeenPlayed => PlayTime > TimeSpan.Zero || LastPlayed is not null;
}
