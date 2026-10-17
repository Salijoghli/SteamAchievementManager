using SAM.App.Models;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// Loads and saves Autopilot configuration and runtime state per Steam account:
/// data\users\&lt;steamid64&gt;\autopilot.json and autopilot-state.json (user data, never cleared
/// with the cache). Thread-safe. Missing or unreadable files give defaults.
/// </summary>
public sealed class AutopilotStore(AppCache cache)
{
    /// <summary>"Natural" (slow, playtime-based, no rares) and "Fast" (shorter curve, higher limits). Fresh copies each call.</summary>
    public static IReadOnlyList<AutopilotProfile> BuiltInProfiles() => throw new NotImplementedException("TODO(autopilot-core)");

    public AutopilotConfig LoadConfig() => throw new NotImplementedException("TODO(autopilot-core)");

    public void SaveConfig(AutopilotConfig config) => throw new NotImplementedException("TODO(autopilot-core)");

    public AutopilotState LoadState() => throw new NotImplementedException("TODO(autopilot-core)");

    public void SaveState(AutopilotState state) => throw new NotImplementedException("TODO(autopilot-core)");

    /// <summary>Built-in profiles followed by the config's custom ones.</summary>
    public static IReadOnlyList<AutopilotProfile> AllProfiles(AutopilotConfig config) => throw new NotImplementedException("TODO(autopilot-core)");
}
