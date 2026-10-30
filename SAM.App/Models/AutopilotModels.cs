namespace SAM.App.Models;

// Autopilot: runs games in the background (Steam counts them as played) and unlocks
// achievements over time according to the user's rules. Everything here is user-configurable
// and persisted as JSON, so prefer plain settable properties with sensible defaults.

#region Configuration
/// <summary>Where the Autopilot game list comes from.</summary>
public enum AutopilotGameSource
{
    /// <summary>Only the games in <see cref="AutopilotConfig.ManualGames"/>.</summary>
    Manual,

    /// <summary>Every owned game that still has unlockable achievements.</summary>
    AllWithLocked,

    /// <summary>The games in <see cref="AutopilotConfig.CollectionKey"/> (a GameCollection.FilterKey).</summary>
    Collection,

    /// <summary>The games in the smart list <see cref="AutopilotConfig.SmartList"/> ("almost-perfect", "never-started").</summary>
    SmartList,

    /// <summary>Every installed game with unlockable achievements.</summary>
    Installed,
}

public enum AutopilotPacingMode
{
    /// <summary>Each achievement needs a minimum playtime that depends on its rarity (<see cref="AutopilotProfile.Curve"/>).</summary>
    Playtime,

    /// <summary>One achievement every <see cref="AutopilotProfile.IntervalMinutes"/> (± jitter) of running time.</summary>
    Interval,
}

public enum AutopilotUnlockOrder
{
    /// <summary>Highest global percentage first (the order most players earn them).</summary>
    MostCommonFirst,

    /// <summary>The game's own schema order.</summary>
    SchemaOrder,

    Random,
}

public enum AutopilotTargetMode
{
    /// <summary>Run until the game's total Steam playtime reaches <see cref="AutopilotProfile.TargetHours"/>.</summary>
    UntilTotalHours,

    /// <summary>Run for <see cref="AutopilotProfile.TargetHours"/> more hours of Autopilot time.</summary>
    AddHours,

    /// <summary>No time target; stop only when everything allowed is unlocked (or never, if StopWhenAllUnlocked is off).</summary>
    Unlimited,
}

/// <summary>A point on the playtime curve: achievements held by <see cref="Percent"/>% of players unlock after <see cref="Hours"/> hours of total playtime.</summary>
public sealed class AutopilotCurvePoint
{
    public double Percent { get; set; }

    public double Hours { get; set; }
}

/// <summary>A named set of rules (a preset). Built-in presets: "Natural", "Fast"; users can add their own.</summary>
public sealed class AutopilotProfile
{
    public string Name { get; set; } = "Custom";

    /// <summary>Built-in presets can be copied but not edited or deleted.</summary>
    public bool IsBuiltIn { get; set; }

    #region What to unlock
    /// <summary>Only achievements held by at least this percentage of players (0 = no minimum).</summary>
    public double MinGlobalPercent { get; set; }

    /// <summary>Allow "rare" achievements (global percent below <see cref="RareThresholdPercent"/>).</summary>
    public bool IncludeRare { get; set; }

    public double RareThresholdPercent { get; set; } = 5;

    /// <summary>Achievements without global percentage data: unlock them (treated as <see cref="UnknownPercentAs"/>) or skip them.</summary>
    public bool IncludeUnknownRarity { get; set; } = true;

    public double UnknownPercentAs { get; set; } = 20;

    public bool SkipHidden { get; set; }

    /// <summary>Before unlocking an achievement linked to a stat (schema progress block), raise that stat to the target value.</summary>
    public bool UpdateLinkedStats { get; set; } = true;
    #endregion

    #region When to unlock
    public AutopilotPacingMode Pacing { get; set; } = AutopilotPacingMode.Playtime;

    /// <summary>Playtime curve, interpolated on a log scale of the percentage. Sorted by Percent descending when used.</summary>
    public List<AutopilotCurvePoint> Curve { get; set; } =
    [
        new() { Percent = 98, Hours = 1 },
        new() { Percent = 50, Hours = 5 },
        new() { Percent = 10, Hours = 20 },
        new() { Percent = 1, Hours = 60 },
    ];

    public double IntervalMinutes { get; set; } = 60;

    /// <summary>Random variation applied to intervals and to curve hours, in percent (0–100).</summary>
    public double JitterPercent { get; set; } = 20;

    public AutopilotUnlockOrder Order { get; set; } = AutopilotUnlockOrder.MostCommonFirst;

    /// <summary>Minimum minutes between two unlocks in the same game (0 = none).</summary>
    public double MinMinutesBetweenUnlocks { get; set; } = 10;

