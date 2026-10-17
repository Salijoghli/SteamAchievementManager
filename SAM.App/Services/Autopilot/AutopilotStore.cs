using System.Text.Json;
using System.Text.Json.Serialization;
using SAM.App.Models;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// Loads and saves Autopilot configuration and runtime state per Steam account:
/// data\users\&lt;steamid64&gt;\autopilot.json and autopilot-state.json (user data, never cleared
/// with the cache). Thread-safe. Missing or unreadable files give defaults.
/// </summary>
/// <remarks>
/// Enums are stored as strings (numbers are accepted on read). Everything is validated on load
/// and before saving (see <see cref="Normalize(AutopilotConfig)"/>), so callers always get a usable
/// configuration. Built-in profiles are never written; a custom profile that uses a built-in name
/// is dropped. Each Load returns a fresh object; Save never modifies the object passed in.
/// </remarks>
public sealed class AutopilotStore
{
    /// <summary>Name of the default built-in profile, used when the active profile doesn't exist.</summary>
    internal const string NaturalProfileName = "Natural";

    /// <summary>Name of the faster built-in profile.</summary>
    internal const string FastProfileName = "Fast";

    /// <summary>Steam's practical limit on games running at once.</summary>
    internal const int MaxConcurrentGamesLimit = 32;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _configPath;
    private readonly string _statePath;
    private readonly object _lock = new();

    /// <summary>Creates the store for the account that <paramref name="cache"/> belongs to.</summary>
    public AutopilotStore(AppCache cache)
        : this(cache.UserDataDirectory)
    {
    }

    /// <summary>Creates the store keeping its files in <paramref name="directory"/> (for tests).</summary>
    internal AutopilotStore(string directory)
    {
        this._configPath = Path.Combine(directory, "autopilot.json");
        this._statePath = Path.Combine(directory, "autopilot-state.json");
    }

    /// <summary>"Natural" (slow, playtime-based, no rares) and "Fast" (shorter curve, higher limits). Fresh copies each call.</summary>
    public static IReadOnlyList<AutopilotProfile> BuiltInProfiles() =>
    [
        // Natural is exactly the AutopilotProfile defaults.
        new AutopilotProfile { Name = NaturalProfileName, IsBuiltIn = true },
        new AutopilotProfile
        {
            Name = FastProfileName,
            IsBuiltIn = true,
            IncludeRare = true,
            Curve =
            [
                new() { Percent = 98, Hours = 0.25 },
                new() { Percent = 50, Hours = 1 },
                new() { Percent = 10, Hours = 3 },
                new() { Percent = 1, Hours = 8 },
            ],
            JitterPercent = 15,
            MaxUnlocksPerHour = 6,
            MaxUnlocksPerDay = 40,
            MinMinutesBetweenUnlocks = 2,
            TargetMode = AutopilotTargetMode.AddHours,
            TargetHours = 4,
        },
    ];

    public AutopilotConfig LoadConfig()
    {
        lock (this._lock)
        {
            var config = Read<AutopilotConfig>(this._configPath) ?? new AutopilotConfig();
            Normalize(config);
            return config;
        }
    }

    public void SaveConfig(AutopilotConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (this._lock)
        {
            var copy = Copy(config) ?? new AutopilotConfig();
            Normalize(copy);
            Write(this._configPath, copy);
        }
    }

    public AutopilotState LoadState()
    {
        lock (this._lock)
        {
            var state = Read<AutopilotState>(this._statePath) ?? new AutopilotState();
            Normalize(state);
            return state;
        }
    }

    public void SaveState(AutopilotState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (this._lock)
        {
            var copy = Copy(state) ?? new AutopilotState();
            Normalize(copy);
            Write(this._statePath, copy);
        }
    }

    /// <summary>Built-in profiles followed by the config's custom ones.</summary>
    /// <remarks>Built-ins are fresh copies; custom profiles are the config's own objects. Custom profiles named like a built-in are left out.</remarks>
    public static IReadOnlyList<AutopilotProfile> AllProfiles(AutopilotConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var all = new List<AutopilotProfile>(BuiltInProfiles());
        foreach (var profile in config.CustomProfiles ?? [])
        {
            if (profile != null && IsBuiltInName(profile.Name) == false)
            {
                all.Add(profile);
            }
        }

        return all;
    }

