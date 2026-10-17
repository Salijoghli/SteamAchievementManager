using System.Diagnostics;
using System.Globalization;
using SAM.App.Services;

namespace SAM.App.ViewModels.Pages;

/// <summary>Actions shared by the Stats, Activity and History pages.</summary>
public static class PageActions
{
    /// <summary>Opens a game's achievement window (a new SAM process with the App ID).</summary>
    public static void OpenGame(uint appId)
    {
        try
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            startInfo.ArgumentList.Add(appId.ToString(CultureInfo.InvariantCulture));
            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't open {appId}", e);
        }
    }

    /// <summary>"Today", "Yesterday" or the long date, for day group headers.</summary>
    public static string DayTitle(DateTime localDate, bool shortDate = false)
    {
        var days = (DateTime.Now.Date - localDate.Date).Days;
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            _ => localDate.ToString(shortDate ? "d" : "D", CultureInfo.CurrentCulture),
        };
    }
}

/// <summary>
/// Global achievement percentages per game, fetched at most a few at a time and memoized for
/// the lifetime of the owner (the service itself caches them on disk for a day).
/// </summary>
public sealed class GlobalPercentLoader(GlobalPercentService service)
{
    private const int MaxConcurrent = 4;

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly Dictionary<uint, Task<IReadOnlyDictionary<string, double>?>> _tasks = [];

    /// <summary>Achievement ID → percent, or null when unavailable. Never throws.</summary>
    public Task<IReadOnlyDictionary<string, double>?> GetAsync(uint appId)
    {
        lock (this._tasks)
        {
            if (this._tasks.TryGetValue(appId, out var task) == false)
            {
                task = this.FetchAsync(appId);
                this._tasks[appId] = task;
            }

            return task;
        }
    }

    /// <summary>Forgets memoized results so the next request asks the service again.</summary>
    public void Clear()
    {
        lock (this._tasks)
        {
            this._tasks.Clear();
        }
    }

    private async Task<IReadOnlyDictionary<string, double>?> FetchAsync(uint appId)
    {
        await this._slots.WaitAsync().ConfigureAwait(false);
        try
        {
            // The service reads its disk cache synchronously; keep that off the UI thread.
            return await Task.Run(() => service.GetAsync(appId, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't get global percentages for {appId}", e);
            return null;
        }
        finally
        {
            this._slots.Release();
        }
    }
}
