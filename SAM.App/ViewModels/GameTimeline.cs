using System.Globalization;
using SAM.App.Controls.Charts;

namespace SAM.App.ViewModels;

/// <summary>The period each point of a game's unlock timeline covers.</summary>
public enum GameTimelineUnit
{
    Day,
    Month,
    Year,
}

/// <summary>
/// Builds the cumulative "achievements unlocked over time" series for one game. The points are
/// evenly spaced periods (days, months or years, picked from the span) so the chart's x axis is
/// proportional to time; periods without unlocks repeat the running total. The series starts
/// one period before the first unlock at zero, so even a single day of unlocks shows a rise.
/// </summary>
public static class GameTimeline
{
    /// <summary>Spans up to this many days are shown day by day.</summary>
    internal const int MaxDays = 92;

    /// <summary>Spans up to this many months are shown month by month; longer ones by year.</summary>
    internal const int MaxMonths = 120;

    /// <summary>About how many x-axis labels to aim for (the chart drops ones that would collide).</summary>
    private const int TargetLabels = 6;

    /// <summary>The unit for a span from <paramref name="first"/> to <paramref name="last"/> (local dates).</summary>
    public static GameTimelineUnit ChooseUnit(DateTime first, DateTime last)
    {
        if ((last.Date - first.Date).TotalDays <= MaxDays)
        {
            return GameTimelineUnit.Day;
        }

        return MonthIndex(last) - MonthIndex(first) <= MaxMonths ? GameTimelineUnit.Month : GameTimelineUnit.Year;
    }

    /// <summary>
    /// The cumulative series for <paramref name="unlockTimes"/> (converted to local time with
    /// <paramref name="toLocal"/>, by default the machine's time zone). Empty when there are none.
    /// </summary>
    public static IReadOnlyList<ChartPoint> Build(
        IEnumerable<DateTimeOffset> unlockTimes,
        Func<DateTimeOffset, DateTime>? toLocal = null,
        CultureInfo? culture = null)
    {
        toLocal ??= time => time.ToLocalTime().DateTime;
        culture ??= CultureInfo.CurrentCulture;

        var dates = unlockTimes.Select(t => toLocal(t).Date).OrderBy(d => d).ToList();
        if (dates.Count == 0)
        {
            return [];
        }

        var unit = ChooseUnit(dates[0], dates[^1]);
        var start = Previous(Floor(dates[0], unit), unit);
        var end = Floor(dates[^1], unit);

        var periods = new List<DateTime>();
        for (var period = start; period <= end; period = Next(period, unit))
        {
            periods.Add(period);
        }

        var stride = Math.Max(1, (int)Math.Ceiling(periods.Count / (double)TargetLabels));
        var points = new List<ChartPoint>(periods.Count);
        var total = 0;
        var index = 0;
        for (var i = 0; i < periods.Count; i++)
        {
            var next = Next(periods[i], unit);
            var added = 0;
            while (index < dates.Count && dates[index] < next)
            {
                index++;
                added++;
            }

            total += added;
            var label = FullLabel(periods[i], unit, culture);
            var valueText = total.ToString("N0", culture);
            var description = added > 0
                ? Strings.Format("Game_TimelinePointNew", label, valueText, added.ToString("N0", culture))
                : Strings.Format("Game_TimelinePoint", label, valueText);

            // Labels on every stride-th period counted back from the last, so the latest is labelled.
            var axisLabel = (periods.Count - 1 - i) % stride == 0 ? AxisLabel(periods[i], unit, culture) : null;
            points.Add(new ChartPoint(axisLabel, total, description, label, valueText));
        }

        return points;
    }

    internal static DateTime Floor(DateTime date, GameTimelineUnit unit) => unit switch
    {
        GameTimelineUnit.Day => date.Date,
        GameTimelineUnit.Month => new DateTime(date.Year, date.Month, 1),
        _ => new DateTime(date.Year, 1, 1),
    };

    private static DateTime Next(DateTime period, GameTimelineUnit unit) => unit switch
    {
        GameTimelineUnit.Day => period.AddDays(1),
        GameTimelineUnit.Month => period.AddMonths(1),
        _ => period.AddYears(1),
    };

    private static DateTime Previous(DateTime period, GameTimelineUnit unit) => unit switch
    {
        GameTimelineUnit.Day => period.AddDays(-1),
        GameTimelineUnit.Month => period.AddMonths(-1),
        _ => period.AddYears(-1),
    };

    private static int MonthIndex(DateTime date) => (date.Year * 12) + date.Month - 1;

    private static string FullLabel(DateTime period, GameTimelineUnit unit, CultureInfo culture) => unit switch
    {
        GameTimelineUnit.Day => period.ToString("d MMM yyyy", culture),
        GameTimelineUnit.Month => period.ToString("MMMM yyyy", culture),
        _ => period.ToString("yyyy", culture),
    };

    private static string AxisLabel(DateTime period, GameTimelineUnit unit, CultureInfo culture) => unit switch
    {
        GameTimelineUnit.Day => period.ToString("d MMM", culture),
        GameTimelineUnit.Month => period.ToString("MMM yyyy", culture),
        _ => period.ToString("yyyy", culture),
    };
}
