using SAM.App.Models;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// The Autopilot rules as pure functions (no I/O, no Steam), so they're fully unit-testable.
/// The engine calls these on every tick; the UI calls Preview for the dry-run table.
/// </summary>
public static class AutopilotPlanner
{
    /// <summary>The profile that applies to a game: its override's profile if set, else the active one, else "Natural"; with the override's TargetMode/TargetHours/IncludeRare applied (returns a copy).</summary>
    public static AutopilotProfile ResolveProfile(AutopilotConfig config, uint appId) => throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>Why <paramref name="achievement"/> would be skipped under these rules, or None if it may be unlocked.</summary>
    public static AutopilotSkipReason Classify(AutopilotProfile profile, AutopilotGameOverride? gameOverride, AutopilotAchievement achievement) =>
        throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>
    /// Playtime-mode threshold: total playtime hours after which an achievement with this global
    /// percent may unlock. Interpolates the curve on a log scale of the percent (clamped to the
    /// curve's ends), then applies the per-achievement <paramref name="jitterSeed"/> (0–1) scaled
    /// by JitterPercent (±). Unknown percent uses UnknownPercentAs.
    /// </summary>
    public static double RequiredHours(AutopilotProfile profile, double? globalPercent, double jitterSeed) =>
        throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>Allowed achievements (Classify == None) in unlock order (profile.Order; Random uses <paramref name="random"/>, stable per call).</summary>
    public static IReadOnlyList<AutopilotAchievement> OrderCandidates(
        AutopilotProfile profile,
        AutopilotGameOverride? gameOverride,
        IEnumerable<AutopilotAchievement> achievements,
        Random random) => throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>
    /// Achievements to unlock right now for one game. Respects pacing (playtime curve or interval,
    /// using/creating seeds in <paramref name="state"/>.JitterSeeds and NextIntervalAtHours),
    /// MinMinutesBetweenUnlocks, MaxUnlocksPerHour/Day (state.RecentUnlocks/UnlocksToday),
    /// and never returns more than the limits allow. Mutates only seed/next-interval fields of
    /// <paramref name="state"/>; the engine records the actual unlocks.
    /// </summary>
    public static IReadOnlyList<AutopilotAchievement> DueNow(
        AutopilotProfile profile,
        AutopilotGameOverride? gameOverride,
        IReadOnlyList<AutopilotAchievement> achievements,
        AutopilotGameState state,
        double currentPlaytimeHours,
        DateTimeOffset now,
        Random random) => throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>
    /// Full dry-run plan for one game: every achievement with its planned playtime and, given
    /// <paramref name="hoursPerDay"/> of running time (from the schedule), an estimated date —
    /// honoring daily/hourly limits and quiet hours approximately. Skipped achievements are
    /// included with their reason and no time. Does not mutate <paramref name="state"/>.
    /// </summary>
    public static IReadOnlyList<AutopilotPlannedUnlock> Preview(
        uint appId,
        AutopilotConfig config,
        IReadOnlyList<AutopilotAchievement> achievements,
        AutopilotGameState? state,
        double currentPlaytimeHours,
        DateTimeOffset now) => throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>Whether games may run at <paramref name="now"/> (ActiveDays + ActiveFrom/To window, which may span midnight).</summary>
    public static bool IsWithinSchedule(AutopilotConfig config, DateTimeOffset now) => throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>Whether <paramref name="now"/> is in quiet hours (no unlocks).</summary>
    public static bool IsQuietTime(AutopilotConfig config, DateTimeOffset now) => throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>Running hours per day the schedule allows (24 when unrestricted), averaged over the week, capped by MaxHoursPerGamePerDay.</summary>
    public static double HoursPerDay(AutopilotConfig config) => throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>Whether a game has reached its time target (or everything allowed is unlocked and StopWhenAllUnlocked).</summary>
    public static bool IsFinished(AutopilotProfile profile, AutopilotGameState state, double currentPlaytimeHours, int remainingAllowed) =>
        throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>The value a linked stat should be raised to before unlocking (ProgressMax), or null if not applicable.</summary>
    public static double? LinkedStatTarget(AutopilotProfile profile, AutopilotAchievement achievement) =>
        throw new NotImplementedException("TODO(autopilot-core)");
}
