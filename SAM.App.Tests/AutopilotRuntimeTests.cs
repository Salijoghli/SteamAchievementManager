using SAM.App.Infrastructure;
using SAM.App.Models;
using SAM.App.Services.Autopilot;

namespace SAM.App.Tests;

public sealed class AutopilotProtocolTests
{
    [Fact]
    public void UnlockRequestRoundTripsAsOneLine()
    {
        var request = new AutopilotRequest
        {
            Type = AutopilotProtocol.Unlock,
            Ids = ["ACH_WIN", "ACH_ÜBER"],
            Stats = new Dictionary<string, double> { ["STAT_KILLS"] = 100, ["STAT_DIST"] = 2.5 },
            Backup = true,
        };

        var line = AutopilotProtocol.Serialize(request);
        var parsed = AutopilotProtocol.ParseRequest(line);

        Assert.DoesNotContain('\n', line);
        Assert.Contains("\"type\":\"unlock\"", line);
        Assert.NotNull(parsed);
        Assert.Equal(AutopilotProtocol.Unlock, parsed!.Type);
        Assert.Equal(["ACH_WIN", "ACH_ÜBER"], parsed.Ids);
        Assert.Equal(100, parsed.Stats!["STAT_KILLS"]);
        Assert.Equal(2.5, parsed.Stats["STAT_DIST"]);
        Assert.True(parsed.Backup);
    }

    [Fact]
    public void SimpleRequestsOmitEmptyFields()
    {
        var line = AutopilotProtocol.Serialize(new AutopilotRequest { Type = AutopilotProtocol.Ping });

        Assert.Equal("{\"type\":\"ping\",\"backup\":false}", line);
        Assert.Equal(AutopilotProtocol.Ping, AutopilotProtocol.ParseRequest("{\"type\":\"ping\"}")!.Type);
    }

    [Fact]
    public void StateResponseRoundTrips()
    {
        var time = new DateTimeOffset(2026, 10, 17, 12, 30, 0, TimeSpan.Zero);
        var response = new AutopilotResponse
        {
            Ok = true,
            AppId = 21690,
            DryRun = true,
            Achievements = [new("A", true, time), new("B", false, null)],
            Stats = new Dictionary<string, double> { ["S"] = 3 },
        };

        var parsed = AutopilotProtocol.ParseResponse(AutopilotProtocol.Serialize(response));

        Assert.NotNull(parsed);
        Assert.True(parsed!.Ok);
        Assert.True(parsed.DryRun);
        Assert.Equal(21690u, parsed.AppId);
        Assert.Equal([new AutopilotAchievementState("A", true, time), new AutopilotAchievementState("B", false, null)], parsed.Achievements);
        Assert.Equal(3, parsed.Stats!["S"]);
        Assert.Null(parsed.Unlocked);
    }

    [Fact]
    public void UnlockResponseRoundTrips()
    {
        var response = new AutopilotResponse
        {
            Ok = false,
            Message = "Steam refused \"x\".",
            Unlocked = ["A"],
            StatChanges = [new("S", 1, 5)],
        };

        var parsed = AutopilotProtocol.ParseResponse(AutopilotProtocol.Serialize(response))!;

        Assert.False(parsed.Ok);
        Assert.Equal("Steam refused \"x\".", parsed.Message);
        Assert.Equal(["A"], parsed.Unlocked);
        Assert.Equal([new AutopilotStatChange("S", 1, 5)], parsed.StatChanges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"ids\":[\"A\"]}")]
    [InlineData("{\"type\":\"\"}")]
    [InlineData("[1,2]")]
    public void InvalidRequestsParseToNull(string? line) => Assert.Null(AutopilotProtocol.ParseRequest(line));

    [Fact]
    public void OverlongLinesAreRejected()
    {
        var line = "{\"type\":\"ping\",\"message\":\"" + new string('x', AutopilotProtocol.MaxLineLength) + "\"}";

        Assert.Null(AutopilotProtocol.ParseRequest(line));
        Assert.Null(AutopilotProtocol.ParseResponse(line));
    }

