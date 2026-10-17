using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Every unlocked achievement with its time, across games, from Steam's local stats cache
/// (UserGameStats files: an achievement counts only if its bit is set in the block's data,
/// since re-locked achievements keep their old AchievementTimes entry) plus each game's schema
/// for names. Safe to call off the UI thread.
/// </summary>
public sealed class TimelineService(SteamSession steam, SchemaService schemas)
{
    /// <summary>Newest first.</summary>
    public IReadOnlyList<UnlockEvent> Load(IEnumerable<GameEntry> games) => throw new NotImplementedException("TODO(data-b)");

    /// <summary>
    /// Every schema achievement for each game with its unlocked state and time (for CSV export).
    /// GlobalPercent is left null; callers fill it if they have it.
    /// </summary>
    public IReadOnlyList<AchievementExportRow> LoadAllAchievements(IEnumerable<GameEntry> games) =>
        throw new NotImplementedException("TODO(data-b)");
}
