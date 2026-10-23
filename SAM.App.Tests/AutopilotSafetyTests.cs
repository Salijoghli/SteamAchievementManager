using SAM.App.Infrastructure;
using SAM.App.Models;
using SAM.App.Services;
using SAM.App.Services.Autopilot;
using SAM.App.ViewModels.Pages;
using static SAM.App.Tests.AutopilotTestData;

namespace SAM.App.Tests;

public sealed class AutopilotOnlyModeTests
{
    private static readonly AutopilotProfile Profile = Plain();

    [Fact]
    public void EmptyOnlyListAllowsNothing()
    {
        var gameOverride = new AutopilotGameOverride { OnlyMode = true };

        Assert.True(AutopilotPlanner.IsOnlyMode(gameOverride));
        Assert.Equal(AutopilotSkipReason.NotInOnlyList, AutopilotPlanner.Classify(Profile, gameOverride, A("X")));
        Assert.Empty(AutopilotPlanner.OrderCandidates(Profile, gameOverride, [A("X"), A("Y", index: 1)], new Random(1)));
    }

    [Fact]
    public void OnlyModeAllowsTheListedOnes()
    {
        var gameOverride = new AutopilotGameOverride { OnlyMode = true, OnlyAchievements = ["Y"] };

        Assert.Equal(AutopilotSkipReason.NotInOnlyList, AutopilotPlanner.Classify(Profile, gameOverride, A("X")));
        Assert.Equal(AutopilotSkipReason.None, AutopilotPlanner.Classify(Profile, gameOverride, A("Y")));
    }

    [Fact]
    public void WithoutOnlyModeAnEmptyListRestrictsNothing()
    {
        var gameOverride = new AutopilotGameOverride { ExcludedAchievements = ["Z"] };

        Assert.False(AutopilotPlanner.IsOnlyMode(gameOverride));
        Assert.Equal(AutopilotSkipReason.None, AutopilotPlanner.Classify(Profile, gameOverride, A("X")));
    }

    [Fact]
    public void OlderFilesWithANonEmptyListAreOnlyMode()
    {
        // A file written before OnlyMode existed.
        var legacy = new AutopilotGameOverride { OnlyAchievements = ["Y"] };
        Assert.True(AutopilotPlanner.IsOnlyMode(legacy));
        Assert.Equal(AutopilotSkipReason.NotInOnlyList, AutopilotPlanner.Classify(Profile, legacy, A("X")));

        var config = new AutopilotConfig { Overrides = { [7] = new AutopilotGameOverride { OnlyAchievements = ["Y"] } } };
        AutopilotStore.Normalize(config);
        Assert.True(config.Overrides[7].OnlyMode);
    }

    [Fact]
    public void EmptyOnlyModeSurvivesSaveAndLoad()
    {
        using var dir = new TempDir();
        var store = new AutopilotStore(dir.Path);
        var config = new AutopilotConfig { Overrides = { [7] = new AutopilotGameOverride { OnlyMode = true } } };

        store.SaveConfig(config);
        var loaded = store.LoadConfig().Overrides[7];

        Assert.True(loaded.OnlyMode);
        Assert.Empty(loaded.OnlyAchievements);
    }

    [Fact]
    public void SwitchingModesNeverMovesOneListIntoTheOther()
    {
        var value = new AutopilotGameOverride { AppId = 7, OnlyMode = true, OnlyAchievements = ["A", "B"] };
        var viewModel = new AutopilotOverridesViewModel(null!, 7, "Game", value, new AutopilotConfig());

        Assert.Equal(1, viewModel.ModeIndex);
        viewModel.ModeIndex = 2;
        viewModel.Apply();

        Assert.False(value.OnlyMode);
        Assert.Empty(value.OnlyAchievements);
        Assert.Empty(value.ExcludedAchievements);
    }

    [Fact]
    public void SwitchingBackRestoresTheModesOwnList()
    {
        var value = new AutopilotGameOverride { AppId = 7, ExcludedAchievements = ["E"] };
        var viewModel = new AutopilotOverridesViewModel(null!, 7, "Game", value, new AutopilotConfig());

        Assert.Equal(2, viewModel.ModeIndex);
        viewModel.ModeIndex = 1;
        Assert.True(viewModel.IsOnlyListEmpty);
        viewModel.ModeIndex = 2;
        viewModel.Apply();

        Assert.False(value.OnlyMode);
        Assert.Equal(["E"], value.ExcludedAchievements);
    }

    [Fact]
    public void OnlyModeWithNothingCheckedIsKept()
    {
        var value = new AutopilotGameOverride { AppId = 7 };
        var viewModel = new AutopilotOverridesViewModel(null!, 7, "Game", value, new AutopilotConfig()) { ModeIndex = 1 };

        Assert.True(viewModel.IsOnlyListEmpty);
        viewModel.Apply();

        Assert.True(value.OnlyMode);
        Assert.Empty(value.OnlyAchievements);
    }
}

public sealed class AutopilotLinkedStatTests
{
    private static readonly AutopilotProfile Profile = Plain();

