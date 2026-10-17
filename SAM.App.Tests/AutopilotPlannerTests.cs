using System.Diagnostics;
using SAM.App.Models;
using SAM.App.Services.Autopilot;

namespace SAM.App.Tests;

/// <summary>Shared builders for the Autopilot tests.</summary>
internal static class AutopilotTestData
{
    /// <summary>Wednesday 2026-10-14 12:00 at UTC+0 (the planner reads the offset's wall clock).</summary>
    public static readonly DateTimeOffset Noon = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public static AutopilotAchievement A(
        string id,
        double? percent = 50,
        int index = 0,
        bool hidden = false,
        bool isProtected = false,
        bool unlocked = false,
        string? stat = null,
        double? max = null) => new(id, "Name " + id, percent, hidden, isProtected, unlocked, stat, max, index);

    /// <summary>A profile without jitter or limits, so timings are exact.</summary>
    public static AutopilotProfile Plain(params (double Percent, double Hours)[] curve)
    {
        var profile = new AutopilotProfile
        {
            Name = "Test",
            JitterPercent = 0,
            MinMinutesBetweenUnlocks = 0,
            MaxUnlocksPerHour = 0,
            MaxUnlocksPerDay = 0,
            TargetMode = AutopilotTargetMode.Unlimited,
            IncludeRare = true,
        };

        if (curve.Length > 0)
        {
            profile.Curve = curve.Select(p => new AutopilotCurvePoint { Percent = p.Percent, Hours = p.Hours }).ToList();
        }

        return profile;
    }

    /// <summary>A config whose active profile is <paramref name="profile"/>.</summary>
    public static AutopilotConfig ConfigWith(AutopilotProfile profile) => new()
    {
        ActiveProfile = profile.Name,
        CustomProfiles = [profile],
    };
}

public sealed class AutopilotClassifyTests
{
    [Fact]
    public void ReasonsFollowDocumentedPrecedence()
    {
        var profile = new AutopilotProfile { SkipHidden = true, IncludeUnknownRarity = false, MinGlobalPercent = 3, IncludeRare = false };
        var gameOverride = new AutopilotGameOverride { ExcludedAchievements = ["X"], OnlyAchievements = ["Y"] };

        // Each step removes the reason that won in the previous one.
        Assert.Equal(AutopilotSkipReason.AlreadyUnlocked, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("X", null, hidden: true, isProtected: true, unlocked: true)));
        Assert.Equal(AutopilotSkipReason.Protected, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("X", null, hidden: true, isProtected: true)));
        Assert.Equal(AutopilotSkipReason.Excluded, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("X", null, hidden: true)));
        Assert.Equal(AutopilotSkipReason.NotInOnlyList, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("Z", null, hidden: true)));
        Assert.Equal(AutopilotSkipReason.Hidden, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("Y", null, hidden: true)));
        Assert.Equal(AutopilotSkipReason.UnknownRarity, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("Y", null)));
        Assert.Equal(AutopilotSkipReason.BelowMinimumPercent, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("Y", 2)));
        Assert.Equal(AutopilotSkipReason.Rare, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("Y", 4)));
        Assert.Equal(AutopilotSkipReason.None, AutopilotPlanner.Classify(profile, gameOverride, AutopilotTestData.A("Y", 5)));
    }

    [Fact]
    public void OptionalRulesOnlyApplyWhenEnabled()
    {
        var profile = new AutopilotProfile { SkipHidden = false, IncludeRare = true, MinGlobalPercent = 0 };

        Assert.Equal(AutopilotSkipReason.None, AutopilotPlanner.Classify(profile, null, AutopilotTestData.A("A", 0.1, hidden: true)));
        Assert.Equal(AutopilotSkipReason.None, AutopilotPlanner.Classify(profile, new AutopilotGameOverride(), AutopilotTestData.A("A", 0.1)));
        Assert.Equal(AutopilotSkipReason.Rare, AutopilotPlanner.Classify(new AutopilotProfile(), null, AutopilotTestData.A("A", 4.99)));
    }

    [Fact]
    public void UnknownPercentIsTreatedAsUnknownPercentAs()
    {
        var unknown = AutopilotTestData.A("A", null);

        Assert.Equal(AutopilotSkipReason.None, AutopilotPlanner.Classify(new AutopilotProfile { UnknownPercentAs = 20 }, null, unknown));
        Assert.Equal(AutopilotSkipReason.Rare, AutopilotPlanner.Classify(new AutopilotProfile { UnknownPercentAs = 1 }, null, unknown));
        Assert.Equal(AutopilotSkipReason.BelowMinimumPercent, AutopilotPlanner.Classify(new AutopilotProfile { UnknownPercentAs = 20, MinGlobalPercent = 30 }, null, unknown));
    }
}

public sealed class AutopilotCurveTests
{
    private static readonly AutopilotProfile Natural = AutopilotTestData.Plain();

    [Theory]
    [InlineData(98, 1)]
    [InlineData(50, 5)]
    [InlineData(10, 20)]
    [InlineData(1, 60)]
    public void CurvePointsAreExact(double percent, double hours) =>
        Assert.Equal(hours, AutopilotPlanner.RequiredHours(Natural, percent, 0.5), 9);

