using System.Reflection;

namespace SAM.App.Services;

public enum LibrarySort
{
    Name,
    AppId,
    RecentlyPlayed,
    Playtime,
    Progress,
}

/// <summary>
/// User preferences, persisted to settings.json. Several SAM processes share the file, so change
/// settings with <see cref="Update"/> (or <see cref="UpdateChanged"/>), which re-read the file
/// under a cross-process lock and write only the change; never write the whole in-memory copy.
/// </summary>
public sealed class AppSettings
{
    private static readonly string DefaultPath = Path.Combine(AppCache.RootDirectory, "settings.json");

    private static readonly PropertyInfo[] Properties = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>The settings.json this instance was loaded from (and saves to).</summary>
    private SharedJsonFile<AppSettings> _file = new(DefaultPath);

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
    /// UI language and culture for dates and numbers: "en-US" or "ka-GE" (Settings offers these;
    /// another culture formats dates and numbers with English text). Defaults to English rather
    /// than following Windows. Takes effect when SAM starts.
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

    /// <summary>
    /// Notify when the library notices an achievement unlocked by playing (not by SAM). Only
    /// applies while <see cref="ShowNotifications"/> is on.
    /// </summary>
    public bool NotifyOnUnlock { get; set; } = true;

    /// <summary>Check GitHub for a newer release on startup.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Release tag the user chose to skip, e.g. "v8.1.0".</summary>
    public string? SkippedUpdateVersion { get; set; }
    #endregion

    /// <summary>
    /// Loads settings.json. Defaults when it's missing; a corrupt file is set aside (see
    /// <see cref="JsonFile.TryRead{T}"/>); when it can't be read, defaults are used and the
    /// file is never written this session.
    /// </summary>
    public static AppSettings Load() => Load(DefaultPath);

    /// <summary>Loads the settings stored at <paramref name="path"/> (for tests).</summary>
    internal static AppSettings Load(string path)
    {
        var file = new SharedJsonFile<AppSettings>(path);
        var settings = file.Read() ?? new AppSettings();
        settings._file = file;
        return settings;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to this instance and to the latest settings.json (re-read
    /// under a cross-process lock, so settings other processes saved meanwhile are kept), then
    /// saves the file. If the file can't be read, only this instance changes.
    /// </summary>
    public void Update(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(this);
        this._file.Update(current =>
        {
            current ??= new AppSettings();
            change(current);
            return current;
        });
    }

    /// <summary>
    /// Applies <paramref name="change"/> to a copy of this instance and saves only the properties
    /// that it actually changed (see <see cref="Update"/>). For callers that set every setting
    /// they own each time, so they don't overwrite settings another process changed.
    /// </summary>
    public void UpdateChanged(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var desired = (AppSettings)this.MemberwiseClone();
        change(desired);
        var changed = Properties.Where(p => Equals(p.GetValue(this), p.GetValue(desired)) == false).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        this.Update(settings =>
        {
            foreach (var property in changed)
            {
                property.SetValue(settings, property.GetValue(desired));
            }
        });
    }

    /// <summary>
    /// The latest settings.json as another process may have saved it, or null if it's missing or
    /// can't be read. Doesn't change this instance.
    /// </summary>
    public AppSettings? ReadLatest() => JsonFile.Read<AppSettings>(this._file.Path);
}
