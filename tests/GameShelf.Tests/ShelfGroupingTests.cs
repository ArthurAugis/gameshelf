using GameShelf.Models;
using GameShelf.Services;
using Xunit;

namespace GameShelf.Tests;

public sealed class ShelfGroupingTests
{
    static Game Make(string name, bool installed = false, int? year = null, params string[] genres) => new(1, name)
    {
        Installed = installed,
        ReleaseDate = year is null ? null : new DateTime(year.Value, 6, 1),
        Genres = genres,
    };

    static List<ShelfGroup<Game>> Group(GroupMode mode, params Game[] games) =>
        ShelfGrouping.Group(mode, games.ToList(), g => g, keepEmptyCollections: false);

    [Fact]
    public void None_GivesOneUntitledGroup()
    {
        var groups = Group(GroupMode.None, Make("A"), Make("B"));

        var group = Assert.Single(groups);
        Assert.Equal("", group.Title);
        Assert.Equal(2, group.Items.Count);
    }

    [Fact]
    public void Status_ListsInstalledFirst()
    {
        var groups = Group(GroupMode.Status, Make("A"), Make("B", installed: true));

        Assert.Equal(new[] { "Installed", "Not installed" }, groups.Select(g => g.Title));
    }

    [Fact]
    public void Year_NewestFirstAndUnknownLast()
    {
        var groups = Group(GroupMode.Year, Make("A", year: 2015), Make("B"), Make("C", year: 2023), Make("D", year: 2015));

        Assert.Equal(new[] { "2023", "2015", "Unknown" }, groups.Select(g => g.Title));
        Assert.Equal(new[] { "A", "D" }, groups[1].Items.Select(g => g.Name)); // the order of the input is kept
    }

    [Fact]
    public void Genre_UsesTheFirstRealGenreAndPutsOtherLast()
    {
        var groups = Group(GroupMode.Genre,
            Make("A", genres: new[] { "Indie", "Platformer" }), // "Indie" says how it is sold, not what it is
            Make("B", genres: new[] { "Indie" }),                // nothing else known: Indie it is
            Make("C"),
            Make("D", genres: new[] { "Action" }));

        Assert.Equal(new[] { "Action", "Indie", "Platformer", "Other" }, groups.Select(g => g.Title));
    }
}
