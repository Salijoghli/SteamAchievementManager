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
    public ObservableCollection<AchievementViewModel> VisibleAchievements { get; } = [];

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
        foreach (var stat in this.Stats)
        {
            stat.IsEditingAllowed = value;
        }
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

    /// <summary>Makes <paramref name="target"/> equal to <paramref name="items"/> with minimal changes, so rows animate.</summary>
    private static void Sync(ObservableCollection<AchievementViewModel> target, List<AchievementViewModel> items)
    {
        var wanted = new HashSet<AchievementViewModel>(items);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(target[i]) == false)
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], items[i]))
            {
                continue;
            }

            var existing = target.IndexOf(items[i]);
            if (existing >= 0)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, items[i]);
            }
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
    [RelayCommand(CanExecute = nameof(CanEditAchievements))]
    private void UnlockAll() => this.SetAll(_ => true);

    [RelayCommand(CanExecute = nameof(CanEditAchievements))]
    private void LockAll() => this.SetAll(_ => false);

    [RelayCommand(CanExecute = nameof(CanEditAchievements))]
    private void Invert() => this.SetAll(a => a.IsChecked == false);

    private bool CanEditAchievements() => this.IsBusy == false && this.IsLoaded;

    private void SetAll(Func<AchievementViewModel, bool> desired)
    {
        this._suspendPendingUpdates = true;
        try
        {
            foreach (var achievement in this._achievements)
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
    /// Writes the pending changes to Steam like the old manager: SetAchievement for each
    /// changed achievement, SetStat for each modified stat, then StoreStats. Then reloads.
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
        try
        {
            result = this.Store();
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

        await this.LoadAsync();
        this.ShowStatus(result);
        return result;
    }

    private GameOperationResult Store()
    {
        var steam = this._services.Steam;
        var achievements = this._achievements.Where(a => a.IsPending && a.IsProtected == false).ToList();
        foreach (var achievement in achievements)
        {
            if (steam.SetAchievement(achievement.Id, achievement.IsChecked) == false)
            {
                return new GameOperationResult(false, "Couldn't store changes",
                    $"An error occurred while setting the state for {achievement.Id}, aborting store.");
            }
        }

        var stats = this.Stats.Where(s => s.IsModified && s.IsProtected == false && s.HasError == false).ToList();
        foreach (var stat in stats)
        {
            var ok = stat.IsInteger
                ? steam.SetStat(stat.Id, (int)stat.Value)
                : steam.SetStat(stat.Id, (float)stat.Value);
            if (ok == false)
            {
                return new GameOperationResult(false, "Couldn't store changes",
                    $"An error occurred while setting the value for {stat.Id}, aborting store.");
            }
        }

        if (steam.StoreStats() == false)
        {
            return new GameOperationResult(false, "Couldn't store changes", "Steam refused to store the changes.");
        }

        return new GameOperationResult(true, "Changes stored",
            $"Stored {Plural(achievements.Count, "achievement")} and {Plural(stats.Count, "stat")}.");
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
