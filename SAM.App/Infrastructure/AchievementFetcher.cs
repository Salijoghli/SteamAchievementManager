using System.Globalization;
using SAM.App.Services;
using SAM.App.Services.Autopilot;

namespace SAM.App.Infrastructure;

/// <summary>
/// Runs <c>SAM.exe &lt;appid&gt; --fetch-achievements</c> without a window: connects to Steam as the
/// game, requests the user's stats (which makes Steam download the achievement schema into
/// appcache\stats) and waits for the schema file. Read-only: nothing is set or stored. The game's
/// window isn't claimed, so a window launch is never blocked or forwarded to this process.
/// </summary>
public static class AchievementFetcher
{
    /// <summary>How long to wait for Steam to write the schema file.</summary>
    private static readonly TimeSpan SchemaTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Fetches the achievement data for <paramref name="appId"/>. Must be called on the UI thread
    /// (Steam callbacks are pumped there). Returns <see cref="CommandLine.ExitSuccess"/> when the
    /// schema file exists afterwards (or the game's window or Autopilot worker is loading it
    /// anyway), else <see cref="CommandLine.ExitFailed"/>.
    /// </summary>
    public static async Task<int> RunAsync(uint appId)
    {
        var name = appId.ToString(CultureInfo.InvariantCulture);

        // The game's window (or a command-line commit) or an Autopilot worker already holds a
        // Steam connection for the game and has requested its stats.
        if (AutopilotWorker.IsWindowOpen(appId) || IsAutopilotWorkerRunning(appId))
        {
            Report($"App {name}: SAM already has the game open; its achievement data is loading there.");
            return CommandLine.ExitSuccess;
        }

        // One fetch per game at a time; a second one has nothing to add.
        // A named event rather than a mutex: it has no thread affinity across the awaits below.
        using var marker = new EventWaitHandle(false, EventResetMode.ManualReset, "SteamAchievementManager.Fetch." + name, out var createdNew);
        if (createdNew == false)
        {
            Report($"App {name}: achievement data is already being loaded.");
            return CommandLine.ExitSuccess;
        }

        SteamSession steam;
        try
        {
            steam = SteamSession.Initialize(appId);
        }
        catch (SteamUnavailableException e)
        {
            Report($"App {name}: can't connect to Steam: {e.Message}", isError: true);
            return CommandLine.ExitFailed;
        }

        try
        {
            var schemas = new SchemaService(steam);
            try
            {
                await steam.RequestUserStatsAsync();
            }
            catch (SteamUnavailableException e)
            {
                // Steam may still have written the schema (e.g. the stats answer timed out).
                Diagnostics.Log($"Fetching achievements for {appId}: {e.Message}");
            }

            var deadline = DateTime.UtcNow + SchemaTimeout;
            while (schemas.SchemaFileExists(appId) == false && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            if (schemas.SchemaFileExists(appId))
            {
                Report($"App {name}: achievement data loaded.");
                return CommandLine.ExitSuccess;
            }

            Report($"App {name}: Steam didn't download the achievement data.", isError: true);
            return CommandLine.ExitFailed;
        }
        finally
        {
            steam.Dispose();
        }
    }

    private static bool IsAutopilotWorkerRunning(uint appId)
    {
        try
        {
            if (Mutex.TryOpenExisting(AutopilotProtocol.MutexName(appId), out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void Report(string text, bool isError = false)
    {
        if (ConsoleOutput.IsAvailable == false)
        {
            return;
        }

        if (isError)
        {
            ConsoleOutput.WriteError(text);
        }
        else
        {
            ConsoleOutput.WriteLine(text);
        }
    }
}
