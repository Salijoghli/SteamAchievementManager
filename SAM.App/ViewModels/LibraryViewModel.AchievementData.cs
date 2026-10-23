// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Infrastructure;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels;

// Games whose achievement data Steam hasn't downloaded yet: store lookups (does the game have
// achievements at all?), watching Steam's stats folder, and "Load achievements".
public sealed partial class LibraryViewModel
{
    /// <summary>Pause between games in "Load missing achievement data".</summary>
    private static readonly TimeSpan FetchGap = TimeSpan.FromSeconds(2);

    /// <summary>A fetch process gets this long before it's stopped (it gives up by itself after ~30 s).</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    private SteamStatsWatcher? _statsWatcher;
    private DispatcherQueueTimer? _achievementDataTimer;
    private bool _achievementDataDirty;
    private CancellationTokenSource? _fetchAllCts;

    /// <summary>True while "Load missing achievement data" runs (progress InfoBar).</summary>
    [ObservableProperty]
    private bool _isFetchingAll;

    [ObservableProperty]
    private string _fetchAllProgressText = "";

    /// <summary>"· 5 not loaded" after the summary, or "" when every game's data is there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingAchievementData))]
    private string _missingAchievementDataText = "";

    public bool HasMissingAchievementData => this.MissingAchievementDataText.Length > 0;

    /// <summary>The games "Load missing achievement data" would load: not loaded or unknown, and not hidden.</summary>
    public IReadOnlyList<GameCardViewModel> GetGamesMissingAchievementData() =>
        this._master.Values
            .Where(c => c.IsAchievementDataMissing && c.IsHidden == false)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    #region Store lookups
    /// <summary>Asks the store, in the background, whether games without a schema have achievements.</summary>
    private void StartStoreLookups(IEnumerable<GameCardViewModel> cards)
    {
        var token = this._lifetime.Token;
        foreach (var card in cards)
        {
            if (card.Progress != null || token.IsCancellationRequested)
            {
                continue;
            }

            this.LookUpStoreCount(card, token);
        }
    }

    private async void LookUpStoreCount(GameCardViewModel card, CancellationToken token)
    {
        StoreAchievementCount result;
        try
        {
            result = await this._services.AchievementCounts.GetAsync(card.AppId, token);
        }
        catch (Exception e)
        {
            this.LogOnce("StoreCount", e);
            result = StoreAchievementCount.Failed;
        }

        // Back on the UI thread (the await resumed here).
        if (token.IsCancellationRequested || this._isShutDown)
        {
            return;
        }

        // A cancelled lookup must not mark the game unknown when an earlier answer is there.
        if (result.Status != StoreAchievementStatus.Failed || card.StoreCount == null)
        {
            card.StoreCount = result;
        }

        this.MarkAchievementDataChanged();
    }

    /// <summary>Re-sorts, re-filters and updates the summary shortly after achievement data changed (coalesced).</summary>
    private void MarkAchievementDataChanged()
    {
        this._achievementDataDirty = true;
        if (this._achievementDataTimer == null)
        {
            this._achievementDataTimer = this._dispatcher.CreateTimer();
            this._achievementDataTimer.Interval = TimeSpan.FromMilliseconds(400);
            this._achievementDataTimer.IsRepeating = false;
            this._achievementDataTimer.Tick += (_, _) =>
            {
                if (this._achievementDataDirty == false || this._isShutDown)
                {
                    return;
                }

                this._achievementDataDirty = false;
                if (this.IsSummaryReady)
                {
                    this.UpdateSummary();
                }

                if (this.CompletionFilterIndex != 0 || this.IsSmartListActive ||
                    this.SortIndex == (int)LibrarySort.Progress || this._columnSort != null)
                {
                    this.ApplyFilter();
                }
            };
        }

        if (this._achievementDataTimer.IsRunning == false)
        {
            this._achievementDataTimer.Start();
        }
    }
    #endregion

    #region Watching Steam's stats folder
    /// <summary>Starts watching appcache\stats so cards update when Steam writes a game's schema or stats.</summary>
    private void StartWatchingStats()
    {
        if (this._statsWatcher != null || this._isShutDown)
        {
            return;
        }

        try
        {
            var steam = this._services.Steam;
            this._statsWatcher = new SteamStatsWatcher(steam.SteamInstallPath, steam.AccountId);
            this._statsWatcher.Changed += this.OnStatsFilesChanged;
        }
        catch (Exception e)
        {
            this.LogOnce("StatsWatcher", e);
        }
    }

    /// <summary>Called on a thread-pool thread with the App IDs whose files changed (empty: re-read everything).</summary>
    private void OnStatsFilesChanged(object? sender, IReadOnlyList<uint> appIds)
    {
        if (this._isShutDown)
        {
            return;
        }

        if (appIds.Count == 0)
        {
            this._dispatcher.TryEnqueue(() =>
            {
                if (this._isShutDown == false && this.IsLoading == false && this._master.Count > 0)
                {
                    this.StartEnrichment();
                }
            });
            return;
        }

        foreach (var appId in appIds)
        {
            this._services.Schemas.Invalidate(appId);
        }

        this._dispatcher.TryEnqueue(() => this.RereadAchievementData(appIds));
    }

