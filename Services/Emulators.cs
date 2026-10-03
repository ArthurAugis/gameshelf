using System.IO;

namespace GameShelf.Services;

/// <summary>
/// How to start a game in an emulator from the command line. <see cref="Template"/> has {0} where the game file goes
/// (quoted). <see cref="Console"/> is the console of the "Add a game" list that the emulator is for, null when it
/// runs several. The command lines come from each emulator's own documentation.
/// </summary>
internal sealed record EmulatorPreset(string Name, string FileNameContains, string? Console, string Template, string Extensions)
{
    /// <summary>The arguments that start <paramref name="gameFile"/>.</summary>
    public string ArgumentsFor(string gameFile) => Template.Replace("{0}", "\"" + gameFile + "\"", StringComparison.Ordinal);
}

internal static class Emulators
{
    public static readonly IReadOnlyList<EmulatorPreset> All = new EmulatorPreset[]
    {
        // -b: close with the game; -e: the game to run
        new("Dolphin", "dolphin", null, "-b -e {0}", "*.iso;*.gcm;*.gcz;*.rvz;*.wbfs;*.ciso;*.wad;*.dol;*.elf"),
        // -- ends the options, so a file name that starts with a dash is not taken for one
        new("PCSX2", "pcsx2", "PlayStation 2", "-batch -fullscreen -- {0}", "*.iso;*.chd;*.bin;*.cso;*.gz;*.elf"),
        new("DuckStation", "duckstation", "PlayStation", "-batch -fullscreen {0}", "*.cue;*.chd;*.bin;*.iso;*.img;*.pbp;*.ecm"),
        // a PS3 game is its EBOOT.BIN (in the game's folder), or an ISO
        new("RPCS3", "rpcs3", "PlayStation 3", "--no-gui {0}", "EBOOT.BIN;*.iso"),
        new("PPSSPP", "ppsspp", "PSP", "--fullscreen {0}", "*.iso;*.cso;*.pbp;*.elf"),
        // -g: the game; -f: full screen
        new("Cemu", "cemu", "Wii U", "-g {0} -f", "*.wua;*.wud;*.wux;*.rpx;*.wuhb"),
    };

    /// <summary>The emulator a program is, from its file name (Dolphin.exe, pcsx2-qt.exe...), or null.</summary>
    public static EmulatorPreset? Detect(string programPath)
    {
        var name = Path.GetFileNameWithoutExtension(programPath);
        return All.FirstOrDefault(preset => name.Contains(preset.FileNameContains, StringComparison.OrdinalIgnoreCase));
    }
}