    [Fact]
    public void RaisesTheStatWhenNothingElseWouldUnlock()
    {
        var due = A("K10", stat: "KILLS", max: 10);
        var achievements = new[] { due, A("K50", stat: "KILLS", max: 50, index: 1) };

        var stats = AutopilotPlanner.LinkedStatTargets(Profile, achievements, [due]);

        Assert.Equal(10, stats["KILLS"]);
    }

    [Fact]
    public void DoesntRaiseWhenALowerLockedAchievementIsNotDue()
    {
        // K5 is excluded (or rare, hidden, not due yet...): raising KILLS to 10 would unlock it too.
        var due = A("K10", stat: "KILLS", max: 10);
        var achievements = new[] { A("K5", stat: "KILLS", max: 5), due };

        Assert.Empty(AutopilotPlanner.LinkedStatTargets(Profile, achievements, [due]));
    }

    [Fact]
    public void ProtectedAndEqualThresholdsBlockToo()
    {
        var due = A("K10", stat: "KILLS", max: 10);

        Assert.Empty(AutopilotPlanner.LinkedStatTargets(Profile, [due, A("P10", isProtected: true, stat: "KILLS", max: 10)], [due]));
    }

    [Fact]
    public void UnlockedOnesDontBlock()
    {
        var due = A("K10", stat: "KILLS", max: 10);

        Assert.Equal(10, AutopilotPlanner.LinkedStatTargets(Profile, [A("K5", unlocked: true, stat: "KILLS", max: 5), due], [due])["KILLS"]);
    }

    [Fact]
    public void FallsBackToTheHighestSafeTarget()
    {
        var low = A("K5", stat: "KILLS", max: 5);
        var high = A("K20", stat: "KILLS", max: 20);
        var achievements = new[] { low, A("K10", stat: "KILLS", max: 10), high };

        // K10 isn't due: 20 would unlock it, 5 is safe.
        Assert.Equal(5, AutopilotPlanner.LinkedStatTargets(Profile, achievements, [low, high])["KILLS"]);
    }

    [Fact]
    public void NothingWhenTheProfileDoesntUpdateStats()
    {
        var due = A("K10", stat: "KILLS", max: 10);
        var profile = Plain();
        profile.UpdateLinkedStats = false;

        Assert.Empty(AutopilotPlanner.LinkedStatTargets(profile, [due], [due]));
    }
}

public sealed class AutopilotAccountingTests
{
    private static AutopilotGameState NewState() => new() { AppId = 1, Day = DateOnly.FromDateTime(Noon.DateTime) };

    [Fact]
    public void ARequestCountsBeforeItsAnswer()
    {
        var state = NewState();

        AutopilotAccounting.Begin(state, ["A", "B"], Noon);

        Assert.Equal(2, state.UnlocksToday);
        Assert.Equal(2, state.RecentUnlocks.Count);
        Assert.Equal(Noon, state.LastUnlockAt);
        Assert.Equal(["A", "B"], state.PendingUnlocks);
    }

    [Fact]
    public void SuccessKeepsWhatWasUnlockedAndGivesBackTheRest()
    {
        var state = NewState();
        var ticket = AutopilotAccounting.Begin(state, ["A", "B"], Noon);

        AutopilotAccounting.Complete(state, ticket, ["A"]);

        Assert.Equal(1, state.UnlocksToday);
        Assert.Single(state.RecentUnlocks);
        Assert.Equal(["A"], state.UnlockedByAutopilot);
        Assert.Empty(state.PendingUnlocks);
        Assert.Equal(Noon, state.LastUnlockAt);
    }

    [Fact]
    public void AFailureGivesEverythingBack()
    {
        var earlier = Noon.AddHours(-3);
        var state = NewState();
        state.LastUnlockAt = earlier;
        var ticket = AutopilotAccounting.Begin(state, ["A"], Noon);

        AutopilotAccounting.Fail(state, ticket);

        Assert.Equal(0, state.UnlocksToday);
        Assert.Empty(state.RecentUnlocks);
        Assert.Empty(state.PendingUnlocks);
        Assert.Equal(earlier, state.LastUnlockAt);
    }

    [Fact]
    public void AnUncertainOutcomeStaysCountedUntilTheNextStateRead()
    {
        var state = NewState();
        var ticket = AutopilotAccounting.Begin(state, ["A", "B"], Noon);
        AutopilotAccounting.Uncertain(state, ticket);

        Assert.Equal(2, state.UnlocksToday);
        Assert.Equal(["A", "B"], state.PendingUnlocks);

        Assert.True(AutopilotAccounting.Reconcile(state, new HashSet<string> { "A" }, Noon.AddMinutes(1)));

        Assert.Equal(["A"], state.UnlockedByAutopilot);
        Assert.Empty(state.PendingUnlocks);
        Assert.Equal(2, state.UnlocksToday);
    }

