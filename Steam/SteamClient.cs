using System.Text.Json;
using GameShelf.Services;

namespace GameShelf.Steam;

/// <summary>
/// Asks the running Steam client for the library it displays (owned and family-shared games, no refunds).
/// This needs Steam's debug mode: an empty ".cef-enable-remote-debugging" file in the Steam folder and a
/// Steam restart. Steam's embedded Chromium then listens on localhost, on port 8080 by default or on the
/// port given with "-devtools-port" (see <see cref="SteamControl"/> and <see cref="SteamDebug"/>).
/// </summary>
internal static class SteamClient
{
    const string CacheFileName = "library.json";

    // Runs inside Steam's library UI. app_type: 1 game, 2 software, 8 demo, 65536 playtest.
    // Type 4 (tools, dedicated servers, SDKs) is left out.
    const string LibraryScript =
        "JSON.stringify(appStore.allApps.filter(a=>[1,2,8,65536].includes(a.app_type)).map(a=>[a.appid,a.display_name]))";

    /// <summary>App id to name, or null when Steam is closed or its debug mode is off.</summary>
    public static async Task<Dictionary<uint, string>?> GetLibraryAsync()
    {
        if (await SteamDebug.EvaluateAsync(LibraryScript) is not { } json) return null;
        try
        {
            using var apps = JsonDocument.Parse(json);
            var library = new Dictionary<uint, string>();
            foreach (var app in apps.RootElement.EnumerateArray())
                library[app[0].GetUInt32()] = app[1].GetString() ?? $"App {app[0].GetUInt32()}";
            return library.Count > 0 ? library : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Steam is still loading its library right after it starts, so a first read can be partial:
    /// waits until two reads in a row return the same number of apps.
    /// </summary>
    public static async Task<Dictionary<uint, string>?> WaitForLibraryAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int previousCount = -1;
        while (DateTime.UtcNow < deadline)
        {
            var library = await GetLibraryAsync();
            if (library is not null)
            {
                if (library.Count == previousCount) return library;
                previousCount = library.Count;
            }
            await Task.Delay(3000);
        }
        return null;
    }

    public static void SaveCache(Dictionary<uint, string> library) =>
        AppData.WriteText(CacheFileName, JsonSerializer.Serialize(library));

    /// <summary>The last exact library, used while Steam is closed.</summary>
    public static Dictionary<uint, string>? LoadCache()
    {
        try
        {
            return AppData.ReadText(CacheFileName) is { } json
                ? JsonSerializer.Deserialize<Dictionary<uint, string>>(json)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
