using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SAM.App.Services;

/// <summary>
/// Keeps one window per App ID (0 = the library). A second launch for the same App ID passes
/// its command-line arguments to the running copy, signals it to bring its window to the front,
/// and then exits, so two windows can never edit the same game's achievements at once.
/// </summary>
/// <remarks>
/// Arguments travel as small JSON files in %LocalAppData%\SteamAchievementManager\instance,
/// one per forwarded launch (<c>&lt;appid&gt;-&lt;ticks&gt;-&lt;guid&gt;.args</c>, written to a temp
/// name and renamed so the reader never sees a partial file). The running copy reads and deletes
/// every pending file for its App ID on each wake-up, so back-to-back launches can't overwrite
/// each other, and a signal that arrives early or twice is harmless.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private static readonly string ArgsDirectory = Path.Combine(AppCache.RootDirectory, "instance");

    private readonly uint _appId;
    private readonly EventWaitHandle _activateSignal;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(uint appId, EventWaitHandle activateSignal)
    {
        this._appId = appId;
        this._activateSignal = activateSignal;
    }

    /// <summary>
    /// Claims the App ID for this process. Returns null if another process already owns it;
    /// in that case <paramref name="arguments"/> (the command line without the executable) have
    /// been handed to that process, it has been asked to show its window, and this one should exit.
    /// </summary>
    public static SingleInstance? TryClaim(uint appId, IReadOnlyList<string> arguments)
    {
        // Session-local name, so different Windows users don't collide.
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $"SteamAchievementManager.{appId}.Activate", out var createdNew);
        if (createdNew)
        {
            // Arguments left by launches whose target exited before reading them.
            foreach (var stale in TakePendingArguments(appId))
            {
                Diagnostics.Log($"Ignoring stale forwarded launch arguments: {string.Join(' ', stale)}");
            }

            return new SingleInstance(appId, signal);
        }

        WriteArguments(appId, arguments);

        // Let the running copy take the foreground, then wake it.
        AllowSetForegroundWindow(AsfwAny);
        signal.Set();
        signal.Dispose();
        return null;
    }

    /// <summary>
    /// Brings <paramref name="window"/> to the front whenever another launch asks for it, then
    /// calls <paramref name="onForwardedLaunch"/> (on the UI thread) with each forwarded launch's
    /// arguments, oldest first.
    /// </summary>
    public void BringToFrontOnRequest(Window window, Action<IReadOnlyList<string>>? onForwardedLaunch = null)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var appId = this._appId;
        this._registration = ThreadPool.RegisterWaitForSingleObject(
            this._activateSignal,
            (_, _) => dispatcher.TryEnqueue(() =>
            {
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SwRestore);
                }

                window.Activate();
                SetForegroundWindow(hwnd);

                foreach (var arguments in TakePendingArguments(appId))
                {
                    try
                    {
                        onForwardedLaunch?.Invoke(arguments);
                    }
                    catch (Exception e)
                    {
                        Diagnostics.Log("Couldn't apply forwarded launch arguments", e);
                    }
                }
            }),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        this._registration?.Unregister(null);
        this._activateSignal.Dispose();
    }

    private static string FilePrefix(uint appId) => appId.ToString(CultureInfo.InvariantCulture) + "-";

    private static void WriteArguments(uint appId, IReadOnlyList<string> arguments)
    {
        try
        {
            Directory.CreateDirectory(ArgsDirectory);
            var name = string.Create(CultureInfo.InvariantCulture, $"{FilePrefix(appId)}{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}");
            var temp = Path.Combine(ArgsDirectory, name + ".tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(arguments));
            File.Move(temp, Path.Combine(ArgsDirectory, name + ".args"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The window is still brought to the front; only the flags are lost.
            Diagnostics.Log("Couldn't forward launch arguments", e);
        }
    }

    /// <summary>Reads and deletes the pending argument files for <paramref name="appId"/>, oldest first.</summary>
    private static List<IReadOnlyList<string>> TakePendingArguments(uint appId)
    {
        var result = new List<IReadOnlyList<string>>();
        string[] files;
        try
        {
            if (Directory.Exists(ArgsDirectory) == false)
            {
                return result;
            }

            files = Directory.GetFiles(ArgsDirectory, FilePrefix(appId) + "*.args");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("Couldn't list forwarded launch arguments", e);
            return result;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            try
            {
                var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file));
                File.Delete(file);
                if (arguments != null)
                {
                    result.Add(arguments);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Diagnostics.Log($"Couldn't read forwarded launch arguments {file}", e);
                try
                {
                    File.Delete(file);
                }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return result;
    }

    private const int AsfwAny = -1;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
