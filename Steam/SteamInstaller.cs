using System.IO;
using System.Globalization;
using System.Text.Json;
using GameShelf.Models;

namespace GameShelf.Steam;

/// <summary>
/// Installs games through Steam's own install manager, without Steam's install window: GameShelf asks the
/// user for the folder and shortcuts, then replays what Steam's wizard does. Needs Steam's debug mode.
/// These are internal Steam functions, not a public API: a Steam update can change them.
/// </summary>
internal static class SteamInstaller
{
    // The wizard state after OpenInstallWizard. ContinueInstall only works from this state.
    const string PrepareScript = """
        (async()=>{
          const app=__APP__;
          await SteamClient.Installs.SetAppList([app]);
          let info;
          for(let i=0;i<40;i++){ // wait until Steam reports the size, not a fixed delay
            info=await SteamClient.Installs.GetInstallManagerInfo();
            if((info.rgApps||[]).some(a=>a.nAppID===app&&a.lDiskSpaceRequiredBytes>0)) break;
            await new Promise(r=>setTimeout(r,40));
          }
          const folders=await SteamClient.InstallFolder.GetInstallFolders();
          return JSON.stringify({
            required:((info.rgApps||[]).find(a=>a.nAppID===app)||{}).lDiskSpaceRequiredBytes||0,
            folders:folders.filter(f=>f.bIsMounted).map(f=>({
              index:f.nFolderIndex, path:f.strFolderPath, label:f.strUserLabel,
              drive:f.strDriveName, free:f.nFreeSpace, isDefault:f.bIsDefaultFolder
            }))
          });
        })()
        """;

    const string StartScript = """
        (async()=>{
          const I=SteamClient.Installs, app=__APP__;
          const sleep=ms=>new Promise(r=>setTimeout(r,ms));
          await I.OpenInstallWizard([app]);
          const WizardState=7;
          for(let i=0;i<30;i++){ if((await I.GetInstallManagerInfo()).eInstallState===WizardState) break; await sleep(100); }
          await I.SetInstallFolder(__FOLDER__);
          await I.SetCreateShortcuts(__DESKTOP__,__MENU__);
          await I.ContinueInstall();
          return 'ok';
        })()
        """;

    // Reads the download queue (the item of this app) and the overview (the download running right now).
    const string ProgressScript = """
        (async()=>{
          const D=SteamClient.Downloads, app=__APP__;
          const read=reg=>new Promise(res=>{
            let done=false, handle;
            const finish=v=>{ if(done) return; done=true; try{ handle&&handle.unregister&&handle.unregister(); }catch(e){} res(v); };
            handle=reg(finish); setTimeout(()=>finish(null),1500);
          });
          const items=await read(cb=>D.RegisterForDownloadItems((downloading,list)=>cb(list)));
          const overview=await read(cb=>D.RegisterForDownloadOverview(cb));
          const item=(((items||[])[0]||{}).item_data||[]).find(x=>x.appid===app);
          const current=!!overview&&overview.update_appid===app;
          return JSON.stringify({
            found:!!item, active:!!(item&&item.active), paused:!!(item&&item.paused), completed:!!(item&&item.completed),
            error:(item&&item.update_error)||'',
            percent:current?overview.overall_percent_complete:0,
            speed:current?overview.update_network_bytes_per_second:0,
            eta:current?overview.overall_estimated_time_remaining_sec:-1,
            state:(overview&&overview.update_state)||''
          });
        })()
        """;

