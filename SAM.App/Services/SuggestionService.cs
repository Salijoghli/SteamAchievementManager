using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>A schema achievement the user hasn't unlocked yet.</summary>
/// <param name="IconFile">Icon for the unlocked state (null if the schema has none).</param>
/// <param name="LockedIconFile">Icon for the locked state (falls back to <paramref name="IconFile"/>).</param>
public sealed record MissingAchievement(
    uint AppId,
    string GameName,
    string Id,
    string Name,
    string Description,
    bool IsHidden,
    string? IconFile,
    string? LockedIconFile);

/// <summary>An owned game with achievement data and at least one achievement left to unlock.</summary>
/// <param name="Total">Achievements in the game's schema.</param>
/// <param name="Missing">The locked ones, in schema order.</param>
public sealed record IncompleteGame(uint AppId, string Name, bool IsInstalled, int Total, IReadOnlyList<MissingAchievement> Missing)
{
    public int Unlocked => this.Total - this.Missing.Count;
}

/// <summary>A suggested achievement with its global unlock percentage.</summary>
public sealed record RankedAchievement(MissingAchievement Achievement, double Percent);

/// <summary>A game close to 100%: how many achievements are left and how common the hardest of them is.</summary>
/// <param name="HardestPercent">Lowest global percent among the remaining achievements; null when unknown.</param>
public sealed record ReachableGame(IncompleteGame Game, int Remaining, double? HardestPercent);

/// <summary>Filters shared by the "what to unlock next" lists.</summary>
/// <param name="InstalledOnly">Only games that are installed.</param>
/// <param name="HideAllRare">Leave out games whose remaining achievements are all rarer than <see cref="Suggestions.RarePercent"/>.</param>
public sealed record SuggestionFilter(bool InstalledOnly, bool HideAllRare);

/// <summary>
/// Reads which achievements the user is missing in each game, off the UI thread, for the Stats
/// page's "Up next" section. Unlocked state comes from <see cref="TimelineService.LoadAllAchievements"/>
/// (Steam's local stats cache); hidden flags and icons from the memoized schemas.
/// </summary>
public sealed class SuggestionService(TimelineService timeline, SchemaService schemas)
{
    /// <summary>
    /// Every game in <paramref name="games"/> with at least one locked achievement. Games without a
    /// schema (achievement data not loaded) or with every achievement unlocked are left out.
    /// </summary>
    /// <param name="installed">App IDs of installed games.</param>
    public IReadOnlyList<IncompleteGame> LoadIncomplete(IReadOnlyList<GameEntry> games, IReadOnlySet<uint> installed, CancellationToken token)
    {
        var rows = timeline.LoadAllAchievements(games);
        token.ThrowIfCancellationRequested();

        var result = new List<IncompleteGame>();
        foreach (var group in rows.GroupBy(r => r.AppId))
        {
            token.ThrowIfCancellationRequested();
            var locked = group.Where(r => r.Unlocked == false).ToList();
            if (locked.Count == 0)
            {
                continue;
            }

            var definitions = schemas.Load(group.Key)?.Achievements
                .GroupBy(a => a.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var missing = locked
                .Select(r =>
                {
                    AchievementDefinition? definition = null;
                    definitions?.TryGetValue(r.AchievementId, out definition);
                    return new MissingAchievement(
                        r.AppId,
                        r.GameName,
                        r.AchievementId,
                        r.Name,
                        r.Description,
                        definition?.IsHidden ?? false,
                        definition?.IconNormal,
                        string.IsNullOrEmpty(definition?.IconLocked) ? definition?.IconNormal : definition.IconLocked);
                })
                .ToList();

            var first = group.First();
            result.Add(new IncompleteGame(first.AppId, first.GameName, installed.Contains(first.AppId), group.Count(), missing));
        }

        return result;
    }
}

/// <summary>
/// Ranking for "what to unlock next": global unlock percentage is the difficulty signal (the more
/// players have an achievement, the easier it probably is). Pure functions, so they're testable.
/// </summary>
public static class Suggestions
{
    /// <summary>Below this global percent an achievement counts as rare for <see cref="SuggestionFilter.HideAllRare"/>.</summary>
    public const double RarePercent = 5;

    /// <summary>A game is "within reach" with this many achievements left or fewer...</summary>
    public const int ReachableRemaining = 3;

    /// <summary>...or at least this fraction unlocked.</summary>
    public const double ReachableFraction = 0.8;

