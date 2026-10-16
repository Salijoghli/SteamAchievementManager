namespace SAM.App.Services;

// Data layer entry point. Each service lives in its own file in this folder:
// SchemaService, ProgressService, PlaytimeService, GlobalPercentService, ImageCache, GameArt
// and GameListService. UI code depends only on their public signatures.

/// <summary>Creates and holds the services for the process.</summary>
public sealed class AppServices
{
    public AppServices(SteamSession steam)
    {
        this.Steam = steam;
        this.Settings = AppSettings.Load();
        this.Cache = new AppCache(steam.SteamId);
        this.AccountSwitched = this.Cache.HandleAccountSwitch(this.Settings.KeepOtherAccountsCache);
        this.Favorites = new FavoritesStore(this.Cache);

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamAchievementManager/8.0");

        this.Schemas = new SchemaService(steam);
        this.Progress = new ProgressService(steam);
        this.Playtime = new PlaytimeService(steam);
        this.GlobalPercents = new GlobalPercentService(this.Cache, http);
        this.Images = new ImageCache(this.Cache, http);
        this.Art = new GameArt(steam);
        this.Games = new GameListService(this.Cache, steam, http);
    }

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

    public ImageCache Images { get; }

    public GameArt Art { get; }

    public GameListService Games { get; }
}