    /// <summary>Per game; 0 = unlimited.</summary>
    public int MaxUnlocksPerHour { get; set; } = 2;

    /// <summary>Per game; 0 = unlimited.</summary>
    public int MaxUnlocksPerDay { get; set; } = 10;
    #endregion

    #region How long to run
    public AutopilotTargetMode TargetMode { get; set; } = AutopilotTargetMode.AddHours;

    public double TargetHours { get; set; } = 10;

    /// <summary>Stop the game once every allowed achievement is unlocked, even if the time target isn't reached.</summary>
    public bool StopWhenAllUnlocked { get; set; } = true;
    #endregion

    public AutopilotProfile Clone(string? name = null)
    {
        var copy = (AutopilotProfile)this.MemberwiseClone();
        copy.Name = name ?? this.Name;
        copy.IsBuiltIn = false;
        copy.Curve = this.Curve.Select(p => new AutopilotCurvePoint { Percent = p.Percent, Hours = p.Hours }).ToList();
        return copy;
    }
}

/// <summary>Per-game settings that override the active profile.</summary>
public sealed class AutopilotGameOverride
{
    public uint AppId { get; set; }

    /// <summary>Use this profile for the game instead of the active one (null = active profile).</summary>
    public string? ProfileName { get; set; }

    /// <summary>Never unlock these achievement IDs.</summary>
    public HashSet<string> ExcludedAchievements { get; set; } = [];

    /// <summary>Only these achievement IDs may be unlocked when <see cref="OnlyMode"/> is on (or, for older files, when non-empty).</summary>
    public HashSet<string> OnlyAchievements { get; set; } = [];

    /// <summary>
    /// "Only these" mode: nothing outside <see cref="OnlyAchievements"/> may unlock, so an empty
    /// list means nothing at all. Older files without it are read as on when the list is non-empty.
    /// </summary>
    public bool OnlyMode { get; set; }

    /// <summary>Overrides the profile's TargetMode/TargetHours when set.</summary>
    public AutopilotTargetMode? TargetMode { get; set; }

    public double? TargetHours { get; set; }

    /// <summary>Overrides the profile's IncludeRare when set.</summary>
    public bool? IncludeRare { get; set; }
}

public enum AutopilotNotifyMode
{
    None,
    EachUnlock,
    DailySummary,
}

/// <summary>Everything the user configures on the Autopilot tab. Stored per Steam account.</summary>
public sealed class AutopilotConfig
{
    #region Games
    public AutopilotGameSource Source { get; set; } = AutopilotGameSource.Manual;

    public List<uint> ManualGames { get; set; } = [];

    public string? CollectionKey { get; set; }

    public string? SmartList { get; set; }

    /// <summary>Never run these games, whatever the source.</summary>
    public List<uint> ExcludedGames { get; set; } = [];

    public Dictionary<uint, AutopilotGameOverride> Overrides { get; set; } = [];

    /// <summary>Games running at the same time (1–32; Steam's limit is about 32).</summary>
    public int MaxConcurrentGames { get; set; } = 4;
    #endregion

    #region Profiles
    public string ActiveProfile { get; set; } = "Natural";

    /// <summary>User-created profiles. Built-in ones come from AutopilotStore.BuiltInProfiles and aren't stored.</summary>
    public List<AutopilotProfile> CustomProfiles { get; set; } = [];
    #endregion

    #region Schedule
    /// <summary>Daily window in which games may run (local time). Null From/To = all day. From &gt; To spans midnight.</summary>
    public TimeSpan? ActiveFrom { get; set; }

    public TimeSpan? ActiveTo { get; set; }

    /// <summary>Days on which games may run. Empty = every day.</summary>
    public List<DayOfWeek> ActiveDays { get; set; } = [];

    /// <summary>Games keep running but nothing is unlocked in this window. Null = no quiet hours.</summary>
    public TimeSpan? QuietFrom { get; set; }

    public TimeSpan? QuietTo { get; set; }

    /// <summary>Per-game running-time cap per day in hours (0 = unlimited).</summary>
    public double MaxHoursPerGamePerDay { get; set; }

    /// <summary>Pause all Autopilot games while the user plays a real game.</summary>
    public bool PauseWhileGaming { get; set; } = true;
    #endregion

    #region Behaviour
    public AutopilotNotifyMode NotifyMode { get; set; } = AutopilotNotifyMode.EachUnlock;

    /// <summary>Back up a game's achievements before each Autopilot unlock.</summary>
    public bool BackupBeforeUnlock { get; set; } = true;

