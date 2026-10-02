using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameShelf.Services;

namespace GameShelf.Launchers;

/// <summary>The Epic servers refused a request or answered with something unexpected.</summary>
internal class EpicException(string message) : Exception(message);

/// <summary>A short-lived access token and who it is for. Kept in memory only.</summary>
internal sealed record EpicSession(string AccessToken, string AccountId, string DisplayName);

/// <summary>
/// Lists the games the user owns on Epic, installed or not. Epic has no public library API and the launcher keeps the
/// list on its servers, so GameShelf does what Legendary and Heroic do: the user signs in on epicgames.com in their
/// browser, Epic shows a one-time authorization code, and GameShelf exchanges it for a short-lived token that lives
/// in memory only. It reads the library and the catalog with it, saves the resulting list (titles and artwork, no
/// credentials) and forgets the token. Signing in again refreshes the list.
/// These are the launcher's own endpoints, not a public API: Epic can change them.
/// </summary>
internal static class EpicClient
{
    const string CacheFileName = "epic-library.json";

    // The Epic Games Launcher's own OAuth client, public (it ships inside the launcher and Legendary uses it too).
    const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    const string ClientSecret = "daafbccc737745039dffe53d94fc76cf";
    const string TokenUrl = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
    const string LibraryUrl = "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true";
    const string CatalogUrl = "https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/";

    /// <summary>The page to open in the browser: signs in, then shows the authorization code.</summary>
    public static readonly string SignInUrl = "https://www.epicgames.com/id/login?redirectUrl="
        + Uri.EscapeDataString($"https://www.epicgames.com/id/api/redirect?clientId={ClientId}&responseType=code");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly HttpClient Http = CreateHttp();

