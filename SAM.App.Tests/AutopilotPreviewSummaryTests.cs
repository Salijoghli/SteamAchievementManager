using SAM.App.Models;
using SAM.App.ViewModels.Pages;

namespace SAM.App.Tests;

/// <summary>
/// The preview's summary and times (English strings).
/// </summary>
public sealed class AutopilotPreviewSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static AutopilotPlannedUnlock Planned(string id, double hours, DateTimeOffset? at) =>
        new(1, id, id, 20, hours, at, AutopilotSkipReason.None);

    [Fact]
    public void UnlocksPastTheRunTargetAreCountedSeparately()
    {
        var plans = new[]
        {
            Planned("A", 8, Now.AddHours(6)),
            Planned("B", 12, Now.AddHours(10)),
            Planned("C", 18, null), // past the time target: no date
        };

        Assert.Contains("(1 more after this run)", AutopilotPreviewViewModel.Totals(plans));
        Assert.DoesNotContain("after this run", AutopilotPreviewViewModel.Totals(plans[..2]));
    }

    [Fact]
    public void WithoutAnyDatesTheSummaryUsesPlaytime()
    {
        // The schedule allows no running time: nothing has a date, and nothing is "after the run".
        var plans = new[] { Planned("A", 8, null), Planned("B", 12, null) };

        var totals = AutopilotPreviewViewModel.Totals(plans);
        Assert.Contains("of playtime", totals);
        Assert.DoesNotContain("after this run", totals);
    }

    [Fact]
    public void TimesTodayAndTomorrowAreNamed()
    {
        var now = DateTimeOffset.Now;
        Assert.StartsWith("Today ", AutopilotPreviewViewModel.FriendlyTime(now, now));
        Assert.StartsWith("Tomorrow ", AutopilotPreviewViewModel.FriendlyTime(now.AddDays(1), now));
        Assert.DoesNotContain("Today", AutopilotPreviewViewModel.FriendlyTime(now.AddDays(3), now));
    }
}
