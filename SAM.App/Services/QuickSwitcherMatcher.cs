using System.Globalization;

namespace SAM.App.Services;

/// <summary>
/// Matching for the Ctrl+K quick switcher: how well a query matches a game's name or App ID.
/// Higher scores rank first; null means no match. Case- and accent-insensitive.
/// </summary>
public static class QuickSwitcherMatcher
{
    public const int ExactName = 1000;
    public const int ExactAppId = 950;
    public const int NamePrefix = 800;
    public const int WordPrefix = 600;
    public const int Initials = 500;
    public const int Contains = 300;
    public const int AppIdPrefix = 200;

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    /// <summary>
    /// The score of <paramref name="query"/> against a game: the whole name or App ID, the start of
    /// the name or of a word in it ("strike" in "Counter-Strike"), the words' initials ("hl2" for
    /// "Half-Life 2", "csgo" for "Counter-Strike: Global Offensive"), anywhere in the name, or the
    /// start of the App ID. An empty query matches everything with score 0.
    /// </summary>
    public static int? Score(string? query, string name, string appIdText)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0)
        {
            return 0;
        }

        name ??= "";
        if (Compare.Compare(name, q, Options) == 0)
        {
            return ExactName;
        }

        if (string.Equals(appIdText, q, StringComparison.Ordinal))
        {
            return ExactAppId;
        }

        if (Compare.IsPrefix(name, q, Options))
        {
            return NamePrefix;
        }

        var words = Words(name);
        if (words.Skip(1).Any(w => Compare.IsPrefix(w, q, Options)))
        {
            return WordPrefix;
        }

        if (q.Contains(' ') == false && q.Length >= 2 && Compare.IsPrefix(string.Concat(words.Select(w => w[0])), q, Options))
        {
            return Initials;
        }

        if (Compare.IndexOf(name, q, Options) >= 0)
        {
            return Contains;
        }

        return q.All(char.IsAsciiDigit) && appIdText.StartsWith(q, StringComparison.Ordinal) ? AppIdPrefix : null;
    }

    /// <summary>
    /// The items matching <paramref name="query"/>, best first; ties keep the input order (so pass
    /// the items in the order to show for an empty query). At most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<T> Filter<T>(IEnumerable<T> items, string? query, Func<T, string> name, Func<T, string> appIdText, int max)
    {
        return items
            .Select((item, index) => (Item: item, Index: index, Score: Score(query, name(item), appIdText(item))))
            .Where(m => m.Score != null)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Index)
            .Take(max)
            .Select(m => m.Item)
            .ToList();
    }

    /// <summary>Letter/digit runs: "Counter-Strike: Global Offensive" → Counter, Strike, Global, Offensive.</summary>
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWordChar && start < 0)
            {
                start = i;
            }
            else if (isWordChar == false && start >= 0)
            {
                words.Add(text[start..i]);
                start = -1;
            }
        }

        return words;
    }
}
