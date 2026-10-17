using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.UI.Notifications;

namespace SAM.App.Services;

/// <summary>Windows toast notifications. No-ops when AppSettings.ShowNotifications is off or unavailable.</summary>
/// <remarks>
/// Prefers the Windows App SDK's <see cref="AppNotificationManager"/>. The app is unpackaged, so
/// each process calls <see cref="Register"/> once at startup (before showing anything) and
/// <see cref="Unregister"/> on exit; clicking a notification after its process has exited then
/// makes Windows start <c>SAM.exe ----AppNotificationActivated:â€¦</c>, which opens the library.
/// <para>
/// AppNotificationManager.Register() fails in self-contained Windows App SDK builds like ours
/// (it needs the framework package's notification host), so registration falls back to the
/// built-in Windows toast API with an AppUserModelID registered under HKCU. Those toasts can
/// only be clicked while the process that showed them is still running.
/// </para>
/// </remarks>
public sealed class NotificationService(AppSettings settings)
{
    /// <summary>The argument Windows passes when it starts the app from a clicked notification.</summary>
    public const string ActivationArgumentPrefix = "----AppNotificationActivated:";

    private const string AppUserModelId = "Salijoghli.SteamAchievementManager";
    private const string DisplayName = "Steam Achievement Manager";

    private static readonly object Lock = new();
    private static Backend _backend;
    private static ToastNotifier? _toastNotifier;
    private static Action? _onInvoked;
    private static DispatcherQueue? _dispatcher;

    private enum Backend
    {
        None,
        AppNotifications,
        Toasts,
    }

    /// <summary>True once <see cref="Register"/> has succeeded in this process.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (Lock)
            {
                return _backend != Backend.None;
            }
        }
    }

    /// <summary>
    /// Registers this process with Windows so it can show notifications. <paramref name="onInvoked"/>
    /// runs on the calling (UI) thread when the user clicks one of this process's notifications.
    /// Returns false (and logs) if notifications aren't supported or registration fails; later
    /// <see cref="Show"/> calls then do nothing. Safe to call more than once.
    /// </summary>
    public static bool Register(Action? onInvoked)
    {
        lock (Lock)
        {
            _onInvoked = onInvoked;
            if (_backend != Backend.None)
            {
                return true;
            }

            _dispatcher = DispatcherQueue.GetForCurrentThread();
            if (TryRegisterAppNotifications())
            {
                _backend = Backend.AppNotifications;
                return true;
            }

            if (TryRegisterToasts())
            {
                _backend = Backend.Toasts;
                return true;
            }

            return false;
        }
    }

    /// <summary>Undoes <see cref="Register"/> for this process. Call on exit; never throws.</summary>
    public static void Unregister()
    {
        lock (Lock)
        {
            var backend = _backend;
            _backend = Backend.None;
            _toastNotifier = null;
            if (backend != Backend.AppNotifications)
            {
                return;
            }

            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception e)
            {
                Diagnostics.Log("Couldn't unregister from notifications", e);
            }
        }
    }

    /// <summary>
    /// Shows a toast with <paramref name="title"/> and <paramref name="message"/>. Does nothing
    /// when notifications are turned off in settings or unavailable. Never throws; callable from
    /// any thread.
    /// </summary>
    public void Show(string title, string message)
    {
        if (settings.ShowNotifications == false)
        {
            return;
        }

        Backend backend;
        ToastNotifier? notifier;
        lock (Lock)
        {
            backend = _backend;
            notifier = _toastNotifier;
        }

        if (backend == Backend.None)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();

            if (backend == Backend.AppNotifications)
            {
                AppNotificationManager.Default.Show(notification);
                return;
            }

            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(notification.Payload);
            var toast = new ToastNotification(xml);
            toast.Activated += (_, _) => OnClicked();
            notifier?.Show(toast);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't show the notification \"{title}\"", e);
        }
    }

    private static bool TryRegisterAppNotifications()
    {
        try
        {
            if (AppNotificationManager.IsSupported() == false)
            {
                return false;
            }

            // The handler must be attached before Register().
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"App notifications are unavailable (0x{e.HResult:X8}); using Windows toasts instead");
            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            }
            catch (Exception)
            {
            }

            return false;
        }
    }

    private static bool TryRegisterToasts()
    {
        try
        {
            // Lets an unpackaged app show toasts under its own name and icon, without a
            // Start menu shortcut.
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}"))
            {
                key.SetValue("DisplayName", DisplayName);
                key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "SAM.ico"));
            }

            // (ToastNotifier.Setting throws "element not found" for unpackaged apps, so it isn't checked.)
            _toastNotifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't register for notifications", e);
            _toastNotifier = null;
            return false;
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        OnClicked();

    /// <summary>Runs the click handler on the UI thread (notification events arrive on a background thread).</summary>
    private static void OnClicked()
    {
        Action? onInvoked;
        DispatcherQueue? dispatcher;
        lock (Lock)
        {
            onInvoked = _onInvoked;
            dispatcher = _dispatcher;
        }

        if (onInvoked == null)
        {
            return;
        }

        void Invoke()
        {
            try
            {
                onInvoked();
            }
            catch (Exception e)
            {
                Diagnostics.Log("Handling a clicked notification failed", e);
            }
        }

        if (dispatcher == null || dispatcher.TryEnqueue(Invoke) == false)
        {
            Invoke();
        }
    }
}
