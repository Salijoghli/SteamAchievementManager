using System.Globalization;
using SAM.App.Models;
using SAM.App.ViewModels;

namespace SAM.App.Tests;

public sealed class GameAchievementProgressTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static AchievementDefinition Counter(double? max, double? min = 0, string? stat = "KILLS") => new()
    {
        Id = "ACH_KILLS",
        Name = "Killer",
        ProgressStatId = stat,
        ProgressMin = min,
        ProgressMax = max,
    };

    [Fact]
    public void FormatsIntegerProgressWithDigitGrouping()
    {
        var progress = GameAchievementProgress.Compute(Counter(1000), 340, isIntegerStat: true, English);

        Assert.NotNull(progress);
        Assert.Equal("340 / 1,000", progress.Text);
        Assert.Equal("Progress: 340 of 1,000", progress.AccessibleText);
        Assert.Equal(34, progress.Percent, 6);
    }

    [Fact]
    public void ClampsToTheTarget()
    {
        var progress = GameAchievementProgress.Compute(Counter(50), 75, isIntegerStat: true, English)!;

        Assert.Equal("50 / 50", progress.Text);
        Assert.Equal(100, progress.Percent);
    }

    [Fact]
    public void ClampsBelowZeroAndMeasuresFromTheMinimum()
    {
        Assert.Equal(0, GameAchievementProgress.Compute(Counter(100), -5, isIntegerStat: true, English)!.Percent);

        var fromMin = GameAchievementProgress.Compute(Counter(200, min: 100), 150, isIntegerStat: true, English)!;
        Assert.Equal(50, fromMin.Percent, 6);
        Assert.Equal("150 / 200", fromMin.Text);
    }

    [Fact]
    public void RoundsFloatStatsSensiblyAndNeverUp()
    {
        // Large targets: whole numbers, rounded down (999.7 must not read as done).
        Assert.Equal("999 / 1,000", GameAchievementProgress.Compute(Counter(1000), 999.7, false, English)!.Text);

        // Small targets: one decimal; tiny ones: two.
        Assert.Equal("2.5 / 10", GameAchievementProgress.Compute(Counter(10), 2.56, false, English)!.Text);
        Assert.Equal("0.30 / 0.50", GameAchievementProgress.Compute(Counter(0.5), (float)0.3, false, English)!.Text);

        // Whole float values stay whole.
        Assert.Equal("3 / 10", GameAchievementProgress.Compute(Counter(10), 3, false, English)!.Text);
    }

    [Fact]
    public void UsesTheCulturesNumberFormat()
    {
        var georgian = CultureInfo.GetCultureInfo("ka-GE");
        var text = GameAchievementProgress.Compute(Counter(10000), 1234, true, georgian)!.Text;

        Assert.Equal($"{1234.ToString("N0", georgian)} / {10000.ToString("N0", georgian)}", text);
    }

    [Fact]
    public void NothingWithoutAUsableProgressBlockOrStatValue()
    {
        Assert.Null(GameAchievementProgress.Compute(Counter(1000), null, true, English));
        Assert.Null(GameAchievementProgress.Compute(Counter(null), 5, true, English));
        Assert.Null(GameAchievementProgress.Compute(Counter(1000, stat: null), 5, true, English));
        Assert.Null(GameAchievementProgress.Compute(Counter(0), 5, true, English));
        Assert.Null(GameAchievementProgress.Compute(Counter(10, min: 10), 5, true, English));
        Assert.Null(GameAchievementProgress.Compute(Counter(10), double.NaN, false, English));
    }

    [Fact]
    public void NothingForAnIntegerDoneFlag()
    {
        Assert.Null(GameAchievementProgress.Compute(Counter(1), 0, isIntegerStat: true, English));
        Assert.Null(GameAchievementProgress.Compute(Counter(6, min: 5), 5, isIntegerStat: true, English));
        Assert.NotNull(GameAchievementProgress.Compute(Counter(2), 1, isIntegerStat: true, English));
    }

    [Fact]
    public void MissingMinimumCountsFromZero()
    {
        var progress = GameAchievementProgress.Compute(Counter(10, min: null), 5, true, English)!;
        Assert.Equal(50, progress.Percent, 6);
    }
}
