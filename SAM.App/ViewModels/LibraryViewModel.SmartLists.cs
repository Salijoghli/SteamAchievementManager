using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels;

// Smart lists ("Almost perfect", "Never started", "Rarest owned") and list view column sorting.
public sealed partial class LibraryViewModel
{
    private const double AlmostPerfectThreshold = 0.8;
    private const int MaxConcurrentPercentFetches = 6;

    // Global unlock percentages by app (null: no data), shared by "Rarest owned" and the CSV export.
    private readonly ConcurrentDictionary<uint, IReadOnlyDictionary<string, double>?> _percents = new();

    // Rarest unlocked achievement's global percent by app; null when unknown. Absent: not loaded yet.
    private readonly Dictionary<uint, double?> _rarest = [];
    private bool _isRarestLoading;
    private bool _isRarestLoaded;

    // Set when the list view is sorted by a column the Sort box doesn't offer (Installed, Favorite).
    private string? _columnSort;

    #region Smart lists
    private static bool MatchesSmartList(GameCardViewModel card, string? smartList) => smartList switch
    {
        AlmostPerfectList => card.Progress is { Total: > 0 } p && p.Fraction >= AlmostPerfectThreshold && p.Unlocked < p.Total,
        NeverStartedList => card.Progress is { Total: > 0, Unlocked: 0 },
        RarestOwnedList => card.Progress is { Total: > 0, Unlocked: > 0 },
        _ => true,
    };

    private string? GetSmartBadge(GameCardViewModel card, string? smartList)
    {
        switch (smartList)
        {
            case AlmostPerfectList when card.Progress is { Total: > 0 } p && p.Unlocked < p.Total:
                return $"{LibraryFormat.Count(p.Total - p.Unlocked)} left";

            case RarestOwnedList when card.Progress is { Unlocked: > 0 }:
                if (this._rarest.TryGetValue(card.AppId, out var rarest))
                {
                    return rarest is { } percent ? $"Rarest {FormatPercent(percent)}" : "No rarity data";
                }

                return this._isRarestLoading ? "" : null;

            default:
                return null;
        }
    }

    private bool IsRarestPending(GameCardViewModel card) =>
        this._isRarestLoading && card.Progress is { Unlocked: > 0 } && this._rarest.ContainsKey(card.AppId) == false;

    private static string FormatPercent(double percent) =>
        (percent < 1 ? percent.ToString("0.##", CultureInfo.CurrentCulture) : percent.ToString("0.#", CultureInfo.CurrentCulture)) + "%";

