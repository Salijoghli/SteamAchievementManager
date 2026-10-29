using SAM.App.Services;

namespace SAM.App.Tests;

public sealed class QuickSwitcherMatcherTests
{
    [Theory]
    [InlineData("Portal 2", "Portal 2", QuickSwitcherMatcher.ExactName)]
    [InlineData("portal 2", "Portal 2", QuickSwitcherMatcher.ExactName)]
    [InlineData("620", "Portal 2", QuickSwitcherMatcher.ExactAppId)]
    [InlineData("por", "Portal 2", QuickSwitcherMatcher.NamePrefix)]
    [InlineData("strike", "Counter-Strike: Global Offensive", QuickSwitcherMatcher.WordPrefix)]
    [InlineData("csgo", "Counter-Strike: Global Offensive", QuickSwitcherMatcher.Initials)]
    [InlineData("hl2", "Half-Life 2", QuickSwitcherMatcher.Initials)]
    [InlineData("p2", "Portal 2", QuickSwitcherMatcher.Initials)]
    [InlineData("ortal", "Portal 2", QuickSwitcherMatcher.Contains)]
    [InlineData("pokemon", "Pokémon Legends", QuickSwitcherMatcher.NamePrefix)]
    [InlineData("62", "Portal 2", QuickSwitcherMatcher.AppIdPrefix)]
    public void ScoresByKindOfMatch(string query, string name, int expected)
    {
        Assert.Equal(expected, QuickSwitcherMatcher.Score(query, name, "620"));
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("p 2x")]
    [InlineData("20")]
    public void NoMatch(string query)
    {
        Assert.Null(QuickSwitcherMatcher.Score(query, "Portal 2", "620"));
    }

    [Fact]
    public void EmptyQueryMatchesEverythingInOrder()
    {
        var games = new[] { ("B", "2"), ("A", "1"), ("C", "3") };

        var result = QuickSwitcherMatcher.Filter(games, "  ", g => g.Item1, g => g.Item2, 2);

        Assert.Equal(["B", "A"], result.Select(g => g.Item1));
    }

    [Fact]
    public void RanksBetterMatchesFirstAndKeepsOrderForTies()
    {
        var games = new[]
        {
            ("Half-Life: Alyx", "546560"),
            ("Black Mesa", "362890"),
            ("Half-Life 2", "220"),
            ("Half-Life", "70"),
            ("Portal", "400"),
        };

        var result = QuickSwitcherMatcher.Filter(games, "half-life", g => g.Item1, g => g.Item2, 10);

        Assert.Equal(["Half-Life", "Half-Life: Alyx", "Half-Life 2"], result.Select(g => g.Item1));
    }

    [Fact]
    public void SplitsWordsOnPunctuation()
    {
        Assert.Equal(["Counter", "Strike", "Global", "Offensive"], QuickSwitcherMatcher.Words("Counter-Strike: Global Offensive"));
        Assert.Empty(QuickSwitcherMatcher.Words(" - "));
    }
}
