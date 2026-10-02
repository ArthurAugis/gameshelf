using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace GameShelf.Services;

/// <param name="Code">Two-letter code, also the name of the translation file (Localization\fr.json).</param>
/// <param name="NativeName">The language written in itself, as shown in the language picker.</param>
/// <param name="SteamName">The name Steam's store API uses for this language.</param>
internal sealed record Language(string Code, string NativeName, string SteamName);

/// <summary>
/// Translations of the UI. The English text is the key: <c>Loc.T("Install")</c> returns the translation in the
/// chosen language, or "Install" itself when there is none (so a missing translation shows English, never nothing).
/// The translations are the embedded files Localization\*.json, a flat object of English text to translated text.
/// To add a language: add its file, and one line to <see cref="Languages"/>.
/// </summary>
internal static class Loc
{
    const string SettingFile = "language.txt";

    public static readonly IReadOnlyList<Language> Languages = new Language[]
    {
        new("en", "English", "english"),
        new("fr", "Français", "french"),
        new("es", "Español", "spanish"),
        new("de", "Deutsch", "german"),
    };

    static readonly Dictionary<string, string> Table;

    static Loc()
    {
        Current = Choose();
        Table = Current.Code == "en" ? new Dictionary<string, string>() : LoadTable(Current.Code);
    }

    /// <summary>The language of this run: the one the user picked, else the one of Windows, else English.</summary>
    public static Language Current { get; }

    /// <summary>Makes dates and numbers follow the chosen language. Call once, when the app starts.</summary>
    public static void Initialize()
    {
        var culture = CultureInfo.GetCultureInfo(Current.Code);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
    }

    /// <summary>Remembers the language for the next start.</summary>
    public static void Save(Language language) => AppData.WriteText(SettingFile, language.Code);

    public static string T(string english) => Table.TryGetValue(english, out var translated) ? translated : english;

    /// <summary>Translates <paramref name="english"/>, a format string such as "{0} of {1} games", then fills it in.</summary>
    public static string T(string english, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, T(english), values);

    /// <summary>"1 game" or "5 games", from the two forms "{0} game" and "{0} games".</summary>
    public static string Count(int count, string one, string other) => T(count == 1 ? one : other, count);

    /// <summary>The translations of one language (empty if there is no such file). Public to the tests.</summary>
    internal static Dictionary<string, string> LoadTable(string code)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"GameShelf.Localization.{code}.json");
        if (stream is null) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Translates the texts written in a window's XAML (text blocks, buttons, check boxes, tooltips, the title),
    /// so the XAML can stay in plain English. Texts set from code are translated where they are set.
    /// </summary>
    public static void Apply(DependencyObject root)
    {
        switch (root)
        {
            case Window window:
                window.Title = T(window.Title);
                break;
            case TextBlock textBlock when textBlock.Text.Length > 0:
                textBlock.Text = T(textBlock.Text);
                break;
            case ContentControl { Content: string content } control:
                control.Content = T(content);
                break;
        }
        if (root is FrameworkElement { ToolTip: string tip } element) element.ToolTip = T(tip);

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) Apply(child);
    }

    /// <summary>The command line (<c>--lang fr</c>), else the saved choice, else the language of Windows, else English.</summary>
    static Language Choose()
    {
        var args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "--lang");
        var requested = flag >= 0 && flag + 1 < args.Length ? args[flag + 1] : null;

        return Find(requested) ?? Find(AppData.ReadText(SettingFile)?.Trim())
            ?? Find(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? Languages[0];
    }

    static Language? Find(string? code) => Languages.FirstOrDefault(language => language.Code == code);
}
