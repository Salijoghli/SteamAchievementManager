using SAM.App.Infrastructure;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.Tests;

public sealed class StoreAchievementCountTests
{
    [Fact]
    public void ParsesTheAchievementTotal()
    {
        const string json = """{"292030":{"success":true,"data":{"achievements":{"total":78,"highlighted":[{"name":"x","path":"y"}]}}}}""";

        var result = AchievementCountService.ParseResponse(json, 292030);

        Assert.Equal(StoreAchievementStatus.Count, result.Status);
        Assert.Equal(78, result.Total);
    }

    [Fact]
    public void ParsesATotalSentAsAString()
    {
        var result = AchievementCountService.ParseResponse("""{"21690":{"success":true,"data":{"achievements":{"total":"70"}}}}""", 21690);

        Assert.Equal(StoreAchievementCount.Of(70), result);
    }

    [Theory]
    [InlineData("""{"570":{"success":true,"data":[]}}""")]
    [InlineData("""{"570":{"success":true,"data":{}}}""")]
    [InlineData("""{"570":{"success":true}}""")]
    [InlineData("""{"570":{"success":true,"data":{"achievements":{"total":0}}}}""")]
    public void NoAchievementsKeyMeansNone(string json)
    {
        var result = AchievementCountService.ParseResponse(json, 570);

        Assert.Equal(StoreAchievementStatus.None, result.Status);
        Assert.Equal(0, result.Total);
    }

    [Theory]
    [InlineData("""{"570":{"success":false}}""")]
    [InlineData("""{"440":{"success":true,"data":{"achievements":{"total":5}}}}""")]
    [InlineData("""{"570":{"success":true,"data":{"achievements":{}}}}""")]
    [InlineData("""{"570":{"success":true,"data":{"achievements":{"total":-3}}}}""")]
    [InlineData("""{"570":{"success":true,"data":{"achievements":{"total":"many"}}}}""")]
    [InlineData("""{"570":null}""")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("<html>Access denied</html>")]
    [InlineData("")]
    public void FailuresAreUnknown(string json) =>
        Assert.Equal(StoreAchievementStatus.Failed, AchievementCountService.ParseResponse(json, 570).Status);

    [Fact]
    public void CountsStayFreshForAWeekAndNoneForADay()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        AchievementCountService.CachedCount Entry(int total, TimeSpan age) => new() { Total = total, FetchedAt = now - age };

        Assert.True(AchievementCountService.IsFresh(Entry(78, TimeSpan.FromDays(6.9)), now));
        Assert.False(AchievementCountService.IsFresh(Entry(78, TimeSpan.FromDays(7.1)), now));
        Assert.True(AchievementCountService.IsFresh(Entry(0, TimeSpan.FromHours(23)), now));
        Assert.False(AchievementCountService.IsFresh(Entry(0, TimeSpan.FromHours(25)), now));

        // A timestamp from the future (clock changed) isn't trusted.
        Assert.False(AchievementCountService.IsFresh(Entry(78, TimeSpan.FromHours(-1)), now));
    }

    [Fact]
    public void OfMapsZeroToNone()
    {
        Assert.Equal(StoreAchievementCount.None, StoreAchievementCount.Of(0));
        Assert.Equal(new StoreAchievementCount(StoreAchievementStatus.Count, 3), StoreAchievementCount.Of(3));
    }
}

public sealed class AchievementStateTests
{
    [Fact]
    public void SchemaPresentIsLoadedOrNone()
    {
        Assert.Equal(AchievementState.Loaded, AchievementStates.Classify(new GameProgress(12, 30), null));
        Assert.Equal(AchievementState.Loaded, AchievementStates.Classify(new GameProgress(0, 30), StoreAchievementCount.Failed));

        // The schema wins over the store: a schema without achievements means none.
        Assert.Equal(AchievementState.None, AchievementStates.Classify(new GameProgress(0, 0), StoreAchievementCount.Of(78)));
    }

    [Fact]
    public void NoSchemaUsesTheStore()
    {
        Assert.Equal(AchievementState.NotLoaded, AchievementStates.Classify(null, StoreAchievementCount.Of(78)));
        Assert.Equal(AchievementState.None, AchievementStates.Classify(null, StoreAchievementCount.None));
        Assert.Equal(AchievementState.Unknown, AchievementStates.Classify(null, StoreAchievementCount.Failed));
        Assert.Equal(AchievementState.Checking, AchievementStates.Classify(null, null));
    }

    [Theory]
    [InlineData(AchievementState.NotLoaded, true)]
    [InlineData(AchievementState.Unknown, true)]
    [InlineData(AchievementState.Loaded, false)]
    [InlineData(AchievementState.None, false)]
    [InlineData(AchievementState.Checking, false)]
    public void MissingDataIsNotLoadedOrUnknown(AchievementState state, bool expected) =>
        Assert.Equal(expected, AchievementStates.IsMissing(state));

    [Fact]
    public void UnknownProgressSortsBetweenNoneAndNotStarted()
    {
        var none = AchievementStates.CompletionSortKey(AchievementState.None, null);
        var notLoaded = AchievementStates.CompletionSortKey(AchievementState.NotLoaded, null);
        var unknown = AchievementStates.CompletionSortKey(AchievementState.Unknown, null);
        var notStarted = AchievementStates.CompletionSortKey(AchievementState.Loaded, new GameProgress(0, 10));
        var half = AchievementStates.CompletionSortKey(AchievementState.Loaded, new GameProgress(5, 10));

        Assert.True(none < notLoaded);
        Assert.Equal(notLoaded, unknown);
        Assert.True(notLoaded < notStarted);
        Assert.Equal(0.5, half);
    }
}

