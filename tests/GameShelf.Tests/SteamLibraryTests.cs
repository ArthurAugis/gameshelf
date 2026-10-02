using System.IO;
using GameShelf.Steam;
using Xunit;

namespace GameShelf.Tests;

/// <summary>
/// Runs against a made-up Steam folder. <see cref="SteamLibrary.SteamPath"/> is global, so these tests share one
/// instance of this class and never run at the same time as another one that uses it.
/// </summary>
public sealed class SteamLibraryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "GameShelfTests-" + Guid.NewGuid().ToString("N"));
    readonly string secondLibrary;
    readonly string originalSteamPath = SteamLibrary.SteamPath;

    public SteamLibraryTests()
    {
        secondLibrary = Path.Combine(root, "SecondLibrary");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(secondLibrary, "steamapps"));
        SteamLibrary.SteamPath = root;

        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "0" { "path" "{{root.Replace("\\", "\\\\")}}" }
                "1" { "path" "{{secondLibrary.Replace("\\", "\\\\")}}" }
            }
            """);

        Manifest(Path.Combine(root, "steamapps"), 504230, "Celeste", stateFlags: 4, size: 1_000_000);
        Manifest(secondLibrary + @"\steamapps", 730, "Counter-Strike 2", stateFlags: 4, size: 5_000_000);
        Manifest(Path.Combine(root, "steamapps"), 999, "Half Downloaded", stateFlags: 1026, size: 10); // installing
    }

    public void Dispose()
    {
        SteamLibrary.SteamPath = originalSteamPath;
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Scan_WithTheClientLibrary_MarksOnlyFullyInstalledGamesAsInstalled()
    {
        var library = new Dictionary<uint, string>
        {
            [504230] = "Celeste",
            [730] = "Counter-Strike 2",
            [999] = "Half Downloaded",
            [42] = "Never Installed",
        };

        var games = SteamLibrary.Scan(library);

        Assert.Equal(4, games.Count);
        Assert.Equal(new[] { "Celeste", "Counter-Strike 2", "Half Downloaded", "Never Installed" }, games.Select(g => g.Name));
        var celeste = games.Single(g => g.AppId == 504230);
        Assert.True(celeste.Installed);
        Assert.Equal(1_000_000, celeste.SizeOnDisk);
        Assert.True(games.Single(g => g.AppId == 730).Installed); // found in the second library folder
        Assert.False(games.Single(g => g.AppId == 999).Installed);
        Assert.False(games.Single(g => g.AppId == 42).Installed);
    }

    [Fact]
    public void ReadInstalledGame_SearchesEveryLibraryFolder()
    {
        Assert.Equal("Counter-Strike 2", SteamLibrary.ReadInstalledGame(730)?.Name);
        Assert.Null(SteamLibrary.ReadInstalledGame(999)); // not fully installed
        Assert.Null(SteamLibrary.ReadInstalledGame(12345));
    }

    [Fact]
    public void ReadAllPlayStats_KeepsTheHighestValuesAcrossAccounts()
    {
        LocalConfig("1001", (504230, 120, 1_700_000_000), (730, 0, 0));
        LocalConfig("1002", (504230, 300, 1_600_000_000));

        var stats = SteamLibrary.ReadAllPlayStats();

        var celeste = stats[504230];
        Assert.Equal(TimeSpan.FromMinutes(300), celeste.PlayTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).LocalDateTime, celeste.LastPlayed);
        Assert.False(stats.ContainsKey(730)); // no play time and no date: never played
    }

    [Fact]
    public void ReadPlayStats_IsNullForAGameNeverPlayed()
    {
        LocalConfig("1001", (504230, 10, 1_700_000_000));

        Assert.NotNull(SteamLibrary.ReadPlayStats(504230));
        Assert.Null(SteamLibrary.ReadPlayStats(777));
    }

    [Fact]
    public void LocalArt_FindsArtInAHashedSubFolder()
    {
        var cache = Path.Combine(root, "appcache", "librarycache");
        Directory.CreateDirectory(Path.Combine(cache, "504230"));
        File.WriteAllText(Path.Combine(cache, "504230", SteamLibrary.CoverFile), "direct");
        Directory.CreateDirectory(Path.Combine(cache, "42", "0123456789abcdef0123456789abcdef01234567"));
        File.WriteAllText(Path.Combine(cache, "42", "0123456789abcdef0123456789abcdef01234567", SteamLibrary.CoverFile), "hashed");

        Assert.EndsWith(Path.Combine("504230", SteamLibrary.CoverFile), SteamLibrary.LocalArt(504230, SteamLibrary.CoverFile));
        Assert.Contains("0123456789abcdef", SteamLibrary.LocalArt(42, SteamLibrary.CoverFile));
        Assert.Null(SteamLibrary.LocalArt(42, SteamLibrary.HeroFile));

        // The newer layout names the portrait cover "library_capsule.jpg".
        Directory.CreateDirectory(Path.Combine(cache, "77", "fedcba9876543210fedcba9876543210fedcba98"));
        File.WriteAllText(Path.Combine(cache, "77", "fedcba9876543210fedcba9876543210fedcba98", SteamLibrary.CapsuleFile), "capsule");
        Assert.EndsWith(SteamLibrary.CapsuleFile, SteamLibrary.LocalArt(77, SteamLibrary.CoverFile));
        Assert.Null(SteamLibrary.LocalArt(999999, SteamLibrary.CoverFile));
    }

    [Fact]
    public void DeletePartialInstall_RemovesWhatACancelledDownloadLeft_ButNeverAnInstalledGame()
    {
        var steamApps = Path.Combine(root, "steamapps");
        // Game 999 ("Half Downloaded", state 1026) is a download that was cancelled; Celeste is installed.
        Directory.CreateDirectory(Path.Combine(steamApps, "downloading", "999"));
        File.WriteAllText(Path.Combine(steamApps, "downloading", "999", "chunk.bin"), "x");
        Directory.CreateDirectory(Path.Combine(steamApps, "common", "Half Downloaded"));
        Directory.CreateDirectory(Path.Combine(steamApps, "common", "Celeste"));
        File.WriteAllText(Path.Combine(steamApps, "common", "Celeste", "game.exe"), "x");

        Assert.True(SteamLibrary.DeletePartialInstall(999));

        Assert.False(File.Exists(Path.Combine(steamApps, "appmanifest_999.acf")));
        Assert.False(Directory.Exists(Path.Combine(steamApps, "downloading", "999")));
        Assert.False(Directory.Exists(Path.Combine(steamApps, "common", "Half Downloaded")));

        Assert.False(SteamLibrary.DeletePartialInstall(504230)); // fully installed: left alone
        Assert.True(File.Exists(Path.Combine(steamApps, "appmanifest_504230.acf")));
        Assert.True(File.Exists(Path.Combine(steamApps, "common", "Celeste", "game.exe")));
    }

    [Fact]
    public void ReadPendingUpdates_ListsInstalledGamesWhoseManifestSaysAnUpdateIsRequired()
    {
        var steamApps = Path.Combine(root, "steamapps");
        Manifest(steamApps, 3000, "Needs Update", stateFlags: 6, size: 10); // installed (4) + update required (2)

        var pending = SteamLibrary.ReadPendingUpdates();

        Assert.Equal(new uint[] { 3000 }, pending.Keys); // Celeste (4) is up to date, "Half Downloaded" (1026) is not installed
    }

    static void Manifest(string steamApps, uint appId, string name, int stateFlags, long size) =>
        File.WriteAllText(Path.Combine(steamApps, $"appmanifest_{appId}.acf"),
            $$"""
            "AppState"
            {
                "appid" "{{appId}}"
                "name" "{{name}}"
                "StateFlags" "{{stateFlags}}"
                "installdir" "{{name}}"
                "SizeOnDisk" "{{size}}"
            }
            """);

    void LocalConfig(string user, params (uint AppId, long Minutes, long LastPlayed)[] apps)
    {
        var folder = Path.Combine(root, "userdata", user, "config");
        Directory.CreateDirectory(folder);
        var entries = string.Concat(apps.Select(a =>
            $"\"{a.AppId}\" {{ \"Playtime\" \"{a.Minutes}\" \"LastPlayed\" \"{a.LastPlayed}\" }}\n"));
        File.WriteAllText(Path.Combine(folder, "localconfig.vdf"),
            "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" {\n" + entries + "} } } } }");
    }
}