    [Fact]
    public void InterpolatesOnLogScale()
    {
        // The geometric mean of two points is halfway on a log scale.
        Assert.Equal(12.5, AutopilotPlanner.RequiredHours(Natural, Math.Sqrt(50 * 10), 0.5), 9);
        Assert.Equal(3, AutopilotPlanner.RequiredHours(Natural, Math.Sqrt(98 * 50), 0.5), 9);
        Assert.Equal(40, AutopilotPlanner.RequiredHours(Natural, Math.Sqrt(10), 0.5), 9);

        // A quarter of the way from 50 to 10 in log terms.
        Assert.Equal(5 + (15 * 0.25), AutopilotPlanner.RequiredHours(Natural, 50 * Math.Pow(0.2, 0.25), 0.5), 9);

        // Not linear in percent: 30% (the linear midpoint of 50 and 10) needs well under 12.5 h.
        var expected = 5 + (15 * (Math.Log10(50) - Math.Log10(30)) / (Math.Log10(50) - Math.Log10(10)));
        Assert.Equal(expected, AutopilotPlanner.RequiredHours(Natural, 30, 0.5), 9);
        Assert.True(expected < 10);
    }

    [Fact]
    public void ClampsOutsideTheCurve()
    {
        Assert.Equal(1, AutopilotPlanner.RequiredHours(Natural, 100, 0.5));
        Assert.Equal(60, AutopilotPlanner.RequiredHours(Natural, 0.1, 0.5));
        Assert.Equal(60, AutopilotPlanner.RequiredHours(Natural, 0, 0.5));
    }

    [Fact]
    public void UnsortedCurveIsSortedAndUnknownUsesUnknownPercentAs()
    {
        var shuffled = AutopilotTestData.Plain((10, 20), (98, 1), (1, 60), (50, 5));

        foreach (var percent in new[] { 99.0, 70, 22, 3, 0.5 })
        {
            Assert.Equal(AutopilotPlanner.RequiredHours(Natural, percent, 0.5), AutopilotPlanner.RequiredHours(shuffled, percent, 0.5), 9);
        }

        Assert.Equal(AutopilotPlanner.RequiredHours(Natural, 20, 0.5), AutopilotPlanner.RequiredHours(Natural, null, 0.5));
        Assert.Equal(0, AutopilotPlanner.RequiredHours(new AutopilotProfile { Curve = [] }, 50, 0.5));
    }

    [Fact]
    public void JitterStaysWithinBounds()
    {
        var profile = new AutopilotProfile { JitterPercent = 20 };

        Assert.Equal(4, AutopilotPlanner.RequiredHours(profile, 50, 0), 9);
        Assert.Equal(5, AutopilotPlanner.RequiredHours(profile, 50, 0.5), 9);
        Assert.Equal(6, AutopilotPlanner.RequiredHours(profile, 50, 1), 9);

        var random = new Random(42);
        for (var i = 0; i < 1000; i++)
        {
            var hours = AutopilotPlanner.RequiredHours(profile, 50, random.NextDouble());
            Assert.InRange(hours, 4, 6);
        }

        // Out-of-range seeds and jitter are clamped; hours are never negative.
        Assert.Equal(6, AutopilotPlanner.RequiredHours(profile, 50, 7), 9);
        Assert.Equal(0, AutopilotPlanner.RequiredHours(new AutopilotProfile { JitterPercent = 100 }, 50, 0));
        Assert.Equal(0, AutopilotPlanner.RequiredHours(new AutopilotProfile { JitterPercent = 500 }, 50, 0));
    }
}

public sealed class AutopilotOrderTests
{
    private static readonly AutopilotAchievement[] Achievements =
    [
        AutopilotTestData.A("D", 10, 3),
        AutopilotTestData.A("A", 50, 0),
        AutopilotTestData.A("C", 90, 2),
        AutopilotTestData.A("B", 50, 1),
        AutopilotTestData.A("U", 60, 4, unlocked: true),
        AutopilotTestData.A("N", null, 5),
    ];

    [Fact]
    public void MostCommonFirstBreaksTiesBySchemaIndex()
    {
        var profile = new AutopilotProfile { Order = AutopilotUnlockOrder.MostCommonFirst, IncludeRare = true, UnknownPercentAs = 20 };

        var ordered = AutopilotPlanner.OrderCandidates(profile, null, Achievements, new Random(1));

        Assert.Equal(["C", "A", "B", "N", "D"], ordered.Select(a => a.Id));
    }

    [Fact]
    public void SchemaOrderUsesSchemaIndexAndSkipsDisallowed()
    {
        var profile = new AutopilotProfile { Order = AutopilotUnlockOrder.SchemaOrder, IncludeRare = true };
        var gameOverride = new AutopilotGameOverride { ExcludedAchievements = ["B"] };

        var ordered = AutopilotPlanner.OrderCandidates(profile, gameOverride, Achievements, new Random(1));

        Assert.Equal(["A", "C", "D", "N"], ordered.Select(a => a.Id));
    }

    [Fact]
    public void RandomIsSeededPermutationIndependentOfInputOrder()
    {
        var profile = new AutopilotProfile { Order = AutopilotUnlockOrder.Random, IncludeRare = true };
        var many = Enumerable.Range(0, 40).Select(i => AutopilotTestData.A("A" + i, 50, i)).ToList();

        var first = AutopilotPlanner.OrderCandidates(profile, null, many, new Random(7)).Select(a => a.Id).ToList();
        var again = AutopilotPlanner.OrderCandidates(profile, null, Enumerable.Reverse(many), new Random(7)).Select(a => a.Id).ToList();
        var other = AutopilotPlanner.OrderCandidates(profile, null, many, new Random(8)).Select(a => a.Id).ToList();

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Equal(many.Select(a => a.Id).Order(), first.Order());
        Assert.NotEqual(many.Select(a => a.Id), first);
    }
}

