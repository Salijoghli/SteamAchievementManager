// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.App.Models;
using SAM.App.Services;
using SAM.App.Services.Autopilot;

namespace SAM.App.ViewModels.Pages;

/// <summary>One achievement in the overrides checklist.</summary>
public sealed partial class AutopilotAchievementRow(string id, string name, double? percent, string statusText, int schemaIndex) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public double? Percent { get; } = percent;

    public string PercentText => AutopilotFormat.Percent(this.Percent);

    public string StatusText { get; } = statusText;

    public int SchemaIndex { get; } = schemaIndex;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => $"{this.Name}, {this.PercentText}, {this.StatusText}";
}

/// <summary>
/// Per-game settings that override the active profile: profile, time target, rare achievements,
/// and an achievement list that is either the only ones allowed or ones never to unlock.
/// </summary>
public sealed partial class AutopilotOverridesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly AutopilotGameOverride _value;
    private readonly IReadOnlyList<string> _profileNames;
    private List<AutopilotAchievementRow> _all = [];

    /// <summary>The checks of each list mode (1 = only, 2 = never), kept separately so switching modes never moves one list into the other.</summary>
    private readonly Dictionary<int, HashSet<string>> _checks = [];

    public AutopilotOverridesViewModel(AppServices services, uint appId, string gameName, AutopilotGameOverride value, AutopilotConfig config)
    {
        this._services = services;
        this._value = value;
        this.AppId = appId;
        this.GameName = gameName;

        IReadOnlyList<AutopilotProfile> profiles;
        try
        {
            profiles = AutopilotStore.AllProfiles(config);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't list the profiles", e);
            profiles = config.CustomProfiles;
        }

        this._profileNames = profiles.Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();
        this.ProfileOptions = [Strings.Format("Autopilot_OverrideUseActive", config.ActiveProfile), .. this._profileNames];
        this._profileIndex = value.ProfileName is { } name && this._profileNames.Contains(name) ? this._profileNames.ToList().IndexOf(name) + 1 : 0;

        this._targetIndex = value.TargetMode is { } mode ? Array.IndexOf(AutopilotProfileEditor.TargetModes, mode) + 1 : 0;
        this._targetHours = value.TargetHours ?? 10;
        this._rareIndex = value.IncludeRare switch { true => 1, false => 2, null => 0 };
        this._modeIndex = AutopilotPlanner.IsOnlyMode(value) ? 1 : value.ExcludedAchievements.Count > 0 ? 2 : 0;
        this._checks[1] = new HashSet<string>(value.OnlyAchievements, StringComparer.Ordinal);
        this._checks[2] = new HashSet<string>(value.ExcludedAchievements, StringComparer.Ordinal);
    }

    public uint AppId { get; }

    public string GameName { get; }

    public IReadOnlyList<string> ProfileOptions { get; }

    public static IReadOnlyList<string> TargetOptions { get; } =
        [Strings.Get("Autopilot_OverrideUseProfile"), .. AutopilotProfileEditor.TargetModeOptions];

    public static IReadOnlyList<string> RareOptions { get; } =
    [
        Strings.Get("Autopilot_OverrideUseProfile"),
        Strings.Get("Autopilot_OverrideRareInclude"),
        Strings.Get("Autopilot_OverrideRareSkip"),
    ];

    [ObservableProperty]
    private int _profileIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetHours))]
    private int _targetIndex;

    /// <summary>Only for an overridden target that has hours (not "Unlimited").</summary>
    public bool HasTargetHours => this.TargetIndex is 1 or 2;

    [ObservableProperty]
    private double _targetHours;

    [ObservableProperty]
    private int _rareIndex;

    /// <summary>0 = rules decide, 1 = only the checked ones, 2 = never the checked ones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListEnabled), nameof(ModeHint), nameof(IsOnlyListEmpty))]
    private int _modeIndex;

    public bool IsListEnabled => this.ModeIndex != 0;

    /// <summary>"Only these" with nothing checked: Autopilot will unlock nothing in this game (shown as a warning).</summary>
    public bool IsOnlyListEmpty => this.ModeIndex == 1 && this._checks[1].Count == 0;

    public string ModeHint => Strings.Get(this.ModeIndex switch
    {
        1 => "Autopilot_OverrideModeOnlyHint",
        2 => "Autopilot_OverrideModeExcludeHint",
        _ => "Autopilot_OverrideModeNoneHint",
    });

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsListVisible))]
    private string? _error;

    public bool HasError => this.Error != null;

    public bool IsListVisible => this.IsLoading == false && this.Error == null;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _countText = "";

    public ObservableCollection<AutopilotAchievementRow> Achievements { get; } = [];

    partial void OnTargetHoursChanged(double oldValue, double newValue)
    {
        if (double.IsFinite(newValue) == false)
        {
            AutopilotProfileEditor.Restore(() => this.TargetHours = oldValue);
        }
    }

    partial void OnSearchTextChanged(string value) => this.ApplyFilter();

    /// <summary>Shows the checks of the new mode's own list (none for "rules decide").</summary>
    partial void OnModeIndexChanged(int value)
    {
        foreach (var row in this._all)
        {
            row.IsChecked = this._checks.TryGetValue(value, out var checks) && checks.Contains(row.Id);
        }

        this.UpdateCount();
    }

    /// <summary>Loads the schema, unlocked state and global percentages.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var appId = this.AppId;
            var steam = this._services.Steam;
            var (schema, stats) = await Task.Run(() =>
            {
                var schema = this._services.Schemas.Load(appId);
                var stats = schema == null ? null : UserStatsCache.Load(ProgressService.GetStatsPath(steam.SteamInstallPath, steam.AccountId, appId));
                return (schema, stats);
            });

            if (schema == null || schema.Achievements.Count == 0)
            {
                this.Error = Strings.Get("Autopilot_OverrideNoSchema");
                return;
            }

            IReadOnlyDictionary<string, double>? percents = null;
            try
            {
                percents = await this._services.GlobalPercents.GetAsync(appId, CancellationToken.None);
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Autopilot: couldn't get global percentages for {appId}", e);
            }

            var chosen = this._checks.GetValueOrDefault(this.ModeIndex) ?? [];
            this._all = schema.Achievements.Select((a, index) =>
            {
                var unlocked = stats?.GetState(a).Unlocked == true;
                var status = unlocked ? "Autopilot_AchievementUnlocked"
                    : a.IsProtected ? "Autopilot_AchievementProtected"
                    : a.IsHidden ? "Autopilot_AchievementHidden"
                    : "Autopilot_AchievementLocked";
                double? percent = percents != null && percents.TryGetValue(a.Id, out var p) ? p : null;
                return new AutopilotAchievementRow(a.Id, a.Name, percent, Strings.Get(status), index) { IsChecked = chosen.Contains(a.Id) };
            })
            .OrderByDescending(r => r.Percent ?? -1)
            .ThenBy(r => r.SchemaIndex)
            .ToList();

            foreach (var row in this._all)
            {
                row.PropertyChanged += (_, _) => this.OnRowChanged(row);
            }

            this.ApplyFilter();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Autopilot: couldn't load the achievements of {this.AppId}", e);
            this.Error = Strings.Format("Autopilot_ErrorDetail", e.Message);
        }
        finally
        {
            this.IsLoading = false;
        }
    }

    [RelayCommand]
    private void CheckAll() => this.SetShown(true);

    [RelayCommand]
    private void CheckNone() => this.SetShown(false);

    private void SetShown(bool value)
    {
        foreach (var row in this.Achievements)
        {
            row.IsChecked = value;
        }
    }

    private void ApplyFilter()
    {
        var query = this.SearchText.Trim();
        this.Achievements.Clear();
        foreach (var row in this._all.Where(r => query.Length == 0 || r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            this.Achievements.Add(row);
        }

        this.UpdateCount();
    }

    /// <summary>A check changed: remember it in the current mode's list.</summary>
    private void OnRowChanged(AutopilotAchievementRow row)
    {
        if (this._checks.TryGetValue(this.ModeIndex, out var checks))
        {
            if (row.IsChecked)
            {
                checks.Add(row.Id);
            }
            else
            {
                checks.Remove(row.Id);
            }
        }

        this.UpdateCount();
    }

    private void UpdateCount()
    {
        this.CountText = Strings.Format("Autopilot_OverrideChecked", this._all.Count(r => r.IsChecked), this._all.Count);
        this.OnPropertyChanged(nameof(this.IsOnlyListEmpty));
    }

    /// <summary>Writes the choices back into the override being edited.</summary>
    public void Apply()
    {
        this._value.ProfileName = this.ProfileIndex > 0 && this.ProfileIndex <= this._profileNames.Count ? this._profileNames[this.ProfileIndex - 1] : null;
        this._value.TargetMode = this.TargetIndex > 0 ? AutopilotProfileEditor.TargetModes[this.TargetIndex - 1] : null;
        this._value.TargetHours = this.HasTargetHours ? Math.Max(0.1, this.TargetHours) : null;
        this._value.IncludeRare = this.RareIndex switch { 1 => true, 2 => false, _ => null };

        // Without a loaded list (no schema, or still loading) the checks are the stored ones, so
        // this keeps them; only the list of the chosen mode is kept, never moved to the other.
        this._value.OnlyMode = this.ModeIndex == 1;
        this._value.OnlyAchievements = this.ModeIndex == 1 ? new HashSet<string>(this._checks[1], StringComparer.Ordinal) : [];
        this._value.ExcludedAchievements = this.ModeIndex == 2 ? new HashSet<string>(this._checks[2], StringComparer.Ordinal) : [];
    }
}
