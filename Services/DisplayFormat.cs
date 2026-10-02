namespace GameShelf.Services;

/// <summary>Human-readable sizes, speeds and durations.</summary>
internal static class DisplayFormat
{
    public static string Size(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.##} TB"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
        : $"{bytes / 1024.0:0} KB";

    public static string Speed(long bytesPerSecond) => $"{Size(bytesPerSecond)}/s";

    public static string TimeLeft(int seconds) =>
        seconds >= 3600 ? Loc.T("{0} h {1} min left", seconds / 3600, seconds % 3600 / 60)
        : seconds >= 60 ? Loc.T("{0} min left", seconds / 60)
        : Loc.T("{0} s left", seconds);

    /// <summary>The text cut to <paramref name="maxLength"/> characters with an ellipsis (some titles are whole sentences).</summary>
    public static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";

    public static string PlayTime(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{time.TotalHours:0.#} h" : $"{time.TotalMinutes:0} min";
}