public sealed class AutopilotDueNowTests
{
    private static readonly DateTimeOffset Now = AutopilotTestData.Noon;

    private static List<AutopilotAchievement> Five() =>
        Enumerable.Range(0, 5).Select(i => AutopilotTestData.A("A" + i, 50, i)).ToList();

    [Fact]
    public void PlaytimeModeReturnsEverythingPastItsThresholdInOrder()
    {
        var profile = AutopilotTestData.Plain();
        var achievements = new[]
        {
            AutopilotTestData.A("Rare", 1, 0),
            AutopilotTestData.A("Common", 98, 1),
            AutopilotTestData.A("Half", 50, 2),
            AutopilotTestData.A("Done", 99, 3, unlocked: true),
        };
        var state = new AutopilotGameState();

        Assert.Empty(AutopilotPlanner.DueNow(profile, null, achievements, state, 0.5, Now, new Random(1)));
        Assert.Equal(["Common"], AutopilotPlanner.DueNow(profile, null, achievements, state, 1, Now, new Random(1)).Select(a => a.Id));
        Assert.Equal(["Common", "Half"], AutopilotPlanner.DueNow(profile, null, achievements, state, 30, Now, new Random(1)).Select(a => a.Id));
        Assert.Equal(["Common", "Half", "Rare"], AutopilotPlanner.DueNow(profile, null, achievements, state, 60, Now, new Random(1)).Select(a => a.Id));
    }

    [Fact]
    public void CreatesMissingSeedsAndKeepsExistingOnes()
    {
        var profile = new AutopilotProfile { JitterPercent = 20, MinMinutesBetweenUnlocks = 0, MaxUnlocksPerHour = 0, MaxUnlocksPerDay = 0 };
        var achievements = new[] { AutopilotTestData.A("A", 50, 0), AutopilotTestData.A("B", 50, 1), AutopilotTestData.A("C", 50, 2, unlocked: true) };
        var state = new AutopilotGameState { JitterSeeds = { ["A"] = 1.0 } };

        AutopilotPlanner.DueNow(profile, null, achievements, state, 0, Now, new Random(3));

        Assert.Equal(1.0, state.JitterSeeds["A"]);
        Assert.Equal(new Random(3).NextDouble(), state.JitterSeeds["B"]);
        Assert.False(state.JitterSeeds.ContainsKey("C"));

        // A needs 6 h (seed 1); B's threshold depends on its seed.
        var b = AutopilotPlanner.RequiredHours(profile, 50, state.JitterSeeds["B"]);
        var due = AutopilotPlanner.DueNow(profile, null, achievements, state, Math.Max(b, 5.99), Now, new Random(4));
        Assert.DoesNotContain(due, a => a.Id == "A");
        Assert.Contains(AutopilotPlanner.DueNow(profile, null, achievements, state, 6, Now, new Random(4)), a => a.Id == "A");
        Assert.Equal(2, state.JitterSeeds.Count);
    }

