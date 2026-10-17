using System.Globalization;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Snapshots of a game's achievements and stats, stored as JSON under
/// data\users\&lt;steamid64&gt;\backups\&lt;appid&gt;\&lt;id&gt;.json. Keeps the newest 50 per game.
/// Thread-safe.
/// </summary>
public sealed class BackupService
{
    /// <summary>How many snapshots are kept per game; older ones are deleted on save.</summary>
    internal const int MaxSnapshotsPerGame = 50;

    private readonly string _directory;
    private readonly object _lock = new();

    /// <summary>Creates the service for the account that <paramref name="cache"/> belongs to.</summary>
    public BackupService(AppCache cache)
        : this(Path.Combine(cache.UserDataDirectory, "backups"))
    {
    }

    /// <summary>Creates the service storing snapshots under <paramref name="directory"/> (for tests).</summary>
    internal BackupService(string directory)
    {
        this._directory = directory;
    }

    /// <summary>Saves <paramref name="snapshot"/> (assigning Id and CreatedAt if unset) and prunes old ones. Returns it.</summary>
    public GameSnapshot Save(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (this._lock)
        {
            if (snapshot.CreatedAt == default)
            {
                snapshot.CreatedAt = DateTimeOffset.Now;
            }

            var gameDirectory = this.GetGameDirectory(snapshot.AppId);
            if (string.IsNullOrEmpty(snapshot.Id))
            {
                snapshot.Id = NewId(snapshot.CreatedAt, gameDirectory);
            }
            else if (IsValidId(snapshot.Id) == false)
            {
                throw new ArgumentException($"Invalid snapshot ID '{snapshot.Id}'.", nameof(snapshot));
            }

            JsonFile.Write(Path.Combine(gameDirectory, snapshot.Id + ".json"), snapshot);
            this.Prune(snapshot.AppId);
            return snapshot;
        }
    }

    /// <summary>Snapshots for a game, newest first. Empty if none. Unreadable files are logged and skipped.</summary>
    public IReadOnlyList<GameSnapshot> List(uint appId)
    {
        lock (this._lock)
        {
            return this.ListCore(appId).Select(e => e.Snapshot).ToList();
        }
    }

    /// <summary>Deletes one snapshot. Does nothing if it doesn't exist.</summary>
    public void Delete(uint appId, string snapshotId)
    {
        if (IsValidId(snapshotId) == false)
        {
            return;
        }

        lock (this._lock)
        {
            TryDelete(Path.Combine(this.GetGameDirectory(appId), snapshotId + ".json"));
        }
    }

    private string GetGameDirectory(uint appId) =>
        Path.Combine(this._directory, appId.ToString(CultureInfo.InvariantCulture));

    private List<(string Path, GameSnapshot Snapshot)> ListCore(uint appId)
    {
        var gameDirectory = this.GetGameDirectory(appId);
        if (Directory.Exists(gameDirectory) == false)
        {
            return [];
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(gameDirectory, "*.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't list backups in {gameDirectory}", e);
            return [];
        }

        var result = new List<(string Path, GameSnapshot Snapshot)>(files.Length);
        foreach (var file in files)
        {
            var snapshot = JsonFile.Read<GameSnapshot>(file);
            if (snapshot == null || snapshot.AppId != appId)
            {
                Diagnostics.Log($"Skipping unreadable backup {file}");
                continue;
            }

            // The file name is the source of truth for the ID (used by Delete).
            snapshot.Id = Path.GetFileNameWithoutExtension(file);
            result.Add((file, snapshot));
        }

        result.Sort((a, b) =>
        {
            var byTime = b.Snapshot.CreatedAt.CompareTo(a.Snapshot.CreatedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(b.Snapshot.Id, a.Snapshot.Id);
        });
        return result;
    }

    private void Prune(uint appId)
    {
        foreach (var (path, _) in this.ListCore(appId).Skip(MaxSnapshotsPerGame))
        {
            TryDelete(path);
        }
    }

    private static string NewId(DateTimeOffset createdAt, string gameDirectory)
    {
        var stamp = createdAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        while (true)
        {
            var id = stamp + "-" + Random.Shared.Next(0x10000).ToString("x4", CultureInfo.InvariantCulture);
            if (File.Exists(Path.Combine(gameDirectory, id + ".json")) == false)
            {
                return id;
            }
        }
    }

    private static bool IsValidId(string? id) =>
        string.IsNullOrWhiteSpace(id) == false &&
        id != "." && id != ".." &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't delete {path}", e);
        }
    }
}
