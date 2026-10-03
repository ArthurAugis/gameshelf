using System.IO;
using GameShelf.Launchers;
using GameShelf.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameShelf.Tests;

/// <summary>Runs against a made-up Galaxy database. <see cref="GogLibrary.DatabasePath"/> is global.</summary>
public sealed class GogLibraryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "GameShelfGogTests-" + Guid.NewGuid().ToString("N"));
    readonly string originalPath = GogLibrary.DatabasePath;

    public GogLibraryTests()
    {
        Directory.CreateDirectory(root);
        GogLibrary.DatabasePath = Path.Combine(root, "galaxy-2.0.db");
        Execute("""
            CREATE TABLE LibraryReleases (id INTEGER PRIMARY KEY, userId INTEGER, releaseKey TEXT);
            CREATE TABLE LicensedReleases (libraryId INTEGER, isOwned INTEGER);
            CREATE TABLE GamePieceTypes (id INTEGER PRIMARY KEY, type TEXT);
            CREATE TABLE GamePieces (releaseKey TEXT, gamePieceTypeId INTEGER, userId INTEGER, value TEXT, languageId INTEGER);
            CREATE TABLE InstalledBaseProducts (productId INTEGER, installationPath TEXT, buildId TEXT, branch TEXT);
            CREATE TABLE Builds (productId INTEGER, manifest TEXT, createdAt TEXT);
            CREATE TABLE ProductSettings (gameReleaseKey TEXT, selectedBuildId TEXT);
            CREATE TABLE GameTimes (userId INTEGER, releaseKey TEXT, minutesInGame INTEGER);
            CREATE TABLE LastPlayedDates (userId INTEGER, gameReleaseKey TEXT, lastPlayedDate TEXT);
            CREATE TABLE DiskSizes (gameReleaseKey TEXT, diskSize INTEGER, diskDrive TEXT);
            INSERT INTO GamePieceTypes VALUES (1, 'title'), (2, 'originalImages'), (3, 'meta'), (4, 'summary');
            """);
    }

    public void Dispose()
    {
        GogLibrary.DatabasePath = originalPath;
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Scan_ListsOwnedGogGamesOnly_WithTheirInstallStateTimeAndArtwork()
    {
        Library(1, "gog_100", owned: true);
        Library(2, "gog_200", owned: true);
        Library(3, "gog_300", owned: false); // shared with the account, not owned
        Library(4, "steam_5", owned: true);  // another platform linked in Galaxy
        Piece("gog_100", 1, """{"title":"Tiny Quest"}""");
        Piece("gog_100", 2, """{"verticalCover":"https://images.gog.com/abc_glx_vertical_cover.webp?namespace=gamesdb","background":"https://images.gog.com/bg.webp?namespace=gamesdb"}""");
        Piece("gog_100", 3, """{"developers":["Tiny Dev"],"genres":["RPG","Indie"],"releaseDate":1778457600}""");
        Piece("gog_100", 4, """{"summary":"A small quest."}""");
        Piece("gog_200", 1, """{"title":"Big Quest"}""");
        Piece("gog_300", 1, """{"title":"Not Mine"}""");
        Piece("steam_5", 1, """{"title":"Steam Copy"}""");
        Execute("""
            INSERT INTO InstalledBaseProducts (productId, installationPath) VALUES (100, 'C:\Games\Tiny Quest');
            INSERT INTO GameTimes VALUES (1, 'gog_100', 90);
            INSERT INTO LastPlayedDates VALUES (1, 'gog_100', '2026-09-01T10:00:00Z');
            INSERT INTO DiskSizes VALUES ('gog_100', 123456, 'C');
            """);

        var games = GogLibrary.Scan();

        Assert.Equal(new[] { "Big Quest", "Tiny Quest" }, games.Select(g => g.Name));
        var tiny = games[1];
        Assert.Equal(Launcher.Gog, tiny.Launcher);
        Assert.True(tiny.Installed);
        Assert.Equal(@"C:\Games\Tiny Quest", tiny.InstallDir);
        Assert.Equal(123456, tiny.SizeOnDisk);
        Assert.Equal(TimeSpan.FromMinutes(90), tiny.PlayTime);
        Assert.NotNull(tiny.LastPlayed);
        Assert.Equal(new[] { "RPG", "Indie" }, tiny.Genres);
        Assert.Equal("goggalaxy://openGameView/100", tiny.InstallUri);
        Assert.False(games[0].Installed);

        // WebP pictures are fetched as JPEG, which WPF can read.
        Assert.Equal("https://images.gog.com/abc_glx_vertical_cover.jpg?namespace=gamesdb", GogLibrary.ArtUrl(tiny.AppId, GogLibrary.CoverArt));
        Assert.Null(GogLibrary.ArtUrl(games[0].AppId, GogLibrary.CoverArt));
        var details = GogLibrary.DetailsOf(tiny.AppId);
        Assert.Equal("A small quest.", details?.Description);
        Assert.Equal("Tiny Dev", details?.Developer);
        Assert.True(GogLibrary.IsGogId(tiny.AppId));
        Assert.False(GogLibrary.IsGogId(12345));
    }

    [Fact]
    public void Scan_IsEmptyWithoutGalaxyOrWithAnUnknownDatabase()
    {
        File.WriteAllText(GogLibrary.DatabasePath, "not a database");
        Assert.Empty(GogLibrary.Scan());
        File.Delete(GogLibrary.DatabasePath);
        Assert.Empty(GogLibrary.Scan());
    }

    const string Manifest = """
        {"items":[
          {"build_id":"3","os":"windows","branch":null,"public":true,"date_published":"2026-05-08T13:58:14+0000"},
          {"build_id":"2","os":"windows","branch":null,"public":true,"date_published":"2026-04-29T12:36:24+0000"},
          {"build_id":"9","os":"osx","branch":null,"public":true,"date_published":"2026-06-01T00:00:00+0000"},
          {"build_id":"5","os":"windows","branch":"beta","public":true,"date_published":"2026-07-01T00:00:00+0000"},
          {"build_id":"8","os":"windows","branch":null,"public":false,"date_published":"2026-08-01T00:00:00+0000"}]}
        """;

    [Fact]
    public void IsUpdatePending_ComparesTheInstalledBuildWithTheNewestPublicOneOfItsBranch()
    {
        Assert.True(GogLibrary.IsUpdatePending("2", null, Manifest));   // build 3 is newer
        Assert.False(GogLibrary.IsUpdatePending("3", null, Manifest));  // the newest already (the Mac, beta and private builds do not count)
        Assert.False(GogLibrary.IsUpdatePending("77", null, Manifest)); // a build GOG does not list: never guess
        Assert.False(GogLibrary.IsUpdatePending("5", "beta", Manifest));
        Assert.False(GogLibrary.IsUpdatePending("2", null, "not json"));
    }

    [Fact]
    public void PendingUpdateProductIds_ListsInstalledGamesBehindTheirNewestBuild_UnlessPinned()
    {
        Execute($$"""
            INSERT INTO InstalledBaseProducts (productId, installationPath, buildId, branch) VALUES (100, 'C:\a', '2', NULL), (200, 'C:\b', '3', NULL), (300, 'C:\c', '2', NULL);
            INSERT INTO Builds VALUES (100, '{{Manifest}}', ''), (200, '{{Manifest}}', ''), (300, '{{Manifest}}', '');
            INSERT INTO ProductSettings VALUES ('gog_300', '2');
            """);

        Assert.Equal(new[] { "100" }, GogLibrary.PendingUpdateProductIds()); // 200 is up to date, 300 is pinned to its build
    }

    [Fact]
    public void IdOf_IsStableAndDiffersFromEpicIds()
    {
        Assert.Equal(GogLibrary.IdOf("100"), GogLibrary.IdOf("100"));
        Assert.NotEqual(GogLibrary.IdOf("100"), GogLibrary.IdOf("101"));
        Assert.NotEqual(GogLibrary.IdOf("100"), EpicLibrary.IdOf("100"));
    }

    void Library(int id, string releaseKey, bool owned) =>
        Execute($"INSERT INTO LibraryReleases VALUES ({id}, 1, '{releaseKey}'); INSERT INTO LicensedReleases VALUES ({id}, {(owned ? 1 : 0)});");

    void Piece(string releaseKey, int type, string json) =>
        Execute($"INSERT INTO GamePieces VALUES ('{releaseKey}', {type}, 1, '{json.Replace("'", "''")}', NULL);");

    void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={GogLibrary.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
