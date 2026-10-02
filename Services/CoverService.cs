using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using GameShelf.Launchers;
using GameShelf.Models;
using GameShelf.Steam;

namespace GameShelf.Services;

/// <summary>
/// Finds the artwork (portrait cover, logo) of a game: Steam's own local cache first,
/// then Steam's public CDN, kept in %LOCALAPPDATA%\GameShelf\covers.
/// </summary>
internal static class CoverService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly string CacheDir = AppData.PathOf("covers");

    // {0} = app id, {1} = file name
    static readonly string[] CdnTemplates =
    {
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/{1}",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/{1}",
    };

    /// <summary>Path of the portrait cover, or null if the game has none.</summary>
    public static Task<string?> EnsureCoverAsync(Game game) =>
        game.Cover is not null ? Task.FromResult<string?>(game.Cover)
        : game.Launcher == Launcher.Epic ? EnsureEpicAsync(game, EpicLibrary.BoxArt, $"epic_{game.AppId}.jpg")
        : EnsureAsync(game.AppId, SteamLibrary.CoverFile, $"{game.AppId}.jpg");

    /// <summary>Path of the transparent logo, or null if the game has none (the spine then shows the title).</summary>
    public static Task<string?> EnsureLogoAsync(Game game) =>
        game.Launcher == Launcher.Epic
            ? EnsureEpicAsync(game, EpicLibrary.LogoArt, $"epic_{game.AppId}_logo.png")
            : EnsureAsync(game.AppId, SteamLibrary.LogoFile, $"{game.AppId}_logo.png");

    // One download per game, shared by whoever asks (a hover prefetch, then the details window).
    static readonly ConcurrentDictionary<uint, Task<string?>> HeroRequests = new();

    /// <summary>Path of the wide hero artwork used as the details window backdrop, or null if none exists.</summary>
    public static Task<string?> EnsureHeroAsync(Game game)
    {
        var request = HeroRequests.GetOrAdd(game.AppId,
            id => game.Launcher == Launcher.Epic
                ? EnsureEpicAsync(game, EpicLibrary.WideArt, $"epic_{id}_hero.jpg")
                : EnsureAsync(id, SteamLibrary.HeroFile, $"{id}_hero.jpg"));
        // A missing hero is not remembered, so the next ask tries again.
        _ = request.ContinueWith(task =>
        {
            if (task.Result is null) HeroRequests.TryRemove(game.AppId, out _);
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
        return request;
    }

    // Known limitation: a miss is retried on every launch. Add a negative cache if startup gets slow.
    static async Task<string?> EnsureAsync(uint appId, string asset, string cacheName)
    {
        if (SteamLibrary.LocalArt(appId, asset) is { } local) return local;

        var cached = Path.Combine(CacheDir, cacheName);
        if (File.Exists(cached)) return cached;

        Directory.CreateDirectory(CacheDir);
        foreach (var template in CdnTemplates)
            if (await TryDownloadAsync(string.Format(CultureInfo.InvariantCulture, template, appId, asset), cached)) return cached;
        return null;
    }

    /// <summary>An image of an Epic game, from the address in Epic's catalog, kept in the cover folder.</summary>
    static async Task<string?> EnsureEpicAsync(Game game, string imageType, string cacheName)
    {
        var cached = Path.Combine(CacheDir, cacheName);
        if (File.Exists(cached)) return cached;
        if (EpicLibrary.ArtUrl(game.AppId, imageType) is not { } url) return null;

        Directory.CreateDirectory(CacheDir);
        return await TryDownloadAsync(url, cached) ? cached : null;
    }

    /// <summary>Downloads the file. False if the server does not have it or cannot be reached.</summary>
    static async Task<bool> TryDownloadAsync(string url, string path)
    {
        try
        {
            await File.WriteAllBytesAsync(path, await Http.GetByteArrayAsync(url));
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false; // not on this server, or a timeout: the caller tries the next one
        }
    }
}
