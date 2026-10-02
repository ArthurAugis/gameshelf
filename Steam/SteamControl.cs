using System.Diagnostics;
using System.Globalization;
using System.IO;
using GameShelf.Services;

namespace GameShelf.Steam;

/// <summary>Talks to the Steam client: debug mode switch, restart, and the steam:// actions.</summary>
internal static class SteamControl
{
    const string PortFileName = "port.txt";
    const string NeverAskFileName = "no-prompt.txt";

    static string DebugFlagFile => Path.Combine(SteamLibrary.SteamPath, ".cef-enable-remote-debugging");
    static string SteamExe => Path.Combine(SteamLibrary.SteamPath, "steam.exe");

    /// <summary>False once the user chose "Don't ask again" in the exact-library dialog.</summary>
    public static bool ShouldPrompt => !AppData.Exists(NeverAskFileName);

    public static void NeverAsk() => AppData.WriteText(NeverAskFileName, "");

    /// <summary>
    /// Debug port used when GameShelf starts Steam: random, picked once per install instead of Steam's
    /// well-known 8080.
    /// Obscurity only: any local process can still scan ports. Deleting the debug flag file turns
    /// the mode off for good.
    /// </summary>
    public static int Port
    {
        get
        {
            if (int.TryParse(AppData.ReadText(PortFileName), out var port) && port is > 1024 and < 65536) return port;
            port = Random.Shared.Next(20000, 60000);
            AppData.WriteText(PortFileName, port.ToString(CultureInfo.InvariantCulture));
            return port;
        }
    }

    public static bool IsRunning => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Creates the file that makes Steam open its debug port at the next start.</summary>
    public static bool TryEnableDebugMode(out string? error)
    {
        try
        {
            File.WriteAllBytes(DebugFlagFile, Array.Empty<byte>());
            error = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>Closes Steam gracefully if it runs, then starts it again so it reads the debug flag file.</summary>
    public static async Task RestartAsync()
    {
        if (IsRunning)
        {
            Process.Start(new ProcessStartInfo(SteamExe, "-shutdown") { UseShellExecute = false });
            for (int i = 0; i < 60 && IsRunning; i++) await Task.Delay(1000);
            await Task.Delay(2000);
        }
        Process.Start(new ProcessStartInfo(SteamExe, $"-devtools-port {Port}") { UseShellExecute = false });
    }

    public static void Play(uint appId) => OpenUri($"steam://rungameid/{appId}");

    public static void Install(uint appId) => OpenUri($"steam://install/{appId}");

    public static void Uninstall(uint appId) => OpenUri($"steam://uninstall/{appId}");

    static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
