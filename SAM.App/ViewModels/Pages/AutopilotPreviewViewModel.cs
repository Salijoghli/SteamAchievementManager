// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using SAM.App.Models;
using SAM.App.Services;
using SAM.App.Services.Autopilot;

namespace SAM.App.ViewModels.Pages;

/// <summary>One row of the dry-run table: when an achievement will unlock, or why it won't.</summary>
public sealed class AutopilotPreviewRow(string gameName, AutopilotPlannedUnlock plan, bool showGame)
{
    public string GameName { get; } = gameName;

    /// <summary>The game column's width: shown only when several games are merged.</summary>
    public GridLength GameWidth { get; } = AutopilotPreviewViewModel.GameColumnWidth(showGame);

    public string Name { get; } = plan.Name;

    public string PercentText { get; } = AutopilotFormat.Percent(plan.GlobalPercent);

    public bool IsSkipped { get; } = plan.Skip != AutopilotSkipReason.None;

    public bool IsPlanned => this.IsSkipped == false;

    /// <summary>"at 5.5 h of playtime", or the localized skip reason.</summary>
    public string PlannedText { get; } = plan.Skip != AutopilotSkipReason.None
        ? AutopilotFormat.SkipReasonText(plan.Skip)
        : Strings.Format("Autopilot_PreviewAtPlaytime", AutopilotFormat.Hours(plan.AtPlaytimeHours));

    public string EstimateText { get; } = plan.Skip != AutopilotSkipReason.None ? "" : AutopilotFormat.DateTime(plan.EstimatedAt);

    public double Opacity => this.IsSkipped ? 0.6 : 1;

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => string.Join(", ", new[] { this.GameName, this.Name, this.PercentText, this.PlannedText, this.EstimateText }.Where(s => s.Length > 0));
}

/// <summary>
/// The Autopilot dry run for one or more games under a (possibly unsaved) configuration. Games
/// are planned one at a time and memoized; "All games" merges them.
/// </summary>
public sealed partial class AutopilotPreviewViewModel : ObservableObject
{
    /// <summary>"Preview all" plans at most this many games.</summary>
    public const int MaxGames = 100;

    private readonly AppServices _services;
    private readonly AutopilotConfig _config;
    private readonly IReadOnlyList<(uint AppId, string Name)> _games;
    private readonly Dictionary<uint, IReadOnlyList<AutopilotPlannedUnlock>> _plans = [];
    private readonly Dictionary<uint, string> _errors = [];
    private readonly CancellationTokenSource _cts = new();

    public AutopilotPreviewViewModel(AppServices services, IReadOnlyList<(uint AppId, string Name)> games, AutopilotConfig config)
    {
        this._services = services;
        this._config = config;
        this._games = games.Take(MaxGames).ToList();
        this.IsTruncated = games.Count > MaxGames;
        this.GameOptions = this._games.Count > 1
            ? [Strings.Plural("Autopilot_PreviewAllGames", this._games.Count), .. this._games.Select(g => g.Name)]
            : this._games.Select(g => g.Name).ToList();
    }

    public IReadOnlyList<string> GameOptions { get; }

    public bool HasGameChoice => this._games.Count > 1;

    public bool IsTruncated { get; }

    public string TruncatedText => Strings.Format("Autopilot_PreviewTruncated", MaxGames);

    public bool HasNoGames => this._games.Count == 0;

