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
    private static readonly ParallelOptions Parallelism = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
    };

    /// <summary>Newest first.</summary>
    /// <remarks>Games without a schema or without cached stats contribute nothing; unlocks with no recorded time are left out.</remarks>
    public IReadOnlyList<UnlockEvent> Load(IEnumerable<GameEntry> games)
    {
        var list = Distinct(games);
        var perGame = new List<UnlockEvent>[list.Count];

        Parallel.For(0, list.Count, Parallelism, i =>
        {
            var game = list[i];
            try
            {
                var schema = schemas.Load(game.AppId);
                var stats = schema == null ? null : this.LoadStats(game.AppId);
                perGame[i] = schema == null || stats == null ? [] : GetUnlocks(game, schema, stats);
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Couldn't read unlock times for {game.AppId}", e);
                perGame[i] = [];
            }
        });

        return SortNewestFirst(perGame.SelectMany(e => e));
    }

    /// <summary>
    /// Every schema achievement for each game with its unlocked state and time (for CSV export).
    /// GlobalPercent is left null; callers fill it if they have it.
    /// </summary>
    /// <remarks>
    /// Rows are grouped by game in input order, then in schema order. Games without a schema are
    /// left out; games with no readable stats cache are listed with every achievement locked.
    /// </remarks>
    public IReadOnlyList<AchievementExportRow> LoadAllAchievements(IEnumerable<GameEntry> games)
    {
        var list = Distinct(games);
        var perGame = new List<AchievementExportRow>[list.Count];

        Parallel.For(0, list.Count, Parallelism, i =>
        {
            var game = list[i];
            try
            {
                var schema = schemas.Load(game.AppId);
                perGame[i] = schema == null
                    ? []
                    : GetExportRows(game, schema, this.LoadStats(game.AppId) ?? UserStatsCache.Empty);
            }
            catch (Exception e)
            {
                Diagnostics.Log($"Couldn't read achievements for {game.AppId}", e);
                perGame[i] = [];
            }
        });

        return perGame.SelectMany(r => r).ToList();
    }

    /// <summary>Unlocks (bit set and time recorded) for one game, in schema order.</summary>
    internal static List<UnlockEvent> GetUnlocks(GameEntry game, GameSchema schema, UserStatsCache stats)
    {
        var result = new List<UnlockEvent>();
        foreach (var achievement in schema.Achievements)
        {
            var (unlocked, time) = stats.GetState(achievement);
            if (unlocked && time != null)
            {
                result.Add(new UnlockEvent(
                    game.AppId,
                    game.Name,
                    achievement.Id,
                    achievement.Name,
                    achievement.Description,
                    achievement.IconNormal,
                    time.Value));
            }
        }

        return result;
    }

    /// <summary>One row per schema achievement for one game, in schema order.</summary>
    internal static List<AchievementExportRow> GetExportRows(GameEntry game, GameSchema schema, UserStatsCache stats)
    {
        var result = new List<AchievementExportRow>(schema.Achievements.Count);
        foreach (var achievement in schema.Achievements)
        {
            var (unlocked, time) = stats.GetState(achievement);
            result.Add(new AchievementExportRow(
                game.AppId,
                game.Name,
                achievement.Id,
                achievement.Name,
                achievement.Description,
                unlocked,
                time,
                null));
        }

        return result;
    }

    /// <summary>Newest first; ties broken by App ID then achievement ID so the order is stable.</summary>
    internal static List<UnlockEvent> SortNewestFirst(IEnumerable<UnlockEvent> events) =>
        events
            .OrderByDescending(e => e.Time)
            .ThenBy(e => e.AppId)
            .ThenBy(e => e.AchievementId, StringComparer.Ordinal)
            .ToList();

    /// <summary>Null when the user has no cached stats for the game (or the file can't be read).</summary>
    private UserStatsCache? LoadStats(uint appId) =>
        UserStatsCache.Load(ProgressService.GetStatsPath(steam.SteamInstallPath, steam.AccountId, appId));

    private static List<GameEntry> Distinct(IEnumerable<GameEntry> games)
    {
        var seen = new HashSet<uint>();
        return games.Where(g => seen.Add(g.AppId)).ToList();
    }
}
