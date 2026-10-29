// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels.Pages;

// "Up next": the easiest achievements the user is missing and the games closest to 100%, ranked
// by global unlock percentages (fetched a few games at a time; the lists fill in as they arrive).
public sealed partial class StatsViewModel
{
    private const int EasiestListSize = 15;
    private const int EasiestPerGame = 3;
    private const int ReachableListSize = 10;

    /// <summary>How often the lists are re-ranked while percentages arrive.</summary>
    private static readonly TimeSpan SuggestionRefreshInterval = TimeSpan.FromMilliseconds(300);

    private readonly Dictionary<uint, IReadOnlyDictionary<string, double>?> _suggestionPercents = [];
    private IReadOnlyList<IncompleteGame> _incomplete = [];
    private IReadOnlyList<ReachableGame> _reachable = [];

    #region Observable state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEasiestSkeletonVisible), nameof(IsReachableSkeletonVisible), nameof(IsEasiestEmpty), nameof(IsReachableEmpty))]
    private bool _isSuggesting;

    [ObservableProperty]
    private double _suggestProgress;

    [ObservableProperty]
    private string _suggestStatusText = "";

    /// <summary>"12 games not loaded" (their achievement data isn't on disk yet), or "" when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestNotLoaded))]
    private string _suggestNotLoadedText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEasiestSkeletonVisible), nameof(IsEasiestEmpty))]
    private int _easiestCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReachableSkeletonVisible), nameof(IsReachableEmpty))]
    private int _reachableCount;

    /// <summary>Only suggest installed games (this session only).</summary>
    [ObservableProperty]
    private bool _suggestInstalledOnly;

    /// <summary>Leave out games whose remaining achievements are all rare (this session only).</summary>
    [ObservableProperty]
    private bool _suggestHideRare;
    #endregion

    public ObservableCollection<SuggestedAchievementItem> EasiestMissing { get; } = [];

    public ObservableCollection<ReachableGameItem> PerfectWithinReach { get; } = [];

    public bool HasSuggestNotLoaded => this.SuggestNotLoadedText.Length > 0;

    public bool IsEasiestSkeletonVisible => this.IsSuggesting && this.EasiestCount == 0;

    public bool IsReachableSkeletonVisible => this.IsSuggesting && this.ReachableCount == 0;

    public bool IsEasiestEmpty => this.IsSuggesting == false && this.EasiestCount == 0;

    public bool IsReachableEmpty => this.IsSuggesting == false && this.ReachableCount == 0;

    public static string SuggestExplanation => Strings.Get("Suggest_Explanation");

    partial void OnSuggestInstalledOnlyChanged(bool value) => this.RankSuggestions();

    partial void OnSuggestHideRareChanged(bool value) => this.RankSuggestions();

    /// <summary>Re-masks hidden descriptions when spoiler protection is switched.</summary>
    private void OnSpoilerProtectionChanged()
    {
        foreach (var item in this.EasiestMissing)
        {
            item.IsSpoilerProtected = this._library.SpoilerProtection;
        }
    }

    /// <summary>
    /// Reads which achievements are missing (off the UI thread), then fetches global percentages
    /// for those games, near-complete ones first, re-ranking the lists as they arrive.
    /// </summary>
    private async Task LoadSuggestionsAsync(CancellationToken token)
    {
        // On a reload the previous lists and percentages stay until the new data replaces them, so
        // the section doesn't flash back to skeletons.
        this.SuggestProgress = 0;
        this.SuggestStatusText = Strings.Get("Suggest_Reading");
        this.IsSuggesting = true;

        // Hidden games are left out: the user asked not to see them.
        var cards = this._library.AllGames.Where(c => c.IsHidden == false).ToList();
        var games = cards.Select(c => new GameEntry(c.AppId, c.Name, c.Type)).ToList();
        var installed = cards.Where(c => c.IsInstalled).Select(c => c.AppId).ToHashSet();
        var notLoaded = this._library.GetGamesMissingAchievementData().Count;
        this.SuggestNotLoadedText = notLoaded == 0 ? "" : Strings.Plural("Suggest_NotLoaded", notLoaded);

        IReadOnlyList<IncompleteGame> incomplete;
        try
        {
            var service = new SuggestionService(this._services.Timeline, this._services.Schemas);
            incomplete = await Task.Run(() => service.LoadIncomplete(games, installed, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Stats: couldn't read missing achievements", e);
            incomplete = [];
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        this._incomplete = incomplete;
        this.RankSuggestions();

        // Near-complete games first (they fill "within reach"), then by how far along they are.
        var queue = new Queue<IncompleteGame>(incomplete
            .OrderBy(g => Suggestions.IsWithinReach(g.Total, g.Missing.Count) ? 0 : 1)
            .ThenBy(g => g.Missing.Count)
            .ThenByDescending(g => (double)g.Unlocked / g.Total));
        var total = queue.Count;
        var done = 0;
        var dirty = false;
        var sinceRank = Stopwatch.StartNew();

        async Task WorkerAsync()
        {
            while (token.IsCancellationRequested == false && queue.TryDequeue(out var game))
            {
                var percents = await this._percents.GetAsync(game.AppId);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // Back on the UI thread.
                this._suggestionPercents[game.AppId] = percents;
                dirty = true;
                done++;
                this.SuggestProgress = (double)done / total * 100;
                this.SuggestStatusText = Strings.Format("Suggest_Calculating", LibraryFormat.Count(done), LibraryFormat.Count(total));
                if (sinceRank.Elapsed >= SuggestionRefreshInterval)
                {
                    dirty = false;
                    sinceRank.Restart();
                    this.RankSuggestions();
                }
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => WorkerAsync()));
        }
        catch (Exception e)
        {
            Diagnostics.Log("Stats: suggestions failed", e);
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (dirty)
        {
            this.RankSuggestions();
        }

        this.SuggestStatusText = Strings.Plural("Suggest_From", incomplete.Count);
        this.IsSuggesting = false;
    }

    /// <summary>Re-ranks both lists from the data so far, reusing rows that stay (so icons don't reload).</summary>
    private void RankSuggestions()
    {
        var filter = new SuggestionFilter(this.SuggestInstalledOnly, this.SuggestHideRare);
        IReadOnlyDictionary<string, double>? Lookup(uint appId) => this._suggestionPercents.GetValueOrDefault(appId);

        var easiest = Suggestions.EasiestMissing(this._incomplete, Lookup, filter, EasiestListSize, EasiestPerGame);
        var existing = this.EasiestMissing.ToDictionary(i => i.Key);
        var spoilers = this._library.SpoilerProtection;
        var items = easiest
            .Select(r =>
            {
                if (existing.TryGetValue((r.Achievement.AppId, r.Achievement.Id), out var item) == false)
                {
                    item = new SuggestedAchievementItem(r.Achievement, r.Percent, spoilers, this._services.Images);
                    item.EnsureIcon();
                }

                return item;
            })
            .ToList();

        if (items.SequenceEqual(this.EasiestMissing) == false)
        {
            this.EasiestMissing.Clear();
            foreach (var item in items)
            {
                this.EasiestMissing.Add(item);
            }
        }

        this.EasiestCount = this.EasiestMissing.Count;

        // Rows are cheap (no icons); rebuild whenever the ranking or a game's rarity changed.
        var reachable = Suggestions.PerfectWithinReach(this._incomplete, Lookup, filter, ReachableListSize);
        if (reachable.SequenceEqual(this._reachable) == false)
        {
            this._reachable = reachable;
            this.PerfectWithinReach.Clear();
            foreach (var game in reachable)
            {
                this.PerfectWithinReach.Add(new ReachableGameItem(game));
            }
        }

        this.ReachableCount = this.PerfectWithinReach.Count;
    }
}
