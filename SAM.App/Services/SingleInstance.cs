using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SAM.App.Services;

/// <summary>
/// Keeps one window per App ID (0 = the library). A second launch for the same App ID signals
/// the running copy to bring its window to the front and then exits, so two windows can never
/// edit the same game's achievements at once.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly EventWaitHandle _activateSignal;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(EventWaitHandle activateSignal)
    {
        this._activateSignal = activateSignal;
    }

    /// <summary>
    /// Claims the App ID for this process. Returns null if another process already owns it;
    /// in that case that process has been asked to show its window and this one should exit.
    /// </summary>
    public static SingleInstance? TryClaim(uint appId)
    {
        // Session-local name, so different Windows users don't collide.
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $"SteamAchievementManager.{appId}.Activate", out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(signal);
        }

        // Let the running copy take the foreground, then wake it.
        AllowSetForegroundWindow(AsfwAny);
        signal.Set();
        signal.Dispose();
        return null;
    }

    /// <summary>Brings <paramref name="window"/> to the front whenever another launch asks for it.</summary>
    public void BringToFrontOnRequest(Window window)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
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
