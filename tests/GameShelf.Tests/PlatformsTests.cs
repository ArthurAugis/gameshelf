using GameShelf.Models;
using GameShelf.Services;
using GameShelf.Theming;
using Xunit;

namespace GameShelf.Tests;

public sealed class PlatformsTests
{
    [Fact]
    public void ATitleIsReducedToItsLettersAndDigits()
    {
        Assert.Equal("pokemongo", Platforms.Key("Pokémon™ GO!"));
        Assert.Equal(Platforms.Key("The Witcher 3: Wild Hunt"), Platforms.Key("the witcher 3 wild hunt"));
        Assert.Equal("", Platforms.Key("™ !"));
    }

    [Fact]
    public void TheSameTitleOnTwoLaunchersShowsBothOnEach()
    {
        var steam = new Game(100, "Celeste") { Launcher = Launcher.Steam };
        var epic = new Game(EpicIdOf("c"), "CELESTE") { Launcher = Launcher.Epic };
        var other = new Game(101, "Hades") { Launcher = Launcher.Steam };

        Platforms.Match(new[] { steam, epic, other });

        Assert.Equal(new[] { Launcher.Steam, Launcher.Epic }, steam.OwnedOn);
        Assert.Equal(new[] { Launcher.Steam, Launcher.Epic }, epic.OwnedOn);
        Assert.Equal(new[] { Launcher.Steam }, other.OwnedOn);
    }

    [Fact]
    public void ATitleOnTwoLaunchersIsOneGameOnTheShelf()
    {
        var steam = new Game(100, "Celeste") { Launcher = Launcher.Steam };
        var epic = new Game(EpicIdOf("c"), "Celeste") { Launcher = Launcher.Epic };

        Platforms.Match(new[] { steam, epic });

        Assert.False(steam.IsAlternate);
        Assert.True(epic.IsAlternate); // neither is installed: the first launcher is shown
        Assert.Equal(steam.AppId, epic.KeyId); // notes, collections and hiding are shared
        Assert.Equal(2, steam.Siblings.Count);
    }

    [Fact]
    public void TheInstalledCopyIsTheOneShown()
    {
        var steam = new Game(100, "Celeste") { Launcher = Launcher.Steam };
        var epic = new Game(EpicIdOf("c"), "Celeste") { Launcher = Launcher.Epic, Installed = true };

        Platforms.Match(new[] { steam, epic });

        Assert.True(steam.IsAlternate);
        Assert.False(epic.IsAlternate);
        Assert.Equal(epic.AppId, steam.KeyId);
    }

    [Fact]
    public void ALauncherListingTheTitleTwiceIsNotMerged()
    {
        var demo = new Game(100, "Doom") { Launcher = Launcher.Steam };
        var full = new Game(101, "DOOM") { Launcher = Launcher.Steam };
        var epic = new Game(EpicIdOf("d"), "Doom") { Launcher = Launcher.Epic };

        Platforms.Match(new[] { demo, full, epic });

        Assert.All(new[] { demo, full, epic }, game => Assert.False(game.IsAlternate));
        Assert.Equal(demo.AppId, demo.KeyId);
    }

    [Fact]
    public void AGameNeverMatchedStillShowsItsOwnLauncher()
    {
        Assert.Equal(new[] { Launcher.Epic }, new Game(1, "Alone") { Launcher = Launcher.Epic }.OwnedOn);
    }

    [Fact]
    public void TheLogosCanBeBuilt()
    {
        // WPF shapes need a single-threaded apartment; this fails if one of the outlines is not valid.
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.NotNull(PlatformLogos.Create(Launcher.Steam, 12));
                Assert.Equal(2, PlatformLogos.Row(new[] { Launcher.Steam, Launcher.Epic }, 12, 3).Children.Count);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
    }

    static uint EpicIdOf(string name) => GameShelf.Launchers.EpicLibrary.IdOf(name);
}
