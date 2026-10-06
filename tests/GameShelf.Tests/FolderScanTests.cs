using System.IO;
using GameShelf.Services;
using Xunit;

namespace GameShelf.Tests;

public sealed class FolderScanTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "GameShelfScanTests-" + Guid.NewGuid().ToString("N"));

    public FolderScanTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    string Touch(string relative, int bytes = 1)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Theory]
    [InlineData("Super Mario 64 (USA)", "Super Mario 64")]
    [InlineData("Zelda_-_Ocarina_of_Time [!] (Europe) (Rev 1)", "Zelda - Ocarina of Time")]
    [InlineData("(Demo)", "(Demo)")]
    public void TitleOf_DropsTheTags(string fileName, string expected) => Assert.Equal(expected, FolderScan.TitleOf(fileName));

    [Theory]
    [InlineData("PS2", "PlayStation 2")]
    [InlineData("Nintendo - Wii U", "Wii U")]
    [InlineData("roms_snes", "Super Nintendo")]
    [InlineData("GBA", "Game Boy Advance")]
    [InlineData("Backups", null)]
    public void ConsoleOfFolder_ReadsTheFolderName(string folder, string? expected) => Assert.Equal(expected, FolderScan.ConsoleOfFolder(folder));

    [Fact]
    public void Roms_BecomeOneGameEachForTheEmulator_WithTheConsoleGuessed()
    {
        var emulator = Touch(Path.Combine("tools", "mgba.exe"));
        Touch(Path.Combine("Roms", "Pokemon Emerald (USA).gba"));
        Touch(Path.Combine("Roms", "Mario Kart 64 (Europe).z64"));
        Touch(Path.Combine("Roms", "readme.txt"));

        var result = FolderScan.Scan(Path.Combine(root, "Roms"), emulator);

        Assert.Equal(new[] { "Mario Kart 64", "Pokemon Emerald" }, result.Games.Select(g => g.Name));
        Assert.Equal(new[] { "Nintendo 64", "Game Boy Advance" }, result.Games.Select(g => g.Console));
        Assert.All(result.Games, g => Assert.Equal(emulator, g.Exe));
        Assert.Contains("Pokemon Emerald (USA).gba", result.Games[1].Arguments);
    }

    [Fact]
    public void ADiscImageTakesTheConsoleOfItsFolder_AndOnlyTheCueSheetStartsIt()
    {
        var emulator = Touch(Path.Combine("DuckStation", "duckstation-qt.exe"));
        Touch(Path.Combine("Roms", "PS1", "Crash Bandicoot (USA).cue"));
        Touch(Path.Combine("Roms", "PS1", "Crash Bandicoot (USA).bin"), 10);

        var result = FolderScan.Scan(Path.Combine(root, "Roms"), emulator);

        var game = Assert.Single(result.Games);
        Assert.Equal("Crash Bandicoot", game.Name);
        Assert.Equal("PlayStation", game.Console);
        Assert.Contains("-batch", game.Arguments);
    }

    [Fact]
    public void DolphinTellsGameCubeFromWiiBySize()
    {
        var emulator = Touch(Path.Combine("Dolphin", "Dolphin.exe"));
        Touch(Path.Combine("Games", "Small Game.iso"), 1000);

        var result = FolderScan.Scan(Path.Combine(root, "Games"), emulator);

        Assert.Equal("GameCube", Assert.Single(result.Games).Console); // a small file stands for a GameCube disc
    }

    [Fact]
    public void PcGames_AreOnePerSubFolder_WithTheRightProgram()
    {
        Touch(Path.Combine("Games", "Hollow Quest", "HollowQuest.exe"), 5000);
        Touch(Path.Combine("Games", "Hollow Quest", "unins000.exe"), 9000);
        Touch(Path.Combine("Games", "Hollow Quest", "_CommonRedist", "vcredist_x64.exe"), 20000);
        Touch(Path.Combine("Games", "Space Thing", "launcher.exe"), 100);
        Touch(Path.Combine("Games", "Space Thing", "bin", "SpaceThing64.exe"), 8000);
        Touch(Path.Combine("Games", "Empty Folder", "notes.txt"));
        Touch(Path.Combine("Games", "Snes Rom.sfc"));

        var result = FolderScan.Scan(Path.Combine(root, "Games"), null);

        Assert.Equal(new[] { "Hollow Quest", "Space Thing" }, result.Games.Select(g => g.Name));
        Assert.EndsWith("HollowQuest.exe", result.Games[0].Exe);
        Assert.EndsWith("SpaceThing64.exe", result.Games[1].Exe);
        Assert.All(result.Games, g => Assert.Equal("PC", g.Console));
        Assert.Equal(1, result.RomsWithoutEmulator);
    }

    [Fact]
    public void AGameFolderChosenItself_IsOneGame()
    {
        Touch(Path.Combine("Lone Game", "LoneGame.exe"), 100);

        var result = FolderScan.Scan(Path.Combine(root, "Lone Game"), null);

        Assert.Equal("Lone Game", Assert.Single(result.Games).Name);
    }
}
