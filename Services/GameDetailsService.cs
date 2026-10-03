using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameShelf.Launchers;
using GameShelf.Models;

namespace GameShelf.Services;

/// <summary>
/// Description, genres, developer and release date of a game, from Steam's public store API (no key, no sign-in).
/// Answers are kept in %LOCALAPPDATA%\GameShelf\details so each game is fetched once.
/// </summary>
internal static class GameDetailsService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // One request per game, shared by whoever asks (a hover prefetch, then the details window).
    static readonly ConcurrentDictionary<uint, Task<GameDetails?>> Requests = new();

    /// <summary>The game's details, or null when offline or when the store has no page for it.</summary>
    public static Task<GameDetails?> GetAsync(uint appId)
    {
        // Not a Steam id: what the launcher says about the game is all there is.
        if (ManualGames.IsManualId(appId)) return Task.FromResult(ManualGames.DetailsOf(appId));
        if (GogLibrary.IsGogId(appId)) return Task.FromResult(GogLibrary.DetailsOf(appId));
        if (EpicLibrary.IsEpicId(appId)) return Task.FromResult(EpicLibrary.DetailsOf(appId));

        var request = Requests.GetOrAdd(appId, FetchAsync);
        // Failures are not remembered, so the next ask tries again.
        _ = request.ContinueWith(task =>
        {
            if (task.Result is null) Requests.TryRemove(appId, out _);
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
        return request;
    }

    static async Task<GameDetails?> FetchAsync(uint appId)
    {
        // The store answers in the chosen language. English keeps the original file name, so earlier caches stay valid.
        var language = Loc.Current.SteamName;
        var cacheName = language == "english" ? $"details/{appId}.json" : $"details/{language}/{appId}.json";
        var json = AppData.ReadText(cacheName);
        if (json is null)
        {
            try
            {
                json = await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l={language}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                return null;
            }
            if (Parse(json, appId) is null) return null; // do not cache failures
            AppData.WriteText(cacheName, json); // Known limitation: cached forever. Add an expiry if stale data becomes a problem.
        }
        return Parse(json, appId);
    }

    static GameDetails? Parse(string json, uint appId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var entry = document.RootElement.GetProperty(appId.ToString(CultureInfo.InvariantCulture));
            if (!entry.GetProperty("success").GetBoolean()) return null;
            var data = entry.GetProperty("data");

            return new GameDetails(
                Description: Clean(Text(data, "short_description")),
                Genres: data.TryGetProperty("genres", out var genres)
                    ? genres.EnumerateArray().Select(g => Text(g, "description")).OfType<string>().ToList()
                    : new List<string>(),
                Developer: FirstOf(data, "developers"),
                Publisher: FirstOf(data, "publishers"),
                ReleaseDate: data.TryGetProperty("release_date", out var release) ? Text(release, "date") : null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static string? FirstOf(JsonElement data, string property) =>
        data.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            ? list[0].GetString()
            : null;

    /// <summary>Removes HTML tags and entities the store leaves in its text.</summary>
    static string? Clean(string? text) =>
        text is null ? null : WebUtility.HtmlDecode(Regex.Replace(text, "<.*?>", " ")).Trim();
}
