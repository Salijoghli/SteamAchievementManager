using System.Globalization;
using Microsoft.UI.Dispatching;
using SAM.App.Models;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// Runs Autopilot inside the library process. For each running game it starts a headless worker
/// (<c>SAM.exe &lt;appid&gt; --autopilot-worker &lt;parent pid&gt;</c>), which keeps a Steam client
/// connection open for that App ID (so Steam counts it as played) and performs unlocks on
/// request. On a timer the engine picks games to run (source, exclusions, concurrency, schedule,
/// pause-while-gaming, daily caps), asks the planner what's due, tells workers to unlock, records
/// state (AutopilotStore), history (Source = Farm), backups and notifications, and updates the
/// tray status. Must be created and used on the UI thread; raises events on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Ticks every <see cref="TickInterval"/> while running, and right away after a config or
/// per-game change. Each tick: roll the per-day counters over at local midnight, credit running
/// workers' uptime to AutopilotHours/HoursToday, reap crashed workers (restarted with exponential
/// backoff), resolve the game list, decide each game's status, stop and start workers to match,
/// then for every running game (outside quiet hours) fetch its live state, ask
/// <see cref="AutopilotPlanner.DueNow"/> and unlock what's due.
/// </para>
/// <para>
/// <see cref="AutopilotGameStatus.TargetHours"/> is in the same unit as PlaytimeHours (total
/// playtime): TargetHours for UntilTotalHours, baseline + TargetHours for AddHours, null for Unlimited.
/// </para>
/// </remarks>
public sealed class AutopilotEngine(AppServices services) : IDisposable
{
    /// <summary>How often the engine re-evaluates while running.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>Most running time credited per tick (a sleeping PC doesn't count as playtime).</summary>
    private static readonly TimeSpan MaxCreditPerTick = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ResolveLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PlaytimeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PercentLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan NextUnlockLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UnlockRetryDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StableWorkerUptime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnlockTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Queued games (beyond the running ones) that get a "next unlock" estimate.</summary>
    private const int PreviewedQueuedGames = 10;

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
    private readonly Dictionary<uint, AutopilotWorkerClient> _workers = [];
    private readonly Dictionary<uint, WorkerFailure> _failures = [];
    private readonly Dictionary<uint, DateTimeOffset> _unlockRetryAt = [];
    private readonly Dictionary<uint, DateTimeOffset> _creditedUntil = [];
    private readonly Dictionary<uint, (DateTimeOffset At, IReadOnlyDictionary<string, double>? Percents)> _percents = [];
    private readonly Dictionary<uint, IReadOnlyList<AutopilotAchievement>> _achievements = [];
    private readonly Dictionary<uint, (DateTimeOffset At, string? Name, DateTimeOffset? Estimate)> _nextUnlocks = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly Random _random = new();

    private AutopilotConfig? _config;
    private AutopilotState? _state;
    private List<ResolvedGame>? _resolved;
    private DateTimeOffset _resolvedAt;
    private IReadOnlyDictionary<uint, PlaytimeInfo> _playtime = new Dictionary<uint, PlaytimeInfo>();
    private DateTimeOffset _playtimeAt = DateTimeOffset.MinValue;
    private IReadOnlyList<AutopilotGameStatus> _status = [];
    private CancellationTokenSource _cancel = new();
    private bool _running;
    private bool _ticking;
    private bool _tickAgain;
    private bool _refreshingIdle;
    private bool _timerHooked;
    private bool _disposed;
    private bool _stateDirty;
    private DateOnly _summaryDay = Today(DateTimeOffset.Now);
    private int _summaryCount;
    private readonly List<string> _summaryGames = [];

    /// <summary>Raised when IsRunning, Config or any game's status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after each unlock batch (also on failure, with Succeeded = false).</summary>
    public event EventHandler<CommitRecord>? Unlocked;

    public bool IsRunning => this._running;

    /// <summary>The current configuration (a live object; call SaveConfig after editing a copy).</summary>
    public AutopilotConfig Config => this._config ??= services.AutopilotStore.LoadConfig();

    private AutopilotState State => this._state ??= services.AutopilotStore.LoadState();

    /// <summary>Saves and applies a new configuration (takes effect on the next tick).</summary>
    public void SaveConfig(AutopilotConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.WasRunning = this._running;
        this._config = config;
        this.SaveConfigFile();
        StartupRegistration.Apply(config.StartWithWindows);

        this._resolved = null;
        this._nextUnlocks.Clear();
        this.RequestTick();
        this.RaiseChanged();
    }

    /// <summary>Status of every game in scope (running, queued, waiting, finished…), in queue order.</summary>
    public IReadOnlyList<AutopilotGameStatus> Status
    {
        get
        {
            if (this._running == false && this._status.Count == 0 && this._resolved == null)
            {
                this.RequestTick();
            }

            return this._status;
        }
    }

    /// <summary>
    /// The tray tooltip while running ("Autopilot: 2 games running · next unlock 14:05"), else null.
    /// </summary>
    internal string? TrayStatus { get; private set; }

    /// <summary>The games Autopilot would consider right now under the current config (resolves the source).</summary>
    public IReadOnlyList<GameEntry> ResolveGames()
    {
        var games = this.Resolve(this.Config);
        return games
            .Where(g => AutopilotWorker.IsWindowOpen(g.AppId) == false)
            .Select(g => new GameEntry(g.AppId, this.NameOf(g), g.Type))
            .ToList();
    }

