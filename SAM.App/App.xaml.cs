using System.Globalization;
using Microsoft.UI.Xaml;
using SAM.App.Services;
using SAM.App.Views;

namespace SAM.App;

/// <summary>
/// Entry point. Without arguments the app opens the game library; with a numeric
/// App ID (<c>SAM.exe 21690</c>) it opens the achievement manager for that game, and
/// <c>SAM.exe 21690 --unlock-all</c> (or <c>--lock-all</c>) opens it with every achievement pre-set.
/// Each game runs in its own process because Steam binds a client connection to
/// one App ID for the lifetime of the process.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    private readonly AppSettings _settings;
    private SingleInstance? _instance;

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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Environment args: [0] is the executable path.
        var commandLine = Environment.GetCommandLineArgs();
        uint appId = 0;
        if (commandLine.Length > 1 &&
            uint.TryParse(commandLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out appId) == false)
        {
            this.Show(new ErrorWindow(
                "Invalid App ID",
                $"\"{commandLine[1]}\" is not a valid Steam App ID."));
            return;
        }

        // One window per game (and one library): if it's already open, focus it and quit.
        this._instance = SingleInstance.TryClaim(appId);
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

        // "SAM.exe <appid> --unlock-all" / "--lock-all" open the game with every achievement
        // pre-set; the user still confirms before anything is committed.
        var flags = commandLine.Skip(2).ToHashSet(StringComparer.OrdinalIgnoreCase);

        this.Show(appId == 0
            ? new LibraryWindow(Services)
            : new GameWindow(Services, appId, flags.Contains("--unlock-all"), flags.Contains("--lock-all")));
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

    private void Show(Window window)
    {
        this._window = window;
        this._instance?.BringToFrontOnRequest(window);
        window.Closed += (_, _) =>
        {
            this._instance?.Dispose();
            Services?.Steam.Dispose();
        };
        window.Activate();
    }
}