    /// <summary>Order for smart lists; ignores the Sort box, direction and favorite pinning.</summary>
    private int CompareForSmartList(GameCardViewModel a, GameCardViewModel b)
    {
        var result = this.SmartList switch
        {
            AlmostPerfectList => Remaining(a).CompareTo(Remaining(b)) is var r && r != 0
                ? r
                : b.CompletionSortKey.CompareTo(a.CompletionSortKey),
            NeverStartedList => b.PlaytimeSpan.CompareTo(a.PlaytimeSpan),
            RarestOwnedList => this.RarestKey(a).CompareTo(this.RarestKey(b)),
            _ => 0,
        };

        if (result == 0)
        {
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        return result != 0 ? result : a.AppId.CompareTo(b.AppId);

        static int Remaining(GameCardViewModel card) => card.Progress is { } p ? p.Total - p.Unlocked : int.MaxValue;
    }

    private double RarestKey(GameCardViewModel card) =>
        this._rarest.TryGetValue(card.AppId, out var value) && value is { } percent ? percent : double.MaxValue;

    /// <summary>
    /// Loads, once per session, each game's rarest unlocked achievement: unlocks from Steam's local
    /// stats cache, then global percentages (cached on disk for a day) a few games at a time.
    /// Cards show a skeleton badge until their game is done; the list re-sorts as results arrive.
    /// </summary>
    private async void StartRarestLoad()
    {
        if (this._isRarestLoading || this._isRarestLoaded || this._isShutDown)
        {
            return;
        }

        this._isRarestLoading = true;
        this.ApplyFilter();

        var token = this._lifetime.Token;
        var entries = this._master.Values.Select(c => new GameEntry(c.AppId, c.Name, c.Type)).ToList();
        var unlocks = await Task.Run(() =>
        {
            try
            {
                return this._services.Timeline.Load(entries);
            }
            catch (Exception e)
            {
                this.LogOnce("Timeline", e);
                return null;
            }
        });

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (unlocks == null)
        {
            // Don't retry (and re-show the notice) on every progress refresh this session.
            this._isRarestLoading = false;
            this._isRarestLoaded = true;
            this.ShowNotice(
                "Couldn't load unlock data",
                "\"Rarest owned\" needs your unlocked achievements from Steam's local stats cache, which couldn't be read.",
                InfoBarSeverity.Warning);
            this.ApplyFilter();
            return;
        }

        var unlockedByApp = unlocks
            .GroupBy(u => u.AppId)
            .ToDictionary(g => g.Key, g => g.Select(u => u.AchievementId).ToHashSet(StringComparer.Ordinal));

        var refresh = this._dispatcher.CreateTimer();
        refresh.Interval = TimeSpan.FromMilliseconds(400);
        var dirty = false;
        refresh.Tick += (_, _) =>
        {
            if (dirty && this.SmartList == RarestOwnedList)
            {
                dirty = false;
                this.ApplyFilter();
            }
        };
        refresh.Start();

        try
        {
            await this.FetchPercentsAsync(unlockedByApp.Keys.ToList(), token, appId =>
            {
                double? rarest = null;
                if (this._percents.TryGetValue(appId, out var percents) && percents != null)
                {
                    foreach (var id in unlockedByApp[appId])
                    {
                        if (percents.TryGetValue(id, out var value) && (rarest == null || value < rarest))
                        {
                            rarest = value;
                        }
                    }
                }

                this._rarest[appId] = rarest;
                dirty = true;
            });
        }
        finally
        {
            refresh.Stop();
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        // Games with unlocks the cache doesn't know about (no unlock times) have no rarity.
        foreach (var card in this._master.Values)
        {
            this._rarest.TryAdd(card.AppId, null);
        }

        this._isRarestLoading = false;
        this._isRarestLoaded = true;
        this.ApplyFilter();
    }

    /// <summary>
    /// Fetches global percentages for <paramref name="appIds"/> not fetched yet this session, a few
    /// at a time. <paramref name="onEach"/> runs on the UI thread after each app (fetched or cached).
    /// </summary>
    private async Task FetchPercentsAsync(IReadOnlyList<uint> appIds, CancellationToken token, Action<uint> onEach)
    {
        using var slots = new SemaphoreSlim(MaxConcurrentPercentFetches);
        var tasks = appIds.Select(async appId =>
        {
            if (this._percents.ContainsKey(appId) == false)
            {
                await slots.WaitAsync(token).ConfigureAwait(true);
                try
                {
                    var percents = await this._services.GlobalPercents.GetAsync(appId, token).ConfigureAwait(true);
                    if (token.IsCancellationRequested == false)
                    {
                        this._percents[appId] = percents;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    this.LogOnce("GlobalPercents", e);
                }
                finally
                {
                    slots.Release();
                }
            }

            if (token.IsCancellationRequested == false)
            {
                onEach(appId);
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }
    #endregion

    #region List view columns
    /// <summary>
    /// Sorts by a list view column (see the *Column constants): a new column picks its natural
    /// direction, the current one flips. Name, Progress/%, Playtime and Last played map onto the
    /// Sort box; Installed and Favorite are list-only and not persisted.
    /// </summary>
    public void SortByColumn(string column)
    {
        if (this.IsSmartListActive)
        {
            return;
        }

        if (column == this.ActiveSortColumn)
        {
            this.SortDescending = !this.SortDescending;
            return;
        }

        var initializing = this._initializing;
        this._initializing = true;
        switch (column)
        {
            case NameColumn:
                this.SortIndex = (int)LibrarySort.Name;
                break;
            case ProgressColumn:
                this.SortIndex = (int)LibrarySort.Progress;
                break;
            case PlaytimeColumn:
                this.SortIndex = (int)LibrarySort.Playtime;
                break;
            case LastPlayedColumn:
                this.SortIndex = (int)LibrarySort.RecentlyPlayed;
                break;
        }

        this._columnSort = column is InstalledColumn or FavoriteColumn ? column : null;
        this.SortDescending = column != NameColumn;
        this._initializing = initializing;
        this.OnPropertyChanged(nameof(this.ActiveSortColumn));
        this.OnViewSettingChanged();
    }
    #endregion
}
