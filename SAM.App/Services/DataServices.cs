namespace SAM.App.Services;

// Data layer entry point. Each service lives in its own file in this folder:
// SchemaService, ProgressService, PlaytimeService, GlobalPercentService, AchievementCountService, ImageCache, GameArt
// and GameListService. UI code depends only on their public signatures.

/// <summary>Creates and holds the services for the process.</summary>
public sealed class AppServices
{
    public AppServices(SteamSession steam, AppSettings settings)
    {
        this.Steam = steam;
        this.Settings = settings;
        this.Cache = new AppCache(steam.SteamId);
        this.AccountSwitched = this.Cache.HandleAccountSwitch(this.Settings.KeepOtherAccountsCache);
        this.Favorites = new FavoritesStore(this.Cache);

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SteamAchievementManager/{UpdateService.CurrentVersion.ToString(3)}");

        this.Schemas = new SchemaService(steam);
        this.Progress = new ProgressService(steam);
        this.Playtime = new PlaytimeService(steam);
        this.GlobalPercents = new GlobalPercentService(this.Cache, http);
        this.AchievementCounts = new AchievementCountService(this.Cache, http);
        this.Friends = new FriendsService(steam, this.Cache, http);
        this.Images = new ImageCache(this.Cache, http);
        this.Art = new GameArt(steam);
        this.Games = new GameListService(this.Cache, steam, http);

        this.Backups = new BackupService(this.Cache);
        this.History = new HistoryService(this.Cache);
        this.Notes = new NotesStore(this.Cache);
        this.Collections = new CollectionsService(steam, this.Cache);
        this.Installed = new InstalledGamesService(steam);
        this.Timeline = new TimelineService(steam, this.Schemas);
        this.Updates = new UpdateService(http, settings);
        this.Notifications = new NotificationService(settings);
        this.AutopilotStore = new Autopilot.AutopilotStore(this.Cache);
    }

    private Autopilot.AutopilotEngine? _autopilot;

    public Autopilot.AutopilotStore AutopilotStore { get; }

    /// <summary>
    /// The Autopilot engine, created on first use. Only the library process should use it (create
    /// it on the UI thread); game windows and workers use AutopilotEngine's static helpers instead.
    /// </summary>
    public Autopilot.AutopilotEngine Autopilot => this._autopilot ??= new Autopilot.AutopilotEngine(this);

    public BackupService Backups { get; }

    public HistoryService History { get; }

    public NotesStore Notes { get; }

    public CollectionsService Collections { get; }

    public InstalledGamesService Installed { get; }

    public TimelineService Timeline { get; }

    public UpdateService Updates { get; }

    public NotificationService Notifications { get; }

    public SteamSession Steam { get; }

    public AppSettings Settings { get; }

    public AppCache Cache { get; }

    /// <summary>True if a different Steam account used the app last time.</summary>
    public bool AccountSwitched { get; }

    public FavoritesStore Favorites { get; }

    public SchemaService Schemas { get; }

    public ProgressService Progress { get; }

    public PlaytimeService Playtime { get; }

    public GlobalPercentService GlobalPercents { get; }

    /// <summary>Whether games have achievements, from the store, for games whose schema Steam hasn't downloaded.</summary>
    public AchievementCountService AchievementCounts { get; }

    /// <summary>Steam friends and their achievements (for comparing).</summary>
    public FriendsService Friends { get; }

    public ImageCache Images { get; }

    public GameArt Art { get; }

    public GameListService Games { get; }
}
