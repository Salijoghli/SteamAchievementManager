using System.Globalization;
using SAM.App.ViewModels;

namespace SAM.App.Tests;

public sealed class GameTimelineTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Treats every time as already local, so the tests don't depend on the machine's time zone.</summary>
    private static DateTime AsLocal(DateTimeOffset time) => time.DateTime;

    private static DateTimeOffset At(int year, int month, int day, int hour = 12) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoUnlocksGiveNoPoints() =>
        Assert.Empty(GameTimeline.Build([], AsLocal, English));

    [Fact]
    public void ShortSpansAreDailyAndCumulativeFromZero()
    {
        var points = GameTimeline.Build([At(2024, 1, 3), At(2024, 1, 3, 18), At(2024, 1, 5)], AsLocal, English);

        // 2 Jan (zero), 3 Jan (+2), 4 Jan (no unlocks), 5 Jan (+1).
        Assert.Equal([0.0, 2, 2, 3], points.Select(p => p.Value));
        Assert.Equal("2 Jan 2024", points[0].Label);
        Assert.Equal("5 Jan 2024", points[^1].Label);
        Assert.Equal("3 Jan 2024: 2 unlocked in total (+2)", points[1].Description);
        Assert.Equal("4 Jan 2024: 2 unlocked in total", points[2].Description);
        Assert.Equal("3", points[^1].ValueText);
        Assert.NotNull(points[^1].AxisLabel); // The latest period is always labelled.
    }

    [Fact]
    public void ASingleUnlockStillRises()
    {
        var points = GameTimeline.Build([At(2024, 3, 1)], AsLocal, English);

        Assert.Equal([0.0, 1], points.Select(p => p.Value));
    }

    [Fact]
    public void LongerSpansAreMonthlyThenYearly()
    {
        Assert.Equal(GameTimelineUnit.Day, GameTimeline.ChooseUnit(new DateTime(2024, 1, 1), new DateTime(2024, 3, 1)));
        Assert.Equal(GameTimelineUnit.Month, GameTimeline.ChooseUnit(new DateTime(2020, 1, 1), new DateTime(2024, 3, 1)));
        Assert.Equal(GameTimelineUnit.Year, GameTimeline.ChooseUnit(new DateTime(2008, 1, 1), new DateTime(2024, 3, 1)));

        var monthly = GameTimeline.Build([At(2023, 9, 20), At(2024, 2, 10), At(2024, 2, 11)], AsLocal, English);

        // Aug (zero), Sep, Oct, Nov, Dec, Jan, Feb.
        Assert.Equal([0.0, 1, 1, 1, 1, 1, 3], monthly.Select(p => p.Value));
        Assert.Equal("February 2024", monthly[^1].Label);

        var yearly = GameTimeline.Build([At(2010, 5, 1), At(2024, 5, 1)], AsLocal, English);
        Assert.Equal(16, yearly.Count); // 2009 (zero) to 2024.
        Assert.Equal("2024", yearly[^1].Label);
        Assert.Equal(2, yearly[^1].Value);
    }

    [Fact]
    public void LabelsOnlyAFewPoints()
    {
        var times = Enumerable.Range(0, 90).Select(i => At(2024, 1, 1).AddDays(i));
        var points = GameTimeline.Build(times, AsLocal, English);

        Assert.InRange(points.Count(p => p.AxisLabel != null), 2, 8);
    }
}