    [Fact]
    public void DailyLimitCountsUnlocksToday()
    {
        var profile = AutopilotTestData.Plain((50, 1));
        profile.MaxUnlocksPerDay = 3;

        Assert.Equal(3, AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState(), 2, Now, new Random(1)).Count);
        Assert.Equal(2, AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState { UnlocksToday = 1 }, 2, Now, new Random(1)).Count);
        Assert.Empty(AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState { UnlocksToday = 3 }, 2, Now, new Random(1)));
    }

    [Fact]
    public void HourlyLimitCountsOnlyTheLastSixtyMinutes()
    {
        var profile = AutopilotTestData.Plain((50, 1));
        profile.MaxUnlocksPerHour = 2;

        var state = new AutopilotGameState { RecentUnlocks = [Now.AddMinutes(-70), Now.AddMinutes(-60), Now.AddMinutes(-10)] };
        var due = AutopilotPlanner.DueNow(profile, null, Five(), state, 2, Now, new Random(1));
        Assert.Equal(["A0"], due.Select(a => a.Id));

        state.RecentUnlocks.Add(Now.AddMinutes(-1));
        Assert.Empty(AutopilotPlanner.DueNow(profile, null, Five(), state, 2, Now, new Random(1)));
        Assert.Equal(2, AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState(), 2, Now, new Random(1)).Count);
    }

    [Fact]
    public void MinMinutesBetweenAllowsOneAtATime()
    {
        var profile = AutopilotTestData.Plain((50, 1));
        profile.MinMinutesBetweenUnlocks = 10;

        Assert.Single(AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState(), 2, Now, new Random(1)));
        Assert.Empty(AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState { LastUnlockAt = Now.AddMinutes(-9) }, 2, Now, new Random(1)));
        Assert.Single(AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState { LastUnlockAt = Now.AddMinutes(-10) }, 2, Now, new Random(1)));

        // A last unlock in the future (clock moved back) doesn't block forever.
        Assert.Single(AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState { LastUnlockAt = Now.AddDays(1) }, 2, Now, new Random(1)));
    }

    [Fact]
    public void IgnoresQuietHoursAndTimeOfDay()
    {
        // Quiet hours are the engine's concern: DueNow gives the same answer at any time.
        var profile = AutopilotTestData.Plain((50, 1));
        var night = new DateTimeOffset(2026, 10, 14, 3, 0, 0, TimeSpan.Zero);

        Assert.Equal(5, AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState(), 2, night, new Random(1)).Count);
        Assert.Equal(5, AutopilotPlanner.DueNow(profile, null, Five(), new AutopilotGameState(), 2, Now, new Random(1)).Count);
    }

    [Fact]
    public void IntervalModeSchedulesOneUnlockPerInterval()
    {
        var profile = AutopilotTestData.Plain();
        profile.Pacing = AutopilotPacingMode.Interval;
        profile.IntervalMinutes = 60;
        profile.JitterPercent = 20;
        var achievements = new[] { AutopilotTestData.A("Low", 10, 0), AutopilotTestData.A("High", 90, 1) };
        var state = new AutopilotGameState { AutopilotHours = 2 };
        var random = new Random(5);

        // First call only schedules.
        Assert.Empty(AutopilotPlanner.DueNow(profile, null, achievements, state, 100, Now, random));
        var first = state.NextIntervalAtHours!.Value;
        Assert.InRange(first, 2.8, 3.2);
        Assert.Empty(state.JitterSeeds);

        state.AutopilotHours = first - 0.01;
        Assert.Empty(AutopilotPlanner.DueNow(profile, null, achievements, state, 100, Now, random));
        Assert.Equal(first, state.NextIntervalAtHours);

        state.AutopilotHours = first + 0.5;
        Assert.Equal(["High"], AutopilotPlanner.DueNow(profile, null, achievements, state, 100, Now, random).Select(a => a.Id));
        Assert.InRange(state.NextIntervalAtHours!.Value, first + 0.5 + 0.8, first + 0.5 + 1.2);

        // Not due again until the next interval, and playtime doesn't matter in this mode.
        Assert.Empty(AutopilotPlanner.DueNow(profile, null, achievements, state, 1000, Now, random));
    }

    [Fact]
    public void IntervalModeRespectsLimitsWithoutSkippingTheStep()
    {
        var profile = AutopilotTestData.Plain();
        profile.Pacing = AutopilotPacingMode.Interval;
        profile.MaxUnlocksPerDay = 1;
        var achievements = new[] { AutopilotTestData.A("A", 90, 0) };
        var state = new AutopilotGameState { AutopilotHours = 5, NextIntervalAtHours = 4, UnlocksToday = 1 };

        Assert.Empty(AutopilotPlanner.DueNow(profile, null, achievements, state, 0, Now, new Random(1)));
        Assert.Equal(4, state.NextIntervalAtHours);

        state.UnlocksToday = 0;
        Assert.Single(AutopilotPlanner.DueNow(profile, null, achievements, state, 0, Now, new Random(1)));
        Assert.Equal(6, state.NextIntervalAtHours);
    }

    [Fact]
    public void OnlyTouchesSeedAndIntervalFields()
    {
        var profile = AutopilotTestData.Plain((50, 1));
        var recent = new List<DateTimeOffset> { Now.AddMinutes(-5) };
        var state = new AutopilotGameState { AutopilotHours = 3, UnlocksToday = 1, RecentUnlocks = recent, LastUnlockAt = Now.AddHours(-1) };

        AutopilotPlanner.DueNow(profile, null, Five(), state, 2, Now, new Random(1));

        Assert.Equal(3, state.AutopilotHours);
        Assert.Equal(1, state.UnlocksToday);
        Assert.Single(state.RecentUnlocks);
        Assert.Equal(Now.AddHours(-1), state.LastUnlockAt);
        Assert.Empty(state.UnlockedByAutopilot);
        Assert.Null(state.NextIntervalAtHours);
    }
}

public sealed class AutopilotScheduleTests
{
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void FixtureDaysAreTheExpectedWeekdays()
    {
        Assert.Equal(DayOfWeek.Wednesday, At(14, 0).DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, At(16, 0).DayOfWeek);
    }

    [Fact]
    public void NoWindowMeansAlways()
    {
        var config = new AutopilotConfig();

        Assert.True(AutopilotPlanner.IsWithinSchedule(config, At(14, 3)));
        Assert.True(AutopilotPlanner.IsWithinSchedule(new AutopilotConfig { ActiveFrom = TimeSpan.FromHours(8) }, At(14, 3)));
        Assert.True(AutopilotPlanner.IsWithinSchedule(new AutopilotConfig { ActiveFrom = TimeSpan.FromHours(8), ActiveTo = TimeSpan.FromHours(8) }, At(14, 3)));
        Assert.False(AutopilotPlanner.IsQuietTime(config, At(14, 3)));
        Assert.False(AutopilotPlanner.IsQuietTime(new AutopilotConfig { QuietFrom = TimeSpan.FromHours(1), QuietTo = TimeSpan.FromHours(1) }, At(14, 1)));
    }

    [Fact]
    public void SameDayWindowIsHalfOpen()
    {
        var config = new AutopilotConfig { ActiveFrom = TimeSpan.FromHours(9), ActiveTo = TimeSpan.FromHours(17) };

        Assert.False(AutopilotPlanner.IsWithinSchedule(config, At(14, 8, 59)));
        Assert.True(AutopilotPlanner.IsWithinSchedule(config, At(14, 9)));
        Assert.True(AutopilotPlanner.IsWithinSchedule(config, At(14, 16, 59)));
        Assert.False(AutopilotPlanner.IsWithinSchedule(config, At(14, 17)));
    }

