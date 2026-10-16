using System.Globalization;
using Microsoft.UI.Xaml;
using SAM.App.Services;
using SAM.App.Views;

namespace SAM.App;

/// <summary>
/// Entry point. Without arguments the app opens the game library; with a numeric
/// App ID (<c>SAM.exe 21690</c>) it opens the achievement manager for that game, and
/// <c>SAM.exe 21690 --unlock-all</c> opens it with all achievements pre-selected.
/// Each game runs in its own process because Steam binds a client connection to
/// one App ID for the lifetime of the process.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
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

        try
        {
            Services = new AppServices(SteamSession.Initialize(appId));
        }
        catch (SteamUnavailableException e)
        {
            this.Show(new ErrorWindow("Can't connect to Steam", e.Message));
            return;
        }

        // "SAM.exe <appid> --unlock-all" opens the game with every achievement pre-selected
        // for unlocking; the user still confirms before anything is committed.
        var unlockAll = commandLine.Skip(2).Contains("--unlock-all", StringComparer.OrdinalIgnoreCase);

        this.Show(appId == 0
            ? new LibraryWindow(Services)
            : new GameWindow(Services, appId, unlockAll));
    }

    private void Show(Window window)
    {
        this._window = window;
        window.Closed += (_, _) => Services?.Steam.Dispose();
        window.Activate();
    }
}
