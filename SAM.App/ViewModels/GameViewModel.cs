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

/// <summary>Outcome of a store or reset, for the status InfoBar.</summary>
public sealed record GameOperationResult(bool Success, string Title, string Message);

/// <summary>
/// The achievement manager for one game: load pipeline, filtering, bulk actions and
/// commit/reset. Must be created and used on the UI thread (Steam calls go there);
/// schema, file and network work is pushed to the background.
/// </summary>
public sealed partial class GameViewModel : ObservableObject
{
    public const string MissingSchemaMessage =
        "This game has no achievements or its schema isn't cached by Steam yet. Launch the game once, then reload.";

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

        this.GameName = string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name;
    }

    public uint AppId { get; }

    public string GameName { get; }

    public string AppIdText => $"App ID {this.AppId}";

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
                parts.Add(this.PendingCount == 1
                    ? "1 change that hasn't been committed to Steam"
                    : $"{this.PendingCount} changes that haven't been committed to Steam");
            }

            if (this.InvalidStatCount > 0)
            {
                parts.Add(this.InvalidStatCount == 1
                    ? "1 stat edit that isn't valid yet"
                    : $"{this.InvalidStatCount} stat edits that aren't valid yet");
            }

            return parts.Count == 0 ? "You have no unsaved changes." : $"You have {string.Join(" and ", parts)}.";
        }
    }

    public string PendingText
    {
        get
        {
            var text = this.PendingCount switch
            {
                0 => "No pending changes",
                1 => "1 pending change",
                var n => $"{n} pending changes",
            };
            return this.HasInvalidStats ? $"{text} · {this.InvalidStatCount} invalid stat value(s)" : text;
        }
    }

    public string CommitLabel => this.PendingCount > 0 ? $"Commit changes ({this.PendingCount})" : "Commit changes";

    /// <summary>Raised by the commit command; the window shows the confirmation dialog.</summary>
    public event EventHandler? CommitRequested;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit() => this.CommitRequested?.Invoke(this, EventArgs.Empty);

    public bool CanCommit() => this.PendingCount > 0 && this.InvalidStatCount == 0 && this.IsBusy == false && this.IsLoaded;

    partial void OnIsLoadingChanged(bool value) => this.RefreshCommandStates();

    partial void OnIsWorkingChanged(bool value) => this.RefreshCommandStates();

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

    public void ShowStatus(GameOperationResult result)
    {
        this.StatusTitle = result.Title;
        this.StatusMessage = result.Message;
        this.StatusSeverity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        this.IsStatusOpen = true;
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
        this.ShowingText = $"Showing {this.VisibleAchievements.Count} of {this._achievements.Count}";
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
            this.LoadError = $"Something went wrong while loading this game's achievements: {e.Message}";
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
                    definition, index++, achieved, unlockTime, this.AppId, this._services.Images, this.OnAchievementChanged);
                this.ApplyPercent(vm);
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
                ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} hours played", info.Playtime.TotalHours)
                : $"{(int)info.Playtime.TotalMinutes} minutes played");
        }

        if (info.LastPlayed is { } last)
        {
            parts.Add("Last played " + last.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
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
        if (achievement.IsProtected == false && this.IsBusy == false)
        {
            achievement.IsChecked = !achievement.IsChecked;
        }
    }
    #endregion

    #region Commit and reset
    /// <summary>"Unlock 3 achievements, lock 1 achievement and change 2 stats."</summary>
    public string GetCommitSummary()
    {
        var parts = new List<string>();
        if (this.UnlockPendingCount > 0)
        {
            parts.Add($"unlock {Plural(this.UnlockPendingCount, "achievement")}");
        }

        if (this.LockPendingCount > 0)
        {
            parts.Add($"lock {Plural(this.LockPendingCount, "achievement")}");
        }

        if (this.ModifiedStatCount > 0)
        {
            parts.Add($"change {Plural(this.ModifiedStatCount, "stat")}");
        }

        if (parts.Count == 0)
        {
            return "There's nothing to commit.";
        }

        var text = parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..] + ".";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// Writes the pending changes to Steam: SetAchievement for each changed achievement,
    /// SetStat for each modified stat, then StoreStats, waiting for Steam's servers to accept
    /// them. All or nothing: if any Set call fails, or Steam refuses or rejects the store,
    /// the changes already staged in Steam are undone. Reloads afterwards unless nothing
    /// reached Steam (then the pending edits stay so the user can retry).
    /// </summary>
    public async Task<GameOperationResult> StoreAsync()
    {
        if (this.CanCommit() == false)
        {
            return new GameOperationResult(false, "Nothing stored", this.HasInvalidStats
                ? "Fix the invalid stat values first."
                : "There are no pending changes.");
        }

        this.IsWorking = true;
        GameOperationResult result;
        var reload = true;
        try
        {
            (result, reload) = await this.StoreCoreAsync();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Storing stats for app {this.AppId} failed", e);
            result = new GameOperationResult(false, "Couldn't store changes", e.Message);
        }
        finally
        {
            this.IsWorking = false;
        }

        if (reload)
        {
            await this.LoadAsync();
        }

        this.ShowStatus(result);
        return result;
    }

    /// <summary>A change staged in Steam's local state, and how to put it back.</summary>
    private sealed record StagedChange(string Id, Func<bool> Undo);

    private async Task<(GameOperationResult Result, bool Reload)> StoreCoreAsync()
    {
        var steam = this._services.Steam;
        var achievements = this._achievements.Where(a => a.IsPending && a.IsProtected == false).ToList();
        var stats = this.Stats.Where(s => s.IsModified && s.IsReadOnly == false && s.HasError == false).ToList();
        if (achievements.Count == 0 && stats.Count == 0)
        {
            return (new GameOperationResult(false, "Nothing stored", "There are no changes Steam can store."), false);
        }

        var staged = new List<StagedChange>();
        string? failure = null;
        try
        {
            foreach (var achievement in achievements)
            {
                if (steam.SetAchievement(achievement.Id, achievement.IsChecked) == false)
                {
                    failure = $"Steam refused to {(achievement.IsChecked ? "unlock" : "lock")} " +
                        $"\"{achievement.Name}\" ({achievement.Id}).";
                    break;
                }

                var original = achievement.IsAchieved;
                staged.Add(new StagedChange(achievement.Id, () => steam.SetAchievement(achievement.Id, original)));
            }

            if (failure == null)
            {
                foreach (var stat in stats)
                {
                    if (SetStatValue(steam, stat, stat.Value) == false)
                    {
                        failure = $"Steam refused the new value for the stat \"{stat.DisplayName}\" ({stat.Id}).";
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
            failure = $"Something went wrong while preparing the changes: {e.Message}";
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
        catch (SteamStoreException e) when (e.Failure is SteamStoreFailure.Refused or SteamStoreFailure.Rejected)
        {
            // Refused: nothing was sent, the edits stay. Rejected: Steam's servers didn't
            // take them; reload to show what Steam has now.
            return (this.FailAndUndo(steam, staged, e.Message), e.Failure == SteamStoreFailure.Rejected);
        }

        // Stored: the rows are no longer pending, so closing during the reload doesn't warn.
        foreach (var achievement in achievements)
        {
            achievement.MarkCommitted();
        }

        foreach (var stat in stats)
        {
            stat.MarkCommitted();
        }

        this.UpdatePending();

        var parts = new List<string>();
        if (achievements.Count > 0)
        {
            parts.Add(Plural(achievements.Count, "achievement"));
        }

        if (stats.Count > 0)
        {
            parts.Add(Plural(stats.Count, "stat"));
        }

        return (new GameOperationResult(true, "Changes stored", $"Stored {string.Join(" and ", parts)}."), true);
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

        var message = $"Nothing was stored. {failure}";
        if (notUndone.Count > 0)
        {
            notUndone.Reverse();
            message += $" SAM couldn't undo the change already made to {string.Join(", ", notUndone)}; " +
                "Steam may upload it the next time this game's stats are stored.";
        }

        return new GameOperationResult(false, "Couldn't store changes", message);
    }

    /// <summary>Resets every stat for this game (and optionally its achievements) on Steam, then reloads.</summary>
    public async Task<GameOperationResult> ResetAllStatsAsync(bool achievementsToo)
    {
        this.IsWorking = true;
        GameOperationResult result;
        try
        {
            result = this._services.Steam.ResetAllStats(achievementsToo)
                ? new GameOperationResult(true, "Reset complete", achievementsToo
                    ? "All stats and achievements were reset."
                    : "All stats were reset.")
                : new GameOperationResult(false, "Reset failed", "Steam refused to reset the stats.");
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Resetting stats for app {this.AppId} failed", e);
            result = new GameOperationResult(false, "Reset failed", e.Message);
        }
        finally
        {
            this.IsWorking = false;
        }

        await this.LoadAsync();
        this.ShowStatus(result);
        return result;
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