    [Fact]
    public void NamesIncludeTheAppIdAndAccount()
    {
        Assert.Equal("SteamAchievementManager.Autopilot.21690", AutopilotProtocol.MutexName(21690));
        Assert.Equal("SteamAchievementManager.Autopilot.76561198000000000.21690", AutopilotProtocol.PipeName(76561198000000000, 21690));
        Assert.Equal("SteamAchievementManager.21690.Activate", AutopilotProtocol.WindowEventName(21690));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("yes", false)]
    public void DryRunValues(string? value, bool expected) => Assert.Equal(expected, AutopilotProtocol.IsDryRunValue(value));
}

public sealed class AutopilotCommandLineTests
{
    [Fact]
    public void ParsesWorker()
    {
        var command = CommandLine.Parse(["21690", "--autopilot-worker", "1234"]);

        Assert.Equal(LaunchMode.AutopilotWorker, command.Mode);
        Assert.Equal(21690u, command.AppId);
        Assert.Equal(1234, command.ParentProcessId);
    }

    [Fact]
    public void ParsesWorkerWithEquals()
    {
        var command = CommandLine.Parse(["21690", "--AUTOPILOT-WORKER=99"]);

        Assert.Equal(LaunchMode.AutopilotWorker, command.Mode);
        Assert.Equal(99, command.ParentProcessId);
    }

    [Theory]
    [InlineData("21690", "--autopilot-worker")]
    [InlineData("21690", "--autopilot-worker", "abc")]
    [InlineData("21690", "--autopilot-worker", "0")]
    [InlineData("21690", "--autopilot-worker", "-5")]
    [InlineData("21690", "--autopilot-worker", "5", "--commit")]
    [InlineData("21690", "--unlock-all", "--autopilot-worker", "5")]
    [InlineData("--autopilot-worker", "5")]
    public void RejectsBadWorkerArguments(params string[] args) =>
        Assert.Equal(LaunchMode.Invalid, CommandLine.Parse(args).Mode);

    [Fact]
    public void ParsesAutopilotLibraryLaunch()
    {
        var command = CommandLine.Parse(["--autopilot"]);

        Assert.Equal(LaunchMode.Library, command.Mode);
        Assert.True(command.StartAutopilot);
        Assert.Equal(0u, command.AppId);
    }

    [Fact]
    public void AutopilotSurvivesARestartAndNotificationArguments()
    {
        var command = CommandLine.Parse(["--restart-after=42", "--Autopilot", "----AppNotificationActivated:"]);

        Assert.Equal(LaunchMode.Library, command.Mode);
        Assert.True(command.StartAutopilot);
        Assert.Equal(42, command.RestartAfterProcessId);
    }

    [Fact]
    public void PlainLaunchesDontStartAutopilot()
    {
        Assert.False(CommandLine.Parse([]).StartAutopilot);
        Assert.False(CommandLine.Parse(["21690"]).StartAutopilot);
        Assert.Equal(LaunchMode.GameWindow, CommandLine.Parse(["21690"]).Mode);
    }

    [Fact]
    public void AutopilotWithAGameIsInvalid() =>
        Assert.Equal(LaunchMode.Invalid, CommandLine.Parse(["21690", "--autopilot"]).Mode);

    [Fact]
    public void StartupCommandQuotesThePath() =>
        Assert.Equal("\"C:\\Program Files\\SAM\\SAM.exe\" --autopilot", StartupRegistration.CommandFor(@"C:\Program Files\SAM\SAM.exe"));
}

public sealed class AutopilotWorkerRulesTests
{
    private static StatDefinition Stat(StatKind kind = StatKind.Integer, double min = 0, double max = 0, int permission = 0, bool server = false) => new()
    {
        Id = "S",
        DisplayName = "S",
        Kind = kind,
        MinValue = min,
        MaxValue = max,
        Permission = permission,
        SetByTrustedGameServer = server,
    };

    [Fact]
    public void RaisesToTheTarget() => Assert.Equal(10, AutopilotWorker.ValidateStatTarget(Stat(), 3, 10, out _));

