using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SAM.App.Infrastructure;
using SAM.App.Services;
using SAM.App.Views;

namespace SAM.App;

/// <summary>
/// Entry point. Without arguments the app opens the game library; with a numeric
/// App ID (<c>SAM.exe 21690</c>) it opens the achievement manager for that game, and
/// <c>SAM.exe 21690 --unlock-all</c> (or <c>--lock-all</c>) opens it with every achievement pre-set.
/// <c>SAM.exe 21690 --unlock ID --commit</c> changes achievements without a window, and
/// <c>SAM.exe --help</c> prints the full usage (see <see cref="CommandLine"/>).
/// Each game runs in its own process because Steam binds a client connection to
/// one App ID for the lifetime of the process.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    private readonly AppSettings _settings;
    private SingleInstance? _instance;

    /// <summary>True while the library window is hidden to the tray.</summary>
    private bool _hiddenToTray;

    /// <summary>Set by the tray's "Exit": the next close really closes.</summary>
    private bool _exiting;

    private bool _trayHintShown;
    private string? _trayStatus;

    public App()
    {
        this._settings = AppSettings.Load();
        ApplyLanguage(this._settings.Language);

        this.InitializeComponent();
        this.UnhandledException += (_, e) =>
        {
            Diagnostics.Log("Unhandled exception", e.Exception);
        };
    }

    public static AppServices? Services { get; private set; }

    /// <summary>
    /// The notification-area icon. Only exists in the library process (null in game windows and
    /// command-line runs). Use <see cref="SetTrayStatus"/> to show a background task's progress
    /// rather than toggling its visibility directly.
    /// </summary>
    public static TrayIcon? Tray { get; private set; }

    private static App? CurrentApp => Current as App;

    /// <summary>
    /// Shows <paramref name="status"/> (e.g. "Farming 3 games") as the tray icon's tooltip and
    /// keeps the icon visible while it's set; null clears it. No-op outside the library process.
    /// Call on the UI thread.
    /// </summary>
    public static void SetTrayStatus(string? status)
    {
        if (CurrentApp is { } app)
        {
            app._trayStatus = string.IsNullOrEmpty(status) ? null : status;
            app.RefreshTray();
        }
    }

    /// <summary>
    /// Shows or hides the tray icon to match the settings (e.g. after the "Minimize to tray"
    /// setting changes). Call on the UI thread.
    /// </summary>
    public static void UpdateTray() => CurrentApp?.RefreshTray();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Environment args: [0] is the executable path.
        var arguments = Environment.GetCommandLineArgs()[1..];
        var command = CommandLine.Parse(arguments);

        switch (command.Mode)
        {
            case LaunchMode.Help:
                if (ConsoleOutput.IsAvailable)
                {
                    ConsoleOutput.WriteLine(CommandLine.Usage);
                    this.ExitWith(CommandLine.ExitSuccess);
                }
                else
                {
                    this.Show(new ErrorWindow("Command-line usage", CommandLine.Usage));
                }

                return;

            case LaunchMode.Invalid:
                if (ConsoleOutput.IsAvailable)
                {
                    ConsoleOutput.WriteError(command.Error ?? "Invalid arguments.");
                    ConsoleOutput.WriteError("Run SAM.exe --help for usage.");
                    this.ExitWith(CommandLine.ExitInvalidArguments);
                }
                else
                {
                    this.Show(new ErrorWindow("Invalid command line", command.Error ?? "Invalid arguments."));
                }

                return;

            case LaunchMode.Commit:
                this.RunCommandLine(command);
                return;
        }

        var appId = command.AppId;

        // One window per game (and one library): if it's already open, hand it our arguments
        // (so "--unlock-all" etc. still apply), focus it and quit.
        this._instance = SingleInstance.TryClaim(appId, arguments);
        if (this._instance == null)
        {
            this.Exit();
            return;
        }

        try
        {
            Services = new AppServices(SteamSession.Initialize(appId), this._settings);
        }
        catch (SteamUnavailableException e)
        {
            this.Show(new ErrorWindow("Can't connect to Steam", e.Message));
            return;
        }

        NotificationService.Register(this.OnNotificationClicked);

        // "SAM.exe <appid> --unlock-all" / "--lock-all" open the game with every achievement
        // pre-set; the user still confirms before anything is committed.
        if (appId == 0)
        {
            var library = new LibraryWindow(Services);
            this.SetUpTray(library);
            this.Show(library);
        }
        else
        {
            this.Show(new GameWindow(Services, appId, command.UnlockAllPreset, command.LockAllPreset));
        }
    }

    /// <summary>
    /// Formats dates and numbers in the app's language instead of the Windows region format,
    /// so they match the (English) UI text. WinUI controls such as date pickers follow
    /// PrimaryLanguageOverride.
    /// </summary>
    private static void ApplyLanguage(string language)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.GetCultureInfo("en-US");
        }

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture.Name;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't set the WinUI language override", e);
        }
    }

    /// <summary>
    /// A second "SAM.exe &lt;appid&gt; --unlock-all" (or --lock-all) while this game's window is
    /// already open: apply the preset to the open window instead of dropping the flag.
    /// </summary>
    private static void ApplyForwardedLaunch(Window window, IReadOnlyList<string> arguments)
    {
        var flags = arguments.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unlockAll = flags.Contains("--unlock-all");
        var lockAll = flags.Contains("--lock-all");
        if (unlockAll || lockAll)
        {
            (window as ILaunchPresetTarget)?.ApplyLaunchPreset(unlockAll, lockAll);
        }
    }

    /// <summary>Runs a headless "--commit" command line, then exits with its exit code.</summary>
    private async void RunCommandLine(CommandLine command)
    {
        var code = CommandLine.ExitFailed;
        try
        {
            code = await CliRunner.RunAsync(command, this._settings);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Command-line run failed", e);
            ConsoleOutput.WriteError($"Something went wrong: {e.Message}");
        }

        this.ExitWith(code);
    }

    private void ExitWith(int exitCode)
    {
        NotificationService.Unregister();
        Environment.ExitCode = exitCode;
        this.Exit();
    }

    private void Show(Window window)
    {
        this._window = window;
        this._instance?.BringToFrontOnRequest(window, arguments =>
        {
            if (this._hiddenToTray)
            {
                this.ShowLibrary();
            }

            ApplyForwardedLaunch(window, arguments);
        });
        window.Closed += (_, _) =>
        {
            Tray?.Dispose();
            Tray = null;
            NotificationService.Unregister();
            this._instance?.Dispose();
            Services?.Steam.Dispose();
        };
        window.Activate();
    }

    #region Tray and notifications
    /// <summary>
    /// Creates the (initially hidden) tray icon for the library and, when "Minimize to tray" is
    /// on, hides the window on close instead of exiting.
    /// </summary>
    private void SetUpTray(Window library)
    {
        try
        {
            Tray = new TrayIcon();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't create the tray icon", e);
            return;
        }

        Tray.OpenRequested += (_, _) => this.ShowLibrary();
        Tray.ExitRequested += (_, _) =>
        {
            this._exiting = true;
            library.Close();
        };
        library.AppWindow.Closing += this.OnLibraryClosing;
        this.RefreshTray();
    }

    private void OnLibraryClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (this._exiting || Tray == null || this._settings.MinimizeToTray == false)
        {
            return;
        }

        Tray.IsVisible = true;
        if (Tray.IsVisible == false)
        {
            // The shell refused the icon: hiding would leave no way back, so really close.
            return;
        }

        args.Cancel = true;
        sender.Hide();
        this._hiddenToTray = true;

        if (this._trayHintShown == false)
        {
            this._trayHintShown = true;
            Services?.Notifications.Show(
                "SAM is still running in the tray",
                "Click the tray icon to open the library, or right-click it and choose Exit to quit.");
        }
    }

    /// <summary>The icon is shown while the window is hidden, the setting is on, or a status is set.</summary>
    private void RefreshTray()
    {
        if (Tray == null)
        {
            return;
        }

        Tray.ToolTip = this._trayStatus;
        Tray.IsVisible = this._hiddenToTray || this._settings.MinimizeToTray || this._trayStatus != null;
    }

    /// <summary>Brings the library window back (from the tray, minimized or behind other windows).</summary>
    private void ShowLibrary()
    {
        if (this._window is not LibraryWindow window)
        {
            return;
        }

        this._hiddenToTray = false;
        window.AppWindow.Show();
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        window.Activate();
        this.RefreshTray();
    }

    /// <summary>A notification was clicked: show the library (starting it if this is a game window).</summary>
    private void OnNotificationClicked()
    {
        if (this._window is LibraryWindow)
        {
            this.ShowLibrary();
            return;
        }

        try
        {
            // Without arguments SAM forwards to the running library, or starts it.
            if (Environment.ProcessPath is { } path)
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })?.Dispose();
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't open the library from a notification", e);
        }
    }
    #endregion
}