    [Fact]
    public void WindowSpanningMidnight()
    {
        var config = new AutopilotConfig { ActiveFrom = TimeSpan.FromHours(22), ActiveTo = TimeSpan.FromHours(2) };

        Assert.True(AutopilotPlanner.IsWithinSchedule(config, At(14, 23)));
        Assert.True(AutopilotPlanner.IsWithinSchedule(config, At(14, 1, 30)));
        Assert.False(AutopilotPlanner.IsWithinSchedule(config, At(14, 2)));
        Assert.False(AutopilotPlanner.IsWithinSchedule(config, At(14, 12)));
    }

    [Fact]
    public void ActiveDaysUseTheDayTheWindowStarted()
    {
        var allDayFriday = new AutopilotConfig { ActiveDays = [DayOfWeek.Friday] };
        Assert.True(AutopilotPlanner.IsWithinSchedule(allDayFriday, At(16, 12)));
        Assert.False(AutopilotPlanner.IsWithinSchedule(allDayFriday, At(17, 1)));

        var fridayNight = new AutopilotConfig { ActiveFrom = TimeSpan.FromHours(22), ActiveTo = TimeSpan.FromHours(2), ActiveDays = [DayOfWeek.Friday] };
        Assert.True(AutopilotPlanner.IsWithinSchedule(fridayNight, At(16, 23)));
        Assert.True(AutopilotPlanner.IsWithinSchedule(fridayNight, At(17, 1)));
        Assert.False(AutopilotPlanner.IsWithinSchedule(fridayNight, At(16, 1)));
        Assert.False(AutopilotPlanner.IsWithinSchedule(fridayNight, At(17, 23)));
    }

    [Fact]
    public void QuietHoursMaySpanMidnightAndIgnoreDays()
    {
        var config = new AutopilotConfig { QuietFrom = TimeSpan.FromHours(23), QuietTo = TimeSpan.FromHours(7), ActiveDays = [DayOfWeek.Monday] };

        Assert.True(AutopilotPlanner.IsQuietTime(config, At(14, 23)));
        Assert.True(AutopilotPlanner.IsQuietTime(config, At(15, 6, 59)));
        Assert.False(AutopilotPlanner.IsQuietTime(config, At(15, 7)));
        Assert.False(AutopilotPlanner.IsQuietTime(config, At(14, 22, 59)));

        var afternoon = new AutopilotConfig { QuietFrom = TimeSpan.FromHours(13), QuietTo = TimeSpan.FromHours(15) };
        Assert.True(AutopilotPlanner.IsQuietTime(afternoon, At(14, 14)));
        Assert.False(AutopilotPlanner.IsQuietTime(afternoon, At(14, 15)));
    }

    [Fact]
    public void HoursPerDayAveragesTheWeekAndAppliesTheCap()
    {
        Assert.Equal(24, AutopilotPlanner.HoursPerDay(new AutopilotConfig()));
        Assert.Equal(8, AutopilotPlanner.HoursPerDay(new AutopilotConfig { ActiveFrom = TimeSpan.FromHours(9), ActiveTo = TimeSpan.FromHours(17) }), 9);
        Assert.Equal(4, AutopilotPlanner.HoursPerDay(new AutopilotConfig { ActiveFrom = TimeSpan.FromHours(22), ActiveTo = TimeSpan.FromHours(2) }), 9);
        Assert.Equal(24.0 * 5 / 7, AutopilotPlanner.HoursPerDay(new AutopilotConfig { ActiveDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] }), 9);
        Assert.Equal(3, AutopilotPlanner.HoursPerDay(new AutopilotConfig { MaxHoursPerGamePerDay = 3 }), 9);
        Assert.Equal(8, AutopilotPlanner.HoursPerDay(new AutopilotConfig { ActiveFrom = TimeSpan.FromHours(9), ActiveTo = TimeSpan.FromHours(17), MaxHoursPerGamePerDay = 12 }), 9);

        // The cap is per active day: 2 h on one day a week.
        Assert.Equal(2.0 / 7, AutopilotPlanner.HoursPerDay(new AutopilotConfig { ActiveDays = [DayOfWeek.Sunday], MaxHoursPerGamePerDay = 2 }), 9);
    }
}

public sealed class AutopilotTargetTests
{
    [Fact]
    public void FinishesPerTargetMode()
    {
        var state = new AutopilotGameState { AutopilotHours = 4 };
        var until = new AutopilotProfile { TargetMode = AutopilotTargetMode.UntilTotalHours, TargetHours = 10 };
        var add = new AutopilotProfile { TargetMode = AutopilotTargetMode.AddHours, TargetHours = 5 };
        var unlimited = new AutopilotProfile { TargetMode = AutopilotTargetMode.Unlimited, TargetHours = 0 };

        Assert.False(AutopilotPlanner.IsFinished(until, state, 9.9, 3));
        Assert.True(AutopilotPlanner.IsFinished(until, state, 10, 3));
        Assert.False(AutopilotPlanner.IsFinished(add, state, 100, 3));
        state.AutopilotHours = 5;
        Assert.True(AutopilotPlanner.IsFinished(add, state, 0, 3));
        Assert.False(AutopilotPlanner.IsFinished(unlimited, state, 1e6, 3));
    }

    [Fact]
    public void FinishesWhenNothingIsLeftOnlyIfStopWhenAllUnlocked()
    {
        var state = new AutopilotGameState();
        var stop = new AutopilotProfile { TargetMode = AutopilotTargetMode.Unlimited, StopWhenAllUnlocked = true };
        var keepGoing = new AutopilotProfile { TargetMode = AutopilotTargetMode.Unlimited, StopWhenAllUnlocked = false };

        Assert.True(AutopilotPlanner.IsFinished(stop, state, 0, 0));
        Assert.False(AutopilotPlanner.IsFinished(stop, state, 0, 1));
        Assert.False(AutopilotPlanner.IsFinished(keepGoing, state, 0, 0));
    }