    public async Task StartAsync()
    {
        if (this._running || this._disposed)
        {
            return;
        }

        this._running = true;
        this._cancel = new CancellationTokenSource();
        this.Config.WasRunning = true;
        this.SaveConfigFile();

        if (this._timerHooked == false)
        {
            this._timerHooked = true;
            this._timer.Interval = TickInterval;
            this._timer.IsRepeating = true;
            this._timer.Tick += async (_, _) => await this.TickAsync();
        }

        this._timer.Start();
        this.RaiseChanged();
        await this.TickAsync();
    }

    /// <summary>Stops all workers and saves state.</summary>
    public async Task StopAsync()
    {
        if (this._disposed)
        {
            return;
        }

        var wasRunning = this._running;
        this._running = false;
        this._timer.Stop();
        this._cancel.Cancel();

        // Let an in-flight tick finish (an unlock that's being stored isn't interrupted).
        while (this._ticking)
        {
            await Task.Delay(100);
        }

        await this.StopAllWorkersAsync();
        if (wasRunning)
        {
            this.Config.WasRunning = false;
            this.SaveConfigFile();
        }

        this.FlushSummary();
        this.SaveState(force: true);
        this.TrayStatus = null;
        this.RaiseChanged();
        this.RequestTick();
    }

    public void PauseGame(uint appId) => this.SetPaused(appId, true);

    public void ResumeGame(uint appId) => this.SetPaused(appId, false);

    /// <summary>Clears a game's Autopilot progress (hours, seeds, completed flag) — not its achievements.</summary>
    /// <remarks>
    /// Keeps the list of achievements Autopilot unlocked, today's unlock counters (so a reset can't
    /// bypass the rate limits) and the paused flag. The baseline playtime is taken again.
    /// </remarks>
    public void ResetGame(uint appId)
    {
        var games = this.State.Games;
        games.TryGetValue(appId, out var old);
        var fresh = new AutopilotGameState
        {
            AppId = appId,
            BaselineSteamHours = this.SteamHours(appId),
            Day = Today(DateTimeOffset.Now),
        };

        if (old != null)
        {
            fresh.UnlockedByAutopilot = old.UnlockedByAutopilot;
            fresh.RecentUnlocks = old.RecentUnlocks;
            fresh.LastUnlockAt = old.LastUnlockAt;
            fresh.Paused = old.Paused;
            if (old.Day == fresh.Day)
            {
                fresh.UnlocksToday = old.UnlocksToday;
            }
        }

        games[appId] = fresh;
        this._failures.Remove(appId);
        this._unlockRetryAt.Remove(appId);
        this._nextUnlocks.Remove(appId);
        this._creditedUntil.Remove(appId);
        if (this._workers.ContainsKey(appId))
        {
            this._creditedUntil[appId] = DateTimeOffset.Now;
        }

        this._stateDirty = true;
        this.SaveState();
        this.RequestTick();
        this.RaiseChanged();
    }

    /// <summary>
    /// Dry-run plan for one game using current state, playtime and global percentages (loads
    /// schema/percents itself; no Steam writes, no worker needed).
    /// </summary>
    public async Task<IReadOnlyList<AutopilotPlannedUnlock>> PreviewAsync(uint appId, AutopilotConfig config, CancellationToken cancellationToken)
    {
        await this.RefreshPlaytimeAsync();
        var achievements = await this.LoadAchievementsAsync(appId, preferWorker: true, cancellationToken);
        if (achievements == null)
        {
            return [];
        }

        this.State.Games.TryGetValue(appId, out var state);
        var playtime = state == null ? this.SteamHours(appId) : this.CurrentPlaytime(state);
        return AutopilotPlanner.Preview(appId, config, achievements, state, playtime, DateTimeOffset.Now);
    }

    /// <summary>
    /// Whether an Autopilot worker currently holds <paramref name="appId"/> (any process; uses a
    /// named mutex "SteamAchievementManager.Autopilot.&lt;appid&gt;"). Game windows use this to warn.
    /// </summary>
    public static bool IsGameRunningUnderAutopilot(uint appId)
    {
        try
        {
            if (Mutex.TryOpenExisting(AutopilotProtocol.MutexName(appId), out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Exists, but we may not open it: still held by a worker.
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't check whether Autopilot runs {appId}", e);
            return false;
        }
    }

    /// <summary>
    /// Stops every worker (asks, waits up to a few seconds, then kills) and saves state, without
    /// clearing WasRunning, so Autopilot resumes next time when ResumeOnStartup is on.
    /// </summary>
    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this._running = false;
        this._timer.Stop();
        this._cancel.Cancel();

        var now = DateTimeOffset.Now;
        foreach (var appId in this._workers.Keys.ToList())
        {
            this.CreditRunningTime(appId, now);
        }

        foreach (var worker in this._workers.Values)
        {
            worker.RequestStop();
        }

        var deadline = DateTimeOffset.Now + StopTimeout;
        foreach (var worker in this._workers.Values)
        {
            var left = deadline - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero || worker.WaitForExit(left) == false)
            {
                worker.Kill();
            }

            worker.Dispose();
        }

        this._workers.Clear();
        this.FlushSummary();
        this.SaveState(force: true);
    }

    #region Tick
    private sealed record ResolvedGame(uint AppId, string? Name, string Type, int? Remaining);

    private sealed record WorkerFailure(int Count, DateTimeOffset RetryAt, string Message);

