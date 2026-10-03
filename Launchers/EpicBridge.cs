using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameShelf.Models;
using Microsoft.Win32;
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

    static bool watchingQueue;
    static DateTime lastRevive = DateTime.MinValue;
    const int ShowMinimizedNoActivate = 7;

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
    public static async Task<bool> IsReachableAsync() => await FindStorePageAsync() is not null;

    /// <summary>
    /// The store page's web socket. When the launcher runs in debug mode but its window was closed to the notification
    /// area, its page is gone: the launcher is asked to show its window again (a second start only does that), then
    /// the window is minimized to the task bar, where the page stays alive. Tried at most every 30 seconds.
    /// </summary>
    static async Task<string?> FindStorePageAsync()
    {
        if (Process.GetProcessesByName("EpicGamesLauncher").Length == 0) return null; // not running: no need to wait for a closed port
        if (await Cdp.FindPageAsync(Port, IsStorePage) is { } socketUrl) return socketUrl;
        if (DateTime.UtcNow - lastRevive < TimeSpan.FromSeconds(30) || !await Cdp.IsListeningAsync(Port)) return null;
        lastRevive = DateTime.UtcNow;

        var exe = Process.GetProcessesByName("EpicGamesLauncher").Select(ExePathOf).FirstOrDefault(path => path is not null);
        if (exe is null) return null;
        Process.Start(new ProcessStartInfo(exe, $"-cefdebug={Port}") { UseShellExecute = false });
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(2000);
            // Still no window: ask Windows to open the launcher through its own link, as clicking a launcher link does.
            if (i == 2) Process.Start(new ProcessStartInfo(EpicLibrary.LibraryUri) { UseShellExecute = true });
            if (await Cdp.FindPageAsync(Port, IsStorePage) is { } revived)
            {
                MinimizeLauncher();
                return revived;
            }
        }
        return null;
    }

    /// <summary>Puts the launcher's window in the task bar without taking the focus.</summary>
    static void MinimizeLauncher()
    {
        foreach (var process in Process.GetProcessesByName("EpicGamesLauncher"))
            if (process.MainWindowHandle != IntPtr.Zero) ShowWindow(process.MainWindowHandle, ShowMinimizedNoActivate);
    }

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);

    /// <summary>Closes the launcher if it runs and starts it again with the debug port. False if it cannot be found.</summary>
    public static async Task<bool> RestartWithDebugPortAsync()
    {
        var running = Process.GetProcessesByName("EpicGamesLauncher");
        var exe = running.Select(ExePathOf).FirstOrDefault(path => path is not null)
            ?? InstalledLauncherExe();
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
        if (running.Length > 0) await Task.Delay(3000); // let it finish ending
        Process.Start(new ProcessStartInfo(exe, $"-cefdebug={Port}") { UseShellExecute = false });

        for (int i = 0; i < 60; i++) // up to two minutes for the launcher to start and load its store page
        {
            await Task.Delay(2000);
            if (await Cdp.FindPageAsync(Port, IsStorePage) is not null)
            {
                MinimizeLauncher();
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Closes the launcher (and with it the debug port) once its download queue has been empty on two looks in a row:
    /// GameShelf only needed it for the download, and it would otherwise stay in the task bar. Runs without any game
    /// page open, and one watcher at a time.
    /// </summary>
    static async Task CloseWhenIdleAsync()
    {
        if (watchingQueue) return;
        watchingQueue = true;
        try
        {
            int quietLooks = 0;
            while (Process.GetProcessesByName("EpicGamesLauncher").Length > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                quietLooks = await IsBusyAsync() ? 0 : quietLooks + 1;
                if (quietLooks == 2)
                {
                    StopLauncher();
                    return;
                }
            }
        }
        finally
        {
            watchingQueue = false;
        }
    }

    static void StopLauncher()
    {
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

    /// <summary>
    /// Uninstalls the game without the launcher: its confirmation box cannot be answered from outside, so the files and
    /// the manifest are removed here, and the launcher is closed so it forgets the game (unless it is downloading
    /// something else; it then shows the game until its next start). False if the files could not be removed.
    /// </summary>
    public static async Task<bool> UninstallAsync(EpicIdentity game)
    {
        if (!EpicLibrary.RemoveInstall(game.AppName)) return false;
        if (await Cdp.FindPageAsync(Port, IsStorePage) is not null && !await IsBusyAsync()) StopLauncher();
        return true;
    }

    static async Task<bool> IsBusyAsync()
    {
        if (await RunAsync("return JSON.stringify(await ue.productinfo.getqueuedapps());") is not { } json) return true; // cannot ask: do not end a download
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("products", out var products) && products.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return true; // unreadable answer: do not end a download
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
        await StayMinimizedAsync(CallAsync(game, "await ue.productinfo.installwithoptions(ns, id, app, false, false); return 'ok';"));

    /// <summary>Starts the update the launcher has for the game.</summary>
    public static async Task<bool> UpdateAsync(EpicIdentity game) =>
        await StayMinimizedAsync(CallAsync(game, $"await ue.productinfo.update(ns, id, app, '{DownloadManager}'); return 'ok';"));

    // Starting a download can bring the launcher's window up: it is put back in the task bar once the launcher has taken the order.
    static async Task<bool> StayMinimizedAsync(Task<string?> call)
    {
        bool accepted = await call == "ok";
        if (accepted)
        {
            await Task.Delay(1500);
            MinimizeLauncher();
            _ = CloseWhenIdleAsync();
        }
        return accepted;
    }

    public static async Task<bool> PauseAsync(EpicIdentity game) =>
        await CallAsync(game, $"await ue.productinfo.pauseinstallation(ns, id, app, '{DownloadManager}'); return 'ok';") == "ok";

    public static async Task<bool> ResumeAsync(EpicIdentity game) =>
        await StayMinimizedAsync(CallAsync(game, $"await ue.productinfo.resumeinstallation(ns, id, app, '{DownloadManager}'); return 'ok';"));

    /// <summary>
    /// Cancels the download and removes what was downloaded. The launcher only pauses it (its cancel asks a box that
    /// cannot be answered from outside), so the download is paused, the launcher is ended so nothing holds the files,
    /// and the game's folder and manifest are deleted. The launcher starts again, in debug mode, on the next order.
    /// False if the launcher did not take the pause.
    /// </summary>
    public static async Task<bool> CancelAsync(EpicIdentity game)
    {
        if (!await PauseAsync(game)) return false;
        await Task.Delay(1500);
        StopLauncher();
        await Task.Delay(1500);
        EpicLibrary.RemoveInstall(game.AppName);
        return true;
    }

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
        if (await FindStorePageAsync() is not { } socketUrl) return null;
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

    /// <summary>
    /// The launcher's program when it is not running: the one Windows starts for com.epicgames.launcher:// links (the
    /// launcher can be installed on any drive), else the default install folder.
    /// </summary>
    static string? InstalledLauncherExe()
    {
        // The command reads: "C:\...\EpicGamesLauncher.exe" %1
        if (Registry.ClassesRoot.OpenSubKey(@"com.epicgames.launcher\shell\open\command")?.GetValue(null) is string command)
        {
            var path = command.StartsWith('"') ? command.Split('"')[1] : command.Split(' ')[0];
            if (File.Exists(path)) return path;
        }
        return File.Exists(DefaultLauncherExe) ? DefaultLauncherExe : null;
    }

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
