namespace GameShelf.Models;

/// <summary>Store information about a game, shown on its details window.</summary>
internal sealed record GameDetails(
    string? Description,
    IReadOnlyList<string> Genres,
    string? Developer,
    string? Publisher,
    string? ReleaseDate)
{
    /// <summary>The description cut at a word boundary to at most about <paramref name="maxLength"/> characters.</summary>
    public string? Summary(int maxLength)
    {
        if (string.IsNullOrWhiteSpace(Description)) return null;
        if (Description.Length <= maxLength) return Description;
        int cut = Description.LastIndexOf(' ', maxLength);
        return Description[..(cut > 0 ? cut : maxLength)] + "...";
    }
}

/// <summary>What Steam recorded locally about the user playing a game.</summary>
internal sealed record PlayStats(TimeSpan PlayTime, DateTime? LastPlayed);
