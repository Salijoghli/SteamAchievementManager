using SAM.App.Models;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// The Autopilot rules as pure functions (no I/O, no Steam), so they're fully unit-testable.
/// The engine calls these on every tick; the UI calls Preview for the dry-run table.
/// </summary>
/// <remarks>
/// Times of day are read from the wall-clock part of the <see cref="DateTimeOffset"/> passed in
/// (its own offset), so passing <see cref="DateTimeOffset.Now"/> means local time and tests can
/// use fixed offsets. A schedule window whose From equals its To means "no restriction" (active
/// all day / no quiet hours).
/// </remarks>
public static class AutopilotPlanner
{
    /// <summary>Lowest percent used on the log scale (log10 of 0 is undefined).</summary>
    private const double MinLogPercent = 0.001;

    /// <summary>Jitter seed used by Preview for achievements that don't have one yet (no jitter).</summary>
    private const double NeutralSeed = 0.5;

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>The profile that applies to a game: its override's profile if set, else the active one, else "Natural"; with the override's TargetMode/TargetHours/IncludeRare applied (returns a copy).</summary>
    /// <remarks>Profile names match case-insensitively. The copy has IsBuiltIn = false (see <see cref="AutopilotProfile.Clone"/>).</remarks>
    public static AutopilotProfile ResolveProfile(AutopilotConfig config, uint appId)
    {
        ArgumentNullException.ThrowIfNull(config);

        var profiles = AutopilotStore.AllProfiles(config);
        AutopilotProfile? Find(string? name) => name == null
            ? null
            : profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        var gameOverride = GetOverride(config, appId);
        var profile = (Find(gameOverride?.ProfileName) ?? Find(config.ActiveProfile) ?? Find(AutopilotStore.NaturalProfileName)!).Clone();

        if (gameOverride != null)
        {
            if (gameOverride.TargetMode is { } mode)
            {
                profile.TargetMode = mode;
            }

            if (gameOverride.TargetHours is { } hours)
            {
                profile.TargetHours = Math.Max(0, hours);
            }

            if (gameOverride.IncludeRare is { } includeRare)
            {
                profile.IncludeRare = includeRare;
            }
        }

        return profile;
    }

    /// <summary>Why <paramref name="achievement"/> would be skipped under these rules, or None if it may be unlocked.</summary>
    /// <remarks>
    /// The first matching reason wins, in this order: AlreadyUnlocked, Protected, Excluded (the
    /// override's exclusion list), NotInOnlyList (the override's "only" list, see <see cref="IsOnlyMode"/>;
    /// an empty list in only-mode allows nothing), Hidden
    /// (when SkipHidden), UnknownRarity (no percent and !IncludeUnknownRarity),
    /// BelowMinimumPercent, Rare (below RareThresholdPercent and !IncludeRare). The last two use
    /// UnknownPercentAs for achievements without a percent.
    /// </remarks>
    public static AutopilotSkipReason Classify(AutopilotProfile profile, AutopilotGameOverride? gameOverride, AutopilotAchievement achievement)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(achievement);

        if (achievement.IsUnlocked)
        {
            return AutopilotSkipReason.AlreadyUnlocked;
        }

        if (achievement.IsProtected)
        {
            return AutopilotSkipReason.Protected;
        }

        if (gameOverride?.ExcludedAchievements?.Contains(achievement.Id) == true)
        {
            return AutopilotSkipReason.Excluded;
        }

        if (IsOnlyMode(gameOverride) && gameOverride!.OnlyAchievements?.Contains(achievement.Id) != true)
        {
            return AutopilotSkipReason.NotInOnlyList;
        }

        if (profile.SkipHidden && achievement.IsHidden)
        {
            return AutopilotSkipReason.Hidden;
        }

        if (achievement.GlobalPercent == null && profile.IncludeUnknownRarity == false)
        {
            return AutopilotSkipReason.UnknownRarity;
        }

        var percent = EffectivePercent(profile, achievement.GlobalPercent);
        if (percent < profile.MinGlobalPercent)
        {
            return AutopilotSkipReason.BelowMinimumPercent;
        }