    /// <summary>Whether <paramref name="name"/> is a built-in profile's name (case-insensitive).</summary>
    internal static bool IsBuiltInName(string? name) =>
        string.Equals(name, NaturalProfileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, FastProfileName, StringComparison.OrdinalIgnoreCase);

    #region Validation
    /// <summary>
    /// Repairs a configuration in place: null collections become empty, numbers are clamped to
    /// their valid ranges, custom profiles are validated and deduplicated by name (built-in names
    /// dropped), and an unknown <see cref="AutopilotConfig.ActiveProfile"/> falls back to "Natural".
    /// </summary>
    internal static void Normalize(AutopilotConfig config)
    {
        config.Source = Defined(config.Source, AutopilotGameSource.Manual);
        config.ManualGames = (config.ManualGames ?? []).Distinct().ToList();
        config.ExcludedGames = (config.ExcludedGames ?? []).Distinct().ToList();
        config.MaxConcurrentGames = Math.Clamp(config.MaxConcurrentGames, 1, MaxConcurrentGamesLimit);

        var overrides = new Dictionary<uint, AutopilotGameOverride>();
        foreach (var (appId, gameOverride) in config.Overrides ?? [])
        {
            if (gameOverride == null)
            {
                continue;
            }

            gameOverride.AppId = appId;
            gameOverride.ProfileName = string.IsNullOrWhiteSpace(gameOverride.ProfileName) ? null : gameOverride.ProfileName;
            gameOverride.ExcludedAchievements = CleanSet(gameOverride.ExcludedAchievements);
            gameOverride.OnlyAchievements = CleanSet(gameOverride.OnlyAchievements);
            if (gameOverride.TargetMode is { } mode && Enum.IsDefined(mode) == false)
            {
                gameOverride.TargetMode = null;
            }

            if (gameOverride.TargetHours is { } hours)
            {
                gameOverride.TargetHours = NonNegative(hours, 0);
            }

            overrides[appId] = gameOverride;
        }

        config.Overrides = overrides;

        var profiles = new List<AutopilotProfile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in config.CustomProfiles ?? [])
        {
            if (profile == null)
            {
                continue;
            }

            Normalize(profile);
            if (IsBuiltInName(profile.Name) || names.Add(profile.Name) == false)
            {
                continue;
            }

            profiles.Add(profile);
        }

