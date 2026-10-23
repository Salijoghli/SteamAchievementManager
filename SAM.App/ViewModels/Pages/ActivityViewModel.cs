// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels.Pages;

/// <summary>One day in the Activity feed: its unlocks, newest first.</summary>
public sealed class ActivityDayGroup(string title, IEnumerable<UnlockItemViewModel> items) : List<UnlockItemViewModel>(items)
{
    public string Title { get; } = title;

    public string CountText => Strings.Plural("Pages_Unlocks", this.Count);

    /// <summary>What screen readers announce for the group header.</summary>
    public override string ToString() => $"{this.Title}, {this.CountText}";
}

/// <summary>
/// Recent unlocks across every game, from the unlock timeline, grouped by day and filtered by a
/// game/achievement search and a date range. Icons and rarity load as rows scroll into view.
/// </summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly AppServices _services;
    private readonly LibraryViewModel _library;
    private readonly GlobalPercentLoader _percents;
    private IReadOnlyList<UnlockItemViewModel> _all = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _searchDebounce;
    private DateTime _loadedAt = DateTime.MinValue;
    private int _loadedGameCount;

    public ActivityViewModel(AppServices services, LibraryViewModel library)
    {
        this._services = services;
        this._library = library;
        this._percents = new GlobalPercentLoader(services.GlobalPercents);
        library.PropertyChanged += this.OnLibraryPropertyChanged;
    }

    /// <summary>Date range choices, in <see cref="RangeIndex"/> order.</summary>
    public IReadOnlyList<string> RangeOptions { get; } =
    [
        Strings.Get("Activity_RangeLast7Days"),
        Strings.Get("Activity_RangeLast30Days"),
        Strings.Get("Activity_RangeLast90Days"),
        Strings.Get("Activity_RangeAllTime"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible), nameof(IsEmpty), nameof(IsNoMatches))]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>Set when the timeline couldn't be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsListVisible), nameof(IsEmpty), nameof(IsNoMatches))]
    private string? _error;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _rangeIndex = 3;

    [ObservableProperty]
    private IReadOnlyList<ActivityDayGroup> _groups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsNoMatches))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoMatches))]
    private int _shownCount;

    [ObservableProperty]
    private string _countText = "";

    public bool HasError => this.Error != null;

    public bool IsListVisible => this.IsLoading == false && this.Error == null;

    /// <summary>No unlocks at all.</summary>
    public bool IsEmpty => this.IsLoading == false && this.Error == null && this.TotalCount == 0;

    /// <summary>Unlocks exist, but none match the filters.</summary>
    public bool IsNoMatches => this.IsLoading == false && this.Error == null && this.TotalCount > 0 && this.ShownCount == 0;

    /// <summary>
    /// True while the feed is rebuilt by a refresh (not a filter change): the page keeps its
    /// scroll position instead of jumping to the top.
    /// </summary>
    public bool KeepScrollPosition { get; private set; }

    partial void OnSearchTextChanged(string value) => this.DebounceFilter();

    partial void OnRangeIndexChanged(int value) => this.ApplyFilter();

    /// <summary>Rebuilding the feed is expensive, so the search waits until typing pauses.</summary>
    private async void DebounceFilter()
    {
        this._searchDebounce?.Cancel();
        var cts = this._searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
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

    /// <summary>Loads on first show, and again when the data is a few minutes old.</summary>
    public void OnShown()
    {
        if (this.IsRefreshing == false && DateTime.UtcNow - this._loadedAt > StaleAfter)
        {
            _ = this.LoadAsync();
        }
    }

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.IsSummaryReady) && this._library.IsSummaryReady &&
            this._loadedGameCount == 0 && this.IsRefreshing == false)
        {
            _ = this.LoadAsync();
        }
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        this._percents.Clear();
        return this.LoadAsync();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        this.SearchText = "";
        this.RangeIndex = 3;
        this.ApplyFilter(); // Don't wait out the search debounce.
    }

    private async Task LoadAsync()
    {
        this._cts?.Cancel();
        var cts = this._cts = new CancellationTokenSource();
        this.IsRefreshing = true;

        int gameCount = 0;
        IReadOnlyList<UnlockEvent>? events = null;
        string? error = null;
        try
        {
            (gameCount, events) = await Task.Run(() =>
            {
                IReadOnlyList<GameEntry> games = this._services.Games.LoadCached() ?? [];
                var unlocks = games.Count == 0 ? Array.Empty<UnlockEvent>() : this._services.Timeline.Load(games);
                return (games.Count, unlocks);
            });
        }
        catch (Exception e)
        {
            Diagnostics.Log("Activity: couldn't load the unlock timeline", e);
            error = e is NotImplementedException
                ? Strings.Get("Pages_TimelineUnavailable")
                : Strings.Format("Pages_TimelineError", e.Message);
        }
        finally
        {
            if (this._cts == cts)
            {
                this.IsRefreshing = false;
            }
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        this._loadedAt = DateTime.UtcNow;
        this._loadedGameCount = gameCount;
        this.Error = error;

        // Rows that were already loaded keep their view models (and loaded icons), so a refresh
        // that finds nothing new leaves the feed as it is.
        var existing = new Dictionary<UnlockEvent, UnlockItemViewModel>();
        foreach (var item in this._all)
        {
            existing.TryAdd(item.Event, item);
        }

        this._all = (events ?? [])
            .OrderByDescending(e => e.Time)
            .Select(e => existing.GetValueOrDefault(e) ?? new UnlockItemViewModel(e, this._services.Images, this._percents))
            .ToList();
        this.TotalCount = this._all.Count;
        this.IsLoading = false;
        this.KeepScrollPosition = true;
        try
        {
            this.ApplyFilter();
        }
        finally
        {
            this.KeepScrollPosition = false;
        }
    }

    private void ApplyFilter()
    {
        // A search waiting out its debounce is covered by this pass.
        this._searchDebounce?.Cancel();
        this._searchDebounce = null;

        var query = this.SearchText.Trim();
        DateTime? since = this.RangeIndex switch
        {
            0 => DateTime.Now.Date.AddDays(-6),
            1 => DateTime.Now.Date.AddDays(-29),
            2 => DateTime.Now.Date.AddDays(-89),
            _ => null,
        };

        var shown = this._all.Where(item =>
            (since == null || item.LocalTime >= since) &&
            (query.Length == 0 ||
             item.GameName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)));

        var groups = shown
            .GroupBy(item => item.LocalTime.Date)
            .Select(g => new ActivityDayGroup(PageActions.DayTitle(g.Key), g))
            .ToList();

        this.ShownCount = groups.Sum(g => g.Count);

        // Same rows as now (e.g. a trailing space, or a refresh with nothing new): keep the
        // feed, and with it the scroll position.
        if (SameGroups(this.Groups, groups) == false)
        {
            this.Groups = groups;
        }
        this.CountText = this.Error != null ? ""
            : this.ShownCount == this.TotalCount ? Unlocks(this.TotalCount)
            : Strings.Plural("Activity_ShownOfTotal", this.TotalCount, LibraryFormat.Count(this.ShownCount));
    }

    private static string Unlocks(int count) => Strings.Plural("Pages_Unlocks", count);

    private static bool SameGroups(IReadOnlyList<ActivityDayGroup> a, IReadOnlyList<ActivityDayGroup> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Title != b[i].Title || a[i].SequenceEqual(b[i]) == false)
            {
                return false;
            }
        }

        return true;
    }
}
