using SAM.App.Models;
using SAM.App.Services.Autopilot;

namespace SAM.App.Tests;

public sealed class AutopilotStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    private AutopilotStore Store() => new(this._dir.Path);

    private string ConfigPath => Path.Combine(this._dir.Path, "autopilot.json");

    private string StatePath => Path.Combine(this._dir.Path, "autopilot-state.json");

    [Fact]
    public void MissingFilesGiveDefaults()
    {
        var config = this.Store().LoadConfig();
        var state = this.Store().LoadState();

        Assert.Equal("Natural", config.ActiveProfile);
        Assert.Equal(4, config.MaxConcurrentGames);
        Assert.Empty(config.CustomProfiles);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void CorruptFilesGiveDefaults()
    {
        this._dir.Write("autopilot.json", "{ not json");
        this._dir.Write("autopilot-state.json", "null");

        Assert.Equal("Natural", this.Store().LoadConfig().ActiveProfile);
        Assert.Empty(this.Store().LoadState().Games);

        this._dir.Write("autopilot.json", """{ "Source": "NoSuchSource" }""");
        Assert.Equal(AutopilotGameSource.Manual, this.Store().LoadConfig().Source);
    }

    [Fact]
    public void ConfigRoundTripsEveryKindOfValue()
    {
        var custom = new AutopilotProfile
        {
            Name = "Mine",
            Pacing = AutopilotPacingMode.Interval,
            Order = AutopilotUnlockOrder.Random,
            TargetMode = AutopilotTargetMode.Unlimited,
            IntervalMinutes = 45,
            Curve = [new() { Percent = 80, Hours = 2 }, new() { Percent = 5, Hours = 30 }],
        };
        var config = new AutopilotConfig
        {
            Source = AutopilotGameSource.Collection,
            CollectionKey = "sam:abc",
            ManualGames = [10, 20],
            ExcludedGames = [30],
            MaxConcurrentGames = 8,
            ActiveProfile = "Mine",
            CustomProfiles = [custom],
            ActiveFrom = new TimeSpan(22, 30, 0),
            ActiveTo = TimeSpan.FromHours(6),
            ActiveDays = [DayOfWeek.Saturday, DayOfWeek.Sunday],
            QuietFrom = TimeSpan.FromHours(1),
            MaxHoursPerGamePerDay = 5,
            NotifyMode = AutopilotNotifyMode.DailySummary,
            WasRunning = true,
        };
        config.Overrides[440] = new AutopilotGameOverride
        {
            AppId = 440,
            ProfileName = "Fast",
            ExcludedAchievements = ["A", "B"],
            OnlyAchievements = ["C"],
            TargetMode = AutopilotTargetMode.UntilTotalHours,
            TargetHours = 100,
            IncludeRare = false,
        };

        this.Store().SaveConfig(config);
        var json = File.ReadAllText(this.ConfigPath);
        var loaded = this.Store().LoadConfig();

        Assert.Contains("\"Collection\"", json);
        Assert.Contains("\"Interval\"", json);
        Assert.Contains("\"Saturday\"", json);
        Assert.Contains("\"UntilTotalHours\"", json);
        Assert.False(File.Exists(this.ConfigPath + ".tmp"));

        Assert.Equal(AutopilotGameSource.Collection, loaded.Source);
        Assert.Equal("sam:abc", loaded.CollectionKey);
        Assert.Equal([10u, 20u], loaded.ManualGames);
        Assert.Equal([30u], loaded.ExcludedGames);
        Assert.Equal(8, loaded.MaxConcurrentGames);
        Assert.Equal("Mine", loaded.ActiveProfile);
        Assert.Equal(new TimeSpan(22, 30, 0), loaded.ActiveFrom);
        Assert.Equal(TimeSpan.FromHours(6), loaded.ActiveTo);
        Assert.Equal(TimeSpan.FromHours(1), loaded.QuietFrom);
        Assert.Null(loaded.QuietTo);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], loaded.ActiveDays);
        Assert.Equal(5, loaded.MaxHoursPerGamePerDay);
        Assert.Equal(AutopilotNotifyMode.DailySummary, loaded.NotifyMode);
        Assert.True(loaded.WasRunning);

        var profile = Assert.Single(loaded.CustomProfiles);
        Assert.Equal("Mine", profile.Name);
        Assert.Equal(AutopilotPacingMode.Interval, profile.Pacing);
        Assert.Equal(AutopilotUnlockOrder.Random, profile.Order);
        Assert.Equal(AutopilotTargetMode.Unlimited, profile.TargetMode);
        Assert.Equal(45, profile.IntervalMinutes);
        Assert.Equal([(80.0, 2.0), (5.0, 30.0)], profile.Curve.Select(p => (p.Percent, p.Hours)));

        var gameOverride = loaded.Overrides[440];
        Assert.Equal(440u, gameOverride.AppId);
        Assert.Equal("Fast", gameOverride.ProfileName);
        Assert.True(gameOverride.ExcludedAchievements.SetEquals(["A", "B"]));
        Assert.True(gameOverride.OnlyAchievements.SetEquals(["C"]));
        Assert.Equal(AutopilotTargetMode.UntilTotalHours, gameOverride.TargetMode);
        Assert.Equal(100, gameOverride.TargetHours);
        Assert.False(gameOverride.IncludeRare);
    }

    [Fact]
    public void StateRoundTrips()
    {
        var unlockedAt = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(4));
        var state = new AutopilotState();
        state.Games[730] = new AutopilotGameState
        {
            AppId = 730,
            BaselineSteamHours = 12.5,
            AutopilotHours = 3.25,
            HoursToday = 1.5,
            Day = new DateOnly(2026, 10, 14),
            UnlocksToday = 2,
            RecentUnlocks = [unlockedAt],
            LastUnlockAt = unlockedAt,
            NextIntervalAtHours = 4,
            JitterSeeds = { ["A"] = 0.25 },
            UnlockedByAutopilot = ["A", "B"],
            Completed = true,
            Paused = true,
            LastError = "oops",
        };

        this.Store().SaveState(state);
        var game = this.Store().LoadState().Games[730];

        Assert.Equal(730u, game.AppId);
        Assert.Equal(12.5, game.BaselineSteamHours);
        Assert.Equal(3.25, game.AutopilotHours);
        Assert.Equal(1.5, game.HoursToday);
        Assert.Equal(new DateOnly(2026, 10, 14), game.Day);
        Assert.Equal(2, game.UnlocksToday);
        Assert.Equal([unlockedAt], game.RecentUnlocks);
        Assert.Equal(TimeSpan.FromHours(4), game.LastUnlockAt!.Value.Offset);
        Assert.Equal(unlockedAt, game.LastUnlockAt);
        Assert.Equal(4, game.NextIntervalAtHours);
        Assert.Equal(0.25, game.JitterSeeds["A"]);
        Assert.Equal(["A", "B"], game.UnlockedByAutopilot);
        Assert.True(game.Completed);
        Assert.True(game.Paused);
        Assert.Equal("oops", game.LastError);
    }

    [Fact]
    public void ClampsConfigOnLoad()
    {
        this._dir.Write("autopilot.json", """
            {
              "MaxConcurrentGames": 99,
              "ActiveProfile": "Missing",
              "ActiveFrom": "1.02:00:00",
              "ActiveDays": [ "Monday", "Monday", 1 ],
              "MaxHoursPerGamePerDay": -3,
              "ManualGames": [ 5, 5, 6 ],
              "Overrides": { "12": { "AppId": 99, "TargetHours": -1, "ExcludedAchievements": null } },
              "CustomProfiles": [
                {
                  "Name": "Odd",
                  "MinGlobalPercent": 150,
                  "RareThresholdPercent": -5,
                  "UnknownPercentAs": 200,
                  "JitterPercent": 300,
                  "IntervalMinutes": 0,
                  "MinMinutesBetweenUnlocks": -1,
                  "MaxUnlocksPerHour": -2,
                  "MaxUnlocksPerDay": -3,
                  "TargetHours": -4,
                  "Curve": [
                    { "Percent": 10, "Hours": 20 },
                    { "Percent": 120, "Hours": -1 },
                    { "Percent": 10, "Hours": 99 },
                    { "Percent": 50, "Hours": 5 }
                  ]
                },
                { "Name": "Empty", "Curve": [] }
              ]
            }
            """);

        var config = this.Store().LoadConfig();

        Assert.Equal(32, config.MaxConcurrentGames);
        Assert.Equal("Natural", config.ActiveProfile);
        Assert.Null(config.ActiveFrom);
        Assert.Equal([DayOfWeek.Monday], config.ActiveDays);
        Assert.Equal(0, config.MaxHoursPerGamePerDay);
        Assert.Equal([5u, 6u], config.ManualGames);

        var gameOverride = config.Overrides[12];
        Assert.Equal(12u, gameOverride.AppId);
        Assert.Equal(0, gameOverride.TargetHours);
        Assert.Empty(gameOverride.ExcludedAchievements);

        var odd = config.CustomProfiles[0];
        Assert.Equal(100, odd.MinGlobalPercent);
        Assert.Equal(0, odd.RareThresholdPercent);
        Assert.Equal(100, odd.UnknownPercentAs);
        Assert.Equal(100, odd.JitterPercent);
        Assert.Equal(1, odd.IntervalMinutes);
        Assert.Equal(0, odd.MinMinutesBetweenUnlocks);
        Assert.Equal(0, odd.MaxUnlocksPerHour);
        Assert.Equal(0, odd.MaxUnlocksPerDay);
        Assert.Equal(0, odd.TargetHours);
        Assert.Equal([(100.0, 0.0), (50.0, 5.0), (10.0, 20.0)], odd.Curve.Select(p => (p.Percent, p.Hours)));

        // An empty curve gets the default one.
        Assert.Equal(new AutopilotProfile().Curve.Select(p => p.Percent), config.CustomProfiles[1].Curve.Select(p => p.Percent));

        this._dir.Write("autopilot.json", """{ "MaxConcurrentGames": 0 }""");
        Assert.Equal(1, this.Store().LoadConfig().MaxConcurrentGames);
    }

    [Fact]
    public void ClampsStateOnLoad()
    {
        this._dir.Write("autopilot-state.json", """
            {
              "Games": {
                "5": { "AppId": 1, "AutopilotHours": -2, "UnlocksToday": -1, "NextIntervalAtHours": -3,
                       "JitterSeeds": { "A": 1.5, "B": -0.5 }, "RecentUnlocks": null, "UnlockedByAutopilot": [ "A", "A" ] },
                "6": null
              }
            }
            """);

        var state = this.Store().LoadState();

        var game = Assert.Single(state.Games).Value;
        Assert.Equal(5u, game.AppId);
        Assert.Equal(0, game.AutopilotHours);
        Assert.Equal(0, game.UnlocksToday);
        Assert.Equal(0, game.NextIntervalAtHours);
        Assert.Equal(1, game.JitterSeeds["A"]);
        Assert.Equal(0, game.JitterSeeds["B"]);
        Assert.Empty(game.RecentUnlocks);
        Assert.Equal(["A"], game.UnlockedByAutopilot);
    }

    [Fact]
    public void BuiltInProfilesAreNeverPersisted()
    {
        var config = new AutopilotConfig { ActiveProfile = "fast" };
        config.CustomProfiles.Add(new AutopilotProfile { Name = "Natural", JitterPercent = 99 });
        config.CustomProfiles.Add(new AutopilotProfile { Name = "Mine" });
        config.CustomProfiles.Add(new AutopilotProfile { Name = "mine", JitterPercent = 1 });

        this.Store().SaveConfig(config);
        var json = File.ReadAllText(this.ConfigPath);
        var loaded = this.Store().LoadConfig();

        Assert.DoesNotContain("\"Natural\"", json);
        Assert.Equal("Fast", loaded.ActiveProfile);
        var mine = Assert.Single(loaded.CustomProfiles);
        Assert.Equal("Mine", mine.Name);
        Assert.False(mine.IsBuiltIn);

        // Saving doesn't change the caller's object.
        Assert.Equal(3, config.CustomProfiles.Count);
        Assert.Equal("fast", config.ActiveProfile);
    }

    [Fact]
    public void BuiltInProfilesMatchTheirPresets()
    {
        var profiles = AutopilotStore.BuiltInProfiles();
        Assert.Equal(["Natural", "Fast"], profiles.Select(p => p.Name));
        Assert.All(profiles, p => Assert.True(p.IsBuiltIn));

        var natural = profiles[0];
        Assert.Equal([(98.0, 1.0), (50.0, 5.0), (10.0, 20.0), (1.0, 60.0)], natural.Curve.Select(p => (p.Percent, p.Hours)));
        Assert.Equal(20, natural.JitterPercent);
        Assert.Equal(AutopilotUnlockOrder.MostCommonFirst, natural.Order);
        Assert.Equal(2, natural.MaxUnlocksPerHour);
        Assert.Equal(10, natural.MaxUnlocksPerDay);
        Assert.Equal(10, natural.MinMinutesBetweenUnlocks);
        Assert.False(natural.IncludeRare);
        Assert.Equal(AutopilotTargetMode.AddHours, natural.TargetMode);
        Assert.Equal(10, natural.TargetHours);

        var fast = profiles[1];
        Assert.Equal([(98.0, 0.25), (50.0, 1.0), (10.0, 3.0), (1.0, 8.0)], fast.Curve.Select(p => (p.Percent, p.Hours)));
        Assert.Equal(15, fast.JitterPercent);
        Assert.Equal(6, fast.MaxUnlocksPerHour);
        Assert.Equal(40, fast.MaxUnlocksPerDay);
        Assert.Equal(2, fast.MinMinutesBetweenUnlocks);
        Assert.True(fast.IncludeRare);
        Assert.Equal(AutopilotTargetMode.AddHours, fast.TargetMode);
        Assert.Equal(4, fast.TargetHours);

        // Fresh copies: editing one doesn't affect the next call.
        natural.Curve.Clear();
        Assert.Equal(4, AutopilotStore.BuiltInProfiles()[0].Curve.Count);
        Assert.NotSame(natural, AutopilotStore.BuiltInProfiles()[0]);
    }

    [Fact]
    public void AllProfilesListsBuiltInsThenCustom()
    {
        var mine = new AutopilotProfile { Name = "Mine" };
        var config = new AutopilotConfig { CustomProfiles = [mine, new AutopilotProfile { Name = "FAST" }] };

        var all = AutopilotStore.AllProfiles(config);

        Assert.Equal(["Natural", "Fast", "Mine"], all.Select(p => p.Name));
        Assert.Same(mine, all[2]);
    }
}
