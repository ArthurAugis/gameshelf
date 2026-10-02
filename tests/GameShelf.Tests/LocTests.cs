using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GameShelf.Services;
using Xunit;

namespace GameShelf.Tests;

public sealed class LocTests
{
    static readonly string[] Codes = Loc.Languages.Where(l => l.Code != "en").Select(l => l.Code).ToArray();

    [Fact]
    public void EveryLanguageHasATranslationFile()
    {
        foreach (var code in Codes)
            Assert.NotEmpty(Loc.LoadTable(code));
    }

    [Fact]
    public void EveryLanguageTranslatesTheSameTexts()
    {
        var reference = Loc.LoadTable("fr").Keys.ToHashSet();

        foreach (var code in Codes)
        {
            var keys = Loc.LoadTable(code).Keys.ToHashSet();
            Assert.Empty(reference.Except(keys).Select(key => $"{code} is missing: {key}"));
            Assert.Empty(keys.Except(reference).Select(key => $"{code} has an unknown text: {key}"));
        }
    }

    [Fact]
    public void TranslationsKeepThePlaceholdersOfTheEnglishText()
    {
        foreach (var code in Codes)
            foreach (var (english, translated) in Loc.LoadTable(code))
                Assert.True(Placeholders(english).SequenceEqual(Placeholders(translated)),
                    $"{code}: placeholders differ for \"{english}\"");
    }

    [Fact]
    public void TranslationsAreNeverEmpty()
    {
        foreach (var code in Codes)
            foreach (var (english, translated) in Loc.LoadTable(code))
                Assert.False(string.IsNullOrWhiteSpace(translated), $"{code}: empty translation for \"{english}\"");
    }

    [Fact]
    public void AnUnknownLanguageFileGivesAnEmptyTable() => Assert.Empty(Loc.LoadTable("xx"));

    /// <summary>Catches a text added to the UI without its translation.</summary>
    [Fact]
    public void EveryTextWrittenInTheSourceHasATranslation()
    {
        var root = RepositoryRoot();
        var translated = Loc.LoadTable("fr").Keys.ToHashSet();
        var used = new HashSet<string>();

        foreach (var file in SourceFiles(root, "*.cs"))
        {
            var code = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(code, @"Loc\.T\(""((?:[^""\\]|\\.)*)"""))
                used.Add(Unescape(match.Groups[1].Value));
            foreach (Match match in Regex.Matches(code, @"Loc\.Count\([^,]+,\s*""((?:[^""\\]|\\.)*)"",\s*""((?:[^""\\]|\\.)*)"""))
            {
                used.Add(Unescape(match.Groups[1].Value));
                used.Add(Unescape(match.Groups[2].Value));
            }
        }

        // Texts written in XAML are translated by Loc.Apply. Glyphs (&#xE721;) and the app name are not texts.
        foreach (var file in SourceFiles(root, "*.xaml"))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\b(?:Text|Content|ToolTip|Title)=""([^""{&][^""]*)"""))
                used.Add(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value));
        used.Remove("GameShelf");

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(translated).Order());
    }

    static string Unescape(string literal) => literal.Replace("\\\"", "\"");

    static IEnumerable<string> SourceFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>The folder with GameShelf.sln, found by walking up from this source file.</summary>
    static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        for (var folder = new FileInfo(thisFile).Directory; folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "GameShelf.sln"))) return folder.FullName;
        throw new InvalidOperationException("GameShelf.sln was not found above " + thisFile);
    }

    /// <summary>The {0}, {1}... of a format string, sorted.</summary>
    static IEnumerable<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order();
}
