using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameShelf.Launchers;
using GameShelf.Models;
using GameShelf.Services;
using Xunit;

namespace GameShelf.Tests;

public sealed class ManualGameTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "GameShelfManualTests-" + Guid.NewGuid().ToString("N"));
    readonly string originalPath = ManualGames.StoragePath;

    public ManualGameTests()
    {
        Directory.CreateDirectory(root);
        ManualGames.StoragePath = Path.Combine(root, "manual-games.json");
    }

    public void Dispose()
    {
        ManualGames.StoragePath = originalPath;
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ParseIndex_ReadsTheNamesOfAnApacheListing()
    {
        const string html = """
            <a href="?C=N;O=D">Name</a> <a href="/Nintendo%20-%20Nintendo%2064/">Parent</a>
            <a href="Super%20Mario%2064%20(USA).png">x</a> <a href="Super%20Mario%2064%20(Japan).png">x</a> <a href="readme.txt">x</a>
            """;

        Assert.Equal(new[] { "Super Mario 64 (USA)", "Super Mario 64 (Japan)" }, CoverSearch.ParseIndex(html));
    }

    [Fact]
    public void Rank_PutsTheSameTitleFirst_AndTheUsaReleaseBeforeTheOthers()
    {
        var names = new[]
        {
            "Super Mario 64 DS (USA)", "Super Mario 64 (Japan)", "Mario Kart 64 (USA)", "Super Mario 64 (Europe)",
            "Super Mario 64 (USA)", "Zelda (USA)",
        };

        var ranked = CoverSearch.Rank(names, "super mario 64", 6);

        Assert.Equal(new[] { "Super Mario 64 (USA)", "Super Mario 64 (Europe)", "Super Mario 64 (Japan)", "Super Mario 64 DS (USA)" }, ranked.Take(4));
        Assert.DoesNotContain("Zelda (USA)", ranked);
        Assert.Contains("Mario Kart 64 (USA)", CoverSearch.Rank(names, "mario 64", 6)); // all the words, in any order
        Assert.Empty(CoverSearch.Rank(names, "  ", 6));
    }

    [Fact]
    public void AddedGames_AreKeptListedPlayableAndRemovable()
    {
        var exe = Path.Combine(root, "game.exe");
        File.WriteAllText(exe, "x");
        var cover = new byte[] { 1, 2, 3 };

        ManualGames.Add("Zebra Quest", exe, " -fullscreen ", "PC", cover);
        ManualGames.Add("Mario 64", @"C:\missing\dolphin.exe", null, "GameCube", cover);

        var games = ManualGames.Scan();
        Assert.Equal(new[] { "Mario 64", "Zebra Quest" }, games.Select(g => g.Name));
        var mario = games[0];
        Assert.Equal(Launcher.Manual, mario.Launcher);
        Assert.False(mario.Installed); // the program is gone
        Assert.Equal("GameCube", mario.Console);
        Assert.Equal("GameCube", mario.LabelOf(Launcher.Manual)); // the console is what the game is called on the shelf, not "Added by hand"
        Assert.True(ManualGames.IsManualId(mario.AppId));
        Assert.NotNull(mario.Cover);
        Assert.True(File.Exists(mario.Cover));
        var zebra = games[1];
        Assert.True(zebra.Installed);
        Assert.Null(zebra.Console); // "PC" is not a console

        ManualGames.Remove(mario);

        Assert.Equal(new[] { "Zebra Quest" }, ManualGames.Scan().Select(g => g.Name));
        Assert.False(File.Exists(mario.Cover));
    }

    [Fact]
    public void Edit_ChangesWhatWasEntered_KeepsTheCoverUnlessANewOneIsGiven_AndKeepsTheTime()
    {
        var exe = Path.Combine(root, "dolphin.exe");
        File.WriteAllText(exe, "x");
        ManualGames.Add("Mario", exe, null, "Wii", new byte[] { 1, 2, 3 });
        var game = Assert.Single(ManualGames.Scan());
        Assert.Equal("Wii", ManualGames.EntryOf(game)?.Console);

        ManualGames.Edit(game, "Super Mario 64", exe, " -e game.iso ", "Nintendo 64", null);

        var edited = Assert.Single(ManualGames.Scan());
        Assert.Equal("Super Mario 64", edited.Name);
        Assert.Equal("Nintendo 64", edited.Console);
        Assert.Equal("-e game.iso", ManualGames.EntryOf(edited)?.Arguments);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(edited.Cover!)); // no new cover: the old one stays

        ManualGames.Edit(edited, "Super Mario 64", exe, null, "Nintendo 64", new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Assert.Single(ManualGames.Scan()).Cover!));
    }
    [Theory]
    [InlineData(@"C:\Emus\Dolphin-x64\Dolphin.exe", "Dolphin", @"-b -e ""D:\Roms\Mario 64.z64""")]
    [InlineData(@"C:\Emus\pcsx2-qt.exe", "PCSX2", @"-batch -fullscreen -- ""D:\Roms\Mario 64.z64""")]
    [InlineData(@"C:\Emus\duckstation-qt-x64-ReleaseLTCG.exe", "DuckStation", @"-batch -fullscreen ""D:\Roms\Mario 64.z64""")]
    [InlineData(@"C:\Emus\RPCS3\rpcs3.exe", "RPCS3", @"--no-gui ""D:\Roms\Mario 64.z64""")]
    [InlineData(@"C:\Emus\PPSSPPWindows64.exe", "PPSSPP", @"--fullscreen ""D:\Roms\Mario 64.z64""")]
    [InlineData(@"C:\Emus\Cemu.exe", "Cemu", @"-g ""D:\Roms\Mario 64.z64"" -f")]
    public void Emulators_AreRecognisedByTheirProgramName_AndGetQuotedArguments(string program, string name, string arguments)
    {
        var preset = Emulators.Detect(program);

        Assert.Equal(name, preset?.Name);
        Assert.Equal(arguments, preset!.ArgumentsFor(@"D:\Roms\Mario 64.z64"));
        Assert.Null(Emulators.Detect(@"C:\Games\LabyModLauncher.exe"));
    }

    [Fact]
    public void ShortcutFile_ReadsTheProgramAndTheArgumentsOfAShortcut()
    {
        var path = Path.Combine(root, "game.lnk");
        var shell = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic link = ((dynamic)Activator.CreateInstance(shell)!).CreateShortcut(path);
        link.TargetPath = @"C:\Windows\notepad.exe";
        link.Arguments = "-hello world";
        link.Save();

        var target = ShortcutFile.Resolve(path);

        Assert.Equal(@"C:\Windows\notepad.exe", target?.Target);
        Assert.Equal("-hello world", target?.Arguments);
        Assert.Null(ShortcutFile.Resolve(Path.Combine(root, "missing.lnk")));
    }

    [Fact]
    public void EveryConsoleOfTheList_HasALogo_ExceptPc()
    {
        foreach (var console in CoverSearch.Consoles.Where(c => c.Label != "PC"))
            Assert.True(GameShelf.Theming.ConsoleLogos.For(console.Label) is not null, console.Label);
        Assert.Null(GameShelf.Theming.ConsoleLogos.For("PC"));
        Assert.Null(GameShelf.Theming.ConsoleLogos.For("Something typed by hand"));
    }

    [Fact]
    public void Covers_AreMadeAtThePortraitSize_FromAWidePictureAndFromNothing()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var wide = new RenderTargetBitmap(300, 100, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen()) context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 300, 100));
                wide.Render(visual);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(wide));
                using var stream = new MemoryStream();
                png.Save(stream);

                foreach (var cover in new[] { CoverImage.FromPicture(stream.ToArray()), CoverImage.Generate("A very long game name that wraps over several lines", null) })
                {
                    var image = CoverImage.Decode(cover);
                    Assert.Equal(CoverImage.Width, image.PixelWidth);
                    Assert.Equal(CoverImage.Height, image.PixelHeight);
                }
                Assert.Null(CoverImage.TryReadPicture(Path.Combine(root, "missing.png")));
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
}
