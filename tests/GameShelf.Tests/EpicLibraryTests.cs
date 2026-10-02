using System.IO;
using System.Text;
using GameShelf.Launchers;
using GameShelf.Models;
using Xunit;

namespace GameShelf.Tests;

/// <summary>Runs against a made-up Epic launcher data folder. <see cref="EpicLibrary.DataPath"/> is global.</summary>
public sealed class EpicLibraryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "GameShelfEpicTests-" + Guid.NewGuid().ToString("N"));
    readonly string originalDataPath = EpicLibrary.DataPath;

    public EpicLibraryTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Manifests"));
        Directory.CreateDirectory(Path.Combine(root, "Catalog"));
        EpicLibrary.DataPath = root;
    }

    public void Dispose()
    {
        EpicLibrary.DataPath = originalDataPath;
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Scan_ListsInstalledGamesOnly()
    {
        Manifest("a.item", """
            { "AppName": "Fortnite", "DisplayName": "Fortnite", "CatalogNamespace": "fn", "CatalogItemId": "item1",
              "InstallLocation": "D:\\Games\\Fortnite", "InstallSize": 123456, "AppCategories": ["public", "games", "applications"],
              "MainGameAppName": "Fortnite" }
            """);
        Manifest("dlc.item", """
            { "AppName": "FortniteSkin", "DisplayName": "A skin", "CatalogNamespace": "fn", "CatalogItemId": "item2",
              "AppCategories": ["public", "games", "addons"], "MainGameAppName": "Fortnite" }
            """);
        Manifest("partial.item", """
            { "AppName": "Half", "DisplayName": "Half installed", "CatalogNamespace": "x", "CatalogItemId": "item3",
              "bIsIncompleteInstall": true, "AppCategories": ["games"] }
            """);
        Manifest("engine.item", """
            { "AppName": "UE_5.4", "DisplayName": "Unreal Engine 5.4", "CatalogNamespace": "ue", "CatalogItemId": "item4",
              "AppCategories": ["public", "engines"] }
            """);
        File.WriteAllText(Path.Combine(root, "Manifests", "broken.item"), "not json");

        var game = Assert.Single(EpicLibrary.Scan());

        Assert.Equal("Fortnite", game.Name);
        Assert.Equal(Launcher.Epic, game.Launcher);
        Assert.True(game.Installed);
        Assert.Equal(123456, game.SizeOnDisk);
        Assert.Equal(@"D:\Games\Fortnite", game.InstallDir);
        Assert.Equal("com.epicgames.launcher://apps/fn%3Aitem1%3AFortnite?action=launch&silent=true", game.LaunchUri);
    }

    [Fact]
    public void Scan_WithoutTheLauncherIsEmpty()
    {
        Directory.Delete(Path.Combine(root, "Manifests"));

        Assert.Empty(EpicLibrary.Scan());
    }

    [Fact]
    public void Ids_AreStableAndNeverCollideWithSteamIds()
    {
        var id = EpicLibrary.IdOf("Fortnite");

        Assert.Equal(id, EpicLibrary.IdOf("Fortnite"));
        Assert.NotEqual(id, EpicLibrary.IdOf("Fortnite2"));
        Assert.True(EpicLibrary.IsEpicId(id));
        Assert.False(EpicLibrary.IsEpicId(504230)); // a Steam id
    }

    [Fact]
    public void Catalog_GivesArtworkAndDetails()
    {
        Manifest("a.item", """
            { "AppName": "Fortnite", "DisplayName": "Fortnite", "CatalogNamespace": "fn", "CatalogItemId": "item1",
              "AppCategories": ["games"] }
            """);
        var catalog = """
            [ { "id": "item1", "title": "Fortnite", "description": "Battle royale", "developer": "Epic Games",
                "keyImages": [ { "type": "DieselGameBoxTall", "url": "https://cdn.example/tall.jpg" },
                               { "type": "DieselGameBoxLogo", "url": "https://cdn.example/logo.png" } ] },
              { "id": "other", "title": "Not installed" } ]
            """;
        File.WriteAllText(Path.Combine(root, "Catalog", "catcache.bin"), Convert.ToBase64String(Encoding.UTF8.GetBytes(catalog)));

        var game = Assert.Single(EpicLibrary.Scan());

        Assert.Equal("https://cdn.example/tall.jpg", EpicLibrary.ArtUrl(game.AppId, EpicLibrary.BoxArt));
        Assert.Equal("https://cdn.example/logo.png", EpicLibrary.ArtUrl(game.AppId, EpicLibrary.LogoArt));
        Assert.Null(EpicLibrary.ArtUrl(game.AppId, EpicLibrary.WideArt));
        var details = EpicLibrary.DetailsOf(game.AppId);
        Assert.Equal("Battle royale", details?.Description);
        Assert.Equal("Epic Games", details?.Developer);
    }

    [Fact]
    public void AnUnreadableCatalogOnlyCostsTheArtwork()
    {
        Manifest("a.item", """{ "AppName": "G", "DisplayName": "G", "CatalogNamespace": "n", "CatalogItemId": "i", "AppCategories": ["games"] }""");
        File.WriteAllText(Path.Combine(root, "Catalog", "catcache.bin"), "!!! not base64 !!!");

        var game = Assert.Single(EpicLibrary.Scan());

        Assert.Null(EpicLibrary.ArtUrl(game.AppId, EpicLibrary.BoxArt));
    }

    [Fact]
    public void OwnedGames_AreListedInstalledOrNot_AndMergedWithTheInstalledOnes()
    {
        Manifest("a.item", """
            { "AppName": "Alan", "DisplayName": "Alan Wake", "CatalogNamespace": "n1", "CatalogItemId": "i1",
              "InstallLocation": "D:\\Alan", "InstallSize": 42, "AppCategories": ["games"] }
            """);
        Manifest("b.item", """
            { "AppName": "Offline", "DisplayName": "Installed, not in the list", "CatalogNamespace": "n3", "CatalogItemId": "i3",
              "AppCategories": ["games"] }
            """);
        var owned = new[]
        {
            new OwnedEpicGame("Alan", "n1", "i1", "Alan Wake", "Remedy", "Thriller", new Dictionary<string, string>()),
            new OwnedEpicGame("Celeste", "n2", "i2", "Celeste", null, null,
                new Dictionary<string, string> { [EpicLibrary.BoxArt] = "https://cdn.example/celeste.jpg" }),
        };

        var games = EpicLibrary.Scan(owned);

        Assert.Equal(new[] { "Alan Wake", "Celeste", "Installed, not in the list" }, games.Select(g => g.Name));
        var alan = games[0];
        Assert.True(alan.Installed);
        Assert.Equal(42, alan.SizeOnDisk);
        var celeste = games[1];
        Assert.False(celeste.Installed);
        Assert.Equal("com.epicgames.launcher://apps/n2%3Ai2%3ACeleste?action=install", celeste.InstallUri);
        Assert.Equal("https://cdn.example/celeste.jpg", EpicLibrary.ArtUrl(celeste.AppId, EpicLibrary.BoxArt));
        Assert.True(games[2].Installed);
    }

    [Fact]
    public void Client_ReadsTheLibraryPages()
    {
        var (records, next) = EpicClient.ParseLibraryPage("""
            { "records": [ { "appName": "Celeste", "namespace": "n2", "catalogItemId": "i2" },
                           { "appName": "", "namespace": "n3", "catalogItemId": "i3" } ],
              "responseMetadata": { "nextCursor": "abc" } }
            """);

        var record = Assert.Single(records);
        Assert.Equal(new EpicClient.LibraryRecord("n2", "i2", "Celeste"), record);
        Assert.Equal("abc", next);
        Assert.Null(EpicClient.ParseLibraryPage("""{ "records": [] }""").NextCursor);
        Assert.Throws<EpicException>(() => EpicClient.ParseLibraryPage("not json"));
    }

    [Fact]
    public void Client_KeepsTheGamesOfTheCatalogOnly()
    {
        var records = new[]
        {
            new EpicClient.LibraryRecord("n", "game", "Celeste"),
            new EpicClient.LibraryRecord("n", "dlc", "CelesteSkin"),
            new EpicClient.LibraryRecord("n", "tool", "Tool"),
            new EpicClient.LibraryRecord("n", "unknown", "Nothing"),
        };
        var json = """
            { "game": { "title": "Celeste", "developer": "", "description": "Climb.", "categories": [ { "path": "games" }, { "path": "applications" } ],
                        "keyImages": [ { "type": "DieselGameBoxTall", "url": "https://cdn.example/t.jpg" }, { "type": "Other", "url": "x" } ] },
              "dlc":  { "title": "Skin", "categories": [ { "path": "games" } ], "mainGameItem": { "id": "game" } },
              "tool": { "title": "Tool", "categories": [ { "path": "applications" } ] } }
            """;

        var game = Assert.Single(EpicClient.ParseCatalog(json, records));

        Assert.Equal("Celeste", game.Title);
        Assert.Null(game.Developer); // empty text is "none"
        Assert.Equal("https://cdn.example/t.jpg", Assert.Single(game.Images).Value);
        Assert.Empty(EpicClient.ParseCatalog("not json", records));
    }

    [Fact]
    public void Bridge_ReadsTheLaunchersDescriptionOfAGame()
    {
        var state = EpicBridge.ParseState("""
            { "isinstalled": false, "ispartiallyinstalled": true, "isupdating": true, "iswaitinginline": false,
              "progress": 0.42, "installlocation": "C:\\Epic\\Game", "installsizeondisk": 1234, "updatestatus": "Downloading", "stateerror": "" }
            """);

        Assert.NotNull(state);
        Assert.True(state.Installing);
        Assert.Equal(@"C:\Epic\Game", state.InstallLocation);
        var progress = EpicBridge.ToProgress(state, paused: false);
        Assert.True(progress.Found);
        Assert.True(progress.Active);
        Assert.False(progress.Completed);
        Assert.Equal(42, progress.Percent, precision: 3);
        Assert.False(EpicBridge.ToProgress(state, paused: true).Active);
        Assert.Null(EpicBridge.ParseState("not json"));
    }

    [Fact]
    public void Bridge_ADoneInstallIsCompleted_AndAnIdleGameIsNotFound()
    {
        var done = EpicBridge.ToProgress(EpicBridge.ParseState("""{ "isinstalled": true, "isupdating": false, "progress": 1 }""")!, paused: false);
        var idle = EpicBridge.ToProgress(EpicBridge.ParseState("""{ "isinstalled": false, "isreadytoinstall": true }""")!, paused: false);

        Assert.True(done.Completed);
        Assert.Equal(100, done.Percent);
        Assert.False(idle.Found);
    }

    [Fact]
    public void Bridge_ACancelledDownloadIsStopped_NotInProgress_UnlessPaused()
    {
        var state = EpicBridge.ParseState("""{ "isinstalled": false, "ispartiallyinstalled": true, "isupdating": false, "progress": 0.3 }""")!;

        Assert.True(EpicBridge.IsStopped(state));
        Assert.False(EpicBridge.ToProgress(state, paused: false).Found); // left partly downloaded: finish it or delete it
        Assert.True(EpicBridge.ToProgress(state, paused: true).Found); // paused: still a download in progress
        Assert.False(EpicBridge.IsStopped(EpicBridge.ParseState("""{ "ispartiallyinstalled": true, "isupdating": true }""")!));
    }

    [Fact]
    public void Client_TakesTheCodeFromTheBarePasteOrTheWholePage()
    {
        Assert.Equal("abc123", EpicClient.ExtractCode("  abc123 \n"));
        Assert.Equal("abc123", EpicClient.ExtractCode("\"abc123\""));
        Assert.Equal("abc123", EpicClient.ExtractCode("""{"redirectUrl":"https://x","authorizationCode":"abc123","sid":null}"""));
    }

    void Manifest(string name, string json) => File.WriteAllText(Path.Combine(root, "Manifests", name), json);
}