    /// <summary>The game column shows only when several games are merged.</summary>
    public bool ShowGameColumn => this.HasGameChoice && this.SelectedGameIndex == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGameColumn))]
    private int _selectedGameIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTableVisible))]
    private bool _isLoading;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsTableVisible))]
    private string? _error;

    public bool HasError => this.Error != null;

    public bool IsTableVisible => this.IsLoading == false && this.Error == null && this.HasNoGames == false;

    [ObservableProperty]
    private IReadOnlyList<AutopilotPreviewRow> _rows = [];

    [ObservableProperty]
    private string _totalsText = "";

    /// <summary>Planning problems for some of the merged games (the rest still show).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    public bool HasWarnings => this.Warnings != null;

    partial void OnSelectedGameIndexChanged(int value) => _ = this.ShowAsync();

    public void Cancel() => this._cts.Cancel();

    public static GridLength GameColumnWidth(bool show) => show ? new GridLength(160) : new GridLength(0);

    /// <summary>Plans the selected game (or all of them) and fills the table.</summary>
    public async Task ShowAsync()
    {
        var index = this.SelectedGameIndex;
        var selected = this.HasGameChoice
            ? index == 0 ? this._games : [this._games[Math.Clamp(index - 1, 0, this._games.Count - 1)]]
            : this._games;
        if (selected.Count == 0)
        {
            return;
        }

        this.IsLoading = true;
        this.Error = null;
        try
        {
            var done = 0;
            foreach (var (appId, _) in selected)
            {
                this.ProgressText = selected.Count > 1 ? Strings.Format("Autopilot_PreviewProgress", done + 1, selected.Count) : "";
                if (this._plans.ContainsKey(appId) == false && this._errors.ContainsKey(appId) == false)
                {
                    try
                    {
                        this._plans[appId] = await this._services.Autopilot.PreviewAsync(appId, this._config, this._cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        Diagnostics.Log($"Autopilot: couldn't preview {appId}", e);
                        this._errors[appId] = AutopilotFormat.ErrorMessage(e);
                    }
                }

                done++;
            }

            if (index != this.SelectedGameIndex)
            {
                return; // the user picked another game meanwhile
            }

            var failed = selected.Where(g => this._errors.ContainsKey(g.AppId)).ToList();
            if (failed.Count == selected.Count)
            {
                this.Error = this._errors[failed[0].AppId];
                this.Rows = [];
                this.TotalsText = "";
                this.Warnings = null;
                return;
            }

            this.Warnings = failed.Count == 0 ? null : Strings.Plural("Autopilot_PreviewSomeFailed", failed.Count, this._errors[failed[0].AppId]);

            var plans = selected
                .Where(g => this._plans.ContainsKey(g.AppId))
                .SelectMany(g => this._plans[g.AppId].Select(p => (g.Name, Plan: p)))
                .ToList();
            this.Rows = plans
                .OrderBy(p => p.Plan.Skip == AutopilotSkipReason.None ? 0 : 1)
                .ThenBy(p => p.Plan.EstimatedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(p => p.Plan.AtPlaytimeHours)
                .ThenBy(p => p.Plan.Skip)
                .Select(p => new AutopilotPreviewRow(p.Name, p.Plan, selected.Count > 1))
                .ToList();
            this.TotalsText = Totals(plans.Select(p => p.Plan).ToList());
        }
        finally
        {
            if (index == this.SelectedGameIndex)
            {
                this.IsLoading = false;
                this.ProgressText = "";
            }
        }
    }

    /// <summary>"12 will unlock over ~9 days; 5 skipped: 3 rare, 2 protected".</summary>
    internal static string Totals(IReadOnlyList<AutopilotPlannedUnlock> plans)
    {
        var planned = plans.Where(p => p.Skip == AutopilotSkipReason.None).ToList();
        var skipped = plans.Where(p => p.Skip != AutopilotSkipReason.None).ToList();

        string unlockPart;
        if (planned.Count == 0)
        {
            unlockPart = Strings.Get("Autopilot_PreviewNothingPlanned");
        }
        else if (planned.Max(p => p.EstimatedAt) is { } last)
        {
            var days = Math.Max(1, (long)Math.Ceiling((last - DateTimeOffset.Now).TotalDays));
            unlockPart = Strings.Plural("Autopilot_PreviewWillUnlockDays", planned.Count, Strings.Plural("Autopilot_Days", days));
        }
        else
        {
            unlockPart = Strings.Plural("Autopilot_PreviewWillUnlockHours", planned.Count, AutopilotFormat.Hours(planned.Max(p => p.AtPlaytimeHours)));
        }

        if (skipped.Count == 0)
        {
            return unlockPart;
        }

        var reasons = skipped
            .GroupBy(p => p.Skip)
            .OrderByDescending(g => g.Count())
            .Select(g => Strings.Format("Autopilot_PreviewReasonCount", g.Count(), Strings.Get("Autopilot_ReasonShort" + g.Key)));
        return Strings.Format("Autopilot_PreviewTotals", unlockPart, Strings.Plural("Autopilot_PreviewSkipped", skipped.Count), string.Join(", ", reasons));
    }
}
