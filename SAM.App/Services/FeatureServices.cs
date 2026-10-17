using SAM.App.Models;

namespace SAM.App.Services;

// Contracts for the second batch of features. UI code depends only on these signatures;
// bodies marked TODO are implemented separately (each class may move to its own file).
// User data (backups, history, notes, collections) lives under AppCache.UserDataDirectory,
// which "Clear cache" and account switches never delete.

/// <summary>
/// Snapshots of a game's achievements and stats, stored as JSON under
/// data\users\&lt;steamid64&gt;\backups\&lt;appid&gt;\&lt;id&gt;.json. Keeps the newest 50 per game.
/// </summary>
public sealed class BackupService(AppCache cache)
{
    /// <summary>Saves <paramref name="snapshot"/> (assigning Id and CreatedAt if unset) and prunes old ones. Returns it.</summary>
    public GameSnapshot Save(GameSnapshot snapshot) => throw new NotImplementedException("TODO(data-a)");

    /// <summary>Snapshots for a game, newest first. Empty if none.</summary>
    public IReadOnlyList<GameSnapshot> List(uint appId) => throw new NotImplementedException("TODO(data-a)");

    public void Delete(uint appId, string snapshotId) => throw new NotImplementedException("TODO(data-a)");
}

/// <summary>Append-only log of commits, in data\users\&lt;steamid64&gt;\history.jsonl.</summary>
public sealed class HistoryService(AppCache cache)
{
    /// <summary>Raised (on the calling thread) after a record is appended.</summary>
    public event EventHandler<CommitRecord>? Appended;

    public void Append(CommitRecord record) => throw new NotImplementedException("TODO(data-a)");

    /// <summary>Newest first, at most <paramref name="max"/> records. Tolerates corrupt lines.</summary>
    public IReadOnlyList<CommitRecord> Load(int max = 1000) => throw new NotImplementedException("TODO(data-a)");
}

/// <summary>Personal notes on achievements, in data\users\&lt;steamid64&gt;\notes.json.</summary>
public sealed class NotesStore(AppCache cache)
{
    /// <summary>The note, or null if none.</summary>
    public string? Get(uint appId, string achievementId) => throw new NotImplementedException("TODO(data-a)");

    /// <summary>Saves the note; null or whitespace deletes it.</summary>
    public void Set(uint appId, string achievementId, string? note) => throw new NotImplementedException("TODO(data-a)");

    /// <summary>Achievement IDs with notes for a game (for showing a note indicator).</summary>
    public IReadOnlySet<string> GetIds(uint appId) => throw new NotImplementedException("TODO(data-a)");
}

/// <summary>
/// Game collections: the user's Steam collections (read-only, from Steam's
/// userdata\&lt;accountid&gt;\config\cloudstorage\cloud-storage-namespace-1.json), SAM's own
/// collections and the hidden-games list (data\users\&lt;steamid64&gt;\collections.json).
/// </summary>
public sealed class CollectionsService(SteamSession steam, AppCache cache)
{
    public event EventHandler? Changed;

    /// <summary>Steam collections with explicit app lists (dynamic/filter collections are skipped). Empty if unreadable.</summary>
    public IReadOnlyList<GameCollection> LoadSteamCollections() => throw new NotImplementedException("TODO(data-a)");

    public IReadOnlyList<GameCollection> GetCustomCollections() => throw new NotImplementedException("TODO(data-a)");

    public GameCollection CreateCustomCollection(string name) => throw new NotImplementedException("TODO(data-a)");

    public void RenameCustomCollection(string id, string name) => throw new NotImplementedException("TODO(data-a)");

    public void DeleteCustomCollection(string id) => throw new NotImplementedException("TODO(data-a)");

    public void SetInCustomCollection(string id, uint appId, bool included) => throw new NotImplementedException("TODO(data-a)");

    public IReadOnlySet<uint> HiddenAppIds => throw new NotImplementedException("TODO(data-a)");

    public void SetHidden(uint appId, bool hidden) => throw new NotImplementedException("TODO(data-a)");
}

/// <summary>Writes CSV files (UTF-8 with BOM so Excel opens them correctly, RFC 4180 quoting, invariant culture).</summary>
public static class CsvExport
{
    public static Task WriteLibraryAsync(string path, IEnumerable<LibraryExportRow> rows) =>
        throw new NotImplementedException("TODO(data-a)");

    public static Task WriteAchievementsAsync(string path, IEnumerable<AchievementExportRow> rows) =>
        throw new NotImplementedException("TODO(data-a)");
}

/// <summary>
/// Installed games: parses steamapps\libraryfolders.vdf for every Steam library folder, then
/// each folder's appmanifest_&lt;appid&gt;.acf. Thread-safe; safe to call off the UI thread.
/// </summary>
public sealed class InstalledGamesService(SteamSession steam)
{
    public IReadOnlyDictionary<uint, InstalledGame> Load() => throw new NotImplementedException("TODO(data-b)");
}

/// <summary>
/// Every unlocked achievement with its time, across games, from Steam's local stats cache
/// (UserGameStats files: an achievement counts only if its bit is set in the block's data,
/// since re-locked achievements keep their old AchievementTimes entry) plus each game's schema
/// for names. Safe to call off the UI thread.
/// </summary>
public sealed class TimelineService(SteamSession steam, SchemaService schemas)
{
    /// <summary>Newest first.</summary>
    public IReadOnlyList<UnlockEvent> Load(IEnumerable<GameEntry> games) => throw new NotImplementedException("TODO(data-b)");

    /// <summary>
    /// Every schema achievement for each game with its unlocked state and time (for CSV export).
    /// GlobalPercent is left null; callers fill it if they have it.
    /// </summary>
    public IReadOnlyList<AchievementExportRow> LoadAllAchievements(IEnumerable<GameEntry> games) =>
        throw new NotImplementedException("TODO(data-b)");
}

/// <summary>
/// Checks GitHub releases (Salijoghli/SteamAchievementManager) for a version newer than the
/// running one. Returns null when up to date, offline, or skipped by the user.
/// </summary>
public sealed class UpdateService(HttpClient http, AppSettings settings)
{
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(infra)");
}

/// <summary>Windows toast notifications. No-ops when AppSettings.ShowNotifications is off or unavailable.</summary>
public sealed class NotificationService(AppSettings settings)
{
    public void Show(string title, string message) => throw new NotImplementedException("TODO(infra)");
}
