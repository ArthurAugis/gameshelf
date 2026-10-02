using GameShelf.Launchers;

namespace GameShelf.Steam;

/// <summary>
/// Runs JavaScript inside the running Steam client, through the debug interface of its embedded Chromium
/// (see <see cref="SteamClient"/> for how that mode is turned on). Everything that talks to Steam's internals
/// goes through here.
/// </summary>
internal static class SteamDebug
{
    public const int DefaultPort = 8080;

    static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(10);

    // Address of Steam's library page once found, so the next calls skip the discovery request.
    static string? cachedSocketUrl;

    /// <summary>
    /// Runs <paramref name="script"/> in Steam's library UI (an async script is awaited) and returns the string
    /// it returns. Null when Steam is closed, its debug mode is off, or the script failed.
    /// </summary>
    public static async Task<string?> EvaluateAsync(string script)
    {
        if (cachedSocketUrl is { } cached)
        {
            if (await Cdp.RunAsync(script, cached, ScriptTimeout) is { } result) return result;
            cachedSocketUrl = null; // Steam was restarted or closed: find the page again
        }

        // Our own port first (Steam started by GameShelf), then Steam's default (Steam started any other way).
        foreach (var port in new[] { SteamControl.Port, DefaultPort })
        {
            // "SharedJSContext" is the page running the library code.
            if (await Cdp.FindPageAsync(port, IsLibraryPage) is not { } url) continue;
            if (await Cdp.RunAsync(script, url, ScriptTimeout) is { } result)
            {
                cachedSocketUrl = url;
                return result;
            }
        }
        return null;
    }

    static bool IsLibraryPage(System.Text.Json.JsonElement page) =>
        page.TryGetProperty("title", out var title) && title.GetString() == "SharedJSContext";
}
