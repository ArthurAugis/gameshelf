using GameShelf.Services;
using Xunit;

namespace GameShelf.Tests;

public class ChangelogTests
{
    [Fact]
    public void Parse_reads_versions_dates_and_changes()
    {
        var entries = Changelog.Parse("# Changelog\n\nintro\n\n## 1.1.0 - 2026-01-02\n\n- First\n- Second\n\n## 1.0.0\n- Only\n");

        Assert.Equal(2, entries.Count);
        Assert.Equal(("1.1.0", "2026-01-02"), (entries[0].Version, entries[0].Date));
        Assert.Equal(new[] { "First", "Second" }, entries[0].Changes);
        Assert.Equal(("1.0.0", ""), (entries[1].Version, entries[1].Date));
    }

    [Fact]
    public void The_embedded_changelog_lists_the_running_version_first()
    {
        var entries = Changelog.Load();

        Assert.NotEmpty(entries);
        Assert.Equal(UpdateChecker.CurrentVersion.ToString(3), entries[0].Version);
        Assert.All(entries, e => Assert.NotEmpty(e.Changes));
    }
}