    /// <summary>Re-reads the schema and progress of <paramref name="appIds"/> off the UI thread and updates their cards.</summary>
    private async void RereadAchievementData(IReadOnlyList<uint> appIds)
    {
        var cards = appIds
            .Select(id => this._master.TryGetValue(id, out var card) ? card : null)
            .OfType<GameCardViewModel>()
            .ToList();
        if (cards.Count == 0 || this._isShutDown)
        {
            return;
        }

        var results = await Task.Run(() => cards.Select(card =>
        {
            try
            {
                var schema = this._services.Schemas.Load(card.AppId);
                return (Card: card, Ok: true, Progress: this._services.Progress.GetCachedProgress(card.AppId, schema));
            }
            catch (Exception e)
            {
                this.LogOnce("RereadProgress", e);
                return (Card: card, Ok: false, Progress: (GameProgress?)null);
            }
        }).ToList());

        if (this._isShutDown)
        {
            return;
        }

        var lookups = new List<GameCardViewModel>();
        foreach (var (card, ok, progress) in results)
        {
            if (ok == false)
            {
                continue;
            }

            // A stats file that's mid-write reads as null; keep the progress we had until it settles.
            if (progress == null && card.Progress != null && this._services.Schemas.SchemaFileExists(card.AppId))
            {
                continue;
            }

            card.Progress = progress;
            card.IsProgressLoaded = true;
            if (progress == null && card.StoreCount == null)
            {
                lookups.Add(card);
            }
        }

        this.StartStoreLookups(lookups);
        this.MarkAchievementDataChanged();
    }
    #endregion

    #region Loading achievement data
    /// <summary>"Load achievements" on a card: asks Steam for the game's achievement data.</summary>
    internal async void LoadAchievements(GameCardViewModel card)
    {
        if (card.IsFetchingAchievements || this._isShutDown)
        {
            return;
        }

        bool ok;
        try
        {
            ok = await this.FetchAchievementDataAsync(card, this._lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // The window closed meanwhile.
            return;
        }

        if (ok == false && this._isShutDown == false)
        {
            this.ShowNotice(
                Strings.Format("Library_LoadAchievementsFailedTitle", card.Name),
                Strings.Get("Library_LoadAchievementsFailed"),
                InfoBarSeverity.Warning);
        }
    }

    /// <summary>Starts "Load missing achievement data" for <paramref name="cards"/>, one game at a time. Call after the user confirmed.</summary>
    public async void FetchAllAchievementData(IReadOnlyList<GameCardViewModel> cards)
    {
        if (this.IsFetchingAll || cards.Count == 0 || this._isShutDown)
        {
            return;
        }

        var cts = this._fetchAllCts = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token);
        var token = cts.Token;
        this.IsFetchingAll = true;
        var done = 0;
        var loaded = 0;
        try
        {
            foreach (var card in cards)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (done > 0)
                {
                    await Task.Delay(FetchGap, token);
                }

                done++;
                this.FetchAllProgressText = Strings.Format(
                    "Library_LoadMissingProgress",
                    LibraryFormat.Count(done),
                    LibraryFormat.Count(cards.Count),
                    card.Name);

                // Skip games that got their data meanwhile (e.g. opened in SAM).
                if (card.IsAchievementDataMissing == false || await this.FetchAchievementDataAsync(card, token))
                {
                    loaded++;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (this._fetchAllCts == cts)
            {
                this._fetchAllCts = null;
            }

            cts.Dispose();
            this.IsFetchingAll = false;
        }

        if (this._isShutDown == false)
        {
            this.ShowNotice(
                Strings.Get("Library_LoadMissingDoneTitle"),
                Strings.Plural("Library_LoadMissingDone", cards.Count, LibraryFormat.Count(loaded)),
                loaded == cards.Count ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        }
    }

    [RelayCommand]
    private void CancelFetchAll() => this._fetchAllCts?.Cancel();

    /// <summary>
    /// Runs <c>SAM.exe &lt;appid&gt; --fetch-achievements</c> for <paramref name="card"/>, waits for it
    /// and re-reads the game's data (the stats watcher usually got there first). True if Steam's
    /// schema file is there afterwards, or the fetch succeeded (e.g. the game's window is loading it).
    /// </summary>
    private async Task<bool> FetchAchievementDataAsync(GameCardViewModel card, CancellationToken token)
    {
        card.IsFetchingAchievements = true;
        var exitedOk = false;
        try
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            startInfo.ArgumentList.Add(card.AppIdText);
            startInfo.ArgumentList.Add(CommandLine.FetchAchievementsOption);

            using (var process = Process.Start(startInfo))
            {
                if (process != null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(FetchTimeout);
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                        exitedOk = process.ExitCode == CommandLine.ExitSuccess;
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled, or it hung: it's our own child process, so stop it.
                        TryKill(process);
                        token.ThrowIfCancellationRequested();
                    }
                }
            }

            this._services.Schemas.Invalidate(card.AppId);
            var exists = await Task.Run(() => this._services.Schemas.SchemaFileExists(card.AppId), CancellationToken.None);
            this.RereadAchievementData([card.AppId]);

            // Exit code 0 without the file: the game is open in SAM, which is loading it anyway.
            return exists || exitedOk;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't load achievement data for {card.AppId}", e);
            return false;
        }
        finally
        {
            card.IsFetchingAchievements = false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (process.HasExited == false)
            {
                process.Kill();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
    #endregion

    /// <summary>Counts the games whose achievement data isn't loaded, for the summary.</summary>
    private void UpdateMissingAchievementDataText()
    {
        var missing = this._master.Values.Count(c => c.IsAchievementDataMissing && c.IsHidden == false);
        this.MissingAchievementDataText = missing == 0
            ? ""
            : Strings.Plural("Library_NotLoadedSummary", missing);
    }

    private void ShutDownAchievementData()
    {
        this._fetchAllCts?.Cancel();
        this._achievementDataTimer?.Stop();
        if (this._statsWatcher != null)
        {
            this._statsWatcher.Changed -= this.OnStatsFilesChanged;
            this._statsWatcher.Dispose();
            this._statsWatcher = null;
        }
    }
}
