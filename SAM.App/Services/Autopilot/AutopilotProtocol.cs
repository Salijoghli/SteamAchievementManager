using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// The wire format between the Autopilot engine (library process) and its workers
/// (<c>SAM.exe &lt;appid&gt; --autopilot-worker &lt;parent pid&gt;</c>): newline-delimited JSON over a
/// named pipe that only the current user can open. Each request line gets exactly one response
/// line. Pure and side-effect free, so it can be unit tested.
/// </summary>
/// <remarks>
/// Requests: <c>{"type":"ping"}</c>, <c>{"type":"state"}</c>,
/// <c>{"type":"unlock","ids":["A","B"],"stats":{"STAT":10},"backup":true}</c> and
/// <c>{"type":"stop"}</c>. Responses always carry <c>ok</c> and, on failure, a user-facing
/// <c>message</c>.
/// </remarks>
public static class AutopilotProtocol
{
    public const string Ping = "ping";
    public const string State = "state";
    public const string Unlock = "unlock";
    public const string Stop = "stop";

    /// <summary>Worker exit code: stopped normally (asked to, or its parent exited).</summary>
    public const int ExitSuccess = 0;

    /// <summary>Worker exit code: couldn't connect to Steam or load the game's stats.</summary>
    public const int ExitSteamFailed = 1;

    /// <summary>Worker exit code: the game's own SAM window is open.</summary>
    public const int ExitWindowOpen = 3;

    /// <summary>Worker exit code: another worker already runs this game.</summary>
    public const int ExitAlreadyRunning = 4;

    /// <summary>Worker exit code: the game has no achievement schema on this PC.</summary>
    public const int ExitNoSchema = 5;

    /// <summary>Environment variable that makes workers log unlocks and stat changes instead of making them.</summary>
    public const string DryRunVariable = "SAM_AUTOPILOT_DRYRUN";

    /// <summary>Longest request or response line accepted (a game with thousands of achievements stays far below).</summary>
    public const int MaxLineLength = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>True when <see cref="DryRunVariable"/> is "1" or "true" in this process's environment.</summary>
    public static bool IsDryRun => IsDryRunValue(Environment.GetEnvironmentVariable(DryRunVariable));

    internal static bool IsDryRunValue(string? value) =>
        value != null && (value.Trim() == "1" || string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Named mutex a worker holds while it runs <paramref name="appId"/> (session-local).</summary>
    public static string MutexName(uint appId) =>
        "SteamAchievementManager.Autopilot." + appId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The worker's pipe name; includes the Steam account so a switched account never talks to a stale worker.</summary>
    public static string PipeName(ulong steamId, uint appId) =>
        "SteamAchievementManager.Autopilot." + steamId.ToString(CultureInfo.InvariantCulture) + "." + appId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The single-instance event that exists while the game's own SAM window is open (see SingleInstance).</summary>
    public static string WindowEventName(uint appId) =>
        "SteamAchievementManager." + appId.ToString(CultureInfo.InvariantCulture) + ".Activate";

    /// <summary>One line of JSON (no line breaks inside; the caller appends the newline).</summary>
    public static string Serialize(AutopilotRequest request) => JsonSerializer.Serialize(request, Options);

    public static string Serialize(AutopilotResponse response) => JsonSerializer.Serialize(response, Options);

    /// <summary>Parses a request line. Null if it's empty, too long, not JSON or has no type.</summary>
    public static AutopilotRequest? ParseRequest(string? line)
    {
        var request = Parse<AutopilotRequest>(line);
        return string.IsNullOrWhiteSpace(request?.Type) ? null : request;
    }

    /// <summary>Parses a response line. Null if it's empty, too long or not JSON.</summary>
    public static AutopilotResponse? ParseResponse(string? line) => Parse<AutopilotResponse>(line);

    private static T? Parse<T>(string? line)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A request from the engine to a worker.</summary>
public sealed class AutopilotRequest
{
    /// <summary>One of <see cref="AutopilotProtocol.Ping"/>, State, Unlock or Stop.</summary>
    public string Type { get; set; } = "";

    /// <summary>Unlock: achievement IDs to unlock.</summary>
    public List<string>? Ids { get; set; }

    /// <summary>Unlock: stats to raise first (stat ID → target value). Only ever raised, never lowered.</summary>
    public Dictionary<string, double>? Stats { get; set; }

    /// <summary>Unlock: save a backup of the game first.</summary>
    public bool Backup { get; set; }
}

/// <summary>A worker's answer to one request.</summary>
public sealed class AutopilotResponse
{
    public bool Ok { get; set; }

    /// <summary>User-facing result or error text.</summary>
    public string? Message { get; set; }

    /// <summary>The worker's App ID (every response).</summary>
    public uint AppId { get; set; }

    /// <summary>Whether the worker is in dry-run mode (every response).</summary>
    public bool DryRun { get; set; }

    /// <summary>Unlock: the achievements that were actually unlocked (already unlocked ones are left out).</summary>
    public List<string>? Unlocked { get; set; }

    /// <summary>Unlock: stat values that were changed.</summary>
    public List<AutopilotStatChange>? StatChanges { get; set; }

    /// <summary>State: every schema achievement.</summary>
    public List<AutopilotAchievementState>? Achievements { get; set; }

    /// <summary>State: every readable stat's current value.</summary>
    public Dictionary<string, double>? Stats { get; set; }
}

public sealed record AutopilotAchievementState(string Id, bool Unlocked, DateTimeOffset? UnlockTime);

public sealed record AutopilotStatChange(string Id, double From, double To);