    [Fact]
    public void LinkedStatTargetNeedsStatAndMax()
    {
        var profile = new AutopilotProfile { UpdateLinkedStats = true };

        Assert.Equal(25, AutopilotPlanner.LinkedStatTarget(profile, AutopilotTestData.A("A", stat: "kills", max: 25)));
        Assert.Null(AutopilotPlanner.LinkedStatTarget(profile, AutopilotTestData.A("A", stat: "kills")));
        Assert.Null(AutopilotPlanner.LinkedStatTarget(profile, AutopilotTestData.A("A", stat: "", max: 25)));
        Assert.Null(AutopilotPlanner.LinkedStatTarget(profile, AutopilotTestData.A("A", max: 25)));
        Assert.Null(AutopilotPlanner.LinkedStatTarget(new AutopilotProfile { UpdateLinkedStats = false }, AutopilotTestData.A("A", stat: "kills", max: 25)));
    }
}

public sealed class AutopilotResolveProfileTests
{
    [Fact]
    public void OverrideProfileThenActiveThenNatural()
    {
        var custom = new AutopilotProfile { Name = "Mine", JitterPercent = 3 };
        var config = new AutopilotConfig { ActiveProfile = "Fast", CustomProfiles = [custom] };
        config.Overrides[7] = new AutopilotGameOverride { AppId = 7, ProfileName = "mine" };
        config.Overrides[8] = new AutopilotGameOverride { AppId = 8, ProfileName = "Deleted" };

        Assert.Equal("Mine", AutopilotPlanner.ResolveProfile(config, 7).Name);
        Assert.Equal("Fast", AutopilotPlanner.ResolveProfile(config, 8).Name);
        Assert.Equal("Fast", AutopilotPlanner.ResolveProfile(config, 9).Name);

        config.ActiveProfile = "Gone";
        Assert.Equal("Natural", AutopilotPlanner.ResolveProfile(config, 9).Name);
    }

    [Fact]
    public void AppliesOverrideFieldsToACopy()
    {
        var custom = new AutopilotProfile { Name = "Mine", TargetMode = AutopilotTargetMode.AddHours, TargetHours = 10, IncludeRare = false };
        var config = new AutopilotConfig { ActiveProfile = "Mine", CustomProfiles = [custom] };
        config.Overrides[7] = new AutopilotGameOverride { AppId = 7, TargetMode = AutopilotTargetMode.UntilTotalHours, TargetHours = 50, IncludeRare = true };
        config.Overrides[8] = new AutopilotGameOverride { AppId = 8, TargetHours = 2 };

        var resolved = AutopilotPlanner.ResolveProfile(config, 7);
        Assert.Equal(AutopilotTargetMode.UntilTotalHours, resolved.TargetMode);
        Assert.Equal(50, resolved.TargetHours);
        Assert.True(resolved.IncludeRare);
        Assert.NotSame(custom, resolved);

        var partial = AutopilotPlanner.ResolveProfile(config, 8);
        Assert.Equal(AutopilotTargetMode.AddHours, partial.TargetMode);
        Assert.Equal(2, partial.TargetHours);
        Assert.False(partial.IncludeRare);

        // The stored profile is untouched.
        Assert.Equal(10, custom.TargetHours);
        Assert.False(custom.IncludeRare);
    }
}

public sealed class AutopilotPreviewTests
{
    private static readonly DateTimeOffset Now = AutopilotTestData.Noon;

    private static AutopilotPlannedUnlock Row(IReadOnlyList<AutopilotPlannedUnlock> plan, string id) => plan.Single(p => p.AchievementId == id);

    [Fact]
    public void OrdersByPlaytimeAndEstimatesAt24HoursPerDay()
    {
        var config = AutopilotTestData.ConfigWith(AutopilotTestData.Plain());
        var achievements = new[]
        {
            AutopilotTestData.A("Rare", 10, 0),
            AutopilotTestData.A("Common", 98, 1),
            AutopilotTestData.A("Done", 99, 2, unlocked: true),
            AutopilotTestData.A("Half", 50, 3),
            AutopilotTestData.A("Locked", 50, 4, isProtected: true),
        };

        var plan = AutopilotPlanner.Preview(42, config, achievements, null, 0, Now);

        Assert.Equal(["Common", "Half", "Rare", "Done", "Locked"], plan.Select(p => p.AchievementId));
        Assert.All(plan, p => Assert.Equal(42u, p.AppId));
        Assert.Equal(1, plan[0].AtPlaytimeHours, 9);
        Assert.Equal(Now.AddHours(1), plan[0].EstimatedAt);
        Assert.Equal(Now.AddHours(5), plan[1].EstimatedAt);
        Assert.Equal(Now.AddHours(20), plan[2].EstimatedAt);
        Assert.Equal("Name Common", plan[0].Name);
        Assert.Equal(98, plan[0].GlobalPercent);

        Assert.Equal(new AutopilotPlannedUnlock(42, "Done", "Name Done", 99, 0, null, AutopilotSkipReason.AlreadyUnlocked), plan[3]);
        Assert.Equal(AutopilotSkipReason.Protected, plan[4].Skip);
        Assert.Null(plan[4].EstimatedAt);
    }