    /// <summary>
    /// Download size and the folders to choose from. Null when Steam's debug interface is not reachable,
    /// in which case the caller falls back to <see cref="SteamControl.Install"/>.
    /// </summary>
    public static async Task<InstallPlan?> PrepareAsync(uint appId)
    {
        var script = PrepareScript.Replace("__APP__", appId.ToString(CultureInfo.InvariantCulture));
        if (await SteamDebug.EvaluateAsync(script) is not { } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var folders = root.GetProperty("folders").EnumerateArray()
                .Select(f => new InstallFolder(
                    f.GetProperty("index").GetInt32(),
                    f.GetProperty("path").GetString() ?? "",
                    f.GetProperty("label").GetString() ?? "",
                    f.GetProperty("drive").GetString() ?? "",
                    f.GetProperty("free").GetInt64(),
                    f.GetProperty("isDefault").GetBoolean()))
                .ToList();
            return folders.Count == 0 ? null : new InstallPlan(root.GetProperty("required").GetInt64(), folders);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Starts the download. False if Steam did not accept the sequence.</summary>
    public static async Task<bool> StartAsync(uint appId, InstallRequest request)
    {
        var script = StartScript
            .Replace("__APP__", appId.ToString(CultureInfo.InvariantCulture))
            .Replace("__FOLDER__", request.FolderIndex.ToString(CultureInfo.InvariantCulture))
            .Replace("__DESKTOP__", request.DesktopShortcut ? "true" : "false")
            .Replace("__MENU__", request.StartMenuShortcut ? "true" : "false");
        // Steam brings up its install window and its main window: keep them hidden.
        return await SteamWindows.KeepHiddenAsync(() => SteamDebug.EvaluateAsync(script)) == "ok";
    }

    /// <summary>The game's place in the download queue. Null when Steam is not reachable.</summary>
    public static async Task<InstallProgress?> GetProgressAsync(uint appId)
    {
        var script = ProgressScript.Replace("__APP__", appId.ToString(CultureInfo.InvariantCulture));
        if (await SteamDebug.EvaluateAsync(script) is not { } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var p = document.RootElement;
            return new InstallProgress(
                Found: p.GetProperty("found").GetBoolean(),
                Active: p.GetProperty("active").GetBoolean(),
                Paused: p.GetProperty("paused").GetBoolean(),
                Completed: p.GetProperty("completed").GetBoolean(),
                Error: p.GetProperty("error").GetString() ?? "",
                Percent: p.GetProperty("percent").GetDouble(),
                BytesPerSecond: (long)p.GetProperty("speed").GetDouble(),
                SecondsRemaining: (int)p.GetProperty("eta").GetDouble(),
                State: p.GetProperty("state").GetString() ?? "");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Uninstalls without Steam's confirmation window: the caller must have asked the user already. Found by trial:
    /// with the second argument false Steam shows its confirm dialog, with true it uninstalls straight away.
    /// </summary>
    public static async Task<bool> UninstallAsync(uint appId) =>
        await SteamWindows.KeepHiddenAsync(
            () => RunAsync($"SteamClient.Installs.OpenUninstallWizard([{appId}], true)")) == "ok";

    // Steam's own Downloads page passes the id of the client the download belongs to as a second argument ("0" is
    // this PC). Without it Steam ignores the call.
    const string LocalClient = "\"0\"";

    public static async Task<bool> PauseAsync(uint appId) =>
        await RunAsync($"SteamClient.Downloads.PauseAppUpdate({appId}, {LocalClient})") == "ok";

    /// <summary>Starts the update Steam has waiting for the game (it is already in Steam's download list).</summary>
    public static async Task<bool> UpdateAsync(uint appId) => await RunAsync(
        $"(async()=>{{ await SteamClient.Downloads.ResumeAppUpdate({appId}, {LocalClient}); await SteamClient.Downloads.EnableAllDownloads(true, {LocalClient}); }})()") == "ok";

    public static async Task<bool> ResumeAsync(uint appId) =>
        await RunAsync($"SteamClient.Downloads.ResumeAppUpdate({appId}, {LocalClient})") == "ok";

    /// <summary>
    /// Stops the download, takes the game off Steam's download list (as the cross on Steam's Downloads page does) and
    /// deletes what was already downloaded, so the game is as if it was never installed. The queue is started again if
    /// it was left paused, so the other downloads go on. False when Steam did not take the request (not reachable, or
    /// the calls are not those of this Steam version).
    /// </summary>
    public static async Task<bool> CancelAsync(uint appId)
    {
        if (await RunAsync(
            $"(async()=>{{ await SteamClient.Downloads.PauseAppUpdate({appId}, {LocalClient}); await SteamClient.Downloads.RemoveFromDownloadList({appId}, {LocalClient}); await SteamClient.Downloads.EnableAllDownloads(true, {LocalClient}); }})()") != "ok")
            return false;

        // The cross of the Downloads page only clears finished downloads: a game left half downloaded stays in the list
        // as "unscheduled". Uninstalling it (what Steam does for "Delete local content") removes it and its files.
        await UninstallAsync(appId);

        // Steam lets go of the files a moment after: remove what it left, trying again for a few seconds.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(1500);
            try
            {
                await Task.Run(() => SteamLibrary.DeletePartialInstall(appId));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still held by Steam.
            }
        }
        return true; // the download is stopped; the leftovers are in Steam's folder and Steam can remove them
    }

    /// <summary>Runs one Steam client call and waits for it. Returns "ok", or null if Steam is not reachable.</summary>
    static Task<string?> RunAsync(string call) =>
        SteamDebug.EvaluateAsync($"(async()=>{{ await ({call}); return 'ok'; }})()");
}
