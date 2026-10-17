// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SAM.App.Models;

namespace SAM.App.ViewModels.Pages;

/// <summary>One editable row of the playtime curve: achievements held by Percent% unlock after Hours hours.</summary>
public sealed partial class AutopilotCurvePointItem : ObservableObject
{
    private readonly AutopilotProfileEditor _owner;

    public AutopilotCurvePointItem(AutopilotProfileEditor owner, double percent, double hours)
    {
        this._owner = owner;
        this._percent = percent;
        this._hours = hours;
    }

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private double _hours;

    /// <summary>Set by the editor when this row is invalid (shown under the row).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => this.Error != null;

    public bool IsEditable => this._owner.IsEditable;

    public string RemoveLabel => Strings.Get("Autopilot_CurveRemovePoint");

    partial void OnPercentChanged(double oldValue, double newValue)
    {
        if (double.IsFinite(newValue) == false)
        {
            AutopilotProfileEditor.Restore(() => this.Percent = oldValue);
            return;
        }

        this._owner.OnCurveChanged();
    }

    partial void OnHoursChanged(double oldValue, double newValue)
    {
        if (double.IsFinite(newValue) == false)
        {
            AutopilotProfileEditor.Restore(() => this.Hours = oldValue);
            return;
        }

        this._owner.OnCurveChanged();
    }

    [RelayCommand]
    private void Remove() => this._owner.RemovePoint(this);
}

/// <summary>
/// Edits one <see cref="AutopilotProfile"/> in place (the page's edited copy of the config), with
/// inline validation. Built-in profiles are shown read-only.
/// </summary>
public sealed partial class AutopilotProfileEditor : ObservableObject
{
    /// <summary>Pacing choices, in <see cref="PacingIndex"/> order.</summary>
    private static readonly AutopilotPacingMode[] PacingModes = [AutopilotPacingMode.Playtime, AutopilotPacingMode.Interval];

    /// <summary>Order choices, in <see cref="OrderIndex"/> order.</summary>
    private static readonly AutopilotUnlockOrder[] Orders = [AutopilotUnlockOrder.MostCommonFirst, AutopilotUnlockOrder.SchemaOrder, AutopilotUnlockOrder.Random];

    /// <summary>Target choices, in <see cref="TargetModeIndex"/> order.</summary>
    internal static readonly AutopilotTargetMode[] TargetModes = [AutopilotTargetMode.AddHours, AutopilotTargetMode.UntilTotalHours, AutopilotTargetMode.Unlimited];

    private readonly AutopilotProfile _profile;
    private readonly Action _changed;
    private bool _syncing;

    public AutopilotProfileEditor(AutopilotProfile profile, Action changed)
    {
        this._profile = profile;
        this._changed = changed;
        foreach (var point in profile.Curve.OrderByDescending(p => p.Percent))
        {
            this.Points.Add(new AutopilotCurvePointItem(this, point.Percent, point.Hours));
        }

        this.Validate();
    }

    public static IReadOnlyList<string> PacingOptions { get; } =
        [Strings.Get("Autopilot_PacingPlaytime"), Strings.Get("Autopilot_PacingInterval")];

    public static IReadOnlyList<string> OrderOptions { get; } =
    [
        Strings.Get("Autopilot_OrderMostCommonFirst"),
        Strings.Get("Autopilot_OrderSchemaOrder"),
        Strings.Get("Autopilot_OrderRandom"),
    ];

    public static IReadOnlyList<string> TargetModeOptions { get; } =
    [
        Strings.Get("Autopilot_TargetAddHours"),
        Strings.Get("Autopilot_TargetUntilTotalHours"),
        Strings.Get("Autopilot_TargetUnlimited"),
    ];

    public AutopilotProfile Profile => this._profile;

    public string Name => this._profile.Name;

    public bool IsBuiltIn => this._profile.IsBuiltIn;

    public bool IsEditable => this._profile.IsBuiltIn == false;

    public ObservableCollection<AutopilotCurvePointItem> Points { get; } = [];

