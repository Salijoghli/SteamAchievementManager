using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SAM.App.Infrastructure;
using SAM.App.Services;
using SAM.App.Services.Autopilot;
using SAM.App.Views;

namespace SAM.App;

/// <summary>
/// Entry point. Without arguments the app opens the game library; with a numeric
/// App ID (<c>SAM.exe 21690</c>) it opens the achievement manager for that game, and
/// <c>SAM.exe 21690 --unlock-all</c> (or <c>--lock-all</c>) opens it with every achievement pre-set.
/// <c>SAM.exe 21690 --unlock ID --commit</c> changes achievements without a window, and
/// <c>SAM.exe --help</c> prints the full usage (see <see cref="CommandLine"/>).
/// <c>SAM.exe --autopilot</c> starts the library hidden in the tray with Autopilot running, and
/// <c>SAM.exe &lt;appid&gt; --autopilot-worker &lt;pid&gt;</c> is a headless Autopilot worker (see <see cref="AutopilotWorker"/>), and
/// <c>SAM.exe &lt;appid&gt; --fetch-achievements</c> has Steam download a game's achievement data (see <see cref="AchievementFetcher"/>).
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
    private bool _autopilotHintShown;
    private string? _trayStatus;

    /// <summary>The library's Autopilot engine (null in game windows, workers and command-line runs).</summary>
    private AutopilotEngine? _autopilot;

    /// <summary>Notifies about achievements unlocked by playing (library process only).</summary>
    private UnlockNotifier? _unlockNotifier;

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

        // Command-line output stays English whatever the UI language, so scripts see stable text.
        if (command.Mode is LaunchMode.Help or LaunchMode.Invalid or LaunchMode.Commit or LaunchMode.FetchAchievements)
        {
            ApplyLanguage("en-US");
        }

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

            case LaunchMode.AutopilotWorker:
                this.RunAutopilotWorker(command);
                return;

            case LaunchMode.FetchAchievements:
                this.RunFetchAchievements(command);
                return;
        }

        var appId = command.AppId;

        // "Restart now" in Settings: let the old library exit before claiming the single instance,
        // otherwise this launch would just be forwarded to it.
        if (command.RestartAfterProcessId != 0)
        {
            WaitForExit(command.RestartAfterProcessId);
        }

        // Autopilot dry-run is a property of the library process that runs the engine. Forwarding
        // this launch would let an already-running (real) library start Autopilot for real.
        if (appId == 0 && AutopilotProtocol.IsDryRun && AutopilotWorker.IsWindowOpen(0))
        {
            this.RefuseDryRunForward();
            return;
        }

        // One window per game (and one library): if it's already open, hand it our arguments
        // (so "--unlock-all" etc. still apply), focus it and quit.
        this._instance = SingleInstance.TryClaim(appId, arguments, out var commandLineCommitRunning);
        if (this._instance == null)
        {
            if (commandLineCommitRunning)
            {
                // A headless "--commit" for this game has no window to bring forward; say why
                // nothing opens instead of vanishing.
                this.Show(new ErrorWindow(Strings.Get("App_CommitRunningTitle"), Strings.Get("App_CommitRunningMessage")));
                return;
            }

            this.Exit();
            return;
        }

        SteamSession steam;
        try
        {
            steam = SteamSession.Initialize(appId);
        }
        catch (SteamUnavailableException e)
        {
            this.Show(new ErrorWindow(Strings.Get("App_CantConnectToSteam"), e.Message));
            return;
        }

        try
        {
            Services = new AppServices(steam, this._settings);
        }
        catch (Exception e)
        {
            // E.g. SAM's data folder can't be read or written: explain instead of crashing.
            Diagnostics.Log("Couldn't start SAM's services", e);
            steam.Dispose();
            this.Show(new ErrorWindow(Strings.Get("App_CantStart"), e.Message));
            return;
        }

        NotificationService.Register(this.OnNotificationClicked);

        // "SAM.exe <appid> --unlock-all" / "--lock-all" open the game with every achievement
        // pre-set; the user still confirms before anything is committed.
        if (appId == 0)
        {
            // After "Update now": the previous exe (SAM.exe.old) has exited by now.
            if (Environment.ProcessPath is { } exePath)
            {
                Task.Run(() => SelfUpdater.CleanUp(exePath));
            }

            var library = new LibraryWindow(Services);
            this.SetUpTray(library);
            this.SetUpAutopilot(command.StartAutopilot);
            this.SetUpUnlockNotifications(Services);

            // "--autopilot" (Start with Windows): stay in the tray; the window opens from the icon.
            this.Show(library, activate: command.StartAutopilot == false || this.HideToTrayAtStart() == false);
        }
        else
        {
            this.Show(new GameWindow(Services, appId, command.UnlockAllPreset, command.LockAllPreset));
        }
    }

    /// <summary>
    /// Starts a new SAM library (e.g. after the UI language changed) and closes this process. The
    /// new process waits for this one to exit (<see cref="CommandLine.RestartAfterOption"/>) so the
    /// single-instance check doesn't hand the launch back to us. Call on the UI thread.
    /// </summary>
    public static void Restart() => CurrentApp?.RestartCore();

    /// <summary>
    /// After "Update now" replaced SAM.exe: stops Autopilot's workers (keeping "was running", so
    /// the new version resumes it when "Resume on startup" is on), then restarts into the new exe.
    /// False if the new process couldn't be started; this one keeps running. Call on the UI thread.
    /// </summary>
    public static bool RestartForUpdate()
    {
        if (CurrentApp is not { } app)
        {
            return false;
        }

        try
        {
            app._autopilot?.Dispose();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't stop Autopilot before updating", e);
        }

        app._autopilot = null;
        return app.RestartCore();
    }

    private bool RestartCore()
    {
        if (Environment.ProcessPath is not { } path)
        {
            return false;
        }

        try
        {
            var arguments = CommandLine.RestartAfterOption + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't restart SAM", e);
            return false;
        }

        this._exiting = true;
        this._window?.Close();
        this.Exit();
        return true;
    }

    /// <summary>Waits (up to 15 seconds) for the process <paramref name="processId"/> to exit.</summary>
    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.WaitForExit(TimeSpan.FromSeconds(15)) == false)
            {
                Diagnostics.Log($"Restart: process {processId} is still running; continuing anyway");
            }
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Restart: couldn't wait for process {processId}", e);
        }
    }

    /// <summary>
    /// Shows UI text (see <see cref="Strings"/> and x:Uid) and formats dates and numbers in the app's
    /// language instead of the Windows display language and region format, so they match. The
    /// resource system and WinUI controls such as date pickers follow PrimaryLanguageOverride.
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

    /// <summary>A dry-run library launch while SAM's library already runs: say so and exit without forwarding.</summary>
    private void RefuseDryRunForward()
    {
        var message = Strings.Get("AutopilotEngine_DryRunForwardMessage");
        Diagnostics.Log("Autopilot dry run: refused because the library is already running; nothing was forwarded");
        if (ConsoleOutput.IsAvailable)
        {
            ConsoleOutput.WriteError(message);
            this.ExitWith(CommandLine.ExitBusy);
        }
        else
        {
            this.Show(new ErrorWindow(Strings.Get("AutopilotEngine_DryRunForwardTitle"), message));
        }
    }

    /// <summary>Runs a headless Autopilot worker until it stops, then exits with its exit code.</summary>
    private async void RunAutopilotWorker(CommandLine command)
    {
        var code = AutopilotProtocol.ExitSteamFailed;
        try
        {
            // The engine passes dry-run explicitly; the variable still works for a worker started by hand.
            var dryRun = command.AutopilotDryRun || AutopilotProtocol.IsDryRun;
            code = await AutopilotWorker.RunAsync(command.AppId, command.ParentProcessId, this._settings, dryRun);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Autopilot worker for {command.AppId} failed", e);
        }

        this.ExitWith(code);
    }

    /// <summary>Runs a headless "--fetch-achievements", then exits with its exit code.</summary>
    private async void RunFetchAchievements(CommandLine command)
    {
        var code = CommandLine.ExitFailed;
        try
        {
            code = await AchievementFetcher.RunAsync(command.AppId);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Fetching achievements for {command.AppId} failed", e);
        }

        this.ExitWith(code);
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

    /// <param name="window">The window.</param>
    /// <param name="activate">False to create the window without showing it (started hidden in the tray).</param>
    private void Show(Window window, bool activate = true)
    {
        this._window = window;
        this._instance?.BringToFrontOnRequest(window, arguments =>
        {
            if (this._hiddenToTray)
            {
                this.ShowLibrary();
            }

            ApplyForwardedLaunch(window, arguments);
            if (this._autopilot is { IsRunning: false } engine && arguments.Contains(CommandLine.AutopilotOption, StringComparer.OrdinalIgnoreCase))
            {
                this.StartAutopilot(engine);
            }
        });
        window.Closed += (_, _) =>
        {
            // Stops the workers (Autopilot resumes next time if "Resume on startup" is on).
            this._autopilot?.Dispose();
            this._autopilot = null;
            this._unlockNotifier?.Dispose();
            this._unlockNotifier = null;
            Tray?.Dispose();
            Tray = null;
            NotificationService.Unregister();
            this._instance?.Dispose();
            Services?.Steam.Dispose();
        };
        if (activate)
        {
            window.Activate();
        }
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
        // While Autopilot runs, closing the window hides it even without "Minimize to tray":
        // closing would stop the games. "Exit" in the tray menu really quits.
        var autopilotRunning = this._autopilot?.IsRunning == true;
        if (this._exiting || Tray == null || (this._settings.MinimizeToTray == false && autopilotRunning == false))
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

        if (autopilotRunning && this._autopilotHintShown == false)
        {
            this._autopilotHintShown = true;
            this._trayHintShown = true;
            Services?.Notifications.Show(Strings.Get("AutopilotEngine_TrayHintTitle"), Strings.Get("AutopilotEngine_TrayHintMessage"));
        }
        else if (this._trayHintShown == false)
        {
            this._trayHintShown = true;
            Services?.Notifications.Show(Strings.Get("App_TrayHintTitle"), Strings.Get("App_TrayHintMessage"));
        }
    }

    /// <summary>For "--autopilot": shows only the tray icon. False if the shell refused it (then the window must show).</summary>
    private bool HideToTrayAtStart()
    {
        if (Tray == null)
        {
            return false;
        }

        Tray.IsVisible = true;
        if (Tray.IsVisible == false)
        {
            return false;
        }

        this._hiddenToTray = true;
        this.RefreshTray();
        return true;
    }

    /// <summary>
    /// Creates the library's Autopilot engine, mirrors its status in the tray (tooltip and the
    /// Pause/Resume menu item) and starts it for "--autopilot" or when it was running last time
    /// and "Resume on startup" is on.
    /// </summary>
    private void SetUpAutopilot(bool start)
    {
        if (Services == null)
        {
            return;
        }

        AutopilotEngine engine;
        try
        {
            engine = Services.Autopilot;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't create the Autopilot engine", e);
            return;
        }

        this._autopilot = engine;
        engine.Changed += (_, _) => this.OnAutopilotChanged(engine);
        if (Tray != null)
        {
            Tray.AutopilotCommandRequested += async (_, _) =>
            {
                try
                {
                    if (engine.IsRunning)
                    {
                        await engine.StopAsync();
                    }
                    else
                    {
                        await engine.StartAsync();
                    }
                }
                catch (Exception e)
                {
                    Diagnostics.Log("Couldn't pause or resume Autopilot from the tray", e);
                }
            };
        }

        try
        {
            start = start || (engine.Config.ResumeOnStartup && engine.Config.WasRunning);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't read the Autopilot configuration", e);
            start = false;
        }

        this.OnAutopilotChanged(engine);
        if (start)
        {
            this.StartAutopilot(engine);
        }
    }

    /// <summary>Starts watching for achievements the user unlocks by playing (see <see cref="UnlockNotifier"/>).</summary>
    private void SetUpUnlockNotifications(AppServices services)
    {
        try
        {
            this._unlockNotifier = new UnlockNotifier(services);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't start unlock notifications", e);
        }
    }

    private async void StartAutopilot(AutopilotEngine engine)
    {
        try
        {
            await engine.StartAsync();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't start Autopilot", e);
        }
    }

    private void OnAutopilotChanged(AutopilotEngine engine)
    {
        if (Tray != null)
        {
            Tray.AutopilotCommandText = Strings.Get(engine.IsRunning ? "AutopilotEngine_TrayPause" : "AutopilotEngine_TrayResume");
        }

        SetTrayStatus(engine.IsRunning ? engine.TrayStatus ?? Strings.Get("AutopilotEngine_TrayIdle") : null);
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
