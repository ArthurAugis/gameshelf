using GameShelf.Launchers;
using GameShelf.Models;
using GameShelf.Steam;

namespace GameShelf.Services;

/// <summary>An installed game with an update waiting; <see cref="Bytes"/> is the size to download, 0 when unknown.</summary>
internal sealed record PendingUpdate(Game Game, long Bytes);

/// <summary>Finds the installed games that have an update to download.</summary>
internal static class PendingUpdates
{
    /// <summary>
    /// Steam games with an update, from the app manifests on this PC (no Steam client needed): instant, works offline.
    /// </summary>
    public static List<PendingUpdate> Steam(IEnumerable<Game> games)
    {
        var pending = SteamLibrary.ReadPendingUpdates();
        return games.Where(game => game.Launcher == Launcher.Steam && pending.ContainsKey(game.AppId))
            .Select(game => new PendingUpdate(game, pending[game.AppId])).ToList();
    }

    /// <summary>GOG games with a newer build, from Galaxy's own library file (as fresh as Galaxy's last check).</summary>
    public static List<PendingUpdate> Gog(IEnumerable<Game> games)
    {
        var pending = GogLibrary.PendingUpdateProductIds().Select(GogLibrary.IdOf).ToHashSet();
        return games.Where(game => game.Launcher == Launcher.Gog && pending.Contains(game.AppId)).Select(game => new PendingUpdate(game, 0)).ToList();
    }

    /// <summary>The updates that are known from files on this PC, with no launcher to ask: Steam's and GOG's.</summary>
    public static List<PendingUpdate> OnDisk(IReadOnlyCollection<Game> games) => Steam(games).Concat(Gog(games)).ToList();

    /// <summary>
    /// Epic games with an update. Epic keeps that in the launcher, so this asks it: only call it while the launcher
    /// runs in the debug mode GameShelf uses to control it (<see cref="EpicBridge.IsReachableAsync"/>).
    /// </summary>
    public static async Task<List<PendingUpdate>> EpicAsync(IEnumerable<Game> games)
    {
        var found = new List<PendingUpdate>();
        foreach (var game in games.Where(game => game.Launcher == Launcher.Epic))
        {
            if (EpicLibrary.IdentityOf(game.AppId) is not { } identity) continue;
            if (await EpicBridge.GetStateAsync(identity) is { HasUpdate: true }) found.Add(new PendingUpdate(game, 0));
        }
        return found;
    }

    /// <summary>Starts the update in the game's launcher. False when the launcher did not take the request.</summary>
    public static async Task<bool> StartAsync(Game game) => game.Launcher switch
    {
        Launcher.Steam => await SteamInstaller.UpdateAsync(game.AppId),
        Launcher.Gog => GogLibrary.OpenPage(game), // Galaxy cannot be driven from outside: its page has the Update button
        _ => EpicLibrary.IdentityOf(game.AppId) is { } identity && await EpicBridge.UpdateAsync(identity),
    };
}
