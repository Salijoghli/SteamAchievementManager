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
public sealed class AutopilotEngine(AppServices services) : IDisposable
{
    /// <summary>Raised when IsRunning, Config or any game's status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after each unlock batch (also on failure, with Succeeded = false).</summary>
    public event EventHandler<CommitRecord>? Unlocked;

    public bool IsRunning => throw new NotImplementedException("TODO(autopilot-runtime)");

    /// <summary>The current configuration (a live object; call SaveConfig after editing a copy).</summary>
    public AutopilotConfig Config => throw new NotImplementedException("TODO(autopilot-runtime)");

    /// <summary>Saves and applies a new configuration (takes effect on the next tick).</summary>
    public void SaveConfig(AutopilotConfig config) => throw new NotImplementedException("TODO(autopilot-runtime)");

    /// <summary>Status of every game in scope (running, queued, waiting, finished…), in queue order.</summary>
    public IReadOnlyList<AutopilotGameStatus> Status => throw new NotImplementedException("TODO(autopilot-runtime)");

    /// <summary>The games Autopilot would consider right now under the current config (resolves the source).</summary>
    public IReadOnlyList<GameEntry> ResolveGames() => throw new NotImplementedException("TODO(autopilot-runtime)");

    public Task StartAsync() => throw new NotImplementedException("TODO(autopilot-runtime)");

    /// <summary>Stops all workers and saves state.</summary>
    public Task StopAsync() => throw new NotImplementedException("TODO(autopilot-runtime)");

    public void PauseGame(uint appId) => throw new NotImplementedException("TODO(autopilot-runtime)");

    public void ResumeGame(uint appId) => throw new NotImplementedException("TODO(autopilot-runtime)");

    /// <summary>Clears a game's Autopilot progress (hours, seeds, completed flag) — not its achievements.</summary>
    public void ResetGame(uint appId) => throw new NotImplementedException("TODO(autopilot-runtime)");

    /// <summary>
    /// Dry-run plan for one game using current state, playtime and global percentages (loads
    /// schema/percents itself; no Steam writes, no worker needed).
    /// </summary>
    public Task<IReadOnlyList<AutopilotPlannedUnlock>> PreviewAsync(uint appId, AutopilotConfig config, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(autopilot-runtime)");

    /// <summary>
    /// Whether an Autopilot worker currently holds <paramref name="appId"/> (any process; uses a
    /// named mutex "SteamAchievementManager.Autopilot.&lt;appid&gt;"). Game windows use this to warn.
    /// </summary>
    public static bool IsGameRunningUnderAutopilot(uint appId) => throw new NotImplementedException("TODO(autopilot-runtime)");

    public void Dispose()
    {
    }
}
