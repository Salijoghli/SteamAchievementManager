// [ObservableProperty] on fields: partial properties need C# 13 and the project uses C# 12;
// the app isn't AOT-compiled, so this WinRT AOT warning doesn't apply.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels;

/// <summary>Index order of the state filter ComboBox.</summary>
public enum AchievementStateFilter
{
    All,
    Locked,
    Unlocked,
    Pending,
    HasNotes,
}

/// <summary>Index order of the unlock date filter ComboBox.</summary>
public enum AchievementDateFilter
{
    Any,
    Today,
    Past7Days,
    Past30Days,
    ThisYear,
    Custom,
}

/// <summary>Index order of the sort ComboBox.</summary>
public enum AchievementSort
{
    Default,
    Name,
    RarestFirst,
    MostCommonFirst,
    NewestUnlockFirst,
}

/// <summary>
/// Outcome of a store or reset, for the status InfoBar. <paramref name="IsWarning"/> marks a
/// failure whose outcome is unknown or partial (shown as a warning instead of an error).
/// </summary>
public sealed record GameOperationResult(bool Success, string Title, string Message, bool IsWarning = false);

/// <summary>
/// The achievement manager for one game: load pipeline, filtering, bulk actions and
/// commit/reset. Must be created and used on the UI thread (Steam calls go there);
/// schema, file and network work is pushed to the background.
/// </summary>
public sealed partial class GameViewModel : ObservableObject
{
    public static string MissingSchemaMessage => Strings.Get("Game_MissingSchema");

    /// <summary>Above this many row changes the visible list is replaced in one go (see <see cref="Sync"/>).</summary>
    private const int MaxIncrementalChanges = 150;

    private readonly AppServices _services;
    private readonly List<AchievementViewModel> _achievements = [];
    private IReadOnlyDictionary<string, double>? _percents;
    private bool _percentsDone;
    private Task? _percentsTask;
    private CancellationTokenSource? _searchDebounce;
    private bool _suspendPendingUpdates;

    public GameViewModel(AppServices services, uint appId)
    {
        this._services = services;
        this.AppId = appId;

        string? name = null;
        try
        {
            name = services.Steam.GetAppData(appId, "name");
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't read the name of app {appId}", e);
        }

        this.GameName = string.IsNullOrWhiteSpace(name) ? Strings.Format("Game_AppName", appId) : name;
        this._isSpoilerProtectionOn = services.Settings.SpoilerProtection;
    }

    public uint AppId { get; }

    public string GameName { get; }

    public string AppIdText => Strings.Format("Game_AppIdText", this.AppId);

    #region Load state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isLoading;

    /// <summary>True once content has been loaded at least once and the last load succeeded.</summary>
    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    public bool HasLoadError => this.LoadError != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isWorking;

    /// <summary>Loading or storing; disables the toolbar.</summary>
    public bool IsBusy => this.IsLoading || this.IsWorking;
    #endregion

    #region Header
    [ObservableProperty]
    private ImageSource? _headerImage;

    [ObservableProperty]
    private bool _isHeaderImageLoaded;

