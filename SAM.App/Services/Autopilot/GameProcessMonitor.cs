using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// Detects whether the user is playing a real game: any running process with a visible window
/// whose executable lives under an installed Steam game's folder (SAM's own processes never count). Used for
/// "Pause while gaming". Safe to call off the UI thread.
/// </summary>
internal static class GameProcessMonitor
{
    /// <summary>
    /// The App ID of a running installed game, or null if none is running. <paramref name="installDirectories"/>
    /// maps App ID → install folder (InstalledGamesService).
    /// </summary>
    public static uint? FindRunningGame(IReadOnlyDictionary<uint, string> installDirectories)
    {
        if (installDirectories.Count == 0)
        {
            return null;
        }

        var roots = installDirectories
            .Select(p => (AppId: p.Key, Root: NormalizeDirectory(p.Value)))
            .Where(p => p.Root != null)
            .Select(p => (p.AppId, Root: p.Root!))
            .ToList();
        var self = Environment.ProcessPath;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't list processes", e);
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                var path = TryGetImagePath(process.Id);
                if (path == null || (self != null && string.Equals(path, self, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var match = MatchInstallDirectory(path, roots);
                if (match != null && HasVisibleWindow(process))
                {
                    return match;
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return null;
    }

    /// <summary>The App ID whose install folder contains <paramref name="executablePath"/>, or null.</summary>
    internal static uint? MatchInstallDirectory(string executablePath, IReadOnlyList<(uint AppId, string Root)> roots)
    {
        foreach (var (appId, root) in roots)
        {
            if (executablePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return appId;
            }
        }

        return null;
    }

    /// <summary>Full path with exactly one trailing separator (so "Game" doesn't match "Game 2"), or null if invalid.</summary>
    internal static string? NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the process has a visible top-level window. Background tools installed through Steam
    /// (e.g. Wallpaper Engine, which starts with Windows) have none, so they don't count as playing.
    /// </summary>
    private static bool HasVisibleWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Works for elevated processes too (limited query rights); null for system processes.</summary>
    private static string? TryGetImagePath(int processId)
    {
        if (processId is 0 or 4)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