    [Fact]
    public void NeverLowers()
    {
        Assert.Null(AutopilotWorker.ValidateStatTarget(Stat(), 12, 10, out var reason));
        Assert.Null(reason);
        Assert.Null(AutopilotWorker.ValidateStatTarget(Stat(), 10, 10, out _));
    }

    [Fact]
    public void ClampsToTheSchemaMaximum() => Assert.Equal(50, AutopilotWorker.ValidateStatTarget(Stat(max: 50), 3, 100, out _));

    [Fact]
    public void IgnoresMaximumWhenUnset() => Assert.Equal(100, AutopilotWorker.ValidateStatTarget(Stat(), 3, 100, out _));

    [Fact]
    public void IntegerStatsNeedWholeTargets()
    {
        Assert.Null(AutopilotWorker.ValidateStatTarget(Stat(), 0, 2.5, out var reason));
        Assert.NotNull(reason);
        Assert.Equal(2.5, AutopilotWorker.ValidateStatTarget(Stat(StatKind.Float), 0, 2.5, out _));
    }

    [Fact]
    public void ProtectedServerAndAverageRateStatsAreNeverSet()
    {
        Assert.Null(AutopilotWorker.ValidateStatTarget(Stat(permission: 2), 0, 5, out var a));
        Assert.Null(AutopilotWorker.ValidateStatTarget(Stat(server: true), 0, 5, out var b));
        Assert.Null(AutopilotWorker.ValidateStatTarget(Stat(StatKind.AverageRate), 0, 5, out var c));
        Assert.Null(AutopilotWorker.ValidateStatTarget(Stat(), 0, double.NaN, out var d));
        Assert.All(new[] { a, b, c, d }, Assert.NotNull);
    }

    [Fact]
    public void BuildsPlannerAchievementsInSchemaOrder()
    {
        var schema = new GameSchema
        {
            AppId = 1,
            Achievements =
            [
                new AchievementDefinition { Id = "A", Name = "Alpha", IsHidden = true, ProgressStatId = "S", ProgressMax = 10 },
                new AchievementDefinition { Id = "B", Name = "Beta", Permission = 1 },
            ],
            Stats = [],
        };

        var list = AutopilotEngine.BuildAchievements(schema, a => a.Id == "B", new Dictionary<string, double> { ["A"] = 42.5 });

        Assert.Equal(new AutopilotAchievement("A", "Alpha", 42.5, true, false, false, "S", 10, 0), list[0]);
        Assert.Equal(new AutopilotAchievement("B", "Beta", null, false, true, true, null, null, 1), list[1]);
    }
}

public sealed class GameProcessMonitorTests
{
    [Fact]
    public void MatchesExecutablesUnderAnInstallFolder()
    {
        var roots = new List<(uint, string)>
        {
            (10, GameProcessMonitor.NormalizeDirectory(@"D:\steam\steamapps\common\Game")!),
            (20, GameProcessMonitor.NormalizeDirectory(@"D:\steam\steamapps\common\Game 2\")!),
        };

        Assert.Equal(10u, GameProcessMonitor.MatchInstallDirectory(@"D:\Steam\SteamApps\common\Game\bin\game.exe", roots));
        Assert.Equal(20u, GameProcessMonitor.MatchInstallDirectory(@"D:\steam\steamapps\common\Game 2\x.exe", roots));
        Assert.Null(GameProcessMonitor.MatchInstallDirectory(@"D:\steam\steamapps\common\Gamer\x.exe", roots));
        Assert.Null(GameProcessMonitor.MatchInstallDirectory(@"D:\steam\steam.exe", roots));
    }

    [Fact]
    public void NormalizesTrailingSeparators()
    {
        Assert.Equal(@"C:\Games\X\", GameProcessMonitor.NormalizeDirectory(@"C:\Games\X"));
        Assert.Equal(@"C:\Games\X\", GameProcessMonitor.NormalizeDirectory(@"C:\Games\X\\"));
        Assert.Null(GameProcessMonitor.NormalizeDirectory(" "));
    }
}
