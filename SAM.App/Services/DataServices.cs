using SAM.App.Models;

namespace SAM.App.Services;

// Contracts for the data layer. UI code depends only on these signatures.
// Bodies marked "TODO(services)" are implemented separately.

/// <summary>Reads game schemas from Steam's appcache\stats\UserGameStatsSchema_&lt;appid&gt;.bin.</summary>
public sealed class SchemaService(SteamSession steam)
{
    /// <summary>
    /// Parses the schema for <paramref name="appId"/>, with names/descriptions localized to
    /// Steam's language (falling back to English, then the raw value). Returns null if the
    /// schema file is missing or unreadable. Unknown stat types are skipped. Thread-safe;
    /// results are memoized per App ID.
    /// </summary>
    public GameSchema? Load(uint appId) => throw new NotImplementedException("TODO(services)");
}

/// <summary>
/// Reads achievement progress from Steam's local stats cache
/// (appcache\stats\UserGameStats_&lt;accountid&gt;_&lt;appid&gt;.bin), with no network calls.
/// </summary>
public sealed class ProgressService(SteamSession steam)
{
    /// <summary>
    /// Unlocked/total for <paramref name="appId"/>. Total comes from the schema; unlocked from
    /// the cache file's achievement bitfields, counting only bits defined in the schema.
    /// Returns (0, total) when the schema exists but Steam has no cached stats for the user
    /// (never played), and null when there's no schema (unknown).
    /// </summary>
    public GameProgress? GetCachedProgress(uint appId, GameSchema? schema) => throw new NotImplementedException("TODO(services)");
}

/// <summary>Reads last-played time and total playtime from userdata\&lt;accountid&gt;\config\localconfig.vdf.</summary>
public sealed class PlaytimeService(SteamSession steam)
{
    /// <summary>Per App ID. Empty if the file can't be read. Safe to call off the UI thread.</summary>
    public IReadOnlyDictionary<uint, PlaytimeInfo> Load() => throw new NotImplementedException("TODO(services)");
}

/// <summary>
/// Global unlock percentages from the public Steam Web API
/// (ISteamUserStats/GetGlobalAchievementPercentagesForApp, no key needed), cached for 24 hours
/// in cache\shared\percentages\&lt;appid&gt;.json. Falls back to a stale cache when offline.
/// </summary>
public sealed class GlobalPercentService(AppCache cache, HttpClient http)
{
    /// <summary>Achievement ID → percent of players (0-100). Null if unavailable.</summary>
    public Task<IReadOnlyDictionary<string, double>?> GetAsync(uint appId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(services)");
}

/// <summary>
/// Disk cache for images under cache\images. File name = SHA-256 of the URL. Images are kept
/// until the cache is cleared. Downloads are limited to a few at a time; concurrent requests
/// for the same URL share one download.
/// </summary>
public sealed class ImageCache(AppCache cache, HttpClient http)
{
    /// <summary>
    /// Tries each URL in order and returns the local file path of the first that's cached or
    /// downloads successfully, or null if none work. Failed URLs are remembered for the session.
    /// </summary>
    public Task<string?> GetAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(services)");
}

/// <summary>Builds Steam CDN URLs for game art and achievement icons.</summary>
public sealed class GameArt(SteamSession steam)
{
    /// <summary>
    /// Candidate URLs for a game's card image, best first: the store header image
    /// (460x215), then the localized/English small capsule and legacy logo from Steam's app data.
    /// </summary>
    public IReadOnlyList<string> GetCardImageUrls(uint appId) => throw new NotImplementedException("TODO(services)");

    /// <summary>URL of an achievement icon file named in the schema.</summary>
    public static string GetAchievementIconUrl(uint appId, string iconFile) =>
        $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{iconFile}";
}

/// <summary>
/// The user's owned games. Uses SAM's community game list (https://gib.me/sam/games.xml,
/// cached in cache\shared\games.xml, refreshed when older than 24 hours, stale copy used when
/// offline) filtered by Steam ownership. The resulting owned list is cached per account in
/// cache\users\&lt;steamid64&gt;\games.json so the library can render instantly on startup.
/// </summary>
public sealed class GameListService(AppCache cache, SteamSession steam, HttpClient http)
{
    /// <summary>The owned list saved by the last refresh, or null if there isn't one yet.</summary>
    public IReadOnlyList<GameEntry>? LoadCached() => throw new NotImplementedException("TODO(services)");

    /// <summary>
    /// Re-checks ownership against the (possibly re-downloaded) game list and saves the result.
    /// Runs the ownership checks off the UI thread. If the list can't be downloaded and there's
    /// no cached copy, throws <see cref="HttpRequestException"/>.
    /// </summary>
    public Task<IReadOnlyList<GameEntry>> RefreshAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(services)");

    /// <summary>
    /// For App IDs not in the community list: returns an entry if the user owns it (name from
    /// Steam's app data, type "normal") and adds it to the cached owned list, else null.
    /// </summary>
    public GameEntry? TryAddOwnedApp(uint appId) => throw new NotImplementedException("TODO(services)");
}

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
