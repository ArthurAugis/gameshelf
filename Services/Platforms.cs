using System.Globalization;
using System.Text;
using GameShelf.Models;

namespace GameShelf.Services;

/// <summary>Finds the games the user owns on several launchers, to show every platform's logo on the cover.</summary>
internal static class Platforms
{
    /// <summary>
    /// A title owned on several launchers (Steam and Epic) is one game on the shelf: its entries are linked, every one
    /// gets the logos of all the launchers, and only one of them (the installed one, else the first launcher) is
    /// shown, the others being reached from its page. A launcher listing the same title twice (a demo, an edition)
    /// is ambiguous: nothing is merged then, only the logos are set.
    /// </summary>
    public static void Match(IEnumerable<Game> games)
    {
        foreach (var sameTitle in games.GroupBy(game => Key(game.Name)).Where(group => group.Key.Length > 0))
        {
            var members = sameTitle.OrderBy(game => game.Launcher).ToList();
            var launchers = members.Select(game => game.Launcher).Distinct().ToArray();
            foreach (var game in members) game.Platforms = launchers;
            if (launchers.Length < 2 || launchers.Length != members.Count) continue;

            var primary = members.OrderByDescending(game => game.Installed).First();
            foreach (var game in members)
            {
                game.Siblings = members;
                game.KeyId = primary.AppId;
                game.IsAlternate = !ReferenceEquals(game, primary);
            }
        }
    }

    /// <summary>A title reduced to its letters and digits, lower case, without accents: "Pokémon™ Go!" and "pokemon go" match.</summary>
    internal static string Key(string title)
    {
        var result = new StringBuilder(title.Length);
        foreach (var c in title.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }
}