        config.CustomProfiles = profiles;
        var active = AllProfiles(config).FirstOrDefault(p => string.Equals(p.Name, config.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        config.ActiveProfile = active?.Name ?? NaturalProfileName;

        config.ActiveFrom = TimeOfDay(config.ActiveFrom);
        config.ActiveTo = TimeOfDay(config.ActiveTo);
        config.QuietFrom = TimeOfDay(config.QuietFrom);
        config.QuietTo = TimeOfDay(config.QuietTo);
        config.ActiveDays = (config.ActiveDays ?? []).Where(d => Enum.IsDefined(d)).Distinct().Order().ToList();
        config.MaxHoursPerGamePerDay = Math.Clamp(NonNegative(config.MaxHoursPerGamePerDay, 0), 0, 24);
        config.NotifyMode = Defined(config.NotifyMode, AutopilotNotifyMode.EachUnlock);
    }

    /// <summary>Repairs a profile in place: percents 0–100, non-negative hours/minutes/limits, curve sorted by percent descending with duplicate percents removed (first kept); an empty curve gets the default one.</summary>
    internal static void Normalize(AutopilotProfile profile)
    {
        var defaults = new AutopilotProfile();

        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? defaults.Name : profile.Name.Trim();
        profile.IsBuiltIn = false;
        profile.MinGlobalPercent = Percent(profile.MinGlobalPercent, defaults.MinGlobalPercent);
        profile.RareThresholdPercent = Percent(profile.RareThresholdPercent, defaults.RareThresholdPercent);
        profile.UnknownPercentAs = Percent(profile.UnknownPercentAs, defaults.UnknownPercentAs);
        profile.Pacing = Defined(profile.Pacing, defaults.Pacing);
        profile.Order = Defined(profile.Order, defaults.Order);
        profile.TargetMode = Defined(profile.TargetMode, defaults.TargetMode);

        var curve = (profile.Curve ?? [])
            .Where(p => p != null && double.IsFinite(p.Percent) && double.IsFinite(p.Hours))
            .Select(p => new AutopilotCurvePoint { Percent = Math.Clamp(p.Percent, 0, 100), Hours = Math.Max(0, p.Hours) })
            .DistinctBy(p => p.Percent)
            .OrderByDescending(p => p.Percent)
            .ToList();
        profile.Curve = curve.Count > 0 ? curve : defaults.Curve;

        // At least a minute between interval unlocks; at most a week.
        profile.IntervalMinutes = Math.Clamp(NonNegative(profile.IntervalMinutes, defaults.IntervalMinutes), 1, 7 * 24 * 60);
        profile.JitterPercent = Percent(profile.JitterPercent, defaults.JitterPercent);
        profile.MinMinutesBetweenUnlocks = NonNegative(profile.MinMinutesBetweenUnlocks, defaults.MinMinutesBetweenUnlocks);
        profile.MaxUnlocksPerHour = Math.Max(0, profile.MaxUnlocksPerHour);
        profile.MaxUnlocksPerDay = Math.Max(0, profile.MaxUnlocksPerDay);
        profile.TargetHours = NonNegative(profile.TargetHours, defaults.TargetHours);
    }

    /// <summary>Repairs runtime state in place: null collections become empty, hours and counters non-negative, seeds 0–1.</summary>
    internal static void Normalize(AutopilotState state)
    {
        var games = new Dictionary<uint, AutopilotGameState>();
        foreach (var (appId, game) in state.Games ?? [])
        {
            if (game == null)
            {
                continue;
            }

            game.AppId = appId;
            game.BaselineSteamHours = NonNegative(game.BaselineSteamHours, 0);
            game.AutopilotHours = NonNegative(game.AutopilotHours, 0);
            game.HoursToday = NonNegative(game.HoursToday, 0);
            game.UnlocksToday = Math.Max(0, game.UnlocksToday);
            game.RecentUnlocks = (game.RecentUnlocks ?? []).Order().ToList();
            if (game.NextIntervalAtHours is { } next)
            {
                game.NextIntervalAtHours = double.IsFinite(next) ? Math.Max(0, next) : null;
            }

            game.JitterSeeds = (game.JitterSeeds ?? [])
                .Where(p => p.Key != null && double.IsFinite(p.Value))
                .ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 0, 1));
            game.UnlockedByAutopilot = (game.UnlockedByAutopilot ?? []).Where(id => id != null).Distinct().ToList();
            games[appId] = game;
        }

        state.Games = games;
    }

    private static HashSet<string> CleanSet(HashSet<string>? set) =>
        set == null ? [] : new HashSet<string>(set.Where(id => string.IsNullOrEmpty(id) == false));

    private static T Defined<T>(T value, T fallback)
        where T : struct, Enum => Enum.IsDefined(value) ? value : fallback;

    private static double NonNegative(double value, double fallback) => double.IsFinite(value) ? Math.Max(0, value) : fallback;

    private static double Percent(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : fallback;

    /// <summary>A time of day (0 ≤ t &lt; 24 h), or null if out of range.</summary>
    private static TimeSpan? TimeOfDay(TimeSpan? value) =>
        value is { } t && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : null;
    #endregion

    #region Files
    // JsonFile's shared options store enums as numbers; Autopilot files use strings, so these
    // mirror JsonFile.Read/Write (atomic replace, failures logged) with this store's options.
    private static T? Read<T>(string path)
    {
        try
        {
            if (File.Exists(path) == false)
            {
                return default;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Diagnostics.Log($"Couldn't read {path}", e);
            return default;
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't write {path}", e);
        }
    }

    /// <summary>A deep copy through JSON, so saving never touches the caller's object.</summary>
    private static T? Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options);
    #endregion
}