    /// <summary>The curve as the chart draws it (most common first).</summary>
    [ObservableProperty]
    private IReadOnlyList<AutopilotCurvePoint> _chartPoints = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurveError))]
    private string? _curveError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurveWarning))]
    private string? _curveWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRareWarning))]
    private string? _rareWarning;

    public bool HasCurveError => this.CurveError != null;

    public bool HasCurveWarning => this.CurveWarning != null;

    public bool HasRareWarning => this.RareWarning != null;

    /// <summary>Every problem that blocks saving, for the save bar.</summary>
    public IEnumerable<string> Errors => new[] { this.CurveError }.OfType<string>();

    #region What to unlock
    public double MinGlobalPercent
    {
        get => this._profile.MinGlobalPercent;
        set => this.Set(this._profile.MinGlobalPercent, Math.Clamp(value, 0, 100), (p, v) => p.MinGlobalPercent = v, value);
    }

    public bool IncludeRare
    {
        get => this._profile.IncludeRare;
        set => this.Set(this._profile.IncludeRare, value, (p, v) => p.IncludeRare = v);
    }

    public double RareThresholdPercent
    {
        get => this._profile.RareThresholdPercent;
        set => this.Set(this._profile.RareThresholdPercent, Math.Clamp(value, 0, 100), (p, v) => p.RareThresholdPercent = v, value);
    }

    public bool IncludeUnknownRarity
    {
        get => this._profile.IncludeUnknownRarity;
        set => this.Set(this._profile.IncludeUnknownRarity, value, (p, v) => p.IncludeUnknownRarity = v);
    }

    public double UnknownPercentAs
    {
        get => this._profile.UnknownPercentAs;
        set => this.Set(this._profile.UnknownPercentAs, Math.Clamp(value, 0.01, 100), (p, v) => p.UnknownPercentAs = v, value);
    }

    public bool SkipHidden
    {
        get => this._profile.SkipHidden;
        set => this.Set(this._profile.SkipHidden, value, (p, v) => p.SkipHidden = v);
    }

    public bool UpdateLinkedStats
    {
        get => this._profile.UpdateLinkedStats;
        set => this.Set(this._profile.UpdateLinkedStats, value, (p, v) => p.UpdateLinkedStats = v);
    }
    #endregion

    #region Pacing
    public int PacingIndex
    {
        get => Array.IndexOf(PacingModes, this._profile.Pacing);
        set
        {
            if (value >= 0 && value < PacingModes.Length &&
                this.Set(this._profile.Pacing, PacingModes[value], (p, v) => p.Pacing = v))
            {
                this.OnPropertyChanged(nameof(this.IsPlaytimePacing));
                this.OnPropertyChanged(nameof(this.IsIntervalPacing));
                this.Validate();
            }
        }
    }

    public bool IsPlaytimePacing => this._profile.Pacing == AutopilotPacingMode.Playtime;

    public bool IsIntervalPacing => this._profile.Pacing == AutopilotPacingMode.Interval;

    public double IntervalMinutes
    {
        get => this._profile.IntervalMinutes;
        set => this.Set(this._profile.IntervalMinutes, Math.Clamp(value, 1, 7 * 24 * 60), (p, v) => p.IntervalMinutes = v, value);
    }

    public double JitterPercent
    {
        get => this._profile.JitterPercent;
        set
        {
            if (this.Set(this._profile.JitterPercent, Math.Round(Math.Clamp(value, 0, 100)), (p, v) => p.JitterPercent = v, value))
            {
                this.OnPropertyChanged(nameof(this.JitterText));
            }
        }
    }

    public string JitterText => Strings.Format("Autopilot_JitterValue", this._profile.JitterPercent.ToString("0", CultureInfo.CurrentCulture));

    public int OrderIndex
    {
        get => Array.IndexOf(Orders, this._profile.Order);
        set
        {
            if (value >= 0 && value < Orders.Length)
            {
                this.Set(this._profile.Order, Orders[value], (p, v) => p.Order = v);
            }
        }
    }

    public double MinMinutesBetweenUnlocks
    {
        get => this._profile.MinMinutesBetweenUnlocks;
        set => this.Set(this._profile.MinMinutesBetweenUnlocks, Math.Clamp(value, 0, 24 * 60), (p, v) => p.MinMinutesBetweenUnlocks = v, value);
    }

    public double MaxUnlocksPerHour
    {
        get => this._profile.MaxUnlocksPerHour;
        set => this.Set(this._profile.MaxUnlocksPerHour, ToCount(value), (p, v) => p.MaxUnlocksPerHour = (int)v, value);
    }

    public double MaxUnlocksPerDay
    {
        get => this._profile.MaxUnlocksPerDay;
        set => this.Set(this._profile.MaxUnlocksPerDay, ToCount(value), (p, v) => p.MaxUnlocksPerDay = (int)v, value);
    }
    #endregion

    #region How long
    public int TargetModeIndex
    {
        get => Array.IndexOf(TargetModes, this._profile.TargetMode);
        set
        {
            if (value >= 0 && value < TargetModes.Length &&
                this.Set(this._profile.TargetMode, TargetModes[value], (p, v) => p.TargetMode = v))
            {
                this.OnPropertyChanged(nameof(this.HasTargetHours));
            }
        }
    }

    public bool HasTargetHours => this._profile.TargetMode != AutopilotTargetMode.Unlimited;

    public double TargetHours
    {
        get => this._profile.TargetHours;
        set => this.Set(this._profile.TargetHours, Math.Clamp(value, 0.1, 100_000), (p, v) => p.TargetHours = v, value);
    }

    public bool StopWhenAllUnlocked
    {
        get => this._profile.StopWhenAllUnlocked;
        set => this.Set(this._profile.StopWhenAllUnlocked, value, (p, v) => p.StopWhenAllUnlocked = v);
    }
    #endregion

    #region Curve
    [RelayCommand]
    private void AddPoint()
    {
        // A new, rarer point below the current rarest one, needing more time.
        var rarest = this.Points.Where(p => p.Percent > 0).Select(p => p.Percent).DefaultIfEmpty(100).Min();
        var longest = this.Points.Select(p => p.Hours).DefaultIfEmpty(1).Max();
        var percent = Math.Max(0.1, Math.Round(rarest / 2, rarest >= 2 ? 0 : 1));
        this.Points.Add(new AutopilotCurvePointItem(this, percent, Math.Round(Math.Max(1, longest * 1.5))));
        this.OnCurveChanged();
    }

    [RelayCommand]
    private void ResetCurve()
    {
        this.Points.Clear();
        foreach (var point in new AutopilotProfile().Curve)
        {
            this.Points.Add(new AutopilotCurvePointItem(this, point.Percent, point.Hours));
        }

        this.OnCurveChanged();
    }

    /// <summary>Sorts the rows most common first (after editing).</summary>
    [RelayCommand]
    private void SortCurve()
    {
        var sorted = this.Points.OrderByDescending(p => p.Percent).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var from = this.Points.IndexOf(sorted[i]);
            if (from != i)
            {
                this.Points.Move(from, i);
            }
        }
    }

    internal void RemovePoint(AutopilotCurvePointItem item)
    {
        this.Points.Remove(item);
        this.OnCurveChanged();
    }

    internal void OnCurveChanged()
    {
        if (this._syncing || this.IsBuiltIn)
        {
            return;
        }

        this._profile.Curve = this.Points.Select(p => new AutopilotCurvePoint { Percent = p.Percent, Hours = p.Hours }).ToList();
        this.Validate();
        this._changed();
    }
    #endregion

    /// <summary>Re-applies a value later, when a NumberBox was cleared (NaN) and the edit was rejected.</summary>
    internal static void Restore(Action restore)
    {
        DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => restore());
    }

    private static double ToCount(double value) => Math.Round(Math.Clamp(value, 0, 10_000));

    private void Validate()
    {
        this._syncing = true;
        try
        {
            string? error = null;
            foreach (var point in this.Points)
            {
                point.Error = point.Percent is <= 0 or > 100
                    ? Strings.Get("Autopilot_CurvePercentRange")
                    : point.Hours < 0 ? Strings.Get("Autopilot_CurveHoursRange") : null;
                error ??= point.Error;
            }

            if (this.IsPlaytimePacing && this.Points.Count == 0)
            {
                error = Strings.Get("Autopilot_CurveEmpty");
            }
            else if (this.Points.GroupBy(p => p.Percent).Any(g => g.Count() > 1))
            {
                error ??= Strings.Get("Autopilot_CurveDuplicate");
            }

            this.CurveError = this.IsPlaytimePacing ? error : null;

            // Rarer achievements normally need more time; a curve that goes the other way is allowed but flagged.
            var ordered = this.Points.Where(p => p.Error == null).OrderByDescending(p => p.Percent).ToList();
            var decreasing = ordered.Zip(ordered.Skip(1)).Any(pair => pair.Second.Hours < pair.First.Hours);
            this.CurveWarning = this.IsPlaytimePacing && error == null && decreasing ? Strings.Get("Autopilot_CurveNotIncreasing") : null;

            // Rare achievements are below the threshold; a higher minimum already rules them all out.
            this.RareWarning = this._profile.IncludeRare && this._profile.MinGlobalPercent >= this._profile.RareThresholdPercent
                ? Strings.Get("Autopilot_RareBelowMinimum")
                : null;

            this.ChartPoints = ordered
                .Where(p => p.Error == null)
                .Select(p => new AutopilotCurvePoint { Percent = p.Percent, Hours = p.Hours })
                .ToList();
        }
        finally
        {
            this._syncing = false;
        }
    }

    /// <summary>Applies a changed value to the profile, then notifies and marks the page dirty.</summary>
    private bool Set<T>(T current, T value, Action<AutopilotProfile, T> apply, double? raw = null, [CallerMemberName] string? name = null)
    {
        if (this.IsBuiltIn)
        {
            return false;
        }

        if (raw is { } r && double.IsFinite(r) == false)
        {
            // A cleared NumberBox: keep the old value and put it back in the box.
            Restore(() => this.OnPropertyChanged(name));
            return false;
        }

        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            if (raw is { } original && original.Equals(value) == false)
            {
                Restore(() => this.OnPropertyChanged(name)); // clamped: show the stored value
            }

            return false;
        }

        apply(this._profile, value);
        this.OnPropertyChanged(name);
        this.Validate();
        this._changed();
        return true;
    }
}