public sealed class SteamStatsWatcherTests
{
    [Theory]
    [InlineData("UserGameStatsSchema_292030.bin", 292030u)]
    [InlineData("usergamestatsschema_21690.BIN", 21690u)]
    [InlineData("UserGameStats_12345_292030.bin", 292030u)]
    public void ParsesSchemaAndOwnStatsFiles(string name, uint expected) =>
        Assert.Equal(expected, SteamStatsWatcher.ParseFileName(name, 12345));

    [Theory]
    [InlineData("UserGameStats_99999_292030.bin")]
    [InlineData("UserGameStatsSchema_292030.bin.tmp")]
    [InlineData("UserGameStatsSchema_.bin")]
    [InlineData("UserGameStatsSchema_0.bin")]
    [InlineData("UserGameStatsSchema_12a.bin")]
    [InlineData("UserGameStats_12345_.bin")]
    [InlineData("something.bin")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresOtherFiles(string? name) =>
        Assert.Null(SteamStatsWatcher.ParseFileName(name, 12345));

    [Fact]
    public void DebouncerCoalescesABurstPerKey()
    {
        var debouncer = new KeyedDebouncer<uint>(TimeSpan.FromSeconds(1));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Steam writes the schema and the stats file in several steps.
        debouncer.Touch(292030, t0);
        debouncer.Touch(292030, t0.AddMilliseconds(300));
        debouncer.Touch(21690, t0.AddMilliseconds(400));
        debouncer.Touch(292030, t0.AddMilliseconds(600));

        Assert.True(debouncer.HasPending);
        Assert.Empty(debouncer.TakeDue(t0.AddMilliseconds(1300)));
        Assert.Equal([21690u], debouncer.TakeDue(t0.AddMilliseconds(1400)));
        Assert.Empty(debouncer.TakeDue(t0.AddMilliseconds(1500)));
        Assert.Equal([292030u], debouncer.TakeDue(t0.AddMilliseconds(1600)));
        Assert.False(debouncer.HasPending);
        Assert.Empty(debouncer.TakeDue(t0.AddSeconds(10)));
    }

    [Fact]
    public void DebouncerHandsOutKeysThatAreDueTogetherAtOnce()
    {
        var debouncer = new KeyedDebouncer<uint>(TimeSpan.FromSeconds(1));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        debouncer.Touch(1, t0);
        debouncer.Touch(2, t0.AddMilliseconds(100));

        var due = debouncer.TakeDue(t0.AddSeconds(2));

        Assert.Equal([1u, 2u], due.OrderBy(x => x));
        Assert.False(debouncer.HasPending);
    }

    [Fact]
    public void WatcherToleratesAMissingFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "sam-tests-" + Guid.NewGuid().ToString("N"));
        using var watcher = new SteamStatsWatcher(path, 12345);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void WatcherReportsANewSchemaFile()
    {
        var steam = Path.Combine(Path.GetTempPath(), "sam-tests-" + Guid.NewGuid().ToString("N"));
        var stats = Path.Combine(steam, "appcache", "stats");
        Directory.CreateDirectory(stats);
        try
        {
            using var changed = new ManualResetEventSlim();
            IReadOnlyList<uint>? reported = null;
            using var watcher = new SteamStatsWatcher(steam, 12345);
            watcher.Changed += (_, ids) =>
            {
                if (ids.Count > 0)
                {
                    reported = ids;
                    changed.Set();
                }
            };

            File.WriteAllBytes(Path.Combine(stats, "UserGameStatsSchema_292030.bin"), [0, 8, 8]);
            File.WriteAllBytes(Path.Combine(stats, "UserGameStats_99999_440.bin"), [8]);

            Assert.True(changed.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal([292030u], reported);
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }
}

public sealed class FetchAchievementsCommandLineTests
{
    [Fact]
    public void ParsesFetchAchievements()
    {
        var command = CommandLine.Parse(["292030", "--fetch-achievements"]);

        Assert.Equal(LaunchMode.FetchAchievements, command.Mode);
        Assert.Equal(292030u, command.AppId);
    }

    [Fact]
    public void IgnoresWindowsActivationArguments()
    {
        var command = CommandLine.Parse(["292030", "--FETCH-ACHIEVEMENTS", "-Embedding"]);

        Assert.Equal(LaunchMode.FetchAchievements, command.Mode);
    }

    [Theory]
    [InlineData("--fetch-achievements")]
    [InlineData("0", "--fetch-achievements")]
    [InlineData("292030", "--fetch-achievements=1")]
    [InlineData("292030", "--fetch-achievements", "--commit")]
    [InlineData("292030", "--unlock-all", "--fetch-achievements")]
    [InlineData("292030", "--fetch-achievements", "--fetch-achievements")]
    [InlineData("292030", "--fetch-achievements", "--autopilot-worker", "5")]
    public void RejectsBadFetchArguments(params string[] args) =>
        Assert.Equal(LaunchMode.Invalid, CommandLine.Parse(args).Mode);

    [Fact]
    public void UsageMentionsTheOption() =>
        Assert.Contains(CommandLine.FetchAchievementsOption, CommandLine.Usage, StringComparison.Ordinal);
}