    private sealed record Decision(ResolvedGame Game, AutopilotGameStatusKind Kind, string? Message);

    /// <summary>Ticks as soon as possible (or refreshes the idle status when not running).</summary>
    private void RequestTick()
    {
        if (this._disposed)
        {
            return;
        }

        if (this._running)
        {
            this._dispatcher.TryEnqueue(async () => await this.TickAsync());
        }
        else if (this._refreshingIdle == false)
        {
            this._refreshingIdle = true;
            this._dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await this.RefreshIdleStatusAsync();
                }
                catch (Exception e)
                {
                    Diagnostics.Log("Autopilot: refreshing the status failed", e);
                }
                finally
                {
                    this._refreshingIdle = false;
                }
            });
        }
    }

    private async Task TickAsync()
    {
        if (this._ticking)
        {
            this._tickAgain = true;
            return;
        }

        this._ticking = true;
        try
        {
            do
            {
                this._tickAgain = false;
                if (this._running == false || this._disposed)
                {
                    break;
                }

                await this.TickCoreAsync(this._cancel.Token);
            }
            while (this._tickAgain);
        }
        catch (OperationCanceledException)
        {
            // Stopped mid-tick.
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot tick failed", e);
        }
        finally
        {
            this._ticking = false;
        }
    }

    private bool Live => this._running && this._disposed == false;

    private async Task TickCoreAsync(CancellationToken token)
    {
        var config = this.Config;
        var now = DateTimeOffset.Now;

        this.RollOver(now);

        // Credit uptime and reap workers that died since the last tick.
        foreach (var (appId, worker) in this._workers.ToList())
        {
            if (worker.HasExited)
            {
                this.CreditRunningTime(appId, now);
                this.RecordFailure(appId, worker.DescribeExit(), now);
                this.RemoveWorker(appId);
            }
            else if (worker.IsReady)
            {
                this.CreditRunningTime(appId, now);
                if (now - worker.StartedAt > StableWorkerUptime)
                {
                    this._failures.Remove(appId);
                }
            }
        }

        var games = await this.GetResolvedAsync(config, token);
        await this.RefreshPlaytimeAsync();
        if (this.Live == false)
        {
            return;
        }

        uint? gaming = null;
        if (config.PauseWhileGaming)
        {
            gaming = await Task.Run(this.FindRunningGame, token);
        }

        var decisions = this.Decide(config, games, gaming, now);
        var wanted = decisions.Where(d => d.Kind is AutopilotGameStatusKind.Running or AutopilotGameStatusKind.Starting)
            .Select(d => d.Game.AppId)
            .ToHashSet();

        // Stop what shouldn't run, then start what should.
        await Task.WhenAll(this._workers.Keys.Where(id => wanted.Contains(id) == false).ToList().Select(this.StopWorkerAsync));
        if (this.Live == false)
        {
            return;
        }

        var starting = wanted.Where(id => this._workers.ContainsKey(id) == false).ToList();
        foreach (var appId in starting)
        {
            this.StartWorker(appId, now);
        }

        if (starting.Count > 0)
        {
            this.PublishStatus(decisions, config, now);
            await Task.WhenAll(starting.Where(this._workers.ContainsKey).Select(id => this.ConnectWorkerAsync(id, token)));
            if (this.Live == false)
            {
                return;
            }

            // Failed starts become errors right away.
            decisions = this.Decide(config, games, gaming, now);
        }

        var quiet = AutopilotPlanner.IsQuietTime(config, now);
        foreach (var (appId, worker) in this._workers.ToList())
        {
            if (this.Live == false)
            {
                return;
            }

            if (worker.IsReady == false || worker.HasExited)
            {
                continue;
            }

            var game = games.FirstOrDefault(g => g.AppId == appId);
            if (game != null)
            {
                await this.ProcessGameAsync(config, game, worker, quiet, token);
            }
        }

        if (this.Live == false)
        {
            return;
        }

        decisions = this.Decide(config, games, gaming, DateTimeOffset.Now);
        await this.RefreshNextUnlocksAsync(config, decisions, token);
        this.SaveState();
        this.PublishStatus(decisions, config, DateTimeOffset.Now);
    }

    /// <summary>Resets HoursToday/UnlocksToday at local midnight and shows the daily summary.</summary>
    private void RollOver(DateTimeOffset now)
    {
        var today = Today(now);
        if (this._summaryDay != today)
        {
            this.FlushSummary();
            this._summaryDay = today;
        }

        foreach (var state in this.State.Games.Values)
        {
            if (state.Day != today)
            {
                state.Day = today;
                state.HoursToday = 0;
                state.UnlocksToday = 0;
                this._stateDirty = true;
            }

            var before = state.RecentUnlocks.Count;
            state.RecentUnlocks.RemoveAll(t => now - t > TimeSpan.FromHours(2));
            if (state.RecentUnlocks.Count != before)
            {
                this._stateDirty = true;
            }
        }
    }

    /// <summary>The status of every game in queue order; the first MaxConcurrentGames eligible ones run.</summary>
    private List<Decision> Decide(AutopilotConfig config, IReadOnlyList<ResolvedGame> games, uint? gaming, DateTimeOffset now)
    {
        var result = new List<Decision>(games.Count);
        var slots = Math.Clamp(config.MaxConcurrentGames, 1, 32);
        var withinSchedule = AutopilotPlanner.IsWithinSchedule(config, now);
        foreach (var game in games)
        {
            this.State.Games.TryGetValue(game.AppId, out var state);
            AutopilotGameStatusKind kind;
            string? message = null;
            if (state?.Completed == true)
            {
                kind = AutopilotGameStatusKind.Completed;
            }
            else if (state?.Paused == true)
            {
                kind = AutopilotGameStatusKind.PausedByUser;
            }
            else if (this._running == false)
            {
                kind = AutopilotGameStatusKind.Queued;
            }
            else if (AutopilotWorker.IsWindowOpen(game.AppId))
            {
                kind = AutopilotGameStatusKind.Queued;
                message = Strings.Get("AutopilotEngine_WindowOpen");
            }
            else if (withinSchedule == false)
            {
                kind = AutopilotGameStatusKind.WaitingForSchedule;
            }
            else if (gaming != null)
            {
                kind = AutopilotGameStatusKind.PausedWhileGaming;
            }
            else if (config.MaxHoursPerGamePerDay > 0 && state != null && state.HoursToday >= config.MaxHoursPerGamePerDay)
            {
                kind = AutopilotGameStatusKind.DailyLimitReached;
            }
            else if (this._workers.ContainsKey(game.AppId) == false &&
                     this._failures.TryGetValue(game.AppId, out var failure) && failure.RetryAt > now)
            {
                kind = AutopilotGameStatusKind.Error;
                message = failure.Message;
            }
            else if (slots > 0)
            {
                slots--;
                kind = this._workers.TryGetValue(game.AppId, out var worker) && worker.IsReady
                    ? AutopilotGameStatusKind.Running
                    : AutopilotGameStatusKind.Starting;
                message = state?.LastError;
            }
            else
            {
                kind = AutopilotGameStatusKind.Queued;
            }

            result.Add(new Decision(game, kind, message));
        }

        return result;
    }

    /// <summary>One running game: refresh its live state, unlock what's due, mark it finished.</summary>
    private async Task ProcessGameAsync(AutopilotConfig config, ResolvedGame game, AutopilotWorkerClient worker, bool quiet, CancellationToken token)
    {
        var appId = game.AppId;
        var state = this.GetOrCreateState(appId);

        AutopilotResponse live;
        try
        {
            live = await worker.SendAsync(new AutopilotRequest { Type = AutopilotProtocol.State }, StateTimeout, token);
        }
        catch (AutopilotWorkerException e)
        {
            this.CreditRunningTime(appId, DateTimeOffset.Now);
            this.RecordFailure(appId, e.Message, DateTimeOffset.Now);
            this.RemoveWorker(appId);
            return;
        }

        if (live.Ok == false || live.Achievements == null)
        {
            return;
        }

        var schema = await Task.Run(() => services.Schemas.Load(appId), token);
        if (schema == null)
        {
            return;
        }

        var unlockedNow = live.Achievements.Where(a => a.Unlocked).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var known = live.Achievements.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var percents = await this.GetPercentsAsync(appId, token);
        // An achievement Steam didn't report is treated as unlocked, so it's never attempted.
        var achievements = BuildAchievements(schema, a => unlockedNow.Contains(a.Id) || known.Contains(a.Id) == false, percents);
        this._achievements[appId] = achievements;

        var profile = AutopilotPlanner.ResolveProfile(config, appId);
        config.Overrides.TryGetValue(appId, out var gameOverride);
        var now = DateTimeOffset.Now;
        var playtime = this.CurrentPlaytime(state);

        if (quiet == false && (this._unlockRetryAt.TryGetValue(appId, out var retryAt) == false || retryAt <= now))
        {
            var due = AutopilotPlanner.DueNow(profile, gameOverride, achievements, state, playtime, now, this._random);
            this._stateDirty = true;
            if (due.Count > 0 && this.Live)
            {
                var unlocked = await this.UnlockAsync(config, profile, game, state, worker, due);
                if (unlocked.Count > 0)
                {
                    achievements = achievements.Select(a => unlocked.Contains(a.Id) ? a with { IsUnlocked = true } : a).ToList();
                    this._achievements[appId] = achievements;
                    this._nextUnlocks.Remove(appId);
                }
            }
        }

        var remaining = achievements.Count(a => AutopilotPlanner.Classify(profile, gameOverride, a) == AutopilotSkipReason.None);
        if (AutopilotPlanner.IsFinished(profile, state, this.CurrentPlaytime(state), remaining))
        {
            state.Completed = true;
            this._stateDirty = true;
            await this.StopWorkerAsync(appId);
        }
    }

    /// <summary>Sends one unlock batch; records the result in state, history event and notifications. Returns the IDs unlocked.</summary>
    private async Task<HashSet<string>> UnlockAsync(
        AutopilotConfig config,
        AutopilotProfile profile,
        ResolvedGame game,
        AutopilotGameState state,
        AutopilotWorkerClient worker,
        IReadOnlyList<AutopilotAchievement> due)
    {
        var appId = game.AppId;
        var stats = new Dictionary<string, double>(StringComparer.Ordinal);
        if (profile.UpdateLinkedStats)
        {
            foreach (var achievement in due)
            {
                if (achievement.ProgressStatId is { } statId && AutopilotPlanner.LinkedStatTarget(profile, achievement) is { } target)
                {
                    stats[statId] = stats.TryGetValue(statId, out var existing) ? Math.Max(existing, target) : target;
                }
            }
        }

        var request = new AutopilotRequest
        {
            Type = AutopilotProtocol.Unlock,
            Ids = due.Select(a => a.Id).ToList(),
            Stats = stats.Count > 0 ? stats : null,
            Backup = config.BackupBeforeUnlock,
        };

        AutopilotResponse response;
        try
        {
            // Not cancellable: once Steam is storing, the result must be recorded.
            response = await worker.SendAsync(request, UnlockTimeout, CancellationToken.None);
        }
        catch (AutopilotWorkerException e)
        {
            response = new AutopilotResponse { Ok = false, Message = e.Message };
            this.CreditRunningTime(appId, DateTimeOffset.Now);
            this.RemoveWorker(appId);
        }

        var now = DateTimeOffset.Now;
        var unlocked = (response.Ok ? response.Unlocked : null) ?? [];
        var hadError = state.LastError != null;
        if (response.Ok)
        {
            foreach (var id in unlocked)
            {
                if (state.UnlockedByAutopilot.Contains(id) == false)
                {
                    state.UnlockedByAutopilot.Add(id);
                }

                state.RecentUnlocks.Add(now);
            }

            state.UnlocksToday += unlocked.Count;
            if (unlocked.Count > 0)
            {
                state.LastUnlockAt = now;
            }

            state.LastError = null;
            this._unlockRetryAt.Remove(appId);
        }
        else
        {
            state.LastError = response.Message ?? Strings.Get("AutopilotEngine_WorkerNoAnswer");
            this._unlockRetryAt[appId] = now + UnlockRetryDelay;
        }

        this._stateDirty = true;
        this.SaveState();

        var name = this.NameOf(game);
        var statChanges = (response.StatChanges ?? []).Select(s => new StatChange(s.Id, s.From, s.To)).ToList();
        var record = new CommitRecord(now, appId, name, unlocked, [], statChanges, CommitSource.Farm, response.Ok, response.Message);
        try
        {
            this.Unlocked?.Invoke(this, record);
        }
        catch (Exception e)
        {
            Diagnostics.Log("An Autopilot Unlocked handler failed", e);
        }

        var dryRun = response.DryRun ? " " + Strings.Get("AutopilotEngine_DryRunTag") : "";
        if (response.Ok && unlocked.Count > 0)
        {
            var names = string.Join(", ", unlocked.Select(id => due.FirstOrDefault(a => a.Id == id)?.Name ?? id));
            if (config.NotifyMode == AutopilotNotifyMode.EachUnlock)
            {
                services.Notifications.Show(
                    Strings.Format("AutopilotEngine_NotifyTitle", name) + dryRun,
                    Strings.Plural("AutopilotEngine_UnlockedMessage", unlocked.Count, names));
            }
            else if (config.NotifyMode == AutopilotNotifyMode.DailySummary)
            {
                this._summaryCount += unlocked.Count;
                if (this._summaryGames.Contains(name) == false)
                {
                    this._summaryGames.Add(name);
                }
            }
        }
        else if (response.Ok == false && hadError == false && config.NotifyMode != AutopilotNotifyMode.None)
        {
            services.Notifications.Show(Strings.Format("AutopilotEngine_NotifyFailedTitle", name), state.LastError ?? "");
        }

        return unlocked.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>"Next unlock" for running games and the first few queued ones, via the planner's preview (cached).</summary>
    private async Task RefreshNextUnlocksAsync(AutopilotConfig config, IReadOnlyList<Decision> decisions, CancellationToken token)
    {
        var now = DateTimeOffset.Now;
        var queued = 0;
        foreach (var decision in decisions)
        {
            var appId = decision.Game.AppId;
            var running = decision.Kind is AutopilotGameStatusKind.Running;
            if (decision.Kind is AutopilotGameStatusKind.Completed or AutopilotGameStatusKind.Error)
            {
                continue;
            }

            if (running == false && queued++ >= PreviewedQueuedGames)
            {
                continue;
            }

            if (this._nextUnlocks.TryGetValue(appId, out var cached) && now - cached.At < NextUnlockLifetime)
            {
                continue;
            }

            try
            {
                var achievements = await this.LoadAchievementsAsync(appId, preferWorker: true, token);
                if (achievements == null)
                {
                    this._nextUnlocks[appId] = (now, null, null);
                    continue;
                }

                this.State.Games.TryGetValue(appId, out var state);
                var playtime = state == null ? this.SteamHours(appId) : this.CurrentPlaytime(state);
                var plan = AutopilotPlanner.Preview(appId, config, achievements, state, playtime, now);
                var next = plan
                    .Where(p => p.Skip == AutopilotSkipReason.None)
                    .OrderBy(p => p.EstimatedAt ?? DateTimeOffset.MaxValue)
                    .ThenBy(p => p.AtPlaytimeHours)
                    .FirstOrDefault();
                this._nextUnlocks[appId] = (now, next?.Name, next?.EstimatedAt);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Autopilot: couldn't preview {appId}", e);
                this._nextUnlocks[appId] = (now, null, null);
            }
        }
    }

    /// <summary>Builds <see cref="Status"/> and the tray text from the decisions and raises Changed.</summary>
    private void PublishStatus(IReadOnlyList<Decision> decisions, AutopilotConfig config, DateTimeOffset now)
    {
        var result = new List<AutopilotGameStatus>(decisions.Count);
        foreach (var (game, kind, message) in decisions)
        {
            var appId = game.AppId;
            this.State.Games.TryGetValue(appId, out var state);
            var steamHours = this.SteamHours(appId);
            var playtime = state == null ? steamHours : this.CurrentPlaytime(state);

            AutopilotProfile? profile = null;
            try
            {
                profile = AutopilotPlanner.ResolveProfile(config, appId);
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Autopilot: couldn't resolve the profile for {appId}", e);
            }

            double? target = profile?.TargetMode switch
            {
                AutopilotTargetMode.UntilTotalHours => profile.TargetHours,
                AutopilotTargetMode.AddHours => (state?.BaselineSteamHours ?? steamHours) + profile.TargetHours,
                _ => null,
            };

            var remaining = game.Remaining ?? 0;
            if (profile != null && this._achievements.TryGetValue(appId, out var achievements))
            {
                config.Overrides.TryGetValue(appId, out var gameOverride);
                remaining = achievements.Count(a => AutopilotPlanner.Classify(profile, gameOverride, a) == AutopilotSkipReason.None);
            }

            this._nextUnlocks.TryGetValue(appId, out var next);
            var kindNow = kind;
            if (kind == AutopilotGameStatusKind.Starting && this._workers.TryGetValue(appId, out var worker) && worker.IsReady)
            {
                kindNow = AutopilotGameStatusKind.Running;
            }

            result.Add(new AutopilotGameStatus(
                appId,
                this.NameOf(game),
                kindNow,
                playtime,
                state?.AutopilotHours ?? 0,
                target,
                state?.UnlockedByAutopilot.Count ?? 0,
                remaining,
                kindNow is AutopilotGameStatusKind.Completed or AutopilotGameStatusKind.PausedByUser ? null : next.Name,
                kindNow is AutopilotGameStatusKind.Completed or AutopilotGameStatusKind.PausedByUser ? null : next.Estimate,
                message));
        }

        this._status = result;
        this.TrayStatus = this._running ? FormatTrayStatus(result, now) : null;
        this.RaiseChanged();
    }

    private static string FormatTrayStatus(IReadOnlyList<AutopilotGameStatus> status, DateTimeOffset now)
    {
        var running = status.Where(s => s.Kind == AutopilotGameStatusKind.Running).ToList();
        if (running.Count == 0)
        {
            return Strings.Get("AutopilotEngine_TrayIdle");
        }

        var next = running.Where(s => s.NextUnlockEstimate != null).Select(s => s.NextUnlockEstimate!.Value).DefaultIfEmpty().Min();
        if (next == default)
        {
            return Strings.Plural("AutopilotEngine_TrayRunning", running.Count);
        }

        var local = next.ToLocalTime();
        var when = local.Date == now.ToLocalTime().Date
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);
        return Strings.Plural("AutopilotEngine_TrayRunningNext", running.Count, when);
    }

    /// <summary>Status while stopped: every game in scope as queued, paused or completed.</summary>
    private async Task RefreshIdleStatusAsync()
    {
        if (this._running || this._disposed)
        {
            return;
        }

        var config = this.Config;
        var games = await this.GetResolvedAsync(config, CancellationToken.None);
        await this.RefreshPlaytimeAsync();
        if (this._running || this._disposed)
        {
            return;
        }

        var decisions = this.Decide(config, games, null, DateTimeOffset.Now);
        await this.RefreshNextUnlocksAsync(config, decisions, CancellationToken.None);
        if (this._running == false && this._disposed == false)
        {
            this.PublishStatus(decisions, config, DateTimeOffset.Now);
        }
    }
    #endregion

    #region Workers
    private void StartWorker(uint appId, DateTimeOffset now)
    {
        this.GetOrCreateState(appId);
        try
        {
            this._workers[appId] = AutopilotWorkerClient.Start(appId, services.Steam.SteamId);
        }
        catch (AutopilotWorkerException e)
        {
            this.RecordFailure(appId, e.Message, now);
        }
    }

    private async Task ConnectWorkerAsync(uint appId, CancellationToken token)
    {
        if (this._workers.TryGetValue(appId, out var worker) == false)
        {
            return;
        }

        try
        {
            await worker.ConnectAsync(token);
            this._creditedUntil[appId] = DateTimeOffset.Now;
            if (worker.DryRun)
            {
                Diagnostics.Log($"Autopilot: worker for {appId} runs in dry-run mode");
            }
        }
        catch (AutopilotWorkerException e)
        {
            this.RecordFailure(appId, e.Message, DateTimeOffset.Now);
            this.RemoveWorker(appId);
        }
        catch (OperationCanceledException)
        {
            this.RemoveWorker(appId);
            throw;
        }
    }

    private async Task StopWorkerAsync(uint appId)
    {
        if (this._workers.TryGetValue(appId, out var worker) == false)
        {
            return;
        }

        this.CreditRunningTime(appId, DateTimeOffset.Now);
        this._workers.Remove(appId);
        this._creditedUntil.Remove(appId);
        try
        {
            await worker.StopAsync(StopTimeout);
        }
        finally
        {
            worker.Dispose();
        }
    }

    private Task StopAllWorkersAsync() => Task.WhenAll(this._workers.Keys.ToList().Select(this.StopWorkerAsync));

    private void RemoveWorker(uint appId)
    {
        if (this._workers.Remove(appId, out var worker))
        {
            worker.Kill();
            worker.Dispose();
        }

        this._creditedUntil.Remove(appId);
    }

    /// <summary>Remembers why a game's worker failed and when to try again (30 s, doubling, at most 30 min).</summary>
    private void RecordFailure(uint appId, string message, DateTimeOffset now)
    {
        var count = this._failures.TryGetValue(appId, out var previous) ? previous.Count + 1 : 1;
        var delay = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, count - 1), 30 * 60));
        this._failures[appId] = new WorkerFailure(count, now + delay, message);
        Diagnostics.Log($"Autopilot: worker for {appId} failed ({count}x): {message}");

        if (this.State.Games.TryGetValue(appId, out var state) && state.LastError != message)
        {
            state.LastError = message;
            this._stateDirty = true;
        }
    }

    /// <summary>Adds the time since the last credit to AutopilotHours/HoursToday (at most <see cref="MaxCreditPerTick"/>).</summary>
    private void CreditRunningTime(uint appId, DateTimeOffset now)
    {
        if (this._creditedUntil.TryGetValue(appId, out var since) == false)
        {
            return;
        }

        this._creditedUntil[appId] = now;
        var elapsed = now - since;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        if (elapsed > MaxCreditPerTick)
        {
            elapsed = MaxCreditPerTick;
        }

        var state = this.GetOrCreateState(appId);
        state.AutopilotHours += elapsed.TotalHours;
        state.HoursToday += elapsed.TotalHours;
        this._stateDirty = true;
    }
    #endregion

    #region Data
    private async Task<List<ResolvedGame>> GetResolvedAsync(AutopilotConfig config, CancellationToken token)
    {
        if (this._resolved != null && DateTimeOffset.Now - this._resolvedAt < ResolveLifetime)
        {
            return this._resolved;
        }

        var games = await Task.Run(() => this.Resolve(config), token);
        this._resolved = games;
        this._resolvedAt = DateTimeOffset.Now;
        return games;
    }

    /// <summary>
    /// The game list for the config's source, minus exclusions: Manual keeps its order and every
    /// game; Collection keeps every game in it; AllWithLocked, Installed and SmartList keep games
    /// with locked, changeable achievements. Except for Manual, ordered by most remaining first.
    /// Runs off the UI thread.
    /// </summary>
    private List<ResolvedGame> Resolve(AutopilotConfig config)
    {
        var owned = services.Games.LoadCached() ?? [];
        var ownedById = new Dictionary<uint, GameEntry>();
        foreach (var game in owned)
        {
            ownedById.TryAdd(game.AppId, game);
        }

        IEnumerable<uint> source = config.Source switch
        {
            AutopilotGameSource.Manual => config.ManualGames,
            AutopilotGameSource.AllWithLocked or AutopilotGameSource.SmartList => owned.Select(g => g.AppId),
            AutopilotGameSource.Installed => services.Installed.Load().Keys,
            AutopilotGameSource.Collection => this.FindCollection(config.CollectionKey)?.AppIds ?? (IEnumerable<uint>)[],
            _ => [],
        };

        var excluded = config.ExcludedGames.ToHashSet();
        var seen = new HashSet<uint>();
        var result = new List<ResolvedGame>();
        foreach (var appId in source)
        {
            if (appId == 0 || excluded.Contains(appId) || seen.Add(appId) == false)
            {
                continue;
            }

            var (unlocked, total, remaining) = this.CountAchievements(appId);
            var keep = config.Source switch
            {
                AutopilotGameSource.Manual or AutopilotGameSource.Collection => true,
                AutopilotGameSource.SmartList => remaining > 0 && config.SmartList switch
                {
                    "almost-perfect" => total > 0 && (double)unlocked / total >= 0.8 && unlocked < total,
                    "never-started" => total > 0 && unlocked == 0,
                    "rarest-owned" => unlocked > 0,
                    _ => false,
                },
                _ => remaining > 0,
            };

            if (keep)
            {
                ownedById.TryGetValue(appId, out var entry);
                result.Add(new ResolvedGame(appId, entry?.Name, entry?.Type ?? "normal", total == null ? null : remaining));
            }
        }

        if (config.Source != AutopilotGameSource.Manual)
        {
            result = result
                .OrderByDescending(g => g.Remaining ?? 0)
                .ThenBy(g => g.Name ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(g => g.AppId)
                .ToList();
        }

        return result;
    }

    private GameCollection? FindCollection(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return services.Collections.GetCustomCollections().FirstOrDefault(c => c.FilterKey == key) ??
            services.Collections.LoadSteamCollections().FirstOrDefault(c => c.FilterKey == key);
    }

    /// <summary>From the schema and Steam's local stats cache: unlocked, total and locked-but-changeable counts (null total without a schema).</summary>
    private (int Unlocked, int? Total, int Remaining) CountAchievements(uint appId)
    {
        try
        {
            var schema = services.Schemas.Load(appId);
            if (schema == null)
            {
                return (0, null, 0);
            }

            var stats = this.LoadStatsCache(appId);
            int unlocked = 0, remaining = 0;
            foreach (var achievement in schema.Achievements)
            {
                if (stats.GetState(achievement).Unlocked)
                {
                    unlocked++;
                }
                else if (achievement.IsProtected == false)
                {
                    remaining++;
                }
            }

            return (unlocked, schema.Achievements.Count, remaining);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Autopilot: couldn't count achievements for {appId}", e);
            return (0, null, 0);
        }
    }

    private UserStatsCache LoadStatsCache(uint appId) =>
        UserStatsCache.Load(ProgressService.GetStatsPath(services.Steam.SteamInstallPath, services.Steam.AccountId, appId)) ?? UserStatsCache.Empty;

    /// <summary>
    /// The planner's view of a game's achievements: from the running worker's last state when
    /// <paramref name="preferWorker"/> and available, else from the schema and Steam's local
    /// stats cache. Null without a schema.
    /// </summary>
    private async Task<IReadOnlyList<AutopilotAchievement>?> LoadAchievementsAsync(uint appId, bool preferWorker, CancellationToken token)
    {
        if (preferWorker && this._workers.ContainsKey(appId) && this._achievements.TryGetValue(appId, out var live))
        {
            return live;
        }

        var schema = await Task.Run(() => services.Schemas.Load(appId), token);
        if (schema == null)
        {
            return null;
        }

        var stats = await Task.Run(() => this.LoadStatsCache(appId), token);
        var percents = await this.GetPercentsAsync(appId, token);
        return BuildAchievements(schema, a => stats.GetState(a).Unlocked, percents);
    }

    internal static List<AutopilotAchievement> BuildAchievements(
        GameSchema schema, Func<AchievementDefinition, bool> isUnlocked, IReadOnlyDictionary<string, double>? percents)
    {
        var result = new List<AutopilotAchievement>(schema.Achievements.Count);
        for (var i = 0; i < schema.Achievements.Count; i++)
        {
            var a = schema.Achievements[i];
            double? percent = percents != null && percents.TryGetValue(a.Id, out var p) ? p : null;
            result.Add(new AutopilotAchievement(a.Id, a.Name, percent, a.IsHidden, a.IsProtected, isUnlocked(a), a.ProgressStatId, a.ProgressMax, i));
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, double>?> GetPercentsAsync(uint appId, CancellationToken token)
    {
        if (this._percents.TryGetValue(appId, out var cached) && DateTimeOffset.Now - cached.At < PercentLifetime)
        {
            return cached.Percents;
        }

        var percents = await services.GlobalPercents.GetAsync(appId, token);
        token.ThrowIfCancellationRequested();
        this._percents[appId] = (DateTimeOffset.Now, percents);
        return percents;
    }

    private async Task RefreshPlaytimeAsync()
    {
        if (DateTimeOffset.Now - this._playtimeAt < PlaytimeLifetime)
        {
            return;
        }

        this._playtimeAt = DateTimeOffset.Now;
        try
        {
            this._playtime = await Task.Run(services.Playtime.Load);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't read Steam playtime", e);
        }
    }

    private double SteamHours(uint appId) =>
        this._playtime.TryGetValue(appId, out var info) ? info.Playtime.TotalHours : 0;

    /// <summary>Baseline + Autopilot hours, or Steam's own playtime if that's higher.</summary>
    private double CurrentPlaytime(AutopilotGameState state) =>
        Math.Max(state.BaselineSteamHours + state.AutopilotHours, this.SteamHours(state.AppId));

    private AutopilotGameState GetOrCreateState(uint appId)
    {
        if (this.State.Games.TryGetValue(appId, out var state))
        {
            return state;
        }

        state = new AutopilotGameState
        {
            AppId = appId,
            BaselineSteamHours = this.SteamHours(appId),
            Day = Today(DateTimeOffset.Now),
        };
        this.State.Games[appId] = state;
        this._stateDirty = true;
        return state;
    }

    private void SetPaused(uint appId, bool paused)
    {
        var state = this.GetOrCreateState(appId);
        if (state.Paused == paused)
        {
            return;
        }

        state.Paused = paused;
        this._stateDirty = true;
        this.SaveState();
        this.RequestTick();
        this.RaiseChanged();
    }

    private string NameOf(ResolvedGame game)
    {
        if (game.Name != null)
        {
            return game.Name;
        }

        if (this._names.TryGetValue(game.AppId, out var name) == false)
        {
            name = services.Steam.GetAppData(game.AppId, "name") ?? $"App {game.AppId.ToString(CultureInfo.InvariantCulture)}";
            this._names[game.AppId] = name;
        }

        return name;
    }

    private uint? FindRunningGame()
    {
        try
        {
            var installed = services.Installed.Load();
            return GameProcessMonitor.FindRunningGame(installed.ToDictionary(p => p.Key, p => p.Value.InstallDirectory));
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't check for running games", e);
            return null;
        }
    }

    private void SaveConfigFile()
    {
        try
        {
            services.AutopilotStore.SaveConfig(this.Config);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't save the configuration", e);
        }
    }

    private void SaveState(bool force = false)
    {
        if ((this._stateDirty == false && force == false) || this._state == null)
        {
            return;
        }

        try
        {
            services.AutopilotStore.SaveState(this._state);
            this._stateDirty = false;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't save its state", e);
        }
    }

    /// <summary>DailySummary mode: one toast with the day's unlocks (at midnight or when Autopilot stops).</summary>
    private void FlushSummary()
    {
        var count = this._summaryCount;
        var games = string.Join(", ", this._summaryGames);
        this._summaryCount = 0;
        this._summaryGames.Clear();
        if (count > 0 && this._config?.NotifyMode == AutopilotNotifyMode.DailySummary)
        {
            services.Notifications.Show(Strings.Get("AutopilotEngine_SummaryTitle"), Strings.Plural("AutopilotEngine_SummaryMessage", count, games));
        }
    }

    private void RaiseChanged()
    {
        try
        {
            this.Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            Diagnostics.Log("An Autopilot Changed handler failed", e);
        }
    }

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.LocalDateTime);
    #endregion
}