    /// <summary>
    /// The most-unlocked achievements the user is missing, highest global percent first, at most
    /// <paramref name="perGame"/> per game so one barely-started game doesn't fill the list.
    /// Achievements (and games) without a known percent are left out.
    /// </summary>
    /// <param name="percents">Global percents by app; null when not known (yet).</param>
    public static IReadOnlyList<RankedAchievement> EasiestMissing(
        IEnumerable<IncompleteGame> games,
        Func<uint, IReadOnlyDictionary<string, double>?> percents,
        SuggestionFilter filter,
        int count,
        int perGame)
    {
        var candidates = new List<RankedAchievement>();
        foreach (var game in games)
        {
            if (filter.InstalledOnly && game.IsInstalled == false)
            {
                continue;
            }

            var known = percents(game.AppId);
            if (known == null || (filter.HideAllRare && AreAllRare(game, known)))
            {
                continue;
            }

            candidates.AddRange(game.Missing
                .Where(m => known.ContainsKey(m.Id))
                .Select(m => new RankedAchievement(m, known[m.Id]))
                .OrderByDescending(r => r.Percent)
                .ThenBy(r => r.Achievement.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(perGame));
        }

        return candidates
            .OrderByDescending(r => r.Percent)
            .ThenBy(r => r.Achievement.GameName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Achievement.AppId)
            .ThenBy(r => r.Achievement.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Achievement.Id, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Games with few achievements left (see <see cref="IsWithinReach"/>): fewest remaining first,
    /// then the ones whose hardest remaining achievement is most common; unknown rarity sorts last.
    /// </summary>
    public static IReadOnlyList<ReachableGame> PerfectWithinReach(
        IEnumerable<IncompleteGame> games,
        Func<uint, IReadOnlyDictionary<string, double>?> percents,
        SuggestionFilter filter,
        int count)
    {
        var result = new List<ReachableGame>();
        foreach (var game in games)
        {
            if (IsWithinReach(game.Total, game.Missing.Count) == false ||
                (filter.InstalledOnly && game.IsInstalled == false))
            {
                continue;
            }

            var known = percents(game.AppId);
            if (filter.HideAllRare && known != null && AreAllRare(game, known))
            {
                continue;
            }

            result.Add(new ReachableGame(game, game.Missing.Count, known == null ? null : HardestPercent(game.Missing.Select(m => m.Id), known)));
        }

        return result
            .OrderBy(g => g.Remaining)
            .ThenByDescending(g => g.HardestPercent ?? -1)
            .ThenBy(g => g.Game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Game.AppId)
            .Take(count)
            .ToList();
    }

    /// <summary>True for a game with 1-<see cref="ReachableRemaining"/> achievements left, or at least <see cref="ReachableFraction"/> unlocked.</summary>
    public static bool IsWithinReach(int total, int remaining) =>
        total > 0 && remaining > 0 && remaining <= total &&
        (remaining <= ReachableRemaining || (double)(total - remaining) / total >= ReachableFraction);

    /// <summary>The highest global percent among <paramref name="lockedIds"/>; null when none is known.</summary>
    public static double? EasiestPercent(IEnumerable<string> lockedIds, IReadOnlyDictionary<string, double> percents)
    {
        double? easiest = null;
        foreach (var id in lockedIds)
        {
            if (percents.TryGetValue(id, out var value) && (easiest == null || value > easiest))
            {
                easiest = value;
            }
        }

        return easiest;
    }

    /// <summary>The lowest global percent among <paramref name="lockedIds"/>; null when none is known.</summary>
    public static double? HardestPercent(IEnumerable<string> lockedIds, IReadOnlyDictionary<string, double> percents)
    {
        double? hardest = null;
        foreach (var id in lockedIds)
        {
            if (percents.TryGetValue(id, out var value) && (hardest == null || value < hardest))
            {
                hardest = value;
            }
        }

        return hardest;
    }

    /// <summary>
    /// True when every remaining achievement has a known percent below <see cref="RarePercent"/>.
    /// One with an unknown percent might be easy, so the game stays.
    /// </summary>
    public static bool AreAllRare(IncompleteGame game, IReadOnlyDictionary<string, double> percents) =>
        game.Missing.Count > 0 &&
        game.Missing.All(m => percents.TryGetValue(m.Id, out var value) && value < RarePercent);
}
