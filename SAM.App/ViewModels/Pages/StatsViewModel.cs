// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.App.Controls.Charts;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels.Pages;

/// <summary>A game in one of the Stats page's ranked lists.</summary>
public sealed partial class RankedGameItem(int rank, uint appId, string name, string valueText, double barPercent, string detailText)
{
    public int Rank { get; } = rank;

    public string RankText => this.Rank.ToString(CultureInfo.CurrentCulture);

    public uint AppId { get; } = appId;

    public string Name { get; } = name;

    public string ValueText { get; } = valueText;

    /// <summary>0-100, for the inline bar.</summary>
    public double BarPercent { get; } = barPercent;

    public string DetailText { get; } = detailText;

    public string AutomationName => $"{this.Rank}. {this.Name}, {this.ValueText}, {this.DetailText}";

    [RelayCommand]
    private void Open() => PageActions.OpenGame(this.AppId);
}

/// <summary>
/// Library-wide statistics. Everything is computed off the UI thread from the cached game list,
/// Steam's local schemas/stats/playtime and the unlock timeline; global percentages for the
/// hunter score and rarest unlocks are fetched afterwards, a few games at a time.
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    /// <summary>Points for an unlock at or below this global percentage (the cap: 100 / 0.1 = 1,000).</summary>
    public const double RarityFloorPercent = 0.1;

    private const int RankedListSize = 8;
    private const int RarestListSize = 10;
    private const int MonthsShown = 24;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly AppServices _services;
    private readonly LibraryViewModel _library;
    private readonly GlobalPercentLoader _percents;
    private CancellationTokenSource? _cts;
    private DateTime _loadedAt = DateTime.MinValue;
    private int _loadedGameCount;

    public StatsViewModel(AppServices services, LibraryViewModel library)
    {
        this._services = services;
        this._library = library;
        this._percents = new GlobalPercentLoader(services.GlobalPercents);
        library.PropertyChanged += this.OnLibraryPropertyChanged;
    }

    #region Observable state
    /// <summary>True until the first load finishes (skeletons).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>True when there are no games to summarize (the library hasn't been loaded yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    private bool _isEmpty;

    [ObservableProperty]
    private string _unlockedText = "";

    [ObservableProperty]
    private string _unlockedDetail = "";

    [ObservableProperty]
    private string _completionText = "";

    [ObservableProperty]
    private string _completionDetail = "";

    [ObservableProperty]
    private string _perfectText = "";

    [ObservableProperty]
    private string _perfectDetail = "";

    [ObservableProperty]
    private string _playtimeText = "";

    [ObservableProperty]
    private string _playtimeDetail = "";

    [ObservableProperty]
    private string _hunterScoreText = "";

    [ObservableProperty]
    private string _hunterScoreDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRarestEmpty), nameof(IsFetchingRarest))]
    private bool _isHunterCalculating;

    [ObservableProperty]
    private double _hunterProgress;

    /// <summary>Set when the unlock timeline couldn't be read; the charts and rarest list show it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline), nameof(HasTimelineError))]
    private string? _timelineError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline), nameof(IsTimelineEmpty))]
    private int _timelineCount;

    [ObservableProperty]
    private IReadOnlyList<ChartPoint>? _monthlyPoints;

    [ObservableProperty]
    private string _monthlySummary = "";

    [ObservableProperty]
    private IReadOnlyList<ChartPoint>? _cumulativePoints;

    [ObservableProperty]
    private string _cumulativeSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRarest), nameof(IsRarestEmpty), nameof(IsFetchingRarest))]
    private int _rarestCount;

    [ObservableProperty]
    private IReadOnlyList<RankedGameItem> _topCompletion = [];

    [ObservableProperty]
    private IReadOnlyList<RankedGameItem> _topPlaytime = [];
    #endregion

    public ObservableCollection<UnlockItemViewModel> RarestUnlocks { get; } = [];

    public bool IsContentVisible => this.IsLoading == false && this.IsEmpty == false;

    public bool HasTimelineError => this.TimelineError != null;

    public bool HasTimeline => this.TimelineError == null && this.TimelineCount > 0;

    public bool IsTimelineEmpty => this.TimelineError == null && this.TimelineCount == 0;

    public bool HasRarest => this.RarestCount > 0;

    public bool IsRarestEmpty => this.RarestCount == 0 && this.IsHunterCalculating == false;

    public bool IsFetchingRarest => this.RarestCount == 0 && this.IsHunterCalculating;

    public static string HunterScoreExplanation =>
        "Hunter score rewards rare unlocks. Each unlocked achievement earns 100 ÷ the percentage of " +
        "players who have it: 1 point if everyone has it, 10 points at 10%, 100 points at 1%, up to " +
        "1,000 points at 0.1% or rarer. Achievements without global stats don't count.";

    /// <summary>Points for one unlocked achievement with the given global percentage.</summary>
    public static double HunterPoints(double percent) => 100 / Math.Max(percent, RarityFloorPercent);

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
        // The game list arrives (or is re-read after clearing the cache): pick it up if we had none.
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

    private async Task LoadAsync()
    {
        this._cts?.Cancel();
        var cts = this._cts = new CancellationTokenSource();
        var token = cts.Token;
        this.IsRefreshing = true;

        Snapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => this.Compute(token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't compute library stats", e);
            snapshot = new Snapshot([], 0, 0, 0, 0, 0, TimeSpan.Zero, 0, [], [], [], e.Message);
        }
        finally
        {
            if (this._cts == cts)
            {
                this.IsRefreshing = false;
            }
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        this._loadedAt = DateTime.UtcNow;
        this._loadedGameCount = snapshot.GameCount;
        this.Apply(snapshot);
        this.IsEmpty = snapshot.GameCount == 0;
        this.IsLoading = false;

        await this.CalculateHunterScoreAsync(snapshot.Events, token);
    }

    private sealed record Snapshot(
        IReadOnlyList<UnlockEvent> Events,
        int GameCount,
        int GamesWithAchievements,
        int Unlocked,
        int Total,
        int Perfect,
        TimeSpan Playtime,
        int PlayedGames,
        IReadOnlyList<RankedGameItem> TopCompletion,
        IReadOnlyList<RankedGameItem> TopPlaytime,
        IReadOnlyList<double> StartedFractions,
        string? TimelineError);

    private Snapshot Compute(CancellationToken token)
    {
        IReadOnlyList<GameEntry> games = this._services.Games.LoadCached() ?? [];

        IReadOnlyDictionary<uint, PlaytimeInfo> playtime;
        try
        {
            playtime = this._services.Playtime.Load();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Stats: couldn't read playtime", e);
            playtime = new Dictionary<uint, PlaytimeInfo>();
        }

        var progress = new (GameEntry Game, GameProgress? Progress)[games.Count];
        Parallel.For(0, games.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = token }, i =>
        {
            GameProgress? value = null;
            try
            {
                var schema = this._services.Schemas.Load(games[i].AppId);
                value = this._services.Progress.GetCachedProgress(games[i].AppId, schema);
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Stats: couldn't read progress for {games[i].AppId}", e);
            }

            progress[i] = (games[i], value);
        });

        int unlocked = 0, total = 0, perfect = 0, withAchievements = 0, played = 0;
        var fractions = new List<double>();
        foreach (var (_, p) in progress)
        {
            if (p is { Total: > 0 } value)
            {
                withAchievements++;
                unlocked += value.Unlocked;
                total += value.Total;
                if (value.IsPerfect)
                {
                    perfect++;
                }

                if (value.Unlocked > 0)
                {
                    fractions.Add(value.Fraction);
                }
            }
        }

        var totalPlaytime = TimeSpan.Zero;
        var owned = games.Select(g => g.AppId).ToHashSet();
        foreach (var (appId, info) in playtime)
        {
            if (owned.Contains(appId) && info.Playtime > TimeSpan.Zero)
            {
                totalPlaytime += info.Playtime;
                played++;
            }
        }

        var topCompletion = progress
            .Where(x => x.Progress is { Total: > 0, Unlocked: > 0 })
            .OrderByDescending(x => x.Progress!.Value.Fraction)
            .ThenByDescending(x => x.Progress!.Value.Total)
            .ThenBy(x => x.Game.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(RankedListSize)
            .Select((x, i) => new RankedGameItem(
                i + 1,
                x.Game.AppId,
                x.Game.Name,
                FormatPercent(x.Progress!.Value.Fraction * 100),
                x.Progress!.Value.Fraction * 100,
                $"{LibraryFormat.Count(x.Progress!.Value.Unlocked)} / {LibraryFormat.Count(x.Progress!.Value.Total)} achievements"))
            .ToList();

        var byPlaytime = games
            .Select(g => (Game: g, Time: playtime.TryGetValue(g.AppId, out var info) ? info.Playtime : TimeSpan.Zero))
            .Where(x => x.Time > TimeSpan.Zero)
            .OrderByDescending(x => x.Time)
            .Take(RankedListSize)
            .ToList();
        var maxTime = byPlaytime.Count > 0 ? byPlaytime[0].Time.TotalHours : 1;
        var topPlaytime = byPlaytime
            .Select((x, i) =>
            {
                var share = totalPlaytime > TimeSpan.Zero ? x.Time / totalPlaytime * 100 : 0;
                return new RankedGameItem(
                    i + 1,
                    x.Game.AppId,
                    x.Game.Name,
                    LibraryFormat.Playtime(x.Time),
                    maxTime > 0 ? x.Time.TotalHours / maxTime * 100 : 0,
                    $"{FormatPercent(share)} of your playtime");
            })
            .ToList();

        token.ThrowIfCancellationRequested();

        IReadOnlyList<UnlockEvent> events = [];
        string? timelineError = null;
        if (games.Count > 0)
        {
            try
            {
                events = this._services.Timeline.Load(games);
            }
            catch (Exception e)
            {
                Diagnostics.Log("Stats: couldn't load the unlock timeline", e);
                timelineError = e is NotImplementedException
                    ? "Unlock history isn't available in this build yet."
                    : $"Couldn't read your unlock history. {e.Message}";
            }
        }

        return new Snapshot(events, games.Count, withAchievements, unlocked, total, perfect, totalPlaytime, played, topCompletion, topPlaytime, fractions, timelineError);
    }

    private void Apply(Snapshot s)
    {
        var percent = s.Total == 0 ? 0 : (double)s.Unlocked / s.Total * 100;
        this.UnlockedText = LibraryFormat.Count(s.Unlocked);
        this.UnlockedDetail = $"of {LibraryFormat.Count(s.Total)} achievements";
        this.CompletionText = FormatPercent(percent);
        this.CompletionDetail = s.StartedFractions.Count == 0
            ? "No games started yet"
            : $"Average started game: {FormatPercent(s.StartedFractions.Average() * 100)}";
        this.PerfectText = LibraryFormat.Count(s.Perfect);
        this.PerfectDetail = $"of {LibraryFormat.Count(s.GamesWithAchievements)} games with achievements";
        this.PlaytimeText = LibraryFormat.Playtime(s.Playtime);
        this.PlaytimeDetail = $"across {LibraryFormat.Count(s.PlayedGames)} played games";
        this.TopCompletion = s.TopCompletion;
        this.TopPlaytime = s.TopPlaytime;

        this.TimelineError = s.TimelineError;
        this.TimelineCount = s.Events.Count;
        this.BuildCharts(s.Events);
        if (s.TimelineError != null)
        {
            this.MonthlySummary = "";
        }
    }

    private void BuildCharts(IReadOnlyList<UnlockEvent> events)
    {
        var culture = CultureInfo.CurrentCulture;
        var thisMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        var byMonth = new Dictionary<DateTime, int>();
        foreach (var unlock in events)
        {
            var local = unlock.Time.LocalDateTime;
            var month = new DateTime(local.Year, local.Month, 1);
            byMonth[month] = byMonth.GetValueOrDefault(month) + 1;
        }

        // Unlocks per month, last 24 months; label every third month, ending on the current one.
        var monthly = new List<ChartPoint>(MonthsShown);
        var recentTotal = 0;
        for (var i = MonthsShown - 1; i >= 0; i--)
        {
            var month = thisMonth.AddMonths(-i);
            var count = byMonth.GetValueOrDefault(month);
            recentTotal += count;
            string? axis = i % 3 == 0
                ? month.ToString(month.Month == 1 || i == MonthsShown - 3 ? "MMM yy" : "MMM", culture)
                : null;
            monthly.Add(new ChartPoint(
                axis,
                count,
                $"{month.ToString("MMMM yyyy", culture)}: {Unlocks(count)}",
                month.ToString("MMMM yyyy", culture),
                LibraryFormat.Count(count)));
        }

        this.MonthlyPoints = monthly;
        this.MonthlySummary = $"{Unlocks(recentTotal)} in the last {MonthsShown} months";

        // Cumulative unlocks, monthly, from the first known unlock to now; label each January.
        var cumulative = new List<ChartPoint>();
        if (byMonth.Count > 0)
        {
            var first = byMonth.Keys.Min();
            var running = 0;
            for (var month = first; month <= thisMonth; month = month.AddMonths(1))
            {
                running += byMonth.GetValueOrDefault(month);
                string? axis = month.Month == 1 ? month.ToString("yyyy", culture) : (month == first ? month.ToString("MMM yyyy", culture) : null);
                cumulative.Add(new ChartPoint(
                    axis,
                    running,
                    $"End of {month.ToString("MMMM yyyy", culture)}: {LibraryFormat.Count(running)} unlocked",
                    month.ToString("MMMM yyyy", culture),
                    LibraryFormat.Count(running)));
            }

            this.CumulativeSummary = $"{LibraryFormat.Count(running)} unlocks with a known date since {first.ToString("MMMM yyyy", culture)}";
        }
        else
        {
            this.CumulativeSummary = "";
        }

        this.CumulativePoints = cumulative;
    }

    /// <summary>
    /// Fetches global percentages for every game with unlocks (most unlocks first, a few at a
    /// time), adding to the score and the rarest list as each game arrives.
    /// </summary>
    private async Task CalculateHunterScoreAsync(IReadOnlyList<UnlockEvent> events, CancellationToken token)
    {
        this.RarestUnlocks.Clear();
        this.RarestCount = 0;

        if (this.TimelineError != null)
        {
            this.HunterScoreText = "—";
            this.HunterScoreDetail = "Needs your unlock history";
            this.IsHunterCalculating = false;
            return;
        }

        var byGame = events.GroupBy(e => e.AppId).OrderByDescending(g => g.Count()).ToList();
        var score = 0.0;
        var rated = 0;
        var done = 0;
        var rarest = new List<(UnlockEvent Event, double Percent)>();

        this.HunterScoreText = "0";
        this.IsHunterCalculating = byGame.Count > 0;
        this.HunterProgress = 0;
        this.HunterScoreDetail = byGame.Count > 0 ? $"Calculating… 0 of {LibraryFormat.Count(byGame.Count)} games" : "No unlocks yet";

        var queue = new Queue<IGrouping<uint, UnlockEvent>>(byGame);
        var rarestChanged = false;

        async Task WorkerAsync()
        {
            while (token.IsCancellationRequested == false && queue.TryDequeue(out var game))
            {
                var percents = await this._percents.GetAsync(game.Key);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // Back on the UI thread: fold this game's unlocks in.
                if (percents != null)
                {
                    foreach (var unlock in game)
                    {
                        if (percents.TryGetValue(unlock.AchievementId, out var percent))
                        {
                            score += HunterPoints(percent);
                            rated++;
                            if (rarest.Count < RarestListSize || percent < rarest[^1].Percent)
                            {
                                rarest.Add((unlock, percent));
                                rarest.Sort((a, b) => a.Percent != b.Percent ? a.Percent.CompareTo(b.Percent) : b.Event.Time.CompareTo(a.Event.Time));
                                if (rarest.Count > RarestListSize)
                                {
                                    rarest.RemoveAt(rarest.Count - 1);
                                }

                                rarestChanged = true;
                            }
                        }
                    }
                }

                done++;
                this.HunterScoreText = Math.Round(score).ToString("N0", CultureInfo.CurrentCulture);
                this.HunterProgress = (double)done / byGame.Count * 100;
                this.HunterScoreDetail = done < byGame.Count
                    ? $"Calculating… {LibraryFormat.Count(done)} of {LibraryFormat.Count(byGame.Count)} games"
                    : $"From {LibraryFormat.Count(rated)} unlocks with global stats";
                if (rarestChanged)
                {
                    rarestChanged = false;
                    this.UpdateRarest(rarest);
                }
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => WorkerAsync()));
        }
        catch (Exception e)
        {
            Diagnostics.Log("Stats: hunter score calculation failed", e);
        }

        if (token.IsCancellationRequested == false)
        {
            this.IsHunterCalculating = false;
        }
    }

    private void UpdateRarest(List<(UnlockEvent Event, double Percent)> rarest)
    {
        // Reuse rows that are still in the list so their icons don't reload.
        var existing = this.RarestUnlocks.ToDictionary(r => r.Event);
        this.RarestUnlocks.Clear();
        foreach (var (unlock, percent) in rarest)
        {
            if (existing.TryGetValue(unlock, out var item) == false)
            {
                item = new UnlockItemViewModel(unlock, this._services.Images, null) { GlobalPercent = percent };
                item.EnsureLoaded();
            }

            this.RarestUnlocks.Add(item);
        }

        this.RarestCount = this.RarestUnlocks.Count;
    }

    private static string Unlocks(int count) => count == 1 ? "1 unlock" : $"{LibraryFormat.Count(count)} unlocks";

    /// <summary>Rounded down, so 99.6% never reads as 100%.</summary>
    private static string FormatPercent(double percent) => percent is > 0 and < 10
        ? (Math.Floor(percent * 10) / 10).ToString("0.#", CultureInfo.CurrentCulture) + "%"
        : Math.Floor(percent).ToString("0", CultureInfo.CurrentCulture) + "%";
}
