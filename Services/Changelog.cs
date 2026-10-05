using System.IO;
using System.Reflection;

namespace GameShelf.Services;

/// <summary>One release of the changelog: its version, date and the changes.</summary>
internal sealed record ChangelogEntry(string Version, string Date, IReadOnlyList<string> Changes);

/// <summary>Reads CHANGELOG.md (embedded in the app): a "## version - date" heading per release, then "- change" lines.</summary>
internal static class Changelog
{
    /// <summary>The releases, newest first as written in the file.</summary>
    public static IReadOnlyList<ChangelogEntry> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return Array.Empty<ChangelogEntry>();
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        var entries = new List<ChangelogEntry>();
        string? version = null, date = "";
        var changes = new List<string>();

        void Flush()
        {
            if (version is not null) entries.Add(new ChangelogEntry(version, date, changes.ToList()));
            changes.Clear();
        }

        foreach (var line in markdown.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                var heading = line[3..].Split(" - ", 2);
                version = heading[0].Trim();
                date = heading.Length > 1 ? heading[1].Trim() : "";
            }
            else if (version is not null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                changes.Add(line[2..].Trim());
            }
        }
        Flush();
        return entries;
    }
}
