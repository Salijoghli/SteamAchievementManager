using SAM.App.Services;

namespace SAM.App.Tests;

public sealed class SuggestionTests
{
    private static readonly SuggestionFilter NoFilter = new(false, false);

    private static IncompleteGame Game(uint appId, string name, int total, bool installed, params string[] missing) =>
        new(appId, name, installed, total, missing.Select(id => new MissingAchievement(appId, name, id, "Name " + id, "", false, null, null)).ToList());

    private static Func<uint, IReadOnlyDictionary<string, double>?> Percents(Dictionary<uint, Dictionary<string, double>> byApp) =>
        appId => byApp.TryGetValue(appId, out var percents) ? percents : null;

    [Fact]
    public void EasiestMissingRanksByGlobalPercentAndCapsPerGame()
    {
        var games = new[]
        {
            Game(1, "Alpha", 10, true, "a1", "a2", "a3", "a4"),
            Game(2, "Beta", 10, false, "b1", "b2"),
            Game(3, "Gamma", 10, true, "c1"),
        };
        var percents = Percents(new()
        {
            [1] = new() { ["a1"] = 90, ["a2"] = 80, ["a3"] = 70, ["a4"] = 60 },
            [2] = new() { ["b1"] = 85, ["b2"] = 1 },

            // Game 3's percentages haven't arrived: it's skipped.
        });

        var result = Suggestions.EasiestMissing(games, percents, NoFilter, count: 4, perGame: 2);

        Assert.Equal(["a1", "b1", "a2", "b2"], result.Select(r => r.Achievement.Id));
        Assert.Equal(90, result[0].Percent);
    }

    [Fact]
    public void EasiestMissingSkipsAchievementsWithoutAPercent()
    {
        var games = new[] { Game(1, "Alpha", 3, true, "known", "removed") };
        var percents = Percents(new() { [1] = new() { ["known"] = 12.5 } });

        var result = Suggestions.EasiestMissing(games, percents, NoFilter, 15, 3);

        Assert.Equal(["known"], result.Select(r => r.Achievement.Id));
    }

    [Fact]
    public void FiltersApplyToBothLists()
    {
        var games = new[]
        {
            Game(1, "Installed rare", 10, true, "r1", "r2"),
            Game(2, "Not installed", 10, false, "n1"),
            Game(3, "Installed easy", 10, true, "e1", "e2"),
            Game(4, "Mixed", 10, true, "m1", "m2"),
        };
        var percents = Percents(new()
        {
            [1] = new() { ["r1"] = 4.9, ["r2"] = 0.3 },
            [2] = new() { ["n1"] = 70 },
            [3] = new() { ["e1"] = 50, ["e2"] = 2 },
            [4] = new() { ["m1"] = 3 },  // m2's percent is unknown: might be easy, so the game stays.
        });
        var filter = new SuggestionFilter(InstalledOnly: true, HideAllRare: true);

        var easiest = Suggestions.EasiestMissing(games, percents, filter, 15, 3);
        var reachable = Suggestions.PerfectWithinReach(games, percents, filter, 10);

        Assert.Equal(["e1", "m1", "e2"], easiest.Select(r => r.Achievement.Id));
        Assert.Equal([3u, 4u], reachable.Select(r => r.Game.AppId).Order());
        Assert.Contains(Suggestions.EasiestMissing(games, percents, NoFilter, 15, 3), r => r.Achievement.Id == "n1");
    }

    [Fact]
    public void PerfectWithinReachSortsByRemainingThenHardestPercent()
    {
        var games = new[]
        {
            Game(1, "Two left", 20, true, "x", "y"),
            Game(2, "One left, rare", 10, true, "z"),
            Game(3, "One left, common", 10, true, "w"),
            Game(4, "One left, unknown", 10, true, "v"),
            Game(5, "Far away", 10, true, "1", "2", "3", "4", "5"),
            Game(6, "Big game, 85%", 100, true, Enumerable.Range(0, 15).Select(i => "b" + i).ToArray()),
        };
        var percents = Percents(new()
        {
            [1] = new() { ["x"] = 40, ["y"] = 9 },
            [2] = new() { ["z"] = 0.5 },
            [3] = new() { ["w"] = 27.9 },
        });

        var result = Suggestions.PerfectWithinReach(games, percents, NoFilter, 10);

        Assert.Equal([3u, 2u, 4u, 1u, 6u], result.Select(r => r.Game.AppId));
        Assert.Equal(27.9, result[0].HardestPercent);
        Assert.Null(result[2].HardestPercent);
        Assert.Equal(9, result[3].HardestPercent);
        Assert.Equal(2, result[3].Remaining);
    }

    [Theory]
    [InlineData(10, 1, true)]
    [InlineData(10, 3, true)]
    [InlineData(10, 4, false)]
    [InlineData(100, 20, true)]
    [InlineData(100, 21, false)]
    [InlineData(2, 2, true)]
    [InlineData(10, 0, false)]
    [InlineData(0, 0, false)]
    public void WithinReachMeansFewLeftOrMostlyDone(int total, int remaining, bool expected) =>
        Assert.Equal(expected, Suggestions.IsWithinReach(total, remaining));

    [Fact]
    public void EasiestAndHardestPercentIgnoreUnknownIds()
    {
        var percents = new Dictionary<string, double> { ["a"] = 62, ["b"] = 3.5, ["c"] = 18 };

        Assert.Equal(62, Suggestions.EasiestPercent(["b", "a", "missing"], percents));
        Assert.Equal(3.5, Suggestions.HardestPercent(["c", "b", "missing"], percents));
        Assert.Null(Suggestions.EasiestPercent(["missing"], percents));
        Assert.Null(Suggestions.HardestPercent([], percents));
    }
}
