// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SAM.App.ViewModels;

public enum LibraryCompletionFilter
{
    All,
    HasAchievements,
    NotStarted,
    InProgress,
    Perfect,
}

public enum LibraryLastPlayedFilter
{
    Any,
    Today,
    Past7Days,
    Past30Days,
    ThisYear,
    Never,
    Custom,
}

/// <summary>A smart list: a built-in filter with its own order.</summary>
public sealed record LibrarySmartList(string Key, string Name, string Description);

/// <summary>An entry in the search box's suggestion list.</summary>
public sealed record LibrarySuggestion(string Text, uint AppId, GameCardViewModel? Card)
{
    public override string ToString() => this.Text;
}

/// <summary>
/// State of the library window: a master list of <see cref="GameCardViewModel"/>s (one per owned game,
/// reused across refreshes) and the filtered/sorted <see cref="Games"/> collection shown in the grid.
/// All members must be used on the UI thread; background work marshals back through the dispatcher.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    // Above this many inserts/removes/moves, swapping in a new collection is cheaper than diffing.
    private const int MaxIncrementalChanges = 150;

    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<uint, GameCardViewModel> _master = [];
    private readonly HashSet<string> _loggedErrors = [];
    private bool _initializing;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _enrichCts;
    private bool _isShutDown;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Smart list keys stored in <see cref="AppSettings.SmartList"/>.</summary>
    public const string AlmostPerfectList = "almost-perfect";

    public const string NeverStartedList = "never-started";

    public const string RarestOwnedList = "rarest-owned";

    // Column keys for the list view's sortable headers.
    public const string NameColumn = "name";
    public const string ProgressColumn = "progress";
    public const string PlaytimeColumn = "playtime";
    public const string LastPlayedColumn = "lastplayed";
    public const string InstalledColumn = "installed";
    public const string FavoriteColumn = "favorite";

    public LibraryViewModel(AppServices services)
    {
        this._services = services;
        this._dispatcher = DispatcherQueue.GetForCurrentThread();

        var settings = services.Settings;
        this._initializing = true;
        this.SortIndex = Enum.IsDefined(settings.LibrarySort) ? (int)settings.LibrarySort : (int)LibrarySort.RecentlyPlayed;
        this.SortDescending = settings.SortDescending;
        this.PinFavorites = settings.PinFavorites;
        this.ShowGames = settings.ShowGames;
        this.ShowDemos = settings.ShowDemos;
        this.ShowMods = settings.ShowMods;
        this.ShowJunk = settings.ShowJunk;
        this.FavoritesOnly = settings.FavoritesOnly;
        this.CompletionFilterIndex = Math.Clamp(settings.CompletionFilter, 0, (int)LibraryCompletionFilter.Perfect);
        this.LastPlayedFilterIndex = Math.Clamp(settings.LastPlayedFilter, 0, (int)LibraryLastPlayedFilter.Custom);
        this.CustomFrom = settings.LastPlayedFrom;
        this.CustomTo = settings.LastPlayedTo;
        this.ThemeIndex = settings.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        this.KeepOtherAccountsCache = settings.KeepOtherAccountsCache;
        this.InstalledOnly = settings.InstalledOnly;
        this.ShowHidden = settings.ShowHidden;
        this.CollectionFilter = string.IsNullOrEmpty(settings.CollectionFilter) ? null : settings.CollectionFilter;
        this.SmartList = SmartLists.Any(l => l.Key == settings.SmartList) ? settings.SmartList : null;
        this.IsListView = string.Equals(settings.LibraryViewMode, "List", StringComparison.OrdinalIgnoreCase);
        this.SpoilerProtection = settings.SpoilerProtection;
        this.BackupBeforeCommit = settings.BackupBeforeCommit;
        this.MinimizeToTray = settings.MinimizeToTray;
        this.ShowNotifications = settings.ShowNotifications;
        this.CheckForUpdates = settings.CheckForUpdates;
        this._initializing = false;
        this.UpdateActiveFilterCount();

        if (services.AccountSwitched)
        {
            this.IsAccountNoticeOpen = true;
            this.AccountNoticeMessage = settings.KeepOtherAccountsCache
                ? "A different Steam account is logged in. Cached data from the previous account was kept."
                : "A different Steam account is logged in. Cached data from the previous account was cleared.";
        }

        services.Favorites.Changed += this.OnFavoritesStoreChanged;
        services.Collections.Changed += this.OnCollectionsStoreChanged;
    }

    /// <summary>The smart lists offered in the header, in menu order.</summary>
    public static IReadOnlyList<LibrarySmartList> SmartLists { get; } =
    [
        new(AlmostPerfectList, "Almost perfect", "80% or more unlocked, fewest remaining first"),
        new(NeverStartedList, "Never started", "Has achievements but none unlocked, most played first"),
        new(RarestOwnedList, "Rarest owned", "Ordered by the rarest achievement you've unlocked"),
    ];

    /// <summary>Every owned game (unfiltered), for pages that summarize the whole library.</summary>
    public IReadOnlyCollection<GameCardViewModel> AllGames => this._master.Values;

    #region Observable state
    [ObservableProperty]
    private ObservableCollection<GameCardViewModel> _games = [];

    /// <summary>True while there is nothing to show yet (skeleton grid).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryEmpty), nameof(IsNoMatches), nameof(IsGridVisible), nameof(IsGridViewShown), nameof(IsListViewShown), nameof(IsGridSkeletonShown), nameof(IsListSkeletonShown))]
    private bool _isLoading = true;

    /// <summary>True while the game list is being refreshed in the background.</summary>
    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>Set when the list couldn't be loaded and there's nothing cached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadErrorOpen), nameof(IsLibraryEmpty), nameof(IsNoMatches), nameof(IsGridVisible), nameof(IsGridViewShown), nameof(IsListViewShown))]
    private string? _loadError;

    [ObservableProperty]
    private int _shownCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryEmpty), nameof(IsNoMatches))]
    private int _totalCount;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private bool _isSummaryReady;

    [ObservableProperty]
    private string _completionText = "";

    [ObservableProperty]
    private string _completionDetail = "";

    [ObservableProperty]
    private string _perfectText = "";

    [ObservableProperty]
    private string _totalPlaytimeText = "";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _sortIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionGlyph), nameof(SortDirectionLabel))]
    private bool _sortDescending;

    [ObservableProperty]
    private bool _pinFavorites;

    [ObservableProperty]
    private bool _showGames;

    [ObservableProperty]
    private bool _showDemos;

    [ObservableProperty]
    private bool _showMods;

    [ObservableProperty]
    private bool _showJunk;

    [ObservableProperty]
    private bool _favoritesOnly;

    [ObservableProperty]
    private int _completionFilterIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRange))]
    private int _lastPlayedFilterIndex;

    [ObservableProperty]
    private DateTimeOffset? _customFrom;

    [ObservableProperty]
    private DateTimeOffset? _customTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters), nameof(ActiveFilterBadge))]
    private int _activeFilterCount;

    [ObservableProperty]
    private bool _isNoticeOpen;

    [ObservableProperty]
    private string _noticeTitle = "";

    [ObservableProperty]
    private string _noticeMessage = "";

    [ObservableProperty]
    private InfoBarSeverity _noticeSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private bool _isAccountNoticeOpen;

    [ObservableProperty]
    private string _accountNoticeMessage = "";

    /// <summary>Set when a refresh failed but cached data is shown.</summary>
    [ObservableProperty]
    private bool _isRefreshWarningOpen;

    [ObservableProperty]
    private string _refreshWarning = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequestedTheme))]
    private int _themeIndex;

    [ObservableProperty]
    private bool _keepOtherAccountsCache;

    [ObservableProperty]
    private bool _installedOnly;

    [ObservableProperty]
    private bool _showHidden;

    /// <summary>The selected collection's <see cref="GameCollection.FilterKey"/>, or null for all games.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollectionFilterName), nameof(IsCollectionFilterActive))]
    private string? _collectionFilter;

    /// <summary>The selected smart list's key, or null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSmartListActive), nameof(SmartListName), nameof(IsSortEnabled), nameof(ActiveSortColumn))]
    private string? _smartList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridViewShown), nameof(IsListViewShown), nameof(IsGridSkeletonShown), nameof(IsListSkeletonShown))]
    private bool _isListView;

    [ObservableProperty]
    private bool _spoilerProtection;

    [ObservableProperty]
    private bool _backupBeforeCommit;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _showNotifications;

    [ObservableProperty]
    private bool _checkForUpdates;

    /// <summary>True while a CSV export runs (progress InfoBar).</summary>
    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _exportProgressText = "";

    [ObservableProperty]
    private bool _isExportDoneOpen;

    [ObservableProperty]
    private string _exportDoneMessage = "";

    /// <summary>Path of the last exported file.</summary>
    [ObservableProperty]
    private string? _exportedPath;

    [ObservableProperty]
    private bool _isUpdateOpen;

    [ObservableProperty]
    private string _updateTitle = "";

    [ObservableProperty]
    private string _updateMessage = "";
    #endregion

    public bool IsLoadErrorOpen
    {
        get => this.LoadError != null;
        set
        {
            if (value == false)
            {
                this.LoadError = null;
            }
        }
    }

    public bool IsGridVisible => this.IsLoading == false && this.LoadError == null;

    public bool IsGridViewShown => this.IsGridVisible && this.IsListView == false;

    public bool IsListViewShown => this.IsGridVisible && this.IsListView;

    public bool IsGridSkeletonShown => this.IsLoading && this.IsListView == false;

    public bool IsListSkeletonShown => this.IsLoading && this.IsListView;

    public bool IsSmartListActive => this.SmartList != null;

    public string SmartListName => SmartLists.FirstOrDefault(l => l.Key == this.SmartList)?.Name ?? "Smart lists";

    /// <summary>Smart lists have their own order, so the sort controls are disabled while one is active.</summary>
    public bool IsSortEnabled => this.IsSmartListActive == false;

    public bool IsCollectionFilterActive => this.CollectionFilter != null;

    /// <summary>Name shown on the collection picker.</summary>
    public string CollectionFilterName =>
        this.CollectionFilter == null ? "All games" : this.FindCollection(this.CollectionFilter)?.Name ?? "All games";

    /// <summary>Steam collections with explicit app lists (read-only).</summary>
    public IReadOnlyList<GameCollection> SteamCollections { get; private set; } = [];

    /// <summary>Collections made in SAM.</summary>
    public IReadOnlyList<GameCollection> CustomCollections { get; private set; } = [];

    /// <summary>The list view column the games are sorted by ("" when none matches, e.g. App ID or a smart list).</summary>
    public string ActiveSortColumn => this.IsSmartListActive
        ? ""
        : this._columnSort ?? (LibrarySort)this.SortIndex switch
        {
            LibrarySort.Name => NameColumn,
            LibrarySort.RecentlyPlayed => LastPlayedColumn,
            LibrarySort.Playtime => PlaytimeColumn,
            LibrarySort.Progress => ProgressColumn,
            _ => "",
        };

    public bool IsLibraryEmpty => this.IsLoading == false && this.LoadError == null && this.TotalCount == 0;

    public bool IsNoMatches => this.IsLoading == false && this.LoadError == null && this.TotalCount > 0 && this.Games.Count == 0;

    public bool IsCustomRange => this.LastPlayedFilterIndex == (int)LibraryLastPlayedFilter.Custom;

    public bool HasActiveFilters => this.ActiveFilterCount > 0;

    public int ActiveFilterBadge => this.ActiveFilterCount;

    public string SortDirectionGlyph => this.SortDescending ? "" : "";

    public string SortDirectionLabel => this.SortDescending ? "Descending (click for ascending)" : "Ascending (click for descending)";

    public ElementTheme RequestedTheme => this.ThemeIndex switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public string SteamIdText => this._services.Steam.SteamId.ToString(CultureInfo.InvariantCulture);

    #region Loading
    /// <summary>Shows the cached list immediately (if any), then refreshes in the background.</summary>
    public async Task InitializeAsync()
    {
        IReadOnlyList<GameEntry>? cached = null;
        try
        {
            cached = this._services.Games.LoadCached();
        }
        catch (Exception e)
        {
            this.LogOnce("LoadCached", e);
        }

        this.ReloadCustomCollections();
        if (cached is { Count: > 0 })
        {
            this.SetGames(cached);
        }

        this.CheckForUpdatesOnStartup();
        await this.RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (this._isShutDown)
        {
            return;
        }

        this._refreshCts?.Cancel();
        var cts = this._refreshCts = new CancellationTokenSource();

        this.IsRefreshing = true;
        this.ReloadSteamCollections();
        this.LoadError = null;
        this.IsRefreshWarningOpen = false;
        if (this._master.Count == 0)
        {
            this.IsLoading = true;
        }

        try
        {
            var games = await this._services.Games.RefreshAsync(cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            this.SetGames(games);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't refresh the game list", e);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var reason = e is HttpRequestException
                ? "The game list couldn't be downloaded. Check your internet connection."
                : e.Message;
            if (this._master.Count == 0)
            {
                this.LoadError = reason;
                this.IsLoading = false;
            }
            else
            {
                this.RefreshWarning = $"Showing your cached library. {reason}";
                this.IsRefreshWarningOpen = true;
            }
        }
        finally
        {
            if (this._refreshCts == cts)
            {
                this.IsRefreshing = false;
                this._refreshCts = null;
            }
        }
    }

    /// <summary>Merges <paramref name="entries"/> into the master list, reusing existing cards.</summary>
    private void SetGames(IReadOnlyList<GameEntry> entries)
    {
        var seen = new HashSet<uint>();
        foreach (var entry in entries)
        {
            if (seen.Add(entry.AppId) == false)
            {
                continue;
            }

            if (this._master.TryGetValue(entry.AppId, out var card))
            {
                card.Update(entry);
            }
            else
            {
                this._master[entry.AppId] = this.CreateCard(entry);
            }
        }

        foreach (var id in this._master.Keys.Where(id => seen.Contains(id) == false).ToList())
        {
            this._master[id].ImageLoad?.Cancel();
            this._master.Remove(id);
        }

        this.TotalCount = this._master.Count;
        this.IsLoading = false;
        this.ApplyFilter();
        this.StartEnrichment();
    }

    private DateTime _lastEnrichment;

    /// <summary>
    /// Re-reads playtime and achievement progress from Steam's local files, e.g. when the user
    /// comes back from a game window where they committed changes. Throttled to once every few
    /// seconds; does nothing while the library is still loading.
    /// </summary>
    public void RefreshProgress()
    {
        if (this._isShutDown || this.IsLoading || this._master.Count == 0 ||
            DateTime.UtcNow - this._lastEnrichment < TimeSpan.FromSeconds(3))
        {
            return;
        }

        this.StartEnrichment();
    }

    /// <summary>Loads playtime and achievement progress off the UI thread, updating cards as results arrive.</summary>
    private async void StartEnrichment()
    {
        this._lastEnrichment = DateTime.UtcNow;
        this._enrichCts?.Cancel();
        var cts = this._enrichCts = new CancellationTokenSource();
        var token = cts.Token;
        var cards = this._master.Values.ToArray();

        // Installed games: one pass over Steam's app manifests.
        var installed = await Task.Run(() =>
        {
            try
            {
                return this._services.Installed.Load();
            }
            catch (Exception e)
            {
                this.LogOnce("Installed", e);
                return null;
            }
        });
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (installed != null)
        {
            this._installed = installed;
            foreach (var card in cards)
            {
                card.Installed = installed.TryGetValue(card.AppId, out var game) ? game : null;
            }

            if (this.InstalledOnly || this._columnSort == InstalledColumn)
            {
                this.ApplyFilter();
            }
        }

        // Playtime: one file read per refresh.
        var playtime = await Task.Run(() =>
        {
            try
            {
                return this._services.Playtime.Load();
            }
            catch (Exception e)
            {
                this.LogOnce("Playtime", e);
                return (IReadOnlyDictionary<uint, PlaytimeInfo>)new Dictionary<uint, PlaytimeInfo>();
            }
        });
        if (token.IsCancellationRequested)
        {
            return;
        }

        foreach (var card in cards)
        {
            card.Playtime = playtime.TryGetValue(card.AppId, out var info) ? info : null;
            card.IsPlaytimeLoaded = true;
        }

        if (this.NeedsResortForPlaytime())
        {
            this.ApplyFilter();
        }

        // Progress: schemas load from disk, so run a few at a time and flush results in batches.
        var results = new ConcurrentQueue<(GameCardViewModel Card, GameProgress? Progress)>();
        var flushTimer = this._dispatcher.CreateTimer();
        flushTimer.Interval = TimeSpan.FromMilliseconds(120);
        flushTimer.Tick += (_, _) => this.FlushProgress(results, token, final: false);
        flushTimer.Start();

        try
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
                CancellationToken = token,
            };
            await Task.Run(() => Parallel.ForEach(cards, options, card =>
            {
                GameProgress? progress = null;
                try
                {
                    var schema = this._services.Schemas.Load(card.AppId);
                    progress = this._services.Progress.GetCachedProgress(card.AppId, schema);
                }
                catch (Exception e)
                {
                    this.LogOnce("Progress", e);
                }

                results.Enqueue((card, progress));
            }), token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            flushTimer.Stop();
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        this.FlushProgress(results, token, final: true);
        this.UpdateSummary();
        if (this.SmartList == RarestOwnedList)
        {
            this.StartRarestLoad();
        }

        if (this.CompletionFilterIndex != 0 || this.SortIndex == (int)LibrarySort.Progress || this.IsSmartListActive)
        {
            this.ApplyFilter();
        }
    }

    private void FlushProgress(ConcurrentQueue<(GameCardViewModel Card, GameProgress? Progress)> results, CancellationToken token, bool final)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        var any = false;
        while (results.TryDequeue(out var result))
        {
            result.Card.Progress = result.Progress;
            result.Card.IsProgressLoaded = true;
            any = true;
        }

        // Grow a completion-filtered view as results arrive; a final pass re-sorts.
        if (any && final == false && (this.CompletionFilterIndex != 0 || this.IsSmartListActive))
        {
            this.ApplyFilter();
        }
    }

    private bool NeedsResortForPlaytime() =>
        this.SortIndex is (int)LibrarySort.RecentlyPlayed or (int)LibrarySort.Playtime ||
        this.LastPlayedFilterIndex != 0 || this.SmartList == NeverStartedList;

    private void UpdateSummary()
    {
        int unlocked = 0, total = 0, perfect = 0;
        var playtime = TimeSpan.Zero;
        foreach (var card in this._master.Values)
        {
            if (card.Progress is { Total: > 0 } p)
            {
                unlocked += p.Unlocked;
                total += p.Total;
                if (p.IsPerfect)
                {
                    perfect++;
                }
            }

            playtime += card.PlaytimeSpan;
        }

        var percent = total == 0 ? 0 : (double)unlocked / total * 100;
        this.CompletionText = $"{percent.ToString("0.#", CultureInfo.CurrentCulture)}% complete";
        this.CompletionDetail = $"{LibraryFormat.Count(unlocked)} of {LibraryFormat.Count(total)} achievements unlocked across games with achievements";
        this.PerfectText = $"{LibraryFormat.Count(perfect)} perfect";
        this.TotalPlaytimeText = $"{LibraryFormat.Playtime(playtime)} played";
        this.IsSummaryReady = true;
    }
    #endregion

    #region Images
    /// <summary>Starts loading the card image if it isn't loaded or loading already.</summary>
    public async void RequestImage(GameCardViewModel card)
    {
        if (card.ImagePath != null || card.ImageFailed || card.ImageLoad != null || this._isShutDown)
        {
            return;
        }

        var cts = card.ImageLoad = new CancellationTokenSource();
        try
        {
            card.ImageUrls ??= this._services.Art.GetCardImageUrls(card.AppId);
            var path = await this._services.Images.GetAsync(card.ImageUrls, cts.Token);
            if (cts.IsCancellationRequested == false)
            {
                card.ImagePath = path;
                card.ImageFailed = path == null;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            this.LogOnce("Image", e);
            card.ImageFailed = true;
        }
        finally
        {
            if (card.ImageLoad == cts)
            {
                card.ImageLoad = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Cancels a pending image load, e.g. when the card's container is recycled.</summary>
    public void CancelImage(GameCardViewModel card)
    {
        var cts = card.ImageLoad;
        card.ImageLoad = null;
        cts?.Cancel();
    }
    #endregion

    #region Filtering and sorting
    partial void OnSearchTextChanged(string value) => this.ApplyFilter();

    partial void OnSortIndexChanged(int value)
    {
        this._columnSort = null;
        this.OnPropertyChanged(nameof(this.ActiveSortColumn));
        this.OnViewSettingChanged();
    }

    partial void OnSortDescendingChanged(bool value) => this.OnViewSettingChanged();

    partial void OnPinFavoritesChanged(bool value) => this.OnViewSettingChanged();

    partial void OnShowGamesChanged(bool value) => this.OnViewSettingChanged();

    partial void OnShowDemosChanged(bool value) => this.OnViewSettingChanged();

    partial void OnShowModsChanged(bool value) => this.OnViewSettingChanged();

    partial void OnShowJunkChanged(bool value) => this.OnViewSettingChanged();

    partial void OnFavoritesOnlyChanged(bool value) => this.OnViewSettingChanged();

    partial void OnCompletionFilterIndexChanged(int value) => this.OnViewSettingChanged();

    partial void OnLastPlayedFilterIndexChanged(int value) => this.OnViewSettingChanged();

    partial void OnCustomFromChanged(DateTimeOffset? value) => this.OnViewSettingChanged();

    partial void OnCustomToChanged(DateTimeOffset? value) => this.OnViewSettingChanged();

    partial void OnInstalledOnlyChanged(bool value) => this.OnViewSettingChanged();

    partial void OnShowHiddenChanged(bool value) => this.OnViewSettingChanged();

    partial void OnCollectionFilterChanged(string? value) => this.OnViewSettingChanged();

    partial void OnSmartListChanged(string? value)
    {
        if (value == RarestOwnedList && this._initializing == false)
        {
            this.StartRarestLoad();
        }

        this.OnViewSettingChanged();
    }

    partial void OnIsListViewChanged(bool value) => this.SaveSettings();

    partial void OnSpoilerProtectionChanged(bool value) => this.SaveSettings();

    partial void OnBackupBeforeCommitChanged(bool value) => this.SaveSettings();

    partial void OnMinimizeToTrayChanged(bool value) => this.SaveSettings();

    partial void OnShowNotificationsChanged(bool value) => this.SaveSettings();

    partial void OnCheckForUpdatesChanged(bool value) => this.SaveSettings();

    partial void OnThemeIndexChanged(int value) => this.SaveSettings();

    partial void OnKeepOtherAccountsCacheChanged(bool value) => this.SaveSettings();

    private void OnViewSettingChanged()
    {
        if (this._initializing)
        {
            return;
        }

        this.UpdateActiveFilterCount();
        this.SaveSettings();
        this.ApplyFilter();
    }

    private void SaveSettings()
    {
        if (this._initializing)
        {
            return;
        }

        var settings = this._services.Settings;
        settings.LibrarySort = Enum.IsDefined((LibrarySort)this.SortIndex) ? (LibrarySort)this.SortIndex : LibrarySort.Name;
        settings.SortDescending = this.SortDescending;
        settings.PinFavorites = this.PinFavorites;
        settings.ShowGames = this.ShowGames;
        settings.ShowDemos = this.ShowDemos;
        settings.ShowMods = this.ShowMods;
        settings.ShowJunk = this.ShowJunk;
        settings.FavoritesOnly = this.FavoritesOnly;
        settings.CompletionFilter = this.CompletionFilterIndex;
        settings.LastPlayedFilter = this.LastPlayedFilterIndex;
        settings.LastPlayedFrom = this.CustomFrom;
        settings.LastPlayedTo = this.CustomTo;
        settings.Theme = this.ThemeIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        settings.KeepOtherAccountsCache = this.KeepOtherAccountsCache;
        settings.InstalledOnly = this.InstalledOnly;
        settings.ShowHidden = this.ShowHidden;
        settings.CollectionFilter = this.CollectionFilter;
        settings.SmartList = this.SmartList;
        settings.LibraryViewMode = this.IsListView ? "List" : "Grid";
        settings.SpoilerProtection = this.SpoilerProtection;
        settings.BackupBeforeCommit = this.BackupBeforeCommit;
        settings.MinimizeToTray = this.MinimizeToTray;
        settings.ShowNotifications = this.ShowNotifications;
        settings.CheckForUpdates = this.CheckForUpdates;
        try
        {
            settings.Save();
        }
        catch (Exception e)
        {
            this.LogOnce("SaveSettings", e);
        }
    }

    private void UpdateActiveFilterCount()
    {
        var count = 0;
        if (this.ShowGames == false || this.ShowDemos || this.ShowMods || this.ShowJunk)
        {
            count++;
        }

        if (this.FavoritesOnly)
        {
            count++;
        }

        // A smart list replaces the completion filter, so it doesn't count while one is active.
        if (this.CompletionFilterIndex != 0 && this.IsSmartListActive == false)
        {
            count++;
        }

        if (this.LastPlayedFilterIndex != 0)
        {
            count++;
        }

        if (this.InstalledOnly)
        {
            count++;
        }

        if (this.ShowHidden)
        {
            count++;
        }

        this.ActiveFilterCount = count;
    }

    [RelayCommand]
    private void ResetFilters()
    {
        var initializing = this._initializing;
        this._initializing = true;
        this.ShowGames = true;
        this.ShowDemos = false;
        this.ShowMods = false;
        this.ShowJunk = false;
        this.FavoritesOnly = false;
        this.CompletionFilterIndex = 0;
        this.LastPlayedFilterIndex = 0;
        this.CustomFrom = null;
        this.CustomTo = null;
        this.InstalledOnly = false;
        this.ShowHidden = false;
        this._initializing = initializing;
        this.OnViewSettingChanged();
    }

    [RelayCommand]
    private void ClearSearchAndFilters()
    {
        this._suspendFilter = true;
        this.SearchText = "";
        this._initializing = true;
        this.CollectionFilter = null;
        this.SmartList = null;
        this._initializing = false;
        this._suspendFilter = false;
        this.ResetFilters();
    }

    private bool _suspendFilter;

    /// <summary>Rebuilds <see cref="Games"/> from the master list with the current search, filters and sort.</summary>
    public void ApplyFilter()
    {
        if (this._suspendFilter || this._initializing)
        {
            return;
        }

        var search = this.SearchText.Trim();
        var digits = search.Length > 0 && search.All(char.IsAsciiDigit);
        var range = this.GetLastPlayedRange();
        var neverPlayed = this.LastPlayedFilterIndex == (int)LibraryLastPlayedFilter.Never;
        var smartList = this.SmartList;

        // A smart list defines its own completion criteria, so the completion filter is ignored.
        var completion = smartList == null ? (LibraryCompletionFilter)this.CompletionFilterIndex : LibraryCompletionFilter.All;
        var collection = this.CollectionFilter is { } key ? this.FindCollection(key) : null;

        var list = new List<GameCardViewModel>(this._master.Count);
        foreach (var card in this._master.Values)
        {
            card.SmartBadge = this.GetSmartBadge(card, smartList);
            card.IsSmartBadgeLoading = smartList == RarestOwnedList && this.IsRarestPending(card);

            if (this.IsTypeShown(card.Type) == false ||
                (this.FavoritesOnly && card.IsFavorite == false) ||
                (this.InstalledOnly && card.IsInstalled == false) ||
                (this.ShowHidden == false && card.IsHidden) ||
                (collection != null && collection.AppIds.Contains(card.AppId) == false) ||
                MatchesSmartList(card, smartList) == false)
            {
                continue;
            }

            if (search.Length > 0 &&
                card.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) == false &&
                (digits == false || card.AppIdText.StartsWith(search, StringComparison.Ordinal) == false))
            {
                continue;
            }

            if (completion != LibraryCompletionFilter.All)
            {
                var p = card.Progress;
                var match = completion switch
                {
                    LibraryCompletionFilter.HasAchievements => p is { Total: > 0 },
                    LibraryCompletionFilter.NotStarted => p is { Total: > 0, Unlocked: 0 },
                    LibraryCompletionFilter.InProgress => p is { Total: > 0 } q && q.Unlocked > 0 && q.Unlocked < q.Total,
                    LibraryCompletionFilter.Perfect => p is { IsPerfect: true },
                    _ => true,
                };
                if (match == false)
                {
                    continue;
                }
            }

            if (neverPlayed)
            {
                if (card.LastPlayed != null)
                {
                    continue;
                }
            }
            else if (range is { } r && r.Contains(card.LastPlayed) == false)
            {
                continue;
            }

            list.Add(card);
        }

        list.Sort(smartList == null ? this.Compare : this.CompareForSmartList);
        this.Sync(list);

        this.ShownCount = list.Count;
        this.CountText = list.Count == this._master.Count
            ? $"{LibraryFormat.Count(list.Count)} games"
            : $"{LibraryFormat.Count(list.Count)} of {LibraryFormat.Count(this._master.Count)} games";
        this.OnPropertyChanged(nameof(this.IsNoMatches));
    }

    private bool IsTypeShown(string type) => type switch
    {
        "demo" => this.ShowDemos,
        "mod" => this.ShowMods,
        "junk" => this.ShowJunk,
        _ => this.ShowGames,
    };

    private DateRange? GetLastPlayedRange()
    {
        var today = new DateTimeOffset(DateTime.Today);
        return (LibraryLastPlayedFilter)this.LastPlayedFilterIndex switch
        {
            LibraryLastPlayedFilter.Today => new DateRange(today, null),
            LibraryLastPlayedFilter.Past7Days => new DateRange(DateTimeOffset.Now.AddDays(-7), null),
            LibraryLastPlayedFilter.Past30Days => new DateRange(DateTimeOffset.Now.AddDays(-30), null),
            LibraryLastPlayedFilter.ThisYear => new DateRange(new DateTimeOffset(new DateTime(DateTime.Today.Year, 1, 1)), null),
            LibraryLastPlayedFilter.Custom => new DateRange(
                this.CustomFrom is { } from ? new DateTimeOffset(from.Date) : null,
                this.CustomTo is { } to ? new DateTimeOffset(to.Date.AddDays(1).AddTicks(-1)) : null),
            _ => null,
        };
    }

    private int Compare(GameCardViewModel a, GameCardViewModel b)
    {
        if (this.PinFavorites && a.IsFavorite != b.IsFavorite)
        {
            return a.IsFavorite ? -1 : 1;
        }

        var result = this._columnSort switch
        {
            InstalledColumn => a.IsInstalled.CompareTo(b.IsInstalled),
            FavoriteColumn => a.IsFavorite.CompareTo(b.IsFavorite),
            _ => (LibrarySort)this.SortIndex switch
            {
                LibrarySort.AppId => a.AppId.CompareTo(b.AppId),
                LibrarySort.RecentlyPlayed => Nullable.Compare(a.LastPlayed, b.LastPlayed),
                LibrarySort.Playtime => a.PlaytimeSpan.CompareTo(b.PlaytimeSpan),
                LibrarySort.Progress => a.CompletionSortKey.CompareTo(b.CompletionSortKey),
                _ => 0,
            },
        };

        if (result == 0)
        {
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        if (result == 0)
        {
            result = a.AppId.CompareTo(b.AppId);
        }

        return this.SortDescending ? -result : result;
    }

    /// <summary>Updates <see cref="Games"/> in place when the change is small, else swaps in a new collection.</summary>
    private void Sync(List<GameCardViewModel> target)
    {
        var current = this.Games;
        var targetSet = new HashSet<GameCardViewModel>(target);
        var removals = current.Count(c => targetSet.Contains(c) == false);
        var insertions = target.Count - (current.Count - removals);
        if (removals + insertions > MaxIncrementalChanges)
        {
            this.Games = new ObservableCollection<GameCardViewModel>(target);
            return;
        }

        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (targetSet.Contains(current[i]) == false)
            {
                current.RemoveAt(i);
            }
        }

        var moves = 0;
        for (var i = 0; i < target.Count; i++)
        {
            var item = target[i];
            if (i < current.Count && ReferenceEquals(current[i], item))
            {
                continue;
            }

            var j = IndexOf(current, item, i);
            if (j >= 0)
            {
                if (++moves > MaxIncrementalChanges)
                {
                    this.Games = new ObservableCollection<GameCardViewModel>(target);
                    return;
                }

                current.Move(j, i);
            }
            else
            {
                current.Insert(i, item);
            }
        }
    }

    private static int IndexOf(ObservableCollection<GameCardViewModel> list, GameCardViewModel item, int start)
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

    /// <summary>Suggestions for the search box: an "Open App ID" entry when the text is a number.</summary>
    public IReadOnlyList<LibrarySuggestion> GetSuggestions(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.All(char.IsAsciiDigit) == false ||
            uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) == false ||
            appId == 0)
        {
            return [];
        }

        return this._master.TryGetValue(appId, out var card)
            ? [new LibrarySuggestion($"Open {card.Name} (App ID {appId})", appId, card)]
            : [new LibrarySuggestion($"Open App ID {appId}", appId, null)];
    }

    /// <summary>Handles a chosen suggestion or a submitted query. Returns true if something was opened.</summary>
    public bool Submit(LibrarySuggestion? suggestion, string text)
    {
        suggestion ??= this.GetSuggestions(text).FirstOrDefault();
        if (suggestion == null)
        {
            // Plain text: open the only match, if there's exactly one.
            if (this.Games.Count == 1)
            {
                this.OpenGame(this.Games[0]);
                return true;
            }

            return false;
        }

        if (suggestion.Card != null)
        {
            this.OpenGame(suggestion.Card);
            return true;
        }

        GameEntry? entry;
        try
        {
            entry = this._services.Games.TryAddOwnedApp(suggestion.AppId);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't check ownership of {suggestion.AppId}", e);
            this.ShowNotice("Couldn't check App ID", $"Couldn't check whether you own App ID {suggestion.AppId}: {e.Message}", InfoBarSeverity.Error);
            return false;
        }

        if (entry == null)
        {
            this.ShowNotice("Not owned", $"You don't own App ID {suggestion.AppId}.", InfoBarSeverity.Warning);
            return false;
        }

        if (this._master.TryGetValue(entry.AppId, out var card) == false)
        {
            card = this.CreateCard(entry);
            this._master[entry.AppId] = card;
            this.TotalCount = this._master.Count;
            this.ApplyFilter();
            this.StartEnrichment();
        }

        this.OpenGame(card);
        return true;
    }
    #endregion

    #region Favorites
    private bool IsFavorite(uint appId)
    {
        try
        {
            return this._services.Favorites.Contains(appId);
        }
        catch (Exception e)
        {
            this.LogOnce("Favorites", e);
            return false;
        }
    }

    internal void OnFavoriteToggled(GameCardViewModel card, bool value)
    {
        try
        {
            this._services.Favorites.Set(card.AppId, value);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't save favorites", e);
            this.ShowNotice("Couldn't save favorites", e.Message, InfoBarSeverity.Error);
        }

        if (this.FavoritesOnly || this.PinFavorites)
        {
            this.ApplyFilter();
        }
    }

    private void OnFavoritesStoreChanged(object? sender, uint appId)
    {
        void Sync()
        {
            if (this._master.TryGetValue(appId, out var card))
            {
                card.SyncFavorite(this._services.Favorites.Contains(appId));
            }
        }

        if (this._dispatcher.HasThreadAccess)
        {
            Sync();
        }
        else
        {
            this._dispatcher.TryEnqueue(Sync);
        }
    }
    #endregion

    #region Actions
    internal void OpenGame(GameCardViewModel card) => this.LaunchGame(card, null);

    internal void LaunchGame(GameCardViewModel card, string? extraArgument)
    {
        try
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            startInfo.ArgumentList.Add(card.AppIdText);
            if (extraArgument != null)
            {
                startInfo.ArgumentList.Add(extraArgument);
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't open {card.AppId}", e);
            this.ShowNotice($"Couldn't open {card.Name}", e.Message, InfoBarSeverity.Error);
        }
    }

    internal void CopyAppId(GameCardViewModel card)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(card.AppIdText);
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't copy to the clipboard", e);
            this.ShowNotice("Couldn't copy the App ID", e.Message, InfoBarSeverity.Error);
        }
    }

    internal void OpenUrl(string url) => this.LaunchUri(url, "Couldn't open the browser");

    /// <summary>Starts the game through Steam if it's installed, or opens Steam's install dialog.</summary>
    internal void PlayOrInstall(GameCardViewModel card) => this.LaunchUri(
        card.IsInstalled ? $"steam://rungameid/{card.AppIdText}" : $"steam://install/{card.AppIdText}",
        card.IsInstalled ? $"Couldn't start {card.Name}" : $"Couldn't install {card.Name}");

    private async void LaunchUri(string url, string failureTitle)
    {
        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(new Uri(url)) == false)
            {
                this.ShowNotice(failureTitle, url, InfoBarSeverity.Error);
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't open {url}", e);
            this.ShowNotice(failureTitle, e.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>Opens a file or folder with its default handler (Explorer for folders).</summary>
    public void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't open {path}", e);
            this.ShowNotice("Couldn't open the file", e.Message, InfoBarSeverity.Error);
        }
    }

    public void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        this.NoticeTitle = title;
        this.NoticeMessage = message;
        this.NoticeSeverity = severity;
        this.IsNoticeOpen = false;
        this.IsNoticeOpen = true;
    }

    /// <summary>Total size of the cache, computed off the UI thread.</summary>
    public static Task<long> GetCacheSizeAsync() => Task.Run(() =>
    {
        try
        {
            return AppCache.GetCacheSize();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't measure the cache", e);
            return 0L;
        }
    });

    /// <summary>Deletes the cache and reloads the library from scratch (skeletons again).</summary>
    public async Task ClearCacheAndReloadAsync()
    {
        this._refreshCts?.Cancel();
        this._enrichCts?.Cancel();
        foreach (var card in this._master.Values)
        {
            card.ImageLoad?.Cancel();
        }

        this._master.Clear();
        this.Games = [];
        this.TotalCount = 0;
        this.ShownCount = 0;
        this.IsSummaryReady = false;
        this.IsLoading = true;

        try
        {
            await Task.Run(this._services.Cache.ClearCache);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't clear the cache", e);
            this.ShowNotice("Couldn't clear the cache", e.Message, InfoBarSeverity.Error);
        }

        await this.RefreshAsync();
    }

    /// <summary>Stops background work; call when the window closes.</summary>
    public void Shutdown()
    {
        this._isShutDown = true;
        this._lifetime.Cancel();
        this._services.Collections.Changed -= this.OnCollectionsStoreChanged;
        this._refreshCts?.Cancel();
        this._enrichCts?.Cancel();
        foreach (var card in this._master.Values)
        {
            card.ImageLoad?.Cancel();
        }

        this._services.Favorites.Changed -= this.OnFavoritesStoreChanged;
    }
    #endregion

    private void LogOnce(string key, Exception e)
    {
        lock (this._loggedErrors)
        {
            if (this._loggedErrors.Add(key + ":" + e.GetType().FullName) == false)
            {
                return;
            }
        }

        Diagnostics.Log($"Library: {key} failed", e);
    }
}