        if (percent < profile.RareThresholdPercent && profile.IncludeRare == false)
        {
            return AutopilotSkipReason.Rare;
        }

        return AutopilotSkipReason.None;
    }

    /// <summary>
    /// Playtime-mode threshold: total playtime hours after which an achievement with this global
    /// percent may unlock. Interpolates the curve on a log scale of the percent (clamped to the
    /// curve's ends), then applies the per-achievement <paramref name="jitterSeed"/> (0–1) scaled
    /// by JitterPercent (±). Unknown percent uses UnknownPercentAs.
    /// </summary>
    /// <remarks>
    /// Between neighbouring points (P1, H1) and (P2, H2) the hours are
    /// H1 + (H2 − H1) · (log P1 − log p) / (log P1 − log P2). Jitter multiplies by
    /// 1 + J · (2·seed − 1) with J = JitterPercent / 100, so seed 0.5 means no change. Never
    /// negative; an empty curve gives 0.
    /// </remarks>
    public static double RequiredHours(AutopilotProfile profile, double? globalPercent, double jitterSeed)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var hours = CurveHours(profile.Curve, EffectivePercent(profile, globalPercent));
        return Math.Max(0, hours * JitterFactor(profile, jitterSeed));
    }

    /// <summary>Allowed achievements (Classify == None) in unlock order (profile.Order; Random uses <paramref name="random"/>, stable per call).</summary>
    /// <remarks>
    /// MostCommonFirst sorts by percent descending (unknown = UnknownPercentAs); ties and
    /// SchemaOrder use SchemaIndex, then Id. Random draws one key per achievement in schema order,
    /// so the result doesn't depend on the input order.
    /// </remarks>
    public static IReadOnlyList<AutopilotAchievement> OrderCandidates(
        AutopilotProfile profile,
        AutopilotGameOverride? gameOverride,
        IEnumerable<AutopilotAchievement> achievements,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(achievements);
        ArgumentNullException.ThrowIfNull(random);

        var candidates = achievements
            .Where(a => Classify(profile, gameOverride, a) == AutopilotSkipReason.None)
            .OrderBy(a => a.SchemaIndex)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();

        switch (profile.Order)
        {
            case AutopilotUnlockOrder.MostCommonFirst:
                // OrderBy is stable, so equal percents keep schema order.
                return candidates.OrderByDescending(a => EffectivePercent(profile, a.GlobalPercent)).ToList();

            case AutopilotUnlockOrder.Random:
                var keys = candidates.Select(_ => random.NextDouble()).ToArray();
                return candidates.Select((a, i) => (Achievement: a, Key: keys[i]))
                    .OrderBy(p => p.Key)
                    .Select(p => p.Achievement)
                    .ToList();

            default:
                return candidates;
        }
    }

    /// <summary>
    /// Achievements to unlock right now for one game. Respects pacing (playtime curve or interval,
    /// using/creating seeds in <paramref name="state"/>.JitterSeeds and NextIntervalAtHours),
    /// MinMinutesBetweenUnlocks, MaxUnlocksPerHour/Day (state.RecentUnlocks/UnlocksToday),
    /// and never returns more than the limits allow. Mutates only seed/next-interval fields of
    /// <paramref name="state"/>; the engine records the actual unlocks.
    /// </summary>
    /// <remarks>
    /// <para>Playtime mode: every candidate whose <see cref="RequiredHours"/> ≤
    /// <paramref name="currentPlaytimeHours"/>, in unlock order. Missing jitter seeds are created
    /// (in schema order) with <c>random.NextDouble()</c>.</para>
    /// <para>Interval mode: on the first call NextIntervalAtHours is set to one jittered interval
    /// after AutopilotHours. When AutopilotHours reaches it and the limits allow, the next candidate
    /// is returned and the following interval is scheduled from the current AutopilotHours (so a
    /// blocked unlock doesn't cause a burst later).</para>
    /// <para>Limits: with MinMinutesBetweenUnlocks &gt; 0 at most one per call, none until that
    /// long after LastUnlockAt (a LastUnlockAt in the future, e.g. after a clock change, is
    /// ignored); MaxUnlocksPerHour counts RecentUnlocks in the last 60 minutes; MaxUnlocksPerDay
    /// counts UnlocksToday (the engine resets it each day). Quiet hours and the schedule are the
    /// engine's job and aren't checked here.</para>
    /// </remarks>
    public static IReadOnlyList<AutopilotAchievement> DueNow(
        AutopilotProfile profile,
        AutopilotGameOverride? gameOverride,
        IReadOnlyList<AutopilotAchievement> achievements,
        AutopilotGameState state,
        double currentPlaytimeHours,
        DateTimeOffset now,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(achievements);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(random);

        state.JitterSeeds ??= [];

        if (profile.Pacing == AutopilotPacingMode.Interval)
        {
            state.NextIntervalAtHours ??= state.AutopilotHours + IntervalHours(profile, random.NextDouble());
        }
        else
        {
            EnsureSeeds(profile, gameOverride, achievements, state, random);
        }

        var candidates = OrderCandidates(profile, gameOverride, achievements, random);
        var allowed = AllowedNow(profile, state, now);
        if (allowed == 0 || candidates.Count == 0)
        {
            return [];
        }

        if (profile.Pacing == AutopilotPacingMode.Interval)
        {
            if (state.AutopilotHours < state.NextIntervalAtHours)
            {
                return [];
            }

            state.NextIntervalAtHours = state.AutopilotHours + IntervalHours(profile, random.NextDouble());
            return [candidates[0]];
        }

        return candidates
            .Where(a => RequiredHours(profile, a.GlobalPercent, state.JitterSeeds[a.Id]) <= currentPlaytimeHours)
            .Take(allowed)
            .ToList();
    }

    /// <summary>
    /// Full dry-run plan for one game: every achievement with its planned playtime and, given
    /// <paramref name="hoursPerDay"/> of running time (from the schedule), an estimated date —
    /// honoring daily/hourly limits and quiet hours approximately. Skipped achievements are
    /// included with their reason and no time. Does not mutate <paramref name="state"/>.
    /// </summary>
    /// <remarks>
    /// <para>Rows: planned unlocks first, in the order they'd happen, then skipped achievements in
    /// schema order with AtPlaytimeHours 0 and EstimatedAt null.</para>
    /// <para>Running time maps to calendar time linearly at <see cref="HoursPerDay"/> from
    /// <paramref name="now"/>. Each unlock is then pushed later until it satisfies
    /// MinMinutesBetweenUnlocks, MaxUnlocksPerHour, MaxUnlocksPerDay (including the state's
    /// recent/today counts) and isn't in quiet hours; AtPlaytimeHours is the total playtime at
    /// that (possibly pushed) moment. Achievements without a jitter seed yet use 0.5 (no jitter),
    /// Interval mode uses the plain interval for future steps, and Random order uses a seed
    /// derived from the App ID, so the plan is stable but the real run will differ slightly.</para>
    /// <para>Unlocks that would happen after the time target (TargetMode/TargetHours) keep
    /// Skip = None and their AtPlaytimeHours but have EstimatedAt null, meaning "not within this
    /// run". EstimatedAt is also null for every row when the schedule allows no running time.</para>
    /// </remarks>
    public static IReadOnlyList<AutopilotPlannedUnlock> Preview(
        uint appId,
        AutopilotConfig config,
        IReadOnlyList<AutopilotAchievement> achievements,
        AutopilotGameState? state,
        double currentPlaytimeHours,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(achievements);

        var profile = ResolveProfile(config, appId);
        var gameOverride = GetOverride(config, appId);
        var hoursPerDay = HoursPerDay(config);
        var autopilotHours = Math.Max(0, state?.AutopilotHours ?? 0);
        var seeds = state?.JitterSeeds ?? [];

        // Playtime beyond which the run has ended.
        var playtimeLimit = profile.TargetMode switch
        {
            AutopilotTargetMode.UntilTotalHours => profile.TargetHours,
            AutopilotTargetMode.AddHours => currentPlaytimeHours + (profile.TargetHours - autopilotHours),
            _ => double.PositiveInfinity,
        };

        var candidates = OrderCandidates(profile, gameOverride, achievements, new Random(unchecked((int)appId)));

        // Desired playtime for each candidate before limits; Interval mode fills these as it goes.
        List<(AutopilotAchievement Achievement, double Playtime)> queue;
        if (profile.Pacing == AutopilotPacingMode.Playtime)
        {
            queue = candidates
                .Select((a, i) => (Achievement: a, Index: i, Hours: RequiredHours(profile, a.GlobalPercent, seeds.GetValueOrDefault(a.Id, NeutralSeed))))
                .OrderBy(p => p.Hours)
                .ThenBy(p => p.Index)
                .Select(p => (p.Achievement, Math.Max(p.Hours, currentPlaytimeHours)))
                .ToList();
        }
        else
        {
            queue = candidates.Select(a => (a, double.NaN)).ToList();
        }

        var limiter = new Limiter(profile, config, state, now);
        var result = new List<AutopilotPlannedUnlock>(achievements.Count);
        var nextIntervalPlaytime = currentPlaytimeHours +
            Math.Max(0, (state?.NextIntervalAtHours ?? autopilotHours + IntervalHours(profile, NeutralSeed)) - autopilotHours);

        foreach (var (achievement, desired) in queue)
        {
            var playtime = profile.Pacing == AutopilotPacingMode.Interval ? nextIntervalPlaytime : desired;
            DateTimeOffset? at = null;

            if (hoursPerDay > 0)
            {
                var time = limiter.Place(now + TimeSpan.FromDays((playtime - currentPlaytimeHours) / hoursPerDay));
                playtime = Math.Max(playtime, currentPlaytimeHours + ((time - now).TotalDays * hoursPerDay));
                at = playtime <= playtimeLimit ? time : null;
            }

            if (profile.Pacing == AutopilotPacingMode.Interval)
            {
                nextIntervalPlaytime = playtime + IntervalHours(profile, NeutralSeed);
            }

            result.Add(new AutopilotPlannedUnlock(appId, achievement.Id, achievement.Name, achievement.GlobalPercent, playtime, at, AutopilotSkipReason.None));
        }

        foreach (var achievement in achievements.OrderBy(a => a.SchemaIndex))
        {
            var reason = Classify(profile, gameOverride, achievement);
            if (reason != AutopilotSkipReason.None)
            {
                result.Add(new AutopilotPlannedUnlock(appId, achievement.Id, achievement.Name, achievement.GlobalPercent, 0, null, reason));
            }
        }

        return result;
    }

    /// <summary>Whether games may run at <paramref name="now"/> (ActiveDays + ActiveFrom/To window, which may span midnight).</summary>
    /// <remarks>
    /// No window (either end null, or From == To) means all day. A window is [From, To). For a
    /// window spanning midnight, the ActiveDays check applies to the day the window started:
    /// with Fri 22:00–02:00, Saturday 01:00 is allowed if Friday is an active day.
    /// </remarks>
    public static bool IsWithinSchedule(AutopilotConfig config, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);

        var timeOfDay = now.TimeOfDay;
        var day = now.DayOfWeek;
        if (Window(config.ActiveFrom, config.ActiveTo) is var (from, to))
        {
            if (InWindow(from, to, timeOfDay) == false)
            {
                return false;
            }

            if (from > to && timeOfDay < to)
            {
                day = now.AddDays(-1).DayOfWeek;
            }
        }

        return config.ActiveDays is not { Count: > 0 } days || days.Contains(day);
    }

    /// <summary>Whether <paramref name="now"/> is in quiet hours (no unlocks).</summary>
    /// <remarks>Quiet hours are [QuietFrom, QuietTo) every day and may span midnight; none if either is null or they're equal.</remarks>
    public static bool IsQuietTime(AutopilotConfig config, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Window(config.QuietFrom, config.QuietTo) is var (from, to) && InWindow(from, to, now.TimeOfDay);
    }

    /// <summary>Running hours per day the schedule allows (24 when unrestricted), averaged over the week, capped by MaxHoursPerGamePerDay.</summary>
    /// <remarks>The cap applies per active day (min(window length, cap)), then that is scaled by active days / 7.</remarks>
    public static double HoursPerDay(AutopilotConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var hours = Window(config.ActiveFrom, config.ActiveTo) is var (from, to)
            ? (from < to ? to - from : TimeSpan.FromDays(1) - (from - to)).TotalHours
            : 24;

        if (config.MaxHoursPerGamePerDay > 0)
        {
            hours = Math.Min(hours, config.MaxHoursPerGamePerDay);
        }

        var days = config.ActiveDays is { Count: > 0 } activeDays ? activeDays.Distinct().Count() : 7;
        return hours * Math.Min(days, 7) / 7;
    }

    /// <summary>Whether a game has reached its time target (or everything allowed is unlocked and StopWhenAllUnlocked).</summary>
    /// <remarks>UntilTotalHours compares total Steam playtime, AddHours compares state.AutopilotHours, Unlimited never finishes by time.</remarks>
    public static bool IsFinished(AutopilotProfile profile, AutopilotGameState state, double currentPlaytimeHours, int remainingAllowed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);

        if (profile.StopWhenAllUnlocked && remainingAllowed <= 0)
        {
            return true;
        }

        return profile.TargetMode switch
        {
            AutopilotTargetMode.UntilTotalHours => currentPlaytimeHours >= profile.TargetHours,
            AutopilotTargetMode.AddHours => state.AutopilotHours >= profile.TargetHours,
            _ => false,
        };
    }

    /// <summary>Whether the override restricts the game to its "only" list (OnlyMode, or a non-empty list from an older file).</summary>
    public static bool IsOnlyMode(AutopilotGameOverride? gameOverride) =>
        gameOverride != null && (gameOverride.OnlyMode || gameOverride.OnlyAchievements is { Count: > 0 });

    /// <summary>
    /// The linked stats to raise before unlocking <paramref name="due"/> (stat ID → value), such that
    /// raising them can't unlock anything that isn't in <paramref name="due"/>.
    /// </summary>
    /// <remarks>
    /// For each stat, the highest <see cref="LinkedStatTarget"/> among the due achievements on it
    /// for which every other locked achievement on that stat with ProgressMax ≤ the target is also
    /// due. A locked achievement outside the batch (excluded, not in the only-list, rare, protected,
    /// hidden, or simply not due yet under the limits) would otherwise unlock as a side effect.
    /// When no target is safe the stat is left alone and the achievement is unlocked by itself.
    /// </remarks>
    public static IReadOnlyDictionary<string, double> LinkedStatTargets(
        AutopilotProfile profile,
        IReadOnlyList<AutopilotAchievement> achievements,
        IReadOnlyList<AutopilotAchievement> due)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(achievements);
        ArgumentNullException.ThrowIfNull(due);

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (profile.UpdateLinkedStats == false)
        {
            return result;
        }

        var dueIds = due.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var byStat = due
            .Where(a => a.ProgressStatId != null && LinkedStatTarget(profile, a) != null)
            .GroupBy(a => a.ProgressStatId!, StringComparer.Ordinal);
        foreach (var group in byStat)
        {
            var others = achievements
                .Where(a => a.IsUnlocked == false && dueIds.Contains(a.Id) == false &&
                            string.Equals(a.ProgressStatId, group.Key, StringComparison.Ordinal) && a.ProgressMax != null)
                .Select(a => a.ProgressMax!.Value)
                .ToList();

            foreach (var target in group.Select(a => LinkedStatTarget(profile, a)!.Value).Distinct().OrderDescending())
            {
                if (others.All(max => max > target))
                {
                    result[group.Key] = target;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>The value a linked stat should be raised to before unlocking (ProgressMax), or null if not applicable.</summary>
    public static double? LinkedStatTarget(AutopilotProfile profile, AutopilotAchievement achievement)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(achievement);

        return profile.UpdateLinkedStats && string.IsNullOrEmpty(achievement.ProgressStatId) == false && achievement.ProgressMax is { } max
            ? max
            : null;
    }

    #region Helpers
    private static AutopilotGameOverride? GetOverride(AutopilotConfig config, uint appId) =>
        config.Overrides != null && config.Overrides.TryGetValue(appId, out var gameOverride) ? gameOverride : null;

    private static double EffectivePercent(AutopilotProfile profile, double? globalPercent) => globalPercent ?? profile.UnknownPercentAs;

    private static double JitterFactor(AutopilotProfile profile, double seed)
    {
        var jitter = Math.Clamp(profile.JitterPercent, 0, 100) / 100;
        return 1 + (jitter * ((2 * Math.Clamp(seed, 0, 1)) - 1));
    }

    /// <summary>One Interval-mode step in hours, jittered by <paramref name="seed"/> (0–1); at least a minute.</summary>
    private static double IntervalHours(AutopilotProfile profile, double seed) =>
        Math.Max(1, profile.IntervalMinutes * JitterFactor(profile, seed)) / 60;

    private static double CurveHours(IReadOnlyList<AutopilotCurvePoint>? curve, double percent)
    {
        if (curve == null || curve.Count == 0)
        {
            return 0;
        }

        // Sort locally: the profile may not have been normalized by the store.
        var points = curve.Count == 1 || IsSortedDescending(curve) ? curve : curve.OrderByDescending(p => p.Percent).ToList();
        var x = Math.Log10(Math.Max(percent, MinLogPercent));

        if (x >= LogPercent(points[0]))
        {
            return points[0].Hours;
        }

        for (var i = 1; i < points.Count; i++)
        {
            var upper = LogPercent(points[i - 1]);
            var lower = LogPercent(points[i]);
            if (x >= lower)
            {
                var t = upper > lower ? (upper - x) / (upper - lower) : 1;
                return points[i - 1].Hours + ((points[i].Hours - points[i - 1].Hours) * t);
            }
        }

        return points[^1].Hours;
    }

    private static double LogPercent(AutopilotCurvePoint point) => Math.Log10(Math.Max(point.Percent, MinLogPercent));

    private static bool IsSortedDescending(IReadOnlyList<AutopilotCurvePoint> curve)
    {
        for (var i = 1; i < curve.Count; i++)
        {
            if (curve[i].Percent > curve[i - 1].Percent)
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureSeeds(
        AutopilotProfile profile,
        AutopilotGameOverride? gameOverride,
        IReadOnlyList<AutopilotAchievement> achievements,
        AutopilotGameState state,
        Random random)
    {
        foreach (var achievement in achievements.OrderBy(a => a.SchemaIndex).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            if (state.JitterSeeds.ContainsKey(achievement.Id) == false &&
                Classify(profile, gameOverride, achievement) == AutopilotSkipReason.None)
            {
                state.JitterSeeds[achievement.Id] = random.NextDouble();
            }
        }
    }

    /// <summary>How many unlocks the limits allow at <paramref name="now"/> (int.MaxValue = unlimited).</summary>
    private static int AllowedNow(AutopilotProfile profile, AutopilotGameState state, DateTimeOffset now)
    {
        var allowed = int.MaxValue;

        if (profile.MaxUnlocksPerDay > 0)
        {
            allowed = Math.Min(allowed, Math.Max(0, profile.MaxUnlocksPerDay - state.UnlocksToday));
        }

        if (profile.MaxUnlocksPerHour > 0)
        {
            var recent = (state.RecentUnlocks ?? []).Count(t => t > now - OneHour && t <= now);
            allowed = Math.Min(allowed, Math.Max(0, profile.MaxUnlocksPerHour - recent));
        }

        if (profile.MinMinutesBetweenUnlocks > 0)
        {
            allowed = Math.Min(allowed, 1);
            if (state.LastUnlockAt is { } last && last <= now && now - last < TimeSpan.FromMinutes(profile.MinMinutesBetweenUnlocks))
            {
                allowed = 0;
            }
        }

        return allowed;
    }

    /// <summary>A schedule window, or null for "no restriction" (an end missing, or both equal).</summary>
    private static (TimeSpan From, TimeSpan To)? Window(TimeSpan? from, TimeSpan? to) =>
        from is { } f && to is { } t && f != t ? (f, t) : null;

    private static bool InWindow(TimeSpan from, TimeSpan to, TimeSpan timeOfDay) =>
        from < to ? timeOfDay >= from && timeOfDay < to : timeOfDay >= from || timeOfDay < to;

    /// <summary>Preview's model of the unlock limits: pushes proposed unlock times later until they're allowed.</summary>
    private sealed class Limiter
    {
        private readonly AutopilotProfile _profile;
        private readonly AutopilotConfig _config;
        private readonly List<DateTimeOffset> _times = [];
        private readonly Dictionary<DateOnly, int> _perDay = [];
        private DateTimeOffset? _last;

        public Limiter(AutopilotProfile profile, AutopilotConfig config, AutopilotGameState? state, DateTimeOffset now)
        {
            this._profile = profile;
            this._config = config;
            if (state == null)
            {
                return;
            }

            this._times.AddRange((state.RecentUnlocks ?? []).Where(t => t <= now).Order());
            if (state.LastUnlockAt is { } last && last <= now)
            {
                this._last = last;
            }

            if (this._times.Count > 0 && (this._last == null || this._times[^1] > this._last))
            {
                this._last = this._times[^1];
            }

            var today = DateOnly.FromDateTime(now.DateTime);
            if (state.UnlocksToday > 0 && (state.Day == today || state.Day == default))
            {
                this._perDay[today] = state.UnlocksToday;
            }
        }

        /// <summary>The earliest time at or after <paramref name="time"/> that satisfies every limit; records it as an unlock.</summary>
        public DateTimeOffset Place(DateTimeOffset time)
        {
            if (this._last is { } last && time < last)
            {
                time = last;
            }

            // Each rule only moves the time forward; repeat until none applies (bounded for safety).
            for (var pass = 0; pass < 64; pass++)
            {
                var moved = time;

                if (this._last is { } previous && this._profile.MinMinutesBetweenUnlocks > 0)
                {
                    var earliest = previous + TimeSpan.FromMinutes(this._profile.MinMinutesBetweenUnlocks);
                    if (moved < earliest)
                    {
                        moved = earliest;
                    }
                }

                var cap = this._profile.MaxUnlocksPerHour;
                if (cap > 0 && this._times.Count >= cap)
                {
                    // The cap-th most recent unlock must have left the 60-minute window.
                    var earliest = this._times[^cap] + OneHour;
                    if (moved < earliest)
                    {
                        moved = earliest;
                    }
                }

                if (this._profile.MaxUnlocksPerDay > 0 &&
                    this._perDay.GetValueOrDefault(DateOnly.FromDateTime(moved.DateTime)) >= this._profile.MaxUnlocksPerDay)
                {
                    moved = new DateTimeOffset(moved.Date.AddDays(1), moved.Offset);
                }

                if (IsQuietTime(this._config, moved))
                {
                    moved = QuietEnd(this._config, moved);
                }

                if (moved == time)
                {
                    break;
                }

                time = moved;
            }

            this._times.Add(time);
            var day = DateOnly.FromDateTime(time.DateTime);
            this._perDay[day] = this._perDay.GetValueOrDefault(day) + 1;
            this._last = time;
            return time;
        }

        /// <summary>When the quiet window containing <paramref name="time"/> ends.</summary>
        private static DateTimeOffset QuietEnd(AutopilotConfig config, DateTimeOffset time)
        {
            var from = config.QuietFrom!.Value;
            var to = config.QuietTo!.Value;
            var date = time.Date;
            if (from > to && time.TimeOfDay >= from)
            {
                date = date.AddDays(1);
            }

            return new DateTimeOffset(date + to, time.Offset);
        }
    }
    #endregion
}
