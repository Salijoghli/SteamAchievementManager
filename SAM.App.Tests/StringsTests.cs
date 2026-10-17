using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SAM.App.Tests;

public sealed partial class StringsTests
{
    private static readonly string StringsDirectory = Path.Combine(FindRepositoryRoot(), "SAM.App", "Strings");

    [Fact]
    public void EveryLanguageHasTheSameKeysAndPlaceholders()
    {
        var english = Load("en-US");
        var georgian = Load("ka-GE");

        Assert.NotEmpty(english);
        Assert.Empty(english.Keys.Except(georgian.Keys));
        Assert.Empty(georgian.Keys.Except(english.Keys));
        foreach (var (key, text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(georgian[key]), key);
            Assert.True(Placeholders(text).SetEquals(Placeholders(georgian[key])), key);
        }
    }

    [Fact]
    public void PluralKeysComeInPairs()
    {
        var keys = Load("en-US").Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys.Where(k => k.EndsWith("_One", StringComparison.Ordinal)))
        {
            Assert.Contains(key[..^4] + "_Other", keys);
        }

        foreach (var key in keys.Where(k => k.EndsWith("_Other", StringComparison.Ordinal)))
        {
            Assert.Contains(key[..^6] + "_One", keys);
        }
    }

    [Fact]
    public void FallsBackToEmbeddedEnglishWithoutTheResourceSystem()
    {
        Assert.Equal("Close", Strings.Get("Common_Close"));
        Assert.Equal("Version 1.2", Strings.Format("Settings_Version", "1.2"));
    }

    private static Dictionary<string, string> Load(string language) =>
        XDocument.Load(Path.Combine(StringsDirectory, language, "Resources.resw"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);

    private static HashSet<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SAM.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Couldn't find SAM.sln above the test output folder.");
    }

    [GeneratedRegex(@"\{\d+(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
