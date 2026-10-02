namespace GameShelf.Models;

/// <summary>A Steam library folder a game can be installed to.</summary>
internal sealed record InstallFolder(int Index, string Path, string Label, string Drive, long FreeBytes, bool IsDefault);

/// <summary>What an installation needs, and where it can go.</summary>
internal sealed record InstallPlan(long RequiredBytes, IReadOnlyList<InstallFolder> Folders);

/// <summary>The user's choices in the install dialog.</summary>
internal sealed record InstallRequest(int FolderIndex, bool DesktopShortcut, bool StartMenuShortcut);

/// <summary>Where a game stands in Steam's download queue.</summary>
internal sealed record InstallProgress(
    bool Found,
    bool Active,
    bool Paused,
    bool Completed,
    string Error,
    double Percent,
    long BytesPerSecond,
    int SecondsRemaining,
    string State);
