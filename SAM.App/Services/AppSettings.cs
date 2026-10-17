namespace SAM.App.Services;

public enum LibrarySort
{
    Name,
    AppId,
    RecentlyPlayed,
    Playtime,
    Progress,
}

/// <summary>User preferences, persisted to settings.json. Call <see cref="Save"/> after changing.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppCache.RootDirectory, "settings.json");

    /// <summary>Keep other Steam accounts' cached data when a different account logs in.</summary>
    public bool KeepOtherAccountsCache { get; set; }

    /// <summary>Defaults to most recently played first; never-played games sort last.</summary>
    public LibrarySort LibrarySort { get; set; } = LibrarySort.RecentlyPlayed;

    public bool SortDescending { get; set; } = true;

    /// <summary>Show favorites above everything else regardless of sort.</summary>
    public bool PinFavorites { get; set; } = true;

    public bool ShowGames { get; set; } = true;

    public bool ShowDemos { get; set; }

    public bool ShowMods { get; set; }

    public bool ShowJunk { get; set; }

    public bool FavoritesOnly { get; set; }

    /// <summary>Index into the library's completion filter (All, Has achievements, Not started, In progress, Perfect).</summary>
    public int CompletionFilter { get; set; }

    /// <summary>Index into the library's last-played filter (Any time … Custom range).</summary>
    public int LastPlayedFilter { get; set; }

    public DateTimeOffset? LastPlayedFrom { get; set; }

    public DateTimeOffset? LastPlayedTo { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Culture for dates and numbers (and, later, UI text), e.g. "en-US". The UI is English-only
    /// for now, so this defaults to English rather than following the Windows region format.
    /// </summary>
    public string Language { get; set; } = "en-US";

    #region Library view
    /// <summary>"Grid" (cards) or "List" (compact table).</summary>
    public string LibraryViewMode { get; set; } = "Grid";

    public bool InstalledOnly { get; set; }

    /// <summary>Show games the user has hidden.</summary>
    public bool ShowHidden { get; set; }

    /// <summary>
    /// Selected collection filter: null for all games, "steam:&lt;id&gt;" for a Steam collection,
    /// "custom:&lt;id&gt;" for a SAM collection.
    /// </summary>
    public string? CollectionFilter { get; set; }

    /// <summary>Selected smart list: null/"" for none, or one of "almost-perfect", "never-started", "rarest-owned".</summary>
    public string? SmartList { get; set; }
    #endregion

    #region Game window
    /// <summary>Blur hidden achievements' descriptions until clicked.</summary>
    public bool SpoilerProtection { get; set; } = true;

    /// <summary>Snapshot a game's achievements and stats before every commit.</summary>
    public bool BackupBeforeCommit { get; set; } = true;
    #endregion

    #region App
    /// <summary>Closing the library window hides it to the system tray instead of exiting.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Show Windows notifications (e.g. when a background task unlocks achievements).</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Check GitHub for a newer release on startup.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Release tag the user chose to skip, e.g. "v8.1.0".</summary>
    public string? SkippedUpdateVersion { get; set; }
    #endregion

    public static AppSettings Load() => JsonFile.Read<AppSettings>(FilePath) ?? new AppSettings();

    public void Save() => JsonFile.Write(FilePath, this);
}
