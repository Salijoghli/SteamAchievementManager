// [ObservableProperty] on fields: partial properties need C# 13 and the project uses C# 12;
// the app isn't AOT-compiled, so this WinRT AOT warning doesn't apply.
#pragma warning disable MVVMTK0045

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SAM.App.Controls.Charts;

namespace SAM.App.ViewModels;

/// <summary>The Timeline tab: this game's unlocks over time.</summary>
public sealed partial class GameViewModel
{
    /// <summary>Cumulative unlocks by period (see <see cref="GameTimeline"/>); empty when no unlock has a date.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline))]
    [NotifyPropertyChangedFor(nameof(HasNoTimeline))]
    private IReadOnlyList<ChartPoint> _timelinePoints = [];

    /// <summary>"19 of 70 unlocked".</summary>
    [ObservableProperty]
    private string _timelineCountText = "";

    /// <summary>"First unlock 3 Jan 2024 · latest 5 Mar 2025", or "" without dated unlocks.</summary>
    [ObservableProperty]
    private string _timelineRangeText = "";

    /// <summary>Why there's no chart: nothing unlocked, or unlocks without dates.</summary>
    [ObservableProperty]
    private string _timelineEmptyText = "";

    public bool HasTimeline => this.TimelinePoints.Count > 0;

    public bool HasNoTimeline => this.TimelinePoints.Count == 0;

    /// <summary>Rebuilds the timeline from the achievements' unlock times as Steam reported them.</summary>
    private void UpdateTimeline()
    {
        var unlocked = this._achievements.Where(a => a.IsAchieved).ToList();
        var times = unlocked.Where(a => a.UnlockTime.HasValue).Select(a => a.UnlockTime!.Value).ToList();

        this.TimelineCountText = Strings.Format(
            "Game_TimelineCount",
            unlocked.Count.ToString("N0", CultureInfo.CurrentCulture),
            this._achievements.Count.ToString("N0", CultureInfo.CurrentCulture));

        if (times.Count == 0)
        {
            this.TimelineRangeText = "";
            this.TimelineEmptyText = Strings.Get(unlocked.Count == 0 ? "Game_TimelineEmpty" : "Game_TimelineNoDates");
            this.TimelinePoints = [];
            return;
        }

        var first = times.Min().ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        var last = times.Max().ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        var range = Strings.Format("Game_TimelineRange", first, last);
        var undated = unlocked.Count - times.Count;
        this.TimelineRangeText = undated > 0 ? range + " · " + Strings.Plural("Game_TimelineUndated", undated) : range;
        this.TimelineEmptyText = "";
        this.TimelinePoints = GameTimeline.Build(times);
    }
}
