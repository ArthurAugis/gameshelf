using GameShelf.Services;
using Xunit;

namespace GameShelf.Tests;

public sealed class UpdateCheckerTests
{
    const string Repository = "someone/GameShelf";

    static string Release(string tag, params string[] assetUrls) =>
        $$"""
        { "tag_name": "{{tag}}", "assets": [ {{string.Join(",", assetUrls.Select(url => $$"""{ "browser_download_url": "{{url}}" }"""))}} ] }
        """;

    static string Msi(string version) =>
        $"https://github.com/{Repository}/releases/download/v{version}/GameShelf-{version}-win-x64.msi";

    [Fact]
    public void ANewerReleaseWithAnInstallerIsOffered()
    {
        var update = UpdateChecker.Parse(Release("v0.3.0", Msi("0.3.0")), Repository, new Version(0, 2, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 3, 0), update.Version);
        Assert.Equal(Msi("0.3.0"), update.InstallerUrl);
    }

    [Theory]
    [InlineData("v0.2.0")] // the same version
    [InlineData("v0.1.9")] // an older one
    public void ASameOrOlderReleaseIsIgnored(string tag) =>
        Assert.Null(UpdateChecker.Parse(Release(tag, Msi("0.2.0")), Repository, new Version(0, 2, 0)));

    [Fact]
    public void AReleaseWithoutAnInstallerIsIgnored() =>
        Assert.Null(UpdateChecker.Parse(
            Release("v0.3.0", $"https://github.com/{Repository}/releases/download/v0.3.0/GameShelf-0.3.0-win-x64.zip"),
            Repository, new Version(0, 2, 0)));

    [Fact]
    public void AnInstallerFromAnotherPlaceIsNeverUsed() =>
        Assert.Null(UpdateChecker.Parse(
            Release("v0.3.0", "https://example.com/GameShelf-0.3.0.msi",
                "https://github.com/someone-else/GameShelf/releases/download/v0.3.0/GameShelf.msi"),
            Repository, new Version(0, 2, 0)));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{ \"tag_name\": \"nightly\", \"assets\": [] }")]
    public void AnUnreadableAnswerIsIgnored(string json) =>
        Assert.Null(UpdateChecker.Parse(json, Repository, new Version(0, 1, 0)));
}
