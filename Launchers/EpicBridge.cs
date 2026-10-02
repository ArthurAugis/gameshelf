using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using GameShelf.Models;
using GameShelf.Services;

namespace GameShelf.Launchers;

/// <summary>Where a game stands in the Epic Games Launcher, as <c>ue.productinfo.getapp</c> reports it.</summary>
internal sealed record EpicAppState(
    bool Installed,
    bool PartiallyInstalled,
    bool Installing,
    bool WaitingInLine,
    bool HasUpdate,
    bool Running,
    double Progress,
    string InstallLocation,
    long SizeOnDisk,
    string StatusText,
    string Error);

/// <summary>
/// Drives the Epic Games Launcher from GameShelf to install, pause, resume, cancel and remove games, the way
/// <see cref="Steam.SteamInstaller"/> does for Steam. The launcher is built on Chromium: started with
/// <c>-cefdebug=PORT</c> it opens a debug port on localhost, and its store page exposes the bridge
/// (<c>ue.productinfo</c>) the launcher's own web interface uses. The launcher does the downloading, so updates,
/// cloud saves and starting the game keep working as usual. These are the launcher's internals, not a public API: an
/// update of the launcher can change them.
/// </summary>
internal static class EpicBridge
{
    const string PortFileName = "epic-port.txt";
    const string DefaultLauncherExe = @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";

    // The source the launcher's web interface passes when it controls a download (its "download manager" page).
    const string DownloadManager = "downloadManager";

    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    // True once GameShelf has started the launcher itself: only then does it close it again (see CloseWhenIdleAsync).
    static bool startedByGameShelf;

    /// <summary>
    /// Debug port used when GameShelf starts the launcher: random, picked once per install. Obscurity only: any
    /// local process can still scan ports.
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

    /// <summary>True when the launcher runs with its debug port open and its store page is loaded.</summary>
    public static async Task<bool> IsReachableAsync() => await Cdp.FindPageAsync(Port, IsStorePage) is not null;