    [Fact]
    public void StartsFromCurrentPlaytimeAndExistingSeeds()
    {
        var profile = AutopilotTestData.Plain();
        profile.JitterPercent = 20;
        var config = AutopilotTestData.ConfigWith(profile);
        var achievements = new[] { AutopilotTestData.A("Common", 98, 0), AutopilotTestData.A("Half", 50, 1) };
        var state = new AutopilotGameState { JitterSeeds = { ["Half"] = 1.0 } };

        var plan = AutopilotPlanner.Preview(1, config, achievements, state, 3, Now);

        // Common (1 h) is already due; Half needs 5 h × 1.2.
        Assert.Equal(3, Row(plan, "Common").AtPlaytimeHours, 9);
        Assert.Equal(Now, Row(plan, "Common").EstimatedAt);
        Assert.Equal(6, Row(plan, "Half").AtPlaytimeHours, 9);
        Assert.Equal(Now.AddHours(3), Row(plan, "Half").EstimatedAt);
        Assert.Single(state.JitterSeeds);
    }

    [Fact]
    public void SpreadsRunningTimeOverTheSchedule()
    {
        var config = AutopilotTestData.ConfigWith(AutopilotTestData.Plain((50, 6)));
        config.ActiveFrom = TimeSpan.FromHours(8);
        config.ActiveTo = TimeSpan.FromHours(20);

        var plan = AutopilotPlanner.Preview(1, config, [AutopilotTestData.A("A")], null, 0, Now);

        // 12 h a day: 6 h of running time is half a day.
        Assert.Equal(Now.AddHours(12), plan[0].EstimatedAt);
    }

    [Fact]
    public void MinMinutesBetweenPushesLater()
    {
        var profile = AutopilotTestData.Plain((50, 1));
        profile.MinMinutesBetweenUnlocks = 30;
        var config = AutopilotTestData.ConfigWith(profile);
        var achievements = Enumerable.Range(0, 3).Select(i => AutopilotTestData.A("A" + i, 50, i)).ToList();
        var state = new AutopilotGameState { LastUnlockAt = Now.AddMinutes(-10) };

        var plan = AutopilotPlanner.Preview(1, config, achievements, state, 0.9, Now);

        // First is due at 0.1 h from now (6 min) but must wait until 20 min after now.
        Assert.Equal([Now.AddMinutes(20), Now.AddMinutes(50), Now.AddMinutes(80)], plan.Select(p => p.EstimatedAt!.Value));
        Assert.Equal(new[] { 0.9 + (20.0 / 60), 0.9 + (50.0 / 60), 0.9 + (80.0 / 60) }.Select(h => Math.Round(h, 9)), plan.Select(p => Math.Round(p.AtPlaytimeHours, 9)));
    }

    [Fact]
    public void HourlyLimitPushesLater()
    {
        var profile = AutopilotTestData.Plain((50, 1));
        profile.MaxUnlocksPerHour = 2;
        var config = AutopilotTestData.ConfigWith(profile);
        var achievements = Enumerable.Range(0, 5).Select(i => AutopilotTestData.A("A" + i, 50, i)).ToList();
        var state = new AutopilotGameState { RecentUnlocks = [Now.AddMinutes(-30)] };

        // All are due now; one unlock 30 minutes ago still counts for the first half hour.
        var plan = AutopilotPlanner.Preview(1, config, achievements, state, 1, Now);

        Assert.Equal(
            [Now, Now.AddHours(0.5), Now.AddHours(1), Now.AddHours(1.5), Now.AddHours(2)],
            plan.Select(p => p.EstimatedAt!.Value));
    }

    [Fact]
    public void DailyLimitPushesToTheNextDay()
    {
        var profile = AutopilotTestData.Plain((50, 1));
        profile.MaxUnlocksPerDay = 2;
        var config = AutopilotTestData.ConfigWith(profile);
        var achievements = Enumerable.Range(0, 4).Select(i => AutopilotTestData.A("A" + i, 50, i)).ToList();
        var state = new AutopilotGameState { Day = DateOnly.FromDateTime(Now.DateTime), UnlocksToday = 1 };

        var plan = AutopilotPlanner.Preview(1, config, achievements, state, 0, Now);

        var thursday = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal([Now.AddHours(1), thursday, thursday, thursday.AddDays(1)], plan.Select(p => p.EstimatedAt!.Value));
        Assert.Equal(12, plan[1].AtPlaytimeHours, 9);

        // Counts from another day don't apply.
        state.Day = DateOnly.FromDateTime(Now.DateTime).AddDays(-1);
        var fresh = AutopilotPlanner.Preview(1, config, achievements, state, 0, Now);
        Assert.Equal([Now.AddHours(1), Now.AddHours(1), thursday, thursday], fresh.Select(p => p.EstimatedAt!.Value));
    }