    [Fact]
    public void WatchedSideEffectsAreRecordedAndCounted()
    {
        var state = NewState();
        AutopilotAccounting.Watch(state, ["K50", "K100"]);
        var later = Noon.AddMinutes(1);

        AutopilotAccounting.Reconcile(state, new HashSet<string> { "K50" }, later);

        Assert.Equal(["K50"], state.UnlockedByAutopilot);
        Assert.Equal(1, state.UnlocksToday);
        Assert.Equal([later], state.RecentUnlocks);
        Assert.Equal(later, state.LastUnlockAt);
        Assert.Empty(state.WatchedUnlocks);
    }

    [Fact]
    public void NothingToReconcile() => Assert.False(AutopilotAccounting.Reconcile(NewState(), new HashSet<string>(), Noon));

    [Fact]
    public void AnswersAfterMidnightDontTouchTheNewDay()
    {
        var state = NewState();
        var ticket = AutopilotAccounting.Begin(state, ["A"], Noon);
        state.Day = state.Day.AddDays(1);
        state.UnlocksToday = 0;

        AutopilotAccounting.Fail(state, ticket);

        Assert.Equal(0, state.UnlocksToday);
    }

    [Fact]
    public void PendingUnlocksSurviveSaveAndLoad()
    {
        using var dir = new TempDir();
        var store = new AutopilotStore(dir.Path);
        var state = new AutopilotState();
        state.Games[5] = new AutopilotGameState { PendingUnlocks = ["A", "A", "B"], WatchedUnlocks = ["C"] };

        store.SaveState(state);
        var loaded = store.LoadState().Games[5];

        Assert.Equal(["A", "B"], loaded.PendingUnlocks);
        Assert.Equal(["C"], loaded.WatchedUnlocks);
    }
}

public sealed class AutopilotCreditTests
{
    private static readonly AwakeClock.Mark Start = new(Noon, TimeSpan.FromHours(5));

    [Fact]
    public void ASlowTickIsCreditedInFull()
    {
        var end = new AwakeClock.Mark(Noon.AddMinutes(7), TimeSpan.FromHours(5) + TimeSpan.FromMinutes(7));

        Assert.Equal(TimeSpan.FromMinutes(7), AwakeClock.Elapsed(Start, end));
    }

    [Fact]
    public void SleepIsNotCredited()
    {
        // Eight hours on the wall clock, of which the PC was awake for 40 seconds.
        var end = new AwakeClock.Mark(Noon.AddHours(8), TimeSpan.FromHours(5) + TimeSpan.FromSeconds(40));

        Assert.Equal(TimeSpan.FromSeconds(40), AwakeClock.Elapsed(Start, end));
    }

    [Fact]
    public void AClockSetBackCreditsNothing() =>
        Assert.Equal(TimeSpan.Zero, AwakeClock.Elapsed(Start, new AwakeClock.Mark(Noon.AddHours(-1), TimeSpan.FromHours(6))));

    [Fact]
    public void WithoutAnAwakeClockTheOldCapApplies() =>
        Assert.Equal(TimeSpan.FromMinutes(2), AwakeClock.Elapsed(new(Noon, TimeSpan.MinValue), new(Noon.AddHours(1), TimeSpan.MinValue)));

    [Fact]
    public void TheAwakeClockRuns() => Assert.NotNull(AwakeClock.Awake);
}

public sealed class AutopilotDryRunFlagTests
{
    [Fact]
    public void ParsesTheWorkersDryRunFlag()
    {
        var command = CommandLine.Parse(["21690", "--autopilot-worker", "1234", "--autopilot-dry-run"]);

        Assert.Equal(LaunchMode.AutopilotWorker, command.Mode);
        Assert.Equal(1234, command.ParentProcessId);
        Assert.True(command.AutopilotDryRun);
    }

    [Fact]
    public void WorkersAreRealByDefault() =>
        Assert.False(CommandLine.Parse(["21690", "--autopilot-worker", "1234"]).AutopilotDryRun);

    [Theory]
    [InlineData("21690", "--autopilot-dry-run")]
    [InlineData("21690", "--autopilot-dry-run", "--unlock", "A", "--commit")]
    public void TheFlagIsOnlyForWorkers(params string[] args) =>
        Assert.Equal(LaunchMode.Invalid, CommandLine.Parse(args).Mode);

    [Fact]
    public void UncertainRoundTrips()
    {
        var line = AutopilotProtocol.Serialize(new AutopilotResponse { Ok = false, Uncertain = true });

        Assert.True(AutopilotProtocol.ParseResponse(line)!.Uncertain);
    }
}

public sealed class GlobalPercentResultTests
{
    [Fact]
    public void EmptyOrMissingIsNoData()
    {
        Assert.Equal(GlobalPercentStatus.NoData, GlobalPercentResult.From(null).Status);
        Assert.Equal(GlobalPercentStatus.NoData, GlobalPercentResult.From([]).Status);
    }

    [Fact]
    public void PercentagesAreOk()
    {
        var result = GlobalPercentResult.From(new Dictionary<string, double> { ["A"] = 12.5 });

        Assert.Equal(GlobalPercentStatus.Ok, result.Status);
        Assert.Equal(12.5, result.Percents!["A"]);
    }

    [Fact]
    public void FailedHasNoPercentages() => Assert.Null(GlobalPercentResult.Failed.Percents);
}
