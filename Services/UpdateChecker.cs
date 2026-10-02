using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace GameShelf.Services;

/// <summary>A newer GameShelf published as a GitHub release.</summary>
internal sealed record UpdateInfo(Version Version, string InstallerUrl);

/// <summary>
/// Finds out whether a newer release exists on GitHub (public API, no key, no sign-in) and installs it. A release
/// carries the installer as a <c>.msi</c> file; installing one replaces the installed version. The check only
/// runs when the repository address in GameShelf.csproj has been filled in, and not when the file
/// no-update-check.txt exists in the GameShelf data folder.
/// </summary>
internal static class UpdateChecker
{
    const string OptOutFile = "no-update-check.txt";
    const string PlaceholderOwner = "your-name";

    static readonly HttpClient Http = CreateClient();

    /// <summary>The repository as "owner/name", from the project file. Null while it is still the placeholder.</summary>
    public static string? Repository { get; } = ReadRepository();

    public static Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>The newest release if it is newer than this one. Null if up to date, disabled, or offline.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (Repository is null || AppData.Exists(OptOutFile)) return null;
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            return Parse(json, Repository, CurrentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Reads GitHub's answer for "latest release". Public to the tests.</summary>
    internal static UpdateInfo? Parse(string json, string repository, Version current)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var release = document.RootElement;
            var tag = release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest) || latest <= current) return null;

            // Only an installer that lives in this repository's releases is ever downloaded.
            var downloadPrefix = $"https://github.com/{repository}/releases/download/";
            var installer = release.GetProperty("assets").EnumerateArray()
                .Select(asset => asset.GetProperty("browser_download_url").GetString())
                .FirstOrDefault(url => url is not null
                    && url.StartsWith(downloadPrefix, StringComparison.OrdinalIgnoreCase)
                    && url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            return installer is null ? null : new UpdateInfo(latest, installer);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the installer and starts it. The caller then closes the app so the installer can replace the files.
    /// False if the download failed.
    /// </summary>
    public static async Task<bool> DownloadAndStartAsync(UpdateInfo update)
    {
        var path = Path.Combine(Path.GetTempPath(), $"GameShelf-{update.Version}.msi");
        try
        {
            await using (var download = await Http.GetStreamAsync(update.InstallerUrl))
            await using (var file = File.Create(path))
                await download.CopyToAsync(file);

            // /passive: a progress bar and no questions.
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{path}\" /passive") { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameShelf-update-check"); // GitHub's API requires one
        return client;
    }

    static string? ReadRepository()
    {
        var url = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryUrl")?.Value;
        const string prefix = "https://github.com/";
        if (url is null || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var repository = url[prefix.Length..].Trim('/');
        return repository.Split('/') is [var owner, _] && owner != PlaceholderOwner ? repository : null;
    }
}