    [Fact]
    public void QuietHoursPushOutOfTheWindow()
    {
        var config = AutopilotTestData.ConfigWith(AutopilotTestData.Plain((98, 1), (1, 11)));
        config.QuietFrom = TimeSpan.FromHours(13);
        config.QuietTo = TimeSpan.FromHours(15);
        var achievements = new[] { AutopilotTestData.A("Soon", 98, 0), AutopilotTestData.A("Night", 1, 1) };

        var plan = AutopilotPlanner.Preview(1, config, achievements, null, 0, Now);
        Assert.Equal(Now.AddHours(3), Row(plan, "Soon").EstimatedAt);
        Assert.Equal(3, Row(plan, "Soon").AtPlaytimeHours, 9);
        Assert.Equal(Now.AddHours(11), Row(plan, "Night").EstimatedAt);

        config.QuietFrom = TimeSpan.FromHours(22);
        config.QuietTo = TimeSpan.FromHours(6);
        plan = AutopilotPlanner.Preview(1, config, achievements, null, 0, Now);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 6, 0, 0, TimeSpan.Zero), Row(plan, "Night").EstimatedAt);
        Assert.Equal(18, Row(plan, "Night").AtPlaytimeHours, 9);
    }

    [Theory]
    [InlineData(AutopilotTargetMode.AddHours, 3)]
    [InlineData(AutopilotTargetMode.UntilTotalHours, 12)]
    public void StopsEstimatingAtTheTimeTarget(AutopilotTargetMode mode, double targetHours)
    {
        var profile = AutopilotTestData.Plain((98, 11), (1, 15));
        profile.TargetMode = mode;
        profile.TargetHours = targetHours;
        var config = AutopilotTestData.ConfigWith(profile);
        var achievements = new[] { AutopilotTestData.A("Within", 98, 0), AutopilotTestData.A("Beyond", 1, 1) };
        var state = new AutopilotGameState { AutopilotHours = 1 };

        // Playtime limit is 12 h either way (10 + 3 − 1 or 12).
        var plan = AutopilotPlanner.Preview(1, config, achievements, state, 10, Now);

        Assert.Equal(Now.AddHours(1), Row(plan, "Within").EstimatedAt);
        var beyond = Row(plan, "Beyond");
        Assert.Equal(AutopilotSkipReason.None, beyond.Skip);
        Assert.Equal(15, beyond.AtPlaytimeHours, 9);
        Assert.Null(beyond.EstimatedAt);
    }

    [Fact]
    public void IntervalModeStepsByTheInterval()
    {
        var profile = AutopilotTestData.Plain();
        profile.Pacing = AutopilotPacingMode.Interval;
        profile.IntervalMinutes = 60;
        profile.JitterPercent = 20;
        var config = AutopilotTestData.ConfigWith(profile);
        var achievements = new[] { AutopilotTestData.A("Low", 2, 0), AutopilotTestData.A("High", 90, 1), AutopilotTestData.A("Mid", 40, 2) };

        var plan = AutopilotPlanner.Preview(1, config, achievements, null, 7, Now);
        Assert.Equal(["High", "Mid", "Low"], plan.Select(p => p.AchievementId));
        Assert.Equal([Now.AddHours(1), Now.AddHours(2), Now.AddHours(3)], plan.Select(p => p.EstimatedAt!.Value));
        Assert.Equal([8.0, 9, 10], plan.Select(p => Math.Round(p.AtPlaytimeHours, 9)));

        var state = new AutopilotGameState { AutopilotHours = 2, NextIntervalAtHours = 2.5 };
        plan = AutopilotPlanner.Preview(1, config, achievements, state, 7, Now);
        Assert.Equal([Now.AddHours(0.5), Now.AddHours(1.5), Now.AddHours(2.5)], plan.Select(p => p.EstimatedAt!.Value));
        Assert.Equal(2.5, state.NextIntervalAtHours);
    }

    [Fact]
    public void UsesTheGameOverride()
    {
        var config = AutopilotTestData.ConfigWith(AutopilotTestData.Plain());
        config.Overrides[5] = new AutopilotGameOverride { AppId = 5, ExcludedAchievements = ["B"], IncludeRare = false };
        var achievements = new[] { AutopilotTestData.A("A", 50, 0), AutopilotTestData.A("B", 50, 1), AutopilotTestData.A("R", 1, 2) };

        var plan = AutopilotPlanner.Preview(5, config, achievements, null, 0, Now);

        Assert.Equal(["A", "B", "R"], plan.Select(p => p.AchievementId));
        Assert.Equal([AutopilotSkipReason.None, AutopilotSkipReason.Excluded, AutopilotSkipReason.Rare], plan.Select(p => p.Skip));
    }

    [Fact]
    public void IsFastForLargeGames()
    {
        var profile = new AutopilotProfile { Name = "Big", IncludeRare = true, TargetMode = AutopilotTargetMode.Unlimited };
        var config = AutopilotTestData.ConfigWith(profile);
        config.QuietFrom = TimeSpan.FromHours(23);
        config.QuietTo = TimeSpan.FromHours(7);
        var random = new Random(9);
        var achievements = Enumerable.Range(0, 500).Select(i => AutopilotTestData.A("A" + i, random.NextDouble() * 100, i, unlocked: i % 10 == 0)).ToList();

        AutopilotPlanner.Preview(1, config, achievements, null, 0, Now);
        var watch = Stopwatch.StartNew();
        var plan = AutopilotPlanner.Preview(1, config, achievements, null, 0, Now);
        watch.Stop();

        Assert.Equal(500, plan.Count);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 250);

        // Estimates never go backwards and respect the default limits (10/day, 2/h, 10 min apart).
        var times = plan.Where(p => p.EstimatedAt != null).Select(p => p.EstimatedAt!.Value).ToList();
        Assert.Equal(450, times.Count);
        Assert.Equal(times.Order(), times);
        Assert.All(times.GroupBy(t => t.Date), g => Assert.InRange(g.Count(), 1, 10));
        Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second - p.First >= TimeSpan.FromMinutes(10)));
        Assert.All(times.Zip(times.Skip(2)), p => Assert.True(p.Second - p.First >= TimeSpan.FromHours(1)));
        Assert.All(times, t => Assert.False(AutopilotPlanner.IsQuietTime(config, t)));
    }
}
