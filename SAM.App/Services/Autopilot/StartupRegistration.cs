using Microsoft.Win32;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// "Start with Windows" for Autopilot: the HKCU Run value "SteamAchievementManager" =
/// <c>"&lt;exe&gt;" --autopilot</c>, which starts the library hidden in the tray with Autopilot running.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SteamAchievementManager";

    /// <summary>The command line stored in the Run key for <paramref name="executablePath"/>.</summary>
    internal static string CommandFor(string executablePath) =>
        "\"" + executablePath + "\" " + Infrastructure.CommandLine.AutopilotOption;

    /// <summary>Adds, updates or removes the Run value. Failures are logged, not thrown.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                if (Environment.ProcessPath is not { } path)
                {
                    return;
                }

                var command = CommandFor(path);
                if (key.GetValue(ValueName) as string != command)
                {
                    key.SetValue(ValueName, command);
                }
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Autopilot: couldn't {(enabled ? "add" : "remove")} the Start with Windows entry", e);
        }
    }
}