    [ObservableProperty]
    private bool _isHeaderImageMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaytime))]
    private string? _playtimeText;

    public bool HasPlaytime => string.IsNullOrEmpty(this.PlaytimeText) == false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletionText))]
    [NotifyPropertyChangedFor(nameof(CompletionPercent))]
    [NotifyPropertyChangedFor(nameof(CompletionPercentText))]
    private int _unlockedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletionText))]
    [NotifyPropertyChangedFor(nameof(CompletionPercent))]
    [NotifyPropertyChangedFor(nameof(CompletionPercentText))]
    private int _totalCount;

    public double CompletionPercent => this.TotalCount == 0 ? 0 : 100.0 * this.UnlockedCount / this.TotalCount;

    public string CompletionPercentText => $"{Math.Floor(this.CompletionPercent):0}%";

    public string CompletionText => $"{this.UnlockedCount} / {this.TotalCount} · {this.CompletionPercentText}";
    #endregion

    #region Pending changes
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingCount))]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyPropertyChangedFor(nameof(PendingText))]
    [NotifyPropertyChangedFor(nameof(CommitLabel))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _unlockPendingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingCount))]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyPropertyChangedFor(nameof(PendingText))]
    [NotifyPropertyChangedFor(nameof(CommitLabel))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _lockPendingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingCount))]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyPropertyChangedFor(nameof(PendingText))]
    [NotifyPropertyChangedFor(nameof(CommitLabel))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _modifiedStatCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInvalidStats))]
    [NotifyPropertyChangedFor(nameof(PendingText))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _invalidStatCount;

    public int PendingCount => this.UnlockPendingCount + this.LockPendingCount + this.ModifiedStatCount;

    public bool HasPending => this.PendingCount > 0;

    public bool HasInvalidStats => this.InvalidStatCount > 0;

    /// <summary>
    /// Pending changes or invalid stat edits: anything closing or reloading would throw away.
    /// Used by the close and reload prompts (not by the commit count).
    /// </summary>
    public bool HasUnsavedChanges => this.HasPending || this.HasInvalidStats;

    /// <summary>Body of the close prompt.</summary>
    public string UnsavedChangesText
    {
        get
        {
            var parts = new List<string>();
            if (this.PendingCount > 0)
            {
                parts.Add(Strings.Plural("Game_UnsavedPending", this.PendingCount));
            }

            if (this.InvalidStatCount > 0)
            {
                parts.Add(Strings.Plural("Game_UnsavedInvalid", this.InvalidStatCount));
            }

            return parts.Count == 0 ? Strings.Get("Game_UnsavedNone") : Strings.Format("Game_UnsavedSentence", JoinList(parts));
        }
    }

    public string PendingText
    {
        get
        {
            var text = this.PendingCount == 0 ? Strings.Get("Game_PendingNone") : Strings.Plural("Game_Pending", this.PendingCount);
            return this.HasInvalidStats ? Strings.Plural("Game_PendingInvalid", this.InvalidStatCount, text) : text;
        }
    }

    public string CommitLabel => this.PendingCount > 0
        ? Strings.Format("Game_CommitLabelCount", this.PendingCount)
        : Strings.Get("Game_CommitLabel");

    /// <summary>Raised by the commit command; the window shows the confirmation dialog.</summary>
    public event EventHandler? CommitRequested;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit() => this.CommitRequested?.Invoke(this, EventArgs.Empty);

    public bool CanCommit() => this.PendingCount > 0 && this.InvalidStatCount == 0 && this.IsBusy == false && this.IsLoaded;

    partial void OnIsLoadingChanged(bool value) => this.OnBusyChanged();

    partial void OnIsWorkingChanged(bool value) => this.OnBusyChanged();

    private void OnBusyChanged()
    {
        this.RefreshCommandStates();

        // Rows can't be changed while loading or storing: a commit stages the values it
        // captured, and edits made meanwhile would be marked committed by mistake.
        var busy = this.IsBusy;
        foreach (var achievement in this._achievements)
        {
            achievement.IsEditingBlocked = busy;
        }

        foreach (var stat in this.Stats)
        {
            stat.IsEditingBlocked = busy;
        }
    }

    partial void OnIsLoadedChanged(bool value) => this.RefreshCommandStates();

    private void RefreshCommandStates()
    {
        this.CommitCommand.NotifyCanExecuteChanged();
        this.UnlockAllCommand.NotifyCanExecuteChanged();
        this.LockAllCommand.NotifyCanExecuteChanged();
        this.InvertCommand.NotifyCanExecuteChanged();
        this.ReloadCommand.NotifyCanExecuteChanged();
    }

    private void UpdatePending()
    {
        if (this._suspendPendingUpdates)
        {
            return;
        }

        this.UnlockPendingCount = this._achievements.Count(a => a.IsPending && a.IsChecked);
        this.LockPendingCount = this._achievements.Count(a => a.IsPending && a.IsChecked == false);
        this.ModifiedStatCount = this.Stats.Count(s => s.IsModified);
        this.InvalidStatCount = this.Stats.Count(s => s.HasError);
    }
    #endregion

    #region Status InfoBar
    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity;

    public void ShowStatus(GameOperationResult result) => this.ShowStatus(
        result.Title,
        result.Message,
        result.Success ? InfoBarSeverity.Success : result.IsWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Error);

    public void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        this.StatusTitle = title;
        this.StatusMessage = message;
        this.StatusSeverity = severity;
        this.IsStatusOpen = true;
    }
    #endregion

    #region Autopilot warning
    private bool _autopilotCheckFailed;
    private bool _wasRunningUnderAutopilot;

    /// <summary>True while an Autopilot worker holds this game (commits here may conflict with it).</summary>
    [ObservableProperty]
    private bool _isAutopilotWarningOpen;

    /// <summary>
    /// Re-checks whether Autopilot is running this game: opens the warning when it starts doing
    /// so (a dismissed warning stays closed until then) and closes it when it stops. Never throws.
    /// </summary>
    public void CheckAutopilot()
    {
        bool running;
        try
        {
            running = Services.Autopilot.AutopilotEngine.IsGameRunningUnderAutopilot(this.AppId);
        }
        catch (Exception e)
        {
            if (this._autopilotCheckFailed == false)
            {
                this._autopilotCheckFailed = true;
                Diagnostics.Log($"Couldn't check whether Autopilot is running app {this.AppId}", e);
            }

            running = false;
        }

        if (running != this._wasRunningUnderAutopilot)
        {
            this._wasRunningUnderAutopilot = running;
            this.IsAutopilotWarningOpen = running;
        }
    }
    #endregion

    #region Collections
    /// <summary>The filtered, sorted achievements shown in the list.</summary>
    public BulkObservableCollection<AchievementViewModel> VisibleAchievements { get; } = [];

    public ObservableCollection<StatViewModel> Stats { get; } = [];

    public IReadOnlyList<AchievementViewModel> AllAchievements => this._achievements;

    [ObservableProperty]
    private string _showingText = "";

    [ObservableProperty]
    private bool _hasNoVisibleAchievements;

    [ObservableProperty]
    private bool _hasNoStats;

    [ObservableProperty]
    private bool _isStatEditingEnabled;

    partial void OnIsStatEditingEnabledChanged(bool value)
    {
        this._suspendPendingUpdates = true;
        try
        {
            foreach (var stat in this.Stats)
            {
                stat.IsEditingAllowed = value;

                // Turning editing off drops every stat edit, valid or not.
                if (value == false)
                {
                    stat.ResetToOriginal();
                }
            }
        }
        finally
        {
            this._suspendPendingUpdates = false;
        }

        this.UpdatePending();
    }
    #endregion

    #region Filters
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _stateFilterIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomDateRange))]
    private int _dateFilterIndex;

    [ObservableProperty]
    private DateTimeOffset? _customFrom;

    [ObservableProperty]
    private DateTimeOffset? _customTo;

    [ObservableProperty]
    private int _rarityFilterIndex;

    [ObservableProperty]
    private int _sortIndex;

    public bool IsCustomDateRange => (AchievementDateFilter)this.DateFilterIndex == AchievementDateFilter.Custom;

    partial void OnSearchTextChanged(string value) => this.DebounceFilter();

    partial void OnStateFilterIndexChanged(int value) => this.ApplyFilter();

    partial void OnDateFilterIndexChanged(int value) => this.ApplyFilter();

    partial void OnCustomFromChanged(DateTimeOffset? value) => this.ApplyFilter();

    partial void OnCustomToChanged(DateTimeOffset? value) => this.ApplyFilter();

    partial void OnRarityFilterIndexChanged(int value) => this.ApplyFilter();

    partial void OnSortIndexChanged(int value) => this.ApplyFilter();

    private async void DebounceFilter()
    {
        this._searchDebounce?.Cancel();
        var cts = this._searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (ReferenceEquals(this._searchDebounce, cts))
        {
            this._searchDebounce = null;
        }

        this.ApplyFilter();
    }

    /// <summary>The unlock date range for the current date filter, or null for "any time".</summary>
    public DateRange? GetDateRange()
    {
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);
        return (AchievementDateFilter)this.DateFilterIndex switch
        {
            AchievementDateFilter.Today => new DateRange(today, null),
            AchievementDateFilter.Past7Days => new DateRange(now.AddDays(-7), null),
            AchievementDateFilter.Past30Days => new DateRange(now.AddDays(-30), null),
            AchievementDateFilter.ThisYear => new DateRange(new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset), null),
            AchievementDateFilter.Custom => new DateRange(
                this.CustomFrom is { } from ? new DateTimeOffset(from.Date, from.Offset) : null,
                this.CustomTo is { } to ? new DateTimeOffset(to.Date, to.Offset).AddDays(1).AddTicks(-1) : null),
            _ => null,
        };
    }

    public void ApplyFilter()
    {
        var query = this.SearchText.Trim();
        var state = (AchievementStateFilter)this.StateFilterIndex;
        var dateRange = this.GetDateRange();
        var rarity = (AchievementRarity)this.RarityFilterIndex; // 0 = All (Unknown), then Common…Legendary.

        IEnumerable<AchievementViewModel> items = this._achievements.Where(a =>
            (query.Length == 0 || a.Matches(query)) &&
            state switch
            {
                AchievementStateFilter.Locked => a.IsAchieved == false,
                AchievementStateFilter.Unlocked => a.IsAchieved,
                AchievementStateFilter.Pending => a.IsPending,
                AchievementStateFilter.HasNotes => a.HasNote,
                _ => true,
            } &&
            (dateRange == null || (a.IsAchieved && dateRange.Value.Contains(a.UnlockTime))) &&
            (rarity == AchievementRarity.Unknown || a.Rarity == rarity));

        items = (AchievementSort)this.SortIndex switch
        {
            AchievementSort.Name => items.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            AchievementSort.RarestFirst => items
                .OrderBy(a => a.GlobalPercent.HasValue ? 0 : 1)
                .ThenBy(a => a.GlobalPercent ?? 0)
                .ThenBy(a => a.SchemaIndex),
            AchievementSort.MostCommonFirst => items
                .OrderBy(a => a.GlobalPercent.HasValue ? 0 : 1)
                .ThenByDescending(a => a.GlobalPercent ?? 0)
                .ThenBy(a => a.SchemaIndex),
            AchievementSort.NewestUnlockFirst => items
                .OrderBy(a => a.UnlockTime.HasValue ? 0 : 1)
                .ThenByDescending(a => a.UnlockTime ?? DateTimeOffset.MinValue)
                .ThenBy(a => a.SchemaIndex),
            _ => items.OrderBy(a => a.SchemaIndex),
        };

        Sync(this.VisibleAchievements, items.ToList());
        this.ShowingText = Strings.Format("Game_ShowingCount", this.VisibleAchievements.Count, this._achievements.Count);
        this.HasNoVisibleAchievements = this.IsLoaded && this.VisibleAchievements.Count == 0;
    }

    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="items"/> with minimal changes, so
    /// rows animate. Like LibraryViewModel.Sync, big changes (thousands of achievements, a new
    /// sort order) replace the contents in one Reset instead of thousands of row notifications.
    /// </summary>
    private static void Sync(BulkObservableCollection<AchievementViewModel> target, List<AchievementViewModel> items)
    {
        var wanted = new HashSet<AchievementViewModel>(items);
        var removals = target.Count(c => wanted.Contains(c) == false);
        var insertions = items.Count - (target.Count - removals);
        if (removals + insertions > MaxIncrementalChanges)
        {
            target.ReplaceAll(items);
            return;
        }

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(target[i]) == false)
            {
                target.RemoveAt(i);
            }
        }

        var moves = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i < target.Count && ReferenceEquals(target[i], item))
            {
                continue;
            }

            var existing = IndexOf(target, item, i);
            if (existing >= 0)
            {
                if (++moves > MaxIncrementalChanges)
                {
                    target.ReplaceAll(items);
                    return;
                }

                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, item);
            }
        }
    }

    private static int IndexOf(BulkObservableCollection<AchievementViewModel> list, AchievementViewModel item, int start)
    {
        for (var i = start; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Applies a search that's still waiting out its debounce, so bulk actions see what's shown.</summary>
    private void FlushPendingSearch()
    {
        if (this._searchDebounce is { IsCancellationRequested: false } debounce)
        {
            debounce.Cancel();
            this.ApplyFilter();
        }
    }
    #endregion

    #region Loading
    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task Reload() => this.LoadAsync();

    private bool CanReload() => this.IsBusy == false;

    /// <summary>
    /// Requests the user's stats from Steam, loads the schema in the background, and reads
    /// each achievement and stat. Returns true on success. Errors go to <see cref="LoadError"/>.
    /// </summary>
    public async Task<bool> LoadAsync()
    {
        if (this.IsLoading)
        {
            return false;
        }

        var ok = await this.LoadCoreAsync();
        this.LoadCompleted?.Invoke(this, ok);
        return ok;
    }

    /// <summary>
    /// Raised after every load attempt (initial, retry, reload, post-commit), once the view
    /// model is no longer loading. The argument is true when the load succeeded.
    /// </summary>
    public event EventHandler<bool>? LoadCompleted;

    private async Task<bool> LoadCoreAsync()
    {
        this.IsLoading = true;
        this.IsLoaded = false;
        this.LoadError = null;
        this.StartSideLoads();

        try
        {
            await this._services.Steam.RequestUserStatsAsync();

            var schema = await Task.Run(() => this._services.Schemas.Load(this.AppId));
            if (schema == null || (schema.Achievements.Count == 0 && schema.Stats.Count == 0))
            {
                throw new SteamUnavailableException(MissingSchemaMessage);
            }

            this.Populate(schema);
            this.IsLoaded = true;
            this.ApplyFilter();
            return true;
        }
        catch (SteamUnavailableException e)
        {
            this.LoadError = e.Message;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Loading achievements for app {this.AppId} failed", e);
            this.LoadError = Strings.Format("Game_LoadFailed", e.Message);
        }
        finally
        {
            this.IsLoading = false;
        }

        return false;
    }

    private void Populate(GameSchema schema)
    {
        var steam = this._services.Steam;
        var notes = this.LoadNotes();
        this._suspendPendingUpdates = true;
        try
        {
            this._achievements.Clear();
            this.VisibleAchievements.Clear();
            var index = 0;
            foreach (var definition in schema.Achievements)
            {
                if (string.IsNullOrEmpty(definition.Id) ||
                    steam.TryGetAchievement(definition.Id, out var achieved, out var unlockTime) == false)
                {
                    continue; // Same as the old manager: skip what Steam doesn't know.
                }

                var vm = new AchievementViewModel(
                    definition, index++, achieved, unlockTime, this.AppId, this._services.Images, this.OnAchievementChanged)
                {
                    IsEditingBlocked = this.IsBusy,
                };
                this.ApplyPercent(vm);
                vm.IsSpoilerProtectionOn = this.IsSpoilerProtectionOn;
                vm.Note = notes.GetValueOrDefault(definition.Id);
                this._achievements.Add(vm);
            }

            // Achievements whose unlock is driven by a stat ("progress" blocks in the schema),
            // shown under each stat as "Used by".
            var usagesByStat = schema.Achievements
                .Where(a => a.ProgressStatId != null && a.ProgressMax.HasValue)
                .ToLookup(
                    a => a.ProgressStatId!,
                    a => new StatUsage(a.Name, a.ProgressMax!.Value),
                    StringComparer.OrdinalIgnoreCase);

            this.Stats.Clear();
            foreach (var definition in schema.Stats)
            {
                if (string.IsNullOrEmpty(definition.Id))
                {
                    continue;
                }

                double value;
                if (definition.Kind == StatKind.Integer)
                {
                    if (steam.TryGetStat(definition.Id, out int intValue) == false)
                    {
                        continue;
                    }

                    value = intValue;
                }
                else
                {
                    if (steam.TryGetStat(definition.Id, out float floatValue) == false)
                    {
                        continue;
                    }

                    value = floatValue;
                }

                var stat = new StatViewModel(definition, value, this.UpdatePending)
                {
                    IsEditingAllowed = this.IsStatEditingEnabled,
                    IsEditingBlocked = this.IsBusy,
                };
                foreach (var usage in usagesByStat[definition.Id].OrderBy(u => u.TargetValue))
                {
                    stat.UsedBy.Add(usage);
                }

                this.Stats.Add(stat);
            }
        }
        finally
        {
            this._suspendPendingUpdates = false;
        }

        this.UnlockedCount = this._achievements.Count(a => a.IsAchieved);
        this.TotalCount = this._achievements.Count;
        this.HasNoStats = this.Stats.Count == 0;
        this.UpdatePending();
    }

    private void OnAchievementChanged() => this.UpdatePending();

    /// <summary>Header image, playtime and global percentages. Each runs once per window.</summary>
    private void StartSideLoads()
    {
        if (this.HeaderImage == null && this.IsHeaderImageMissing == false && this.IsHeaderImageLoaded == false)
        {
            this.LoadHeaderImage();
        }

        if (this.PlaytimeText == null)
        {
            this.LoadPlaytime();
        }

        if (this._percentsTask == null || (this._percentsDone && this._percents == null))
        {
            this._percentsDone = false;
            foreach (var achievement in this._achievements)
            {
                achievement.IsRarityLoading = true;
            }

            this._percentsTask = this.LoadPercentsAsync();
        }
    }

    private async void LoadHeaderImage()
    {
        this.IsHeaderImageLoaded = false;
        try
        {
            var urls = this._services.Art.GetCardImageUrls(this.AppId);
            var path = await this._services.Images.GetAsync(urls, CancellationToken.None);
            if (path != null)
            {
                this.HeaderImage = new BitmapImage(new Uri(path));
            }
            else
            {
                this.IsHeaderImageMissing = true;
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't load the header image for app {this.AppId}", e);
            this.IsHeaderImageMissing = true;
        }

        this.IsHeaderImageLoaded = true;
    }

    private async void LoadPlaytime()
    {
        try
        {
            var all = await Task.Run(this._services.Playtime.Load);
            if (all.TryGetValue(this.AppId, out var info))
            {
                this.PlaytimeText = FormatPlaytime(info);
                return;
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't read playtime", e);
        }

        this.PlaytimeText = "";
    }

    private static string FormatPlaytime(PlaytimeInfo info)
    {
        var parts = new List<string>();
        if (info.Playtime > TimeSpan.Zero)
        {
            parts.Add(info.Playtime.TotalHours >= 1
                ? Strings.Format("Game_HoursPlayed", info.Playtime.TotalHours.ToString("0.#", CultureInfo.CurrentCulture))
                : Strings.Plural("Game_MinutesPlayed", (int)info.Playtime.TotalMinutes));
        }

        if (info.LastPlayed is { } last)
        {
            parts.Add(Strings.Format("Game_LastPlayed", last.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)));
        }

        return string.Join(" · ", parts);
    }

    private async Task LoadPercentsAsync()
    {
        IReadOnlyDictionary<string, double>? percents = null;
        try
        {
            percents = await this._services.GlobalPercents.GetAsync(this.AppId, CancellationToken.None);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't load global achievement percentages for app {this.AppId}", e);
        }

        this._percents = percents;
        this._percentsDone = true;
        foreach (var achievement in this._achievements)
        {
            this.ApplyPercent(achievement);
        }

        // Rarity filters and sorts depend on the percentages.
        if (this.IsLoaded && (this.RarityFilterIndex != 0 ||
            (AchievementSort)this.SortIndex is AchievementSort.RarestFirst or AchievementSort.MostCommonFirst))
        {
            this.ApplyFilter();
        }
    }

    private void ApplyPercent(AchievementViewModel achievement)
    {
        if (this._percentsDone == false)
        {
            achievement.IsRarityLoading = true;
            return;
        }

        achievement.GlobalPercent = this._percents != null && this._percents.TryGetValue(achievement.Id, out var p) ? p : null;
        achievement.IsRarityLoading = false;
    }
    #endregion

    #region Bulk actions
    // Like the old manager, the toolbar actions apply to the achievements currently shown
    // (after search and filters), skipping protected ones.
    [RelayCommand(CanExecute = nameof(CanEditAchievements))]
    private void UnlockAll() => this.SetAll(this.GetShownAchievements(), _ => true);

    [RelayCommand(CanExecute = nameof(CanEditAchievements))]
    private void LockAll() => this.SetAll(this.GetShownAchievements(), _ => false);

    [RelayCommand(CanExecute = nameof(CanEditAchievements))]
    private void Invert() => this.SetAll(this.GetShownAchievements(), a => a.IsChecked == false);

    private bool CanEditAchievements() => this.IsBusy == false && this.IsLoaded;

    /// <summary>
    /// The --unlock-all / --lock-all launch pre-set: checks or unchecks every changeable
    /// achievement regardless of search and filters. Returns false when not loaded or busy.
    /// </summary>
    public bool ApplyLaunchPreset(bool unlockAll)
    {
        if (this.CanEditAchievements() == false)
        {
            return false;
        }

        this.SetAll(this._achievements, _ => unlockAll);
        return true;
    }

    private List<AchievementViewModel> GetShownAchievements()
    {
        this.FlushPendingSearch();
        return [.. this.VisibleAchievements]; // Snapshot: the Pending filter re-filters afterwards.
    }

    private void SetAll(IReadOnlyList<AchievementViewModel> targets, Func<AchievementViewModel, bool> desired)
    {
        this._suspendPendingUpdates = true;
        try
        {
            foreach (var achievement in targets)
            {
                if (achievement.IsProtected == false)
                {
                    achievement.IsChecked = desired(achievement);
                }
            }
        }
        finally
        {
            this._suspendPendingUpdates = false;
        }

        this.UpdatePending();
        if ((AchievementStateFilter)this.StateFilterIndex == AchievementStateFilter.Pending)
        {
            this.ApplyFilter();
        }
    }

    /// <summary>Flips one achievement (for the Space key). Ignores protected ones.</summary>
    public void Toggle(AchievementViewModel achievement)
    {
        if (achievement.CanChange && this.IsBusy == false)
        {
            achievement.IsChecked = !achievement.IsChecked;
        }
    }
    #endregion

    #region Spoiler protection
    /// <summary>
    /// Mirrors <see cref="AppSettings.SpoilerProtection"/>: hidden achievements' descriptions
    /// stay masked until clicked. Changing it updates every row and saves the setting.
    /// </summary>
    [ObservableProperty]
    private bool _isSpoilerProtectionOn;

    partial void OnIsSpoilerProtectionOnChanged(bool value)
    {
        foreach (var achievement in this._achievements)
        {
            achievement.IsSpoilerProtectionOn = value;
        }

        this._services.Settings.SpoilerProtection = value;
        try
        {
            // Other windows (e.g. the library, in another process) may have saved settings
            // since this one started; merge into the file instead of overwriting it with a
            // stale copy.
            var latest = AppSettings.Load();
            latest.SpoilerProtection = value;
            latest.Save();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't save the spoiler protection setting", e);
        }
    }

    /// <summary>Reveals every hidden achievement's description.</summary>
    [RelayCommand]
    private void RevealAllHidden()
    {
        foreach (var achievement in this._achievements)
        {
            if (achievement.IsHidden)
            {
                achievement.IsRevealed = true;
            }
        }
    }
    #endregion

    #region Notes
    /// <summary>The notes for this game's achievements, by achievement ID. Empty if they can't be read.</summary>
    private Dictionary<string, string> LoadNotes()
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var id in this._services.Notes.GetIds(this.AppId))
            {
                if (this._services.Notes.Get(this.AppId, id) is { } note && string.IsNullOrWhiteSpace(note) == false)
                {
                    notes[id] = note;
                }
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't read the achievement notes for app {this.AppId}", e);
        }

        return notes;
    }

    /// <summary>Saves (or, when empty, deletes) the note for <paramref name="achievement"/>.</summary>
    public void SetNote(AchievementViewModel achievement, string? text)
    {
        var note = string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd();
        if (string.Equals(note, achievement.Note, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            this._services.Notes.Set(this.AppId, achievement.Id, note);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't save the note for achievement {achievement.Id} of app {this.AppId}", e);
            this.ShowStatus(new GameOperationResult(false, Strings.Get("Game_NoteSaveFailed"), e.Message));
            return;
        }

        achievement.Note = note;
        if ((AchievementStateFilter)this.StateFilterIndex == AchievementStateFilter.HasNotes ||
            this.SearchText.Trim().Length > 0)
        {
            this.ApplyFilter();
        }
    }
    #endregion

    #region Guides
    /// <summary>Steam Community guides for this game, searched by the achievement's name.</summary>
    public string GetSteamGuideUrl(AchievementViewModel achievement) =>
        $"https://steamcommunity.com/app/{this.AppId}/guides/?searchText={Uri.EscapeDataString(achievement.Name)}&browsefilter=trend";

    /// <summary>A web search for a guide to the achievement.</summary>
    public string GetWebSearchUrl(AchievementViewModel achievement) =>
        "https://www.google.com/search?q=" +
        Uri.EscapeDataString($"{this.GameName} {achievement.Name} achievement guide");
    #endregion

    #region Backups and restore
    /// <summary>Reason recorded on the automatic backup before a normal commit.</summary>
    public const string BeforeCommitReason = "Before commit";

    /// <summary>Reason recorded on the automatic backup before restoring a backup.</summary>
    public const string BeforeRestoreReason = "Before restore";

    /// <summary>Reason recorded on the automatic backup before resetting all stats.</summary>
    public const string BeforeResetReason = "Before reset";

    /// <summary>Reason recorded on a backup the user asked for.</summary>
    public const string ManualReason = "Manual";

    /// <summary>
    /// A snapshot of the state Steam reported at the last load or commit (not the pending
    /// edits): every achievement's unlocked state and time, and every stat's value.
    /// </summary>
    public GameSnapshot CreateSnapshot(string reason) => new()
    {
        AppId = this.AppId,
        GameName = this.GameName,
        CreatedAt = DateTimeOffset.Now,
        Reason = reason,
        Achievements = this._achievements
            .Select(a => new AchievementSnapshotEntry(a.Id, a.IsAchieved, a.IsAchieved ? a.UnlockTime : null))
            .ToList(),
        Stats = this.Stats.Select(s => new StatSnapshotEntry(s.Id, s.Definition.Kind, s.OriginalValue)).ToList(),
    };

    /// <summary>Saves a snapshot of the current Steam state. Throws if the backup can't be saved.</summary>
    public GameSnapshot SaveBackup(string reason)
    {
        if (this.IsLoaded == false)
        {
            throw new InvalidOperationException("The game's achievements aren't loaded.");
        }

        return this._services.Backups.Save(this.CreateSnapshot(reason));
    }

    /// <summary>Puts every achievement and stat back to the state Steam reported (drops all pending edits).</summary>
    public void DiscardChanges()
    {
        this._suspendPendingUpdates = true;
        try
        {
            foreach (var achievement in this._achievements)
            {
                achievement.IsChecked = achievement.IsAchieved;
            }

            foreach (var stat in this.Stats)
            {
                if (stat.IsModified || stat.HasError)
                {
                    stat.ResetToOriginal();
                }
            }
        }
        finally
        {
            this._suspendPendingUpdates = false;
        }

        this.UpdatePending();
        if ((AchievementStateFilter)this.StateFilterIndex == AchievementStateFilter.Pending)
        {
            this.ApplyFilter();
        }
    }

    /// <summary>
    /// Stages <paramref name="snapshot"/> as pending edits: drops any other pending edits, then
    /// sets every unprotected achievement and every editable stat in the snapshot to its saved
    /// state. Achievements and stats missing from the snapshot keep their current state; ones
    /// no longer in the schema are ignored. Returns what couldn't be restored (protected,
    /// read-only, or a stat value the schema's rules no longer allow), one line each.
    /// Nothing is written to Steam; commit with <see cref="StoreAsync"/> or undo with
    /// <see cref="DiscardChanges"/>.
    /// </summary>
    public IReadOnlyList<string> StageRestore(GameSnapshot snapshot)
    {
        this.DiscardChanges();

        var notRestorable = new List<string>();
        var savedAchievements = snapshot.Achievements
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var savedStats = snapshot.Stats
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // Stat edits only count while editing is allowed; restoring is an explicit request to edit.
        if (this.Stats.Any(s => s.IsReadOnly == false &&
                savedStats.TryGetValue(s.Id, out var saved) && SameStatValue(s, saved.Value, s.OriginalValue) == false))
        {
            this.IsStatEditingEnabled = true;
        }

        this._suspendPendingUpdates = true;
        try
        {
            foreach (var achievement in this._achievements)
            {
                if (savedAchievements.TryGetValue(achievement.Id, out var saved) == false ||
                    saved.Achieved == achievement.IsAchieved)
                {
                    continue;
                }

                if (achievement.IsProtected)
                {
                    notRestorable.Add(Strings.Format(
                        saved.Achieved ? "Game_RestoreAchievementUnlocked" : "Game_RestoreAchievementLocked",
                        achievement.Name));
                    continue;
                }

                achievement.IsChecked = saved.Achieved;
            }

            foreach (var stat in this.Stats)
            {
                if (savedStats.TryGetValue(stat.Id, out var saved) == false ||
                    SameStatValue(stat, saved.Value, stat.OriginalValue))
                {
                    continue;
                }

                if (stat.IsReadOnly)
                {
                    notRestorable.Add(Strings.Format(
                        stat.IsProtected ? "Game_RestoreStatProtected" : "Game_RestoreStatAverageRate",
                        stat.DisplayName,
                        FormatStatValue(saved.Value)));
                    continue;
                }

                stat.EditValue = stat.IsInteger ? Math.Round(saved.Value) : (float)saved.Value;
                if (stat.HasError)
                {
                    notRestorable.Add(Strings.Format("Game_RestoreStatError", stat.DisplayName, stat.Error));
                    stat.ResetToOriginal();
                }
            }
        }
        finally
        {
            this._suspendPendingUpdates = false;
        }

        this.UpdatePending();
        if ((AchievementStateFilter)this.StateFilterIndex == AchievementStateFilter.Pending)
        {
            this.ApplyFilter();
        }

        return notRestorable;
    }

    /// <summary>Compares stat values the way the stat is stored (int or float).</summary>
    private static bool SameStatValue(StatViewModel stat, double a, double b) => stat.IsInteger
        ? (long)Math.Round(a) == (long)Math.Round(b)
        : ((float)a).Equals((float)b);

    private static string FormatStatValue(double value) => value.ToString("G7", CultureInfo.CurrentCulture);
    #endregion

    #region Commit and reset
    /// <summary>"Unlock 3 achievements, lock 1 achievement and change 2 stats."</summary>
    public string GetCommitSummary()
    {
        var parts = new List<string>();
        if (this.UnlockPendingCount > 0)
        {
            parts.Add(Strings.Plural("Game_SummaryUnlock", this.UnlockPendingCount));
        }

        if (this.LockPendingCount > 0)
        {
            parts.Add(Strings.Plural("Game_SummaryLock", this.LockPendingCount));
        }

        if (this.ModifiedStatCount > 0)
        {
            parts.Add(Strings.Plural("Game_SummaryStats", this.ModifiedStatCount));
        }

        if (parts.Count == 0)
        {
            return Strings.Get("Game_SummaryNothing");
        }

        var text = JoinList(parts);

        // Only Latin lowercase is capitalized: Georgian has no sentence case (char.ToUpper would
        // turn Mkhedruli into Mtavruli).
        if (char.IsAsciiLetterLower(text[0]))
        {
            text = char.ToUpperInvariant(text[0]) + text[1..];
        }

        return Strings.Format("Game_SummarySentence", text);
    }

    /// <summary>"a, b and c" in the UI language.</summary>
    private static string JoinList(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(Strings.Get("Game_ListSeparator"), parts.Take(parts.Count - 1)) + Strings.Get("Game_ListAnd") + parts[^1],
    };

    /// <summary>
    /// Writes the pending changes to Steam: SetAchievement for each changed achievement,
    /// SetStat for each modified stat, then StoreStats, waiting for Steam's servers to accept
    /// them. If any Set call fails, or Steam refuses the store, the changes already staged in
    /// Steam are undone and the pending edits stay so the user can retry. If Steam's servers
    /// reject some values (they may have kept the rest) or don't answer in time (the outcome
    /// is unknown), nothing is undone and the window reloads to show what Steam has now.
    /// </summary>
    /// <param name="source">Recorded in the commit history (<see cref="CommitSource"/>).</param>
    /// <param name="confirmWithoutBackup">
    /// Asked, with the reason, when the automatic backup can't be saved; returning true
    /// commits anyway. When null (or it returns false) the commit is cancelled with nothing
    /// staged, so a commit never goes through silently without a backup.
    /// </param>
    /// <remarks>
    /// Before staging anything, a snapshot of the current Steam state is saved when
    /// <see cref="AppSettings.BackupBeforeCommit"/> is on (reason "Before commit"), and always
    /// for a restore (reason "Before restore"). Every attempt that reaches Steam is appended to
    /// the commit history, whether it succeeded or not.
    /// </remarks>
    public async Task<GameOperationResult> StoreAsync(
        string source = CommitSource.Manual,
        Func<string, Task<bool>>? confirmWithoutBackup = null)
    {
        if (this.CanCommit() == false)
        {
            return new GameOperationResult(false, Strings.Get("Game_ResultNothingStoredTitle"), this.HasInvalidStats
                ? Strings.Get("Game_ResultFixInvalid")
                : Strings.Get("Game_ResultNoPending"));
        }

        this.IsWorking = true;
        GameOperationResult result;
        var reload = true;
        CommitRecord? record = null;
        try
        {
            if (await this.BackUpBeforeCommitAsync(source, confirmWithoutBackup) == false)
            {
                // Nothing staged, nothing sent; the edits stay for a retry.
                return new GameOperationResult(false, Strings.Get("Game_ResultNothingStoredTitle"), Strings.Get("Game_ResultNoBackup"));
            }

            // The values to store, captured once: they're what gets staged, recorded and marked
            // committed, whatever happens to the rows while Steam answers.
            var achievements = this._achievements
                .Where(a => a.IsPending && a.IsProtected == false)
                .Select(a => new AchievementEdit(a, a.IsChecked))
                .ToList();
            var stats = this.Stats
                .Where(s => s.IsModified && s.IsReadOnly == false && s.HasError == false)
                .Select(s => new StatEdit(s, s.Value))
                .ToList();
            if (achievements.Count > 0 || stats.Count > 0)
            {
                // Captured before the store: MarkCommitted overwrites the originals.
                record = new CommitRecord(
                    DateTimeOffset.Now,
                    this.AppId,
                    this.GameName,
                    achievements.Where(a => a.Achieved).Select(a => a.Achievement.Id).ToList(),
                    achievements.Where(a => a.Achieved == false).Select(a => a.Achievement.Id).ToList(),
                    stats.Select(s => new StatChange(s.Stat.Id, s.Stat.OriginalValue, s.Value)).ToList(),
                    source,
                    false,
                    null);
            }

            try
            {
                (result, reload) = await this.StoreCoreAsync(achievements, stats);
            }
            catch (Exception e)
            {
                // E.g. a timeout: the upload may or may not have happened, so reload.
                Diagnostics.Log($"Storing stats for app {this.AppId} failed", e);
                result = new GameOperationResult(false, Strings.Get("Game_ResultStoreFailedTitle"), e.Message);
            }
        }
        finally
        {
            this.IsWorking = false;
        }

        if (record != null)
        {
            this.AppendHistory(record with { Time = DateTimeOffset.Now, Succeeded = result.Success, Message = result.Message });
        }

        if (reload)
        {
            await this.LoadAsync();
        }

        this.ShowStatus(result);
        return result;
    }

    /// <summary>
    /// Saves the automatic backup when one is due. Returns false when it couldn't be saved and
    /// the user didn't choose to commit anyway.
    /// </summary>
    private async Task<bool> BackUpBeforeCommitAsync(string source, Func<string, Task<bool>>? confirmWithoutBackup)
    {
        var isRestore = source == CommitSource.Restore;
        if (isRestore == false && this._services.Settings.BackupBeforeCommit == false)
        {
            return true;
        }

        try
        {
            this.SaveBackup(isRestore ? BeforeRestoreReason : BeforeCommitReason);
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't back up app {this.AppId} before committing", e);
            if (confirmWithoutBackup == null)
            {
                return false;
            }

            try
            {
                return await confirmWithoutBackup(e.Message);
            }
            catch (Exception confirmError)
            {
                Diagnostics.Log("Asking to commit without a backup failed", confirmError);
                return false;
            }
        }
    }

    private void AppendHistory(CommitRecord record)
    {
        try
        {
            this._services.History.Append(record);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't add the commit for app {this.AppId} to the history", e);
        }
    }

    /// <summary>A change staged in Steam's local state, and how to put it back.</summary>
    private sealed record StagedChange(string Id, Func<bool> Undo);

    /// <summary>An achievement to store and the state captured for it.</summary>
    private sealed record AchievementEdit(AchievementViewModel Achievement, bool Achieved);

    /// <summary>A stat to store and the value captured for it.</summary>
    private sealed record StatEdit(StatViewModel Stat, double Value);

    private async Task<(GameOperationResult Result, bool Reload)> StoreCoreAsync(
        List<AchievementEdit> achievements,
        List<StatEdit> stats)
    {
        var steam = this._services.Steam;
        if (achievements.Count == 0 && stats.Count == 0)
        {
            return (new GameOperationResult(false, Strings.Get("Game_ResultNothingStoredTitle"), Strings.Get("Game_ResultNothingSteamCanStore")), false);
        }

        var staged = new List<StagedChange>();
        string? failure = null;
        try
        {
            foreach (var (achievement, achieved) in achievements)
            {
                if (steam.SetAchievement(achievement.Id, achieved) == false)
                {
                    failure = Strings.Format(
                        achieved ? "Game_FailureRefusedUnlock" : "Game_FailureRefusedLock",
                        achievement.Name,
                        achievement.Id);
                    break;
                }

                var original = achievement.IsAchieved;
                staged.Add(new StagedChange(achievement.Id, () => steam.SetAchievement(achievement.Id, original)));
            }

            if (failure == null)
            {
                foreach (var (stat, value) in stats)
                {
                    if (SetStatValue(steam, stat, value) == false)
                    {
                        failure = Strings.Format("Game_FailureRefusedStat", stat.DisplayName, stat.Id);
                        break;
                    }

                    var original = stat.OriginalValue;
                    staged.Add(new StagedChange(stat.Id, () => SetStatValue(steam, stat, original)));
                }
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Setting achievements/stats for app {this.AppId} failed", e);
            failure = Strings.Format("Game_FailurePreparing", e.Message);
        }

        if (failure != null)
        {
            // Nothing was uploaded. Undo what's staged so a later StoreStats (from here, the
            // game or Steam) can't upload half of the changes, and keep the edits for a retry.
            return (this.FailAndUndo(steam, staged, failure), false);
        }

        try
        {
            await steam.StoreStatsAsync();
        }
        catch (SteamStoreException e) when (e.Failure == SteamStoreFailure.Refused)
        {
            // Nothing was sent: undo, and keep the edits for a retry.
            return (this.FailAndUndo(steam, staged, e.Message), false);
        }
        catch (SteamStoreException e) when (
            e.Failure == SteamStoreFailure.TimedOut || e.Result == SteamStoreException.ServerTimeoutResult)
        {
            // The upload may or may not have gone through (Steam may still retry it). Undoing
            // could revert changes that did reach the servers, so leave them and reload.
            Diagnostics.Log($"Storing stats for app {this.AppId}: no confirmation from Steam ({e.Message})");
            return (UnknownOutcome("Game_ResultUnknownTitle", "Game_ResultUnknownMessage"), true);
        }
        catch (SteamStoreException e) when (e.Result == SteamStoreException.InvalidParamResult)
        {
            // Steam's servers rejected one or more values and sent back their own; the rest
            // may have been stored. Re-staging the old values would silently revert those on
            // the next store, so nothing is undone: reload to show what Steam has now.
            Diagnostics.Log($"Storing stats for app {this.AppId}: Steam rejected some values ({e.Message})");
            return (new GameOperationResult(
                false,
                Strings.Get("Game_ResultPartialTitle"),
                Strings.Get("Game_ResultPartialMessage"),
                IsWarning: true), true);
        }
        catch (SteamStoreException e) when (e.Failure == SteamStoreFailure.Rejected)
        {
            // Rejected outright (no connection, access denied, ...): nothing was stored, so
            // undo what's staged (Steam could upload it later) and reload.
            return (this.FailAndUndo(steam, staged, e.Message), true);
        }

        // Stored: the rows are no longer pending, so closing during the reload doesn't warn.
        foreach (var (achievement, achieved) in achievements)
        {
            achievement.MarkCommitted(achieved);
        }

        foreach (var (stat, value) in stats)
        {
            stat.MarkCommitted(value);
        }

        this.UpdatePending();

        var parts = new List<string>();
        if (achievements.Count > 0)
        {
            parts.Add(Strings.Plural("Game_StoredAchievements", achievements.Count));
        }

        if (stats.Count > 0)
        {
            parts.Add(Strings.Plural("Game_StoredStats", stats.Count));
        }

        return (new GameOperationResult(true, Strings.Get("Game_ResultStoredTitle"), Strings.Format("Game_StoredMessage", JoinList(parts))), true);
    }

    private static bool SetStatValue(SteamSession steam, StatViewModel stat, double value) => stat.IsInteger
        ? steam.SetStat(stat.Id, (int)value)
        : steam.SetStat(stat.Id, (float)value);

    /// <summary>Undoes the staged changes (newest first) and builds the failure message.</summary>
    private GameOperationResult FailAndUndo(SteamSession steam, List<StagedChange> staged, string failure)
    {
        var notUndone = new List<string>();
        for (var i = staged.Count - 1; i >= 0; i--)
        {
            bool ok;
            try
            {
                ok = staged[i].Undo();
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Undoing the staged change to {staged[i].Id} for app {this.AppId} failed", e);
                ok = false;
            }

            if (ok == false)
            {
                notUndone.Add(staged[i].Id);
            }
        }

        var message = Strings.Format("Game_NothingWasStored", failure);
        if (notUndone.Count > 0)
        {
            notUndone.Reverse();
            message = Strings.Format("Game_NotUndone", message, string.Join(", ", notUndone));
        }

        return new GameOperationResult(false, Strings.Get("Game_ResultStoreFailedTitle"), message);
    }

    /// <summary>A failure whose outcome is unknown (Steam didn't confirm in time), shown as a warning.</summary>
    private static GameOperationResult UnknownOutcome(string titleKey, string messageKey) =>
        new(false, Strings.Get(titleKey), Strings.Get(messageKey), IsWarning: true);

    /// <summary>
    /// Resets every stat for this game (and optionally its achievements) on Steam, waits for
    /// Steam's servers to confirm it, then reloads. Pending edits are discarded. A "Before
    /// reset" backup is saved first; when it can't be, <paramref name="confirmWithoutBackup"/>
    /// is asked with the reason (null, or false, cancels with nothing reset). Every attempt
    /// that reaches Steam is added to the commit history.
    /// </summary>
    public async Task<GameOperationResult> ResetAllStatsAsync(
        bool achievementsToo,
        Func<string, Task<bool>>? confirmWithoutBackup = null)
    {
        if (this.IsLoaded == false || this.IsBusy)
        {
            return new GameOperationResult(false, Strings.Get("Game_ResetFailedTitle"), Strings.Get("Game_ResetNotIdle"));
        }

        // The state before the reset, for the history entry.
        var unlockedBefore = this._achievements.Where(a => a.IsAchieved).Select(a => a.Id).ToList();
        var statsBefore = this.Stats.Select(s => (s.Id, s.OriginalValue)).ToList();

        this.IsWorking = true;
        GameOperationResult result;
        try
        {
            if (await this.BackUpBeforeResetAsync(confirmWithoutBackup) == false)
            {
                // Nothing was sent to Steam.
                return new GameOperationResult(false, Strings.Get("Game_ResetFailedTitle"), Strings.Get("Game_ResetNoBackup"));
            }

            // The reload replaces every row anyway; drop the edits now so nothing looks pending.
            this.DiscardChanges();
            try
            {
                await this._services.Steam.ResetAllStatsAsync(achievementsToo);
                result = new GameOperationResult(
                    true,
                    Strings.Get("Game_ResetCompleteTitle"),
                    Strings.Get(achievementsToo ? "Game_ResetAllDone" : "Game_ResetStatsDone"));
            }
            catch (SteamStoreException e) when (
                e.Failure == SteamStoreFailure.TimedOut || e.Result == SteamStoreException.ServerTimeoutResult)
            {
                Diagnostics.Log($"Resetting stats for app {this.AppId}: no confirmation from Steam ({e.Message})");
                result = UnknownOutcome("Game_ResetUnknownTitle", "Game_ResetUnknownMessage");
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Resetting stats for app {this.AppId} failed", e);
                result = new GameOperationResult(false, Strings.Get("Game_ResetFailedTitle"), e.Message);
            }
        }
        finally
        {
            this.IsWorking = false;
        }

        var loaded = await this.LoadAsync();
        this.AppendHistory(this.CreateResetRecord(achievementsToo, result, loaded, unlockedBefore, statsBefore));
        this.ShowStatus(result);
        return result;
    }

    /// <summary>
    /// The history entry for a reset. When the reload after it succeeded, the changes are what
    /// Steam reports now compared with before; otherwise a confirmed reset of achievements
    /// records every previously unlocked one as locked, and stat changes are left out.
    /// </summary>
    private CommitRecord CreateResetRecord(
        bool achievementsToo,
        GameOperationResult result,
        bool reloaded,
        List<string> unlockedBefore,
        List<(string Id, double Value)> statsBefore)
    {
        List<string> unlocked = [];
        List<string> locked = [];
        List<StatChange> stats = [];
        if (reloaded)
        {
            var wasUnlocked = new HashSet<string>(unlockedBefore, StringComparer.Ordinal);
            foreach (var achievement in this._achievements)
            {
                if (achievement.IsAchieved && wasUnlocked.Contains(achievement.Id) == false)
                {
                    unlocked.Add(achievement.Id);
                }
                else if (achievement.IsAchieved == false && wasUnlocked.Contains(achievement.Id))
                {
                    locked.Add(achievement.Id);
                }
            }

            var now = new Dictionary<string, StatViewModel>(StringComparer.Ordinal);
            foreach (var stat in this.Stats)
            {
                now.TryAdd(stat.Id, stat);
            }

            foreach (var (id, before) in statsBefore)
            {
                if (now.TryGetValue(id, out var stat) && SameStatValue(stat, before, stat.OriginalValue) == false)
                {
                    stats.Add(new StatChange(id, before, stat.OriginalValue));
                }
            }
        }
        else if (result.Success && achievementsToo)
        {
            locked = unlockedBefore;
        }

        var label = Strings.Get(achievementsToo ? "Game_ResetHistoryAll" : "Game_ResetHistoryStats");
        return new CommitRecord(
            DateTimeOffset.Now,
            this.AppId,
            this.GameName,
            unlocked,
            locked,
            stats,
            CommitSource.Manual,
            result.Success,
            result.Success ? label : Strings.Format("Game_ResetHistoryFailed", label, result.Message));
    }

    /// <summary>
    /// Saves the "Before reset" backup. Returns false when it couldn't be saved and the user
    /// didn't choose to reset anyway.
    /// </summary>
    private async Task<bool> BackUpBeforeResetAsync(Func<string, Task<bool>>? confirmWithoutBackup)
    {
        try
        {
            this._services.Backups.Save(this.CreateSnapshot(BeforeResetReason));
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't back up app {this.AppId} before resetting its stats", e);
            if (confirmWithoutBackup == null)
            {
                return false;
            }

            try
            {
                return await confirmWithoutBackup(e.Message);
            }
            catch (Exception confirmError)
            {
                Diagnostics.Log("Asking to reset without a backup failed", confirmError);
                return false;
            }
        }
    }
    #endregion
}

/// <summary>An <see cref="ObservableCollection{T}"/> whose contents can be swapped with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        this.CheckReentrancy();
        this.Items.Clear();
        foreach (var item in items)
        {
            this.Items.Add(item);
        }

        this.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(this.Count)));
        this.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        this.OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