    /// <summary>Closes the launcher if it runs and starts it again with the debug port. False if it cannot be found.</summary>
    public static async Task<bool> RestartWithDebugPortAsync()
    {
        var running = Process.GetProcessesByName("EpicGamesLauncher");
        var exe = running.Select(ExePathOf).FirstOrDefault(path => path is not null)
            ?? (File.Exists(DefaultLauncherExe) ? DefaultLauncherExe : null);
        if (exe is null) return false;

        // The launcher keeps no unsaved work, and it has no "quit" command line: end it and its web helpers.
        foreach (var process in running.Concat(Process.GetProcessesByName("EpicWebHelper")))
        {
            try
            {
                process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to end.
            }
        }
        await Task.Delay(3000);
        Process.Start(new ProcessStartInfo(exe, $"-cefdebug={Port}") { UseShellExecute = false });

        for (int i = 0; i < 40; i++) // up to two minutes for the launcher to start and load its store page
        {
            await Task.Delay(3000);
            if (await IsReachableAsync())
            {
                startedByGameShelf = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Closes the launcher (and with it the debug port) once it has nothing left to download, if GameShelf started it:
    /// it only had to run for the installation, and it would otherwise stay in the notification area.
    /// </summary>
    public static async Task CloseWhenIdleAsync()
    {
        if (!startedByGameShelf) return;
        await Task.Delay(TimeSpan.FromSeconds(10)); // the launcher is still finishing the install
        if (await RunAsync("return JSON.stringify(await ue.productinfo.getqueuedapps());") is not { } json) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("products", out var products) && products.GetArrayLength() > 0) return; // still busy
        }
        catch (JsonException)
        {
            return;
        }

        startedByGameShelf = false;
        foreach (var process in Process.GetProcessesByName("EpicGamesLauncher").Concat(Process.GetProcessesByName("EpicWebHelper")))
        {
            try
            {
                process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to end.
            }
        }
    }

    /// <summary>The game's state, or null when the launcher cannot be reached or does not know the game.</summary>
    public static async Task<EpicAppState?> GetStateAsync(EpicIdentity game) =>
        await CallAsync(game, "return JSON.stringify(await ue.productinfo.getapp(ns, id, app));") is { } json ? ParseState(json) : null;

    /// <summary>
    /// Starts the installation in the launcher's default install folder, without opening the launcher's window.
    /// Updates are left to the launcher's own setting.
    /// </summary>
    public static async Task<bool> InstallAsync(EpicIdentity game) =>
        await CallAsync(game, "await ue.productinfo.installwithoptions(ns, id, app, false, false); return 'ok';") == "ok";

    /// <summary>Starts the update the launcher has for the game.</summary>
    public static async Task<bool> UpdateAsync(EpicIdentity game) =>
        await CallAsync(game, $"await ue.productinfo.update(ns, id, app, '{DownloadManager}'); return 'ok';") == "ok";

    public static async Task<bool> PauseAsync(EpicIdentity game) =>
        await CallAsync(game, $"await ue.productinfo.pauseinstallation(ns, id, app, '{DownloadManager}'); return 'ok';") == "ok";

    public static async Task<bool> ResumeAsync(EpicIdentity game) =>
        await CallAsync(game, $"await ue.productinfo.resumeinstallation(ns, id, app, '{DownloadManager}'); return 'ok';") == "ok";

    public static async Task<bool> CancelAsync(EpicIdentity game) =>
        await CallAsync(game, $"await ue.productinfo.cancelinstallation(ns, id, app, '{DownloadManager}'); return 'ok';") == "ok";

    public static async Task<bool> UninstallAsync(EpicIdentity game) =>
        await CallAsync(game, $"await ue.productinfo.uninstall(ns, id, app, '{DownloadManager}'); return 'ok';") == "ok";

    /// <summary>The folder the launcher installs the game in, or null if it cannot be asked.</summary>
    public static async Task<string?> GetInstallLocationAsync(EpicIdentity game) =>
        await CallAsync(game, "return JSON.stringify(await ue.productinfo.getinstalllocation(ns, id, app));") is { } json
        && JsonSerializer.Deserialize<string>(json) is { Length: > 0 } location ? location : null;

    /// <summary>Runs <paramref name="body"/> in the launcher's store page with the game's three names as ns, id and app.</summary>
    static Task<string?> CallAsync(EpicIdentity game, string body) =>
        RunAsync($"const ns = {Literal(game.Namespace)}, id = {Literal(game.ItemId)}, app = {Literal(game.AppName)}; {body}");

    /// <summary>Runs <paramref name="body"/> in the launcher's store page. Null if the launcher cannot be reached or the script failed.</summary>
    static async Task<string?> RunAsync(string body)
    {
        if (await Cdp.FindPageAsync(Port, IsStorePage) is not { } socketUrl) return null;
        var script = $"(async()=>{{ try {{ {body} }} catch (e) {{ return 'ERR:' + (e && e.message); }} }})()";
        var answer = await Cdp.RunAsync(script, socketUrl, CallTimeout);
        return answer is null || answer.StartsWith("ERR:", StringComparison.Ordinal) ? null : answer;
    }

    static string Literal(string text) => JsonSerializer.Serialize(text);

    /// <summary>Reads the launcher's description of a game. Public to the tests.</summary>
    internal static EpicAppState? ParseState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new EpicAppState(
                Installed: Flag(root, "isinstalled"),
                PartiallyInstalled: Flag(root, "ispartiallyinstalled"),
                Installing: Flag(root, "isupdating"),
                WaitingInLine: Flag(root, "iswaitinginline"),
                HasUpdate: Flag(root, "hasupdate"),
                Running: Flag(root, "isrunning"),
                Progress: root.TryGetProperty("progress", out var progress) && progress.TryGetDouble(out var value) ? value : 0,
                InstallLocation: Text(root, "installlocation"),
                SizeOnDisk: root.TryGetProperty("installsizeondisk", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                StatusText: Text(root, "updatestatus"),
                Error: Text(root, "stateerror"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Some of the game is on the disk but nothing is downloading: cancelled, or stopped by a failure. It has to be finished or removed.</summary>
    public static bool IsStopped(EpicAppState state) =>
        state is { PartiallyInstalled: true, Installed: false, Installing: false, WaitingInLine: false };

    /// <summary>The state as the progress model the game page already shows for Steam.</summary>
    public static InstallProgress ToProgress(EpicAppState state, bool paused)
    {
        // The launcher reports a share of 1 or of 100 depending on the version: both are understood.
        double percent = Math.Clamp(state.Progress <= 1 ? state.Progress * 100 : state.Progress, 0, 100);
        // Partly downloaded and not paused: the download was stopped. That is a state of its own (see IsStopped), not progress.
        bool started = state.Installing || state.WaitingInLine || (state.PartiallyInstalled && paused);
        return new InstallProgress(
            Found: started || state.Installed,
            Active: state.Installing && !paused && !state.WaitingInLine,
            Paused: paused,
            Completed: state.Installed && !state.Installing,
            Error: state.Error,
            Percent: percent,
            BytesPerSecond: 0, // the launcher does not report a speed
            SecondsRemaining: 0,
            State: "Downloading");
    }

    static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    static bool IsStorePage(JsonElement page) =>
        page.TryGetProperty("type", out var type) && type.GetString() == "page"
        && page.TryGetProperty("url", out var url) && url.GetString()?.StartsWith("https://launcher.store.epicgames.com", StringComparison.Ordinal) == true;

    static string? ExePathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
