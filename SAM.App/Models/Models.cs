namespace SAM.App.Models;

/// <summary>A game from SAM's game list that the user owns. Type is "normal", "demo", "mod" or "junk".</summary>
public sealed record GameEntry(uint AppId, string Name, string Type);

/// <summary>Achievement definition from the game's schema (UserGameStatsSchema_&lt;appid&gt;.bin).</summary>
public sealed class AchievementDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Icon file name for the unlocked state, relative to the app's community image folder.</summary>
    public string? IconNormal { get; init; }

    /// <summary>Icon file name for the locked state (falls back to IconNormal).</summary>
    public string? IconLocked { get; init; }

    public bool IsHidden { get; init; }

    /// <summary>Non-zero (bits 0-1) means protected: only the game's servers may change it.</summary>
    public int Permission { get; init; }

    public bool IsProtected => (this.Permission & 3) != 0;

    /// <summary>Schema stat block ID this achievement's bit lives in (used to read Steam's local stats cache).</summary>
    public int StatBlockId { get; init; }

    /// <summary>Bit index within <see cref="StatBlockId"/>.</summary>
    public int BitIndex { get; init; }
}

public enum StatKind
{
    Integer,
    Float,
    AverageRate,
}

/// <summary>Stat definition from the game's schema.</summary>
public sealed class StatDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public StatKind Kind { get; init; }

    public double MinValue { get; init; }

    public double MaxValue { get; init; }

    public double MaxChange { get; init; }

    public bool IncrementOnly { get; init; }

    public bool SetByTrustedGameServer { get; init; }

    public double DefaultValue { get; init; }

    public int Permission { get; init; }

    public bool IsProtected => (this.Permission & 2) != 0;
}

public sealed class GameSchema
{
    public required uint AppId { get; init; }

    public required IReadOnlyList<AchievementDefinition> Achievements { get; init; }

    public required IReadOnlyList<StatDefinition> Stats { get; init; }
}

/// <summary>Achievement progress for one game.</summary>
public readonly record struct GameProgress(int Unlocked, int Total)
{
    public double Fraction => this.Total == 0 ? 0 : (double)this.Unlocked / this.Total;

    public bool IsPerfect => this.Total > 0 && this.Unlocked == this.Total;
}

/// <summary>From Steam's localconfig.vdf for the current account.</summary>
public readonly record struct PlaytimeInfo(DateTimeOffset? LastPlayed, TimeSpan Playtime);

/// <summary>Date range for date filters. Null bounds are open.</summary>
public readonly record struct DateRange(DateTimeOffset? From, DateTimeOffset? To)
{
    public bool Contains(DateTimeOffset? value) =>
        value.HasValue &&
        (this.From == null || value >= this.From) &&
        (this.To == null || value <= this.To);
}
