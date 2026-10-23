using SAM.App.Services;

namespace SAM.App.Models;

/// <summary>What a library card knows about a game's achievements.</summary>
public enum AchievementState
{
    /// <summary>Steam's schema is on this PC: progress is known (e.g. "12 / 30 · 40%").</summary>
    Loaded,

    /// <summary>No schema yet, but the store says the game has achievements: Steam hasn't downloaded them.</summary>
    NotLoaded,

    /// <summary>The game has no achievements (the schema has none, or there's no schema and the store lists none).</summary>
    None,

    /// <summary>No schema and the store lookup failed: the game may or may not have achievements.</summary>
    Unknown,

    /// <summary>No schema and the store lookup hasn't answered yet.</summary>
    Checking,
}

/// <summary>Classifies a game's <see cref="AchievementState"/>. Pure, so it can be unit tested.</summary>
public static class AchievementStates
{
    /// <param name="progress">Progress from the local schema and stats cache; null when there's no schema.</param>
    /// <param name="store">The store lookup's result; null while it hasn't answered.</param>
    public static AchievementState Classify(GameProgress? progress, StoreAchievementCount? store)
    {
        if (progress is { } p)
        {
            return p.Total > 0 ? AchievementState.Loaded : AchievementState.None;
        }

        return store?.Status switch
        {
            null => AchievementState.Checking,
            StoreAchievementStatus.Count => AchievementState.NotLoaded,
            StoreAchievementStatus.None => AchievementState.None,
            _ => AchievementState.Unknown,
        };
    }

    /// <summary>True for games whose achievement data can be loaded (asked from Steam) but isn't.</summary>
    public static bool IsMissing(AchievementState state) => state is AchievementState.NotLoaded or AchievementState.Unknown;

    /// <summary>
    /// Completion sort key: the unlocked fraction for loaded games, -1 for games without
    /// achievements, and -0.5 for games whose progress isn't known (between the two).
    /// </summary>
    public static double CompletionSortKey(AchievementState state, GameProgress? progress) => state switch
    {
        AchievementState.Loaded when progress is { } p => p.Fraction,
        AchievementState.None => -1,
        _ => -0.5,
    };
}