    // Like Legendary, present ourselves as the launcher whose client id we use.
    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
        return http;
    }

    /// <summary>True when a refresh token is stored: the list can be refreshed without signing in again.</summary>
    public static bool IsSignedIn => EpicCredentials.Load() is not null;

    /// <summary>Signs in with the authorization code, reads the owned games and saves the list.</summary>
    public static async Task<List<OwnedEpicGame>> ImportAsync(string pastedCode) =>
        await ReadLibraryAsync((await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = ExtractCode(pastedCode),
        })).AccessToken);

    /// <summary>
    /// Reads the library again with the stored refresh token. Null when nobody is signed in, or when Epic no longer
    /// accepts the token (expired or revoked): the token is then deleted and the user has to sign in again.
    /// </summary>
    public static async Task<List<OwnedEpicGame>?> RefreshAsync()
    {
        var session = await SessionAsync();
        return session is null ? null : await ReadLibraryAsync(session.AccessToken);
    }

    static readonly SemaphoreSlim SessionLock = new(1, 1);

    /// <summary>
    /// A fresh access token from the stored refresh token (null when nobody is signed in, or when Epic no longer
    /// accepts the stored token: it is then deleted). One at a time, because Epic gives out a new refresh token each
    /// time and the old one stops working.
    /// </summary>
    public static async Task<EpicSession?> SessionAsync()
    {
        await SessionLock.WaitAsync();
        try
        {
            if (EpicCredentials.Load() is not { } refreshToken) return null;
            try
            {
                return await TokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                });
            }
            catch (EpicRefusedException)
            {
                EpicCredentials.Clear();
                return null;
            }
        }
        finally
        {
            SessionLock.Release();
        }
    }

    /// <summary>Signs out: forgets the stored token and the saved list.</summary>
    public static void SignOut()
    {
        EpicCredentials.Clear();
        var cache = AppData.PathOf(CacheFileName);
        if (File.Exists(cache)) File.Delete(cache);
    }

    static async Task<List<OwnedEpicGame>> ReadLibraryAsync(string token)
    {
        var records = new List<LibraryRecord>();
        string? cursor = null;
        do
        {
            var page = ParseLibraryPage(await GetAsync(LibraryUrl + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), token));
            records.AddRange(page.Records);
            cursor = page.NextCursor;
        } while (cursor is not null);

        // One catalog request per namespace, a few at a time.
        var games = new List<OwnedEpicGame>();
        using var limiter = new SemaphoreSlim(6);
        await Task.WhenAll(records.GroupBy(record => record.Namespace).Select(async group =>
        {
            await limiter.WaitAsync();
            try
            {
                var ids = string.Join("&", group.Select(record => "id=" + record.ItemId).Distinct());
                var url = $"{CatalogUrl}{group.Key}/bulk/items?{ids}&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en-US";
                string json;
                try
                {
                    json = await GetAsync(url, token);
                }
                catch (EpicException)
                {
                    return; // one namespace the catalog does not know costs only those games
                }
                lock (games) games.AddRange(ParseCatalog(json, group));
            }
            finally
            {
                limiter.Release();
            }
        }));

        var library = games.DistinctBy(game => game.AppName).OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ToList();
        SaveCache(library);
        return library;
    }

    /// <summary>The code from what the user pasted: the bare code, or the whole answer page (JSON with authorizationCode).</summary>
    internal static string ExtractCode(string pasted)
    {
        var text = pasted.Trim();
        if (text.Contains("authorizationCode", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                text = document.RootElement.GetProperty("authorizationCode").GetString() ?? "";
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new EpicException(Loc.T("That is not the code Epic shows after signing in."));
            }
        }
        return text.Trim('"', ' ', '\r', '\n');
    }

    internal sealed record LibraryRecord(string Namespace, string ItemId, string AppName);

    internal static (List<LibraryRecord> Records, string? NextCursor) ParseLibraryPage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var records = new List<LibraryRecord>();
            foreach (var item in document.RootElement.GetProperty("records").EnumerateArray())
            {
                var appName = Text(item, "appName");
                var ns = Text(item, "namespace");
                var id = Text(item, "catalogItemId");
                if (appName is not null && ns is not null && id is not null) records.Add(new LibraryRecord(ns, id, appName));
            }
            string? next = document.RootElement.TryGetProperty("responseMetadata", out var meta) ? Text(meta, "nextCursor") : null;
            return (records, next);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new EpicException(Loc.T("Epic's library answer could not be read."));
        }
    }

    /// <summary>The games among the catalog's items: not add-ons, in the Games category.</summary>
    internal static List<OwnedEpicGame> ParseCatalog(string json, IEnumerable<LibraryRecord> records)
    {
        var games = new List<OwnedEpicGame>();
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var record in records)
            {
                if (!document.RootElement.TryGetProperty(record.ItemId, out var item)) continue;
                if (item.TryGetProperty("mainGameItem", out var main) && main.ValueKind == JsonValueKind.Object) continue;
                if (!IsGame(item)) continue;

                var images = new Dictionary<string, string>();
                if (item.TryGetProperty("keyImages", out var keyImages) && keyImages.ValueKind == JsonValueKind.Array)
                    foreach (var image in keyImages.EnumerateArray())
                        if (Text(image, "type") is { } type && Text(image, "url") is { } url
                            && type is EpicLibrary.BoxArt or EpicLibrary.LogoArt or EpicLibrary.WideArt)
                            images[type] = url;

                var description = Text(item, "description");
                games.Add(new OwnedEpicGame(record.AppName, record.Namespace, record.ItemId, Text(item, "title") ?? record.AppName,
                    Text(item, "developer"), description is { Length: > 600 } ? description[..600] : description, images));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // An unreadable catalog answer costs only this namespace's games.
        }
        return games;
    }

    static bool IsGame(JsonElement item) =>
        item.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
        && categories.EnumerateArray().Any(category => Text(category, "path") is { } path
            && (path == "games" || path.StartsWith("games/", StringComparison.Ordinal)));

    /// <summary>
    /// Asks Epic for an access token with an authorization code or a refresh token. The access token is returned and
    /// used from memory only; the new refresh token Epic hands out (it changes each time) replaces the stored one.
    /// </summary>
    static async Task<EpicSession> TokenAsync(Dictionary<string, string> grant)
    {
        if (grant.GetValueOrDefault("code") is { Length: 0 })
            throw new EpicException(Loc.T("That is not the code Epic shows after signing in."));
        grant["token_type"] = "eg1";
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(grant) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}")));
        try
        {
            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new EpicRefusedException(Loc.T("Epic refused the code (it works once and expires after a few minutes). Sign in again to get a new one.")
                    + $"\n\n{(int)response.StatusCode} {ErrorOf(body)}");
            using var document = JsonDocument.Parse(body);
            if (Text(document.RootElement, "refresh_token") is { } refreshToken) EpicCredentials.Save(refreshToken);
            return new EpicSession(
                Text(document.RootElement, "access_token") ?? throw new EpicException(Loc.T("Epic's sign-in answer could not be read.")),
                Text(document.RootElement, "account_id") ?? "",
                Text(document.RootElement, "displayName") ?? "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new EpicException(Loc.T("Epic could not be reached: {0}", e.Message));
        }
    }

    /// <summary>Epic's error code and message from a failed answer (never a credential), for the error box.</summary>
    static string ErrorOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return $"{Text(document.RootElement, "errorCode")} {Text(document.RootElement, "errorMessage")}".Trim();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    static async Task<string> GetAsync(string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new EpicException(Loc.T("Epic answered with an error ({0}).", ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)));
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new EpicException(Loc.T("Epic could not be reached: {0}", e.Message));
        }
    }

    static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    public static void SaveCache(IReadOnlyList<OwnedEpicGame> library) =>
        AppData.WriteText(CacheFileName, JsonSerializer.Serialize(library, JsonOptions));

    /// <summary>The last list read from Epic, shown on the shelf.</summary>
    public static List<OwnedEpicGame>? LoadCache()
    {
        try
        {
            return AppData.ReadText(CacheFileName) is { } json
                ? JsonSerializer.Deserialize<List<OwnedEpicGame>>(json)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
