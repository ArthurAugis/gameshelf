using GameShelf.Models;
using GameShelf.Services;
using Xunit;

namespace GameShelf.Tests;

public sealed class ShelfFilterTests
{
    static Game Make(string name, uint id = 1, bool installed = false, params string[] genres) =>
        new(id, name) { Installed = installed, Genres = genres };

    [Theory]
    [InlineData("pokemon", true)]          // accents are ignored
    [InlineData("POKÉMON", true)]          // so is the case
    [InlineData("poke rpg", true)]         // every word may be in the name or in a genre
    [InlineData("poke strategy", false)]   // but every word must match something
    [InlineData("  ", true)]               // a blank search keeps everything
    public void Query_MatchesNameAndGenreWords(string query, bool expected)
    {
        var filter = new ShelfFilter { Query = query };

        Assert.Equal(expected, filter.Matches(Make("Pokémon Red", genres: "RPG")));
    }

    [Fact]
    public void Status_SeparatesInstalledFromNotInstalled()
    {
        var installed = Make("A", installed: true);
        var remote = Make("B");

        var onlyInstalled = new ShelfFilter { Status = StatusFilter.Installed };
        var onlyRemote = new ShelfFilter { Status = StatusFilter.NotInstalled };

        Assert.True(onlyInstalled.Matches(installed));
        Assert.False(onlyInstalled.Matches(remote));
        Assert.False(onlyRemote.Matches(installed));
        Assert.True(onlyRemote.Matches(remote));
    }

    [Fact]
    public void Play_ASingleLastPlayedDateCountsAsPlayed()
    {
        var neverPlayed = Make("A");
        var playedLongAgo = Make("B");
        playedLongAgo.LastPlayed = new DateTime(2020, 1, 1);
        var withTime = Make("C");
        withTime.PlayTime = TimeSpan.FromMinutes(5);

        var played = new ShelfFilter { Play = PlayFilter.Played };
        var never = new ShelfFilter { Play = PlayFilter.NeverPlayed };

        Assert.False(played.Matches(neverPlayed));
        Assert.True(played.Matches(playedLongAgo));
        Assert.True(played.Matches(withTime));
        Assert.True(never.Matches(neverPlayed));
        Assert.False(never.Matches(withTime));
    }

    [Fact]
    public void Genres_AnyOfThemMatches_FeaturesNeedAll()
    {
        var game = Make("A", genres: new[] { "Action", "Indie" });
        game.Features = new HashSet<string> { "Single-player", "Co-op" };

        var anyGenre = new ShelfFilter();
        anyGenre.Genres.UnionWith(new[] { "Strategy", "Indie" });
        var bothFeatures = new ShelfFilter();
        bothFeatures.Features.UnionWith(new[] { "Single-player", "Co-op" });
        var missingFeature = new ShelfFilter();
        missingFeature.Features.UnionWith(new[] { "Single-player", "Multiplayer" });

        Assert.True(anyGenre.Matches(game));
        Assert.True(bothFeatures.Matches(game));
        Assert.False(missingFeature.Matches(game));
    }

    [Fact]
    public void Launchers_KeepOnlyTheChosenOnes_AndPlayFiltersLeaveOutGamesWithoutPlayData()
    {
        var steam = Make("A");
        var epic = new Game(0x80000001, "B") { Launcher = Launcher.Epic };

        var onlyEpic = new ShelfFilter();
        onlyEpic.Launchers.Add("Epic Games");
        var neverPlayed = new ShelfFilter { Play = PlayFilter.NeverPlayed };

        Assert.False(onlyEpic.Matches(steam));
        Assert.True(onlyEpic.Matches(epic));
        Assert.True(neverPlayed.Matches(steam));
        Assert.False(neverPlayed.Matches(epic)); // play time is only known for Steam games
        Assert.Equal(1, onlyEpic.ActiveCount);
    }

    [Fact]
    public void ActiveCount_CountsChoicesButNotSearchSortOrGroup()
    {
        var filter = new ShelfFilter
        {
            Query = "celeste",
            Sort = SortMode.PlayTime,
            Group = GroupMode.Year,
            Status = StatusFilter.Installed,
            Play = PlayFilter.Played,
        };
        filter.Genres.Add("Indie");
        filter.Features.Add("Co-op");

        Assert.Equal(4, filter.ActiveCount);
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void Clear_ResetsFiltersAndKeepsSortAndGroup()
    {
        var filter = new ShelfFilter { Query = "x", Sort = SortMode.Rating, Group = GroupMode.Genre, Status = StatusFilter.Installed };
        filter.Genres.Add("Indie");

        filter.Clear();

        Assert.False(filter.IsActive);
        Assert.Equal(0, filter.ActiveCount);
        Assert.Equal(SortMode.Rating, filter.Sort);
        Assert.Equal(GroupMode.Genre, filter.Group);
    }

    [Fact]
    public void Order_ByName_IgnoresCase()
    {
        var games = new[] { Make("banana"), Make("Apple"), Make("cherry") };

        var ordered = new ShelfFilter().Order(games, g => g);

        Assert.Equal(new[] { "Apple", "banana", "cherry" }, ordered.Select(g => g.Name));
    }

    [Fact]
    public void Order_ByPlayTime_PutsTheLongestFirstThenByName()
    {
        var a = Make("A");
        var b = Make("B");
        var c = Make("C");
        a.PlayTime = TimeSpan.FromHours(1);
        b.PlayTime = TimeSpan.FromHours(5);
        c.PlayTime = TimeSpan.FromHours(1);

        var ordered = new ShelfFilter { Sort = SortMode.PlayTime }.Order(new[] { c, a, b }, g => g);

        Assert.Equal(new[] { "B", "A", "C" }, ordered.Select(g => g.Name));
    }

    [Fact]
    public void Order_ByRating_PutsGamesWithoutScoreLast()
    {
        var unrated = Make("A");
        var good = Make("B");
        good.Metacritic = 90;
        var poor = Make("C");
        poor.Metacritic = 40;

        var ordered = new ShelfFilter { Sort = SortMode.Rating }.Order(new[] { unrated, poor, good }, g => g);

        Assert.Equal(new[] { "B", "C", "A" }, ordered.Select(g => g.Name));
    }

    [Fact]
    public void Order_ByRecentlyPlayed_PutsNeverPlayedLast()
    {
        var never = Make("A");
        var old = Make("B");
        old.LastPlayed = new DateTime(2019, 5, 1);
        var recent = Make("C");
        recent.LastPlayed = new DateTime(2024, 5, 1);

        var ordered = new ShelfFilter { Sort = SortMode.RecentlyPlayed }.Order(new[] { never, old, recent }, g => g);

        Assert.Equal(new[] { "C", "B", "A" }, ordered.Select(g => g.Name));
    }
}