    /// <summary>Start Autopilot (in the tray) when Windows starts.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>When SAM starts, resume Autopilot if it was running when SAM closed.</summary>
    public bool ResumeOnStartup { get; set; } = true;

    /// <summary>Set by the engine: was Autopilot running when SAM last closed.</summary>
    public bool WasRunning { get; set; }
    #endregion
}
#endregion

#region Runtime state (persisted so Autopilot can resume)
/// <summary>Per-game progress, kept across restarts.</summary>
public sealed class AutopilotGameState
{
    public uint AppId { get; set; }

    /// <summary>Steam playtime (hours) when Autopilot first ran this game.</summary>
    public double BaselineSteamHours { get; set; }

    /// <summary>Total time this game has run under Autopilot, in hours.</summary>
    public double AutopilotHours { get; set; }

    /// <summary>Autopilot hours on <see cref="Day"/> (for MaxHoursPerGamePerDay).</summary>
    public double HoursToday { get; set; }

    /// <summary>Local date the per-day counters refer to.</summary>
    public DateOnly Day { get; set; }

    public int UnlocksToday { get; set; }

    /// <summary>Unlock times in the last hour or so (for MaxUnlocksPerHour); trimmed by the engine.</summary>
    public List<DateTimeOffset> RecentUnlocks { get; set; } = [];

    public DateTimeOffset? LastUnlockAt { get; set; }

    /// <summary>Autopilot hours at which the next Interval-mode unlock is due (randomized once per step).</summary>
    public double? NextIntervalAtHours { get; set; }

    /// <summary>Per-achievement random factor (0.0–1.0) for curve jitter, fixed once chosen so plans stay stable.</summary>
    public Dictionary<string, double> JitterSeeds { get; set; } = [];

    /// <summary>Achievement IDs unlocked by Autopilot.</summary>
    public List<string> UnlockedByAutopilot { get; set; } = [];

    /// <summary>
    /// Requested IDs whose outcome isn't known yet (the request is in flight, timed out, or SAM
    /// closed meanwhile). Already counted against the limits; the engine checks them on the next
    /// state read and records the ones that turned out unlocked.
    /// </summary>
    public List<string> PendingUnlocks { get; set; } = [];

    /// <summary>
    /// Locked achievements on a stat Autopilot raised, not requested themselves. Any that turn out
    /// unlocked on the next state read are recorded and counted against the limits.
    /// </summary>
    public List<string> WatchedUnlocks { get; set; } = [];

    public bool Completed { get; set; }

    /// <summary>User paused this game.</summary>
    public bool Paused { get; set; }

    public string? LastError { get; set; }
}

public sealed class AutopilotState
{
    public Dictionary<uint, AutopilotGameState> Games { get; set; } = [];
}
#endregion

#region Planning
/// <summary>An achievement as the planner sees it.</summary>
public sealed record AutopilotAchievement(
    string Id,
    string Name,
    double? GlobalPercent,
    bool IsHidden,
    bool IsProtected,
    bool IsUnlocked,
    string? ProgressStatId,
    double? ProgressMax,
    int SchemaIndex);

/// <summary>Why an achievement won't be unlocked by Autopilot (shown in the preview).</summary>
public enum AutopilotSkipReason
{
    None,
    AlreadyUnlocked,
    Protected,
    Hidden,
    BelowMinimumPercent,
    Rare,
    UnknownRarity,
    Excluded,
    NotInOnlyList,
}

/// <summary>One planned unlock: when (in playtime and, if estimable, wall-clock time) it will happen.</summary>
public sealed record AutopilotPlannedUnlock(
    uint AppId,
    string AchievementId,
    string Name,
    double? GlobalPercent,
    double AtPlaytimeHours,
    DateTimeOffset? EstimatedAt,
    AutopilotSkipReason Skip);

/// <summary>Live status of one game, for the UI.</summary>
public enum AutopilotGameStatusKind
{
    Queued,
    Starting,
    Running,
    WaitingForSchedule,
    PausedWhileGaming,
    PausedByUser,
    DailyLimitReached,
    Completed,
    Error,
}

public sealed record AutopilotGameStatus(
    uint AppId,
    string Name,
    AutopilotGameStatusKind Kind,
    double PlaytimeHours,
    double AutopilotHours,
    double? TargetHours,
    int UnlockedByAutopilot,
    int RemainingAllowed,
    string? NextUnlockName,
    DateTimeOffset? NextUnlockEstimate,
    string? Message,
    bool UnlockingNow = false);
#endregion
