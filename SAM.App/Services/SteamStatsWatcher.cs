using System.Globalization;

namespace SAM.App.Services;

/// <summary>
/// Collects keys that changed and hands each one out once it has been quiet for
/// <see cref="Delay"/>, so a burst of changes for one key (Steam writes a file in several steps)
/// becomes a single update. The caller supplies the time, so it can be unit tested. Thread-safe.
/// </summary>
/// <typeparam name="TKey">The key, e.g. an App ID.</typeparam>
public sealed class KeyedDebouncer<TKey>(TimeSpan delay)
    where TKey : notnull
{
    private readonly Dictionary<TKey, DateTime> _due = [];

    /// <summary>How long a key must be quiet before it's due.</summary>
    public TimeSpan Delay { get; } = delay;

    /// <summary>True while any key is waiting.</summary>
    public bool HasPending
    {
        get
        {
            lock (this._due)
            {
                return this._due.Count > 0;
            }
        }
    }

    /// <summary>Records a change of <paramref name="key"/> at <paramref name="now"/> (restarting its quiet period).</summary>
    public void Touch(TKey key, DateTime now)
    {
        lock (this._due)
        {
            this._due[key] = now + this.Delay;
        }
    }

    /// <summary>Removes and returns the keys that have been quiet long enough at <paramref name="now"/>.</summary>
    public IReadOnlyList<TKey> TakeDue(DateTime now)
    {
        lock (this._due)
        {
            var due = this._due.Where(p => p.Value <= now).Select(p => p.Key).ToList();
            foreach (var key in due)
            {
                this._due.Remove(key);
            }

            return due;
        }
    }
}

/// <summary>
/// Watches Steam's appcache\stats folder for schema files (<c>UserGameStatsSchema_&lt;appid&gt;.bin</c>)
/// and the current user's stats files (<c>UserGameStats_&lt;accountid&gt;_&lt;appid&gt;.bin</c>), and
/// raises <see cref="Changed"/> (on a thread-pool thread) with the App IDs whose files changed,
/// about a second after Steam stopped writing them. If the folder doesn't exist yet, it checks
/// again every 30 seconds. Dispose to stop.
/// </summary>
public sealed class SteamStatsWatcher : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _directory;
    private readonly uint _accountId;
    private readonly KeyedDebouncer<uint> _debouncer = new(TimeSpan.FromSeconds(1));
    private readonly Timer _timer;
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public SteamStatsWatcher(string steamInstallPath, uint accountId)
    {
        this._directory = Path.Combine(steamInstallPath, "appcache", "stats");
        this._accountId = accountId;
        this._timer = new Timer(_ => this.OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        this.TryStartWatching();
    }

    /// <summary>
    /// The App IDs whose schema or stats files changed. An empty list means changes may have been
    /// missed (the watcher overflowed or was restarted): re-read everything.
    /// </summary>
    public event EventHandler<IReadOnlyList<uint>>? Changed;

    /// <summary>
    /// The App ID a file in appcache\stats belongs to: a schema file, or a stats file of
    /// <paramref name="accountId"/>. Null for any other file (other users' stats, temp files).
    /// </summary>
    public static uint? ParseFileName(string? fileName, uint accountId)
    {
        if (string.IsNullOrEmpty(fileName) ||
            fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) == false)
        {
            return null;
        }

        var stem = fileName[..^4];
        const string schemaPrefix = "UserGameStatsSchema_";
        var statsPrefix = "UserGameStats_" + accountId.ToString(CultureInfo.InvariantCulture) + "_";

        string digits;
        if (stem.StartsWith(schemaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            digits = stem[schemaPrefix.Length..];
        }
        else if (stem.StartsWith(statsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            digits = stem[statsPrefix.Length..];
        }
        else
        {
            return null;
        }

        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) && appId != 0
            ? appId
            : null;
    }

    public void Dispose()
    {
        lock (this._lock)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._watcher?.Dispose();
            this._watcher = null;
        }

        this._timer.Dispose();
    }

    private void TryStartWatching()
    {
        lock (this._lock)
        {
            if (this._disposed || this._watcher != null)
            {
                return;
            }

            try
            {
                if (Directory.Exists(this._directory) == false)
                {
                    this._timer.Change(RetryInterval, Timeout.InfiniteTimeSpan);
                    return;
                }

                var watcher = new FileSystemWatcher(this._directory, "*.bin")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += this.OnFileEvent;
                watcher.Changed += this.OnFileEvent;
                watcher.Renamed += this.OnFileEvent;
                watcher.Error += this.OnWatcherError;
                watcher.EnableRaisingEvents = true;
                this._watcher = watcher;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Diagnostics.Log($"Couldn't watch {this._directory}", e);
                this._timer.Change(RetryInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (ParseFileName(e.Name, this._accountId) is not { } appId)
        {
            return;
        }

        this._debouncer.Touch(appId, DateTime.UtcNow);
        lock (this._lock)
        {
            if (this._disposed == false)
            {
                this._timer.Change(TickInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // E.g. the buffer overflowed or the folder was deleted: start over and re-read everything.
        Diagnostics.Log($"Watching {this._directory} failed", e.GetException());
        lock (this._lock)
        {
            this._watcher?.Dispose();
            this._watcher = null;
        }

        this.Raise([]);
        this.TryStartWatching();
    }

    private void OnTimer()
    {
        bool watching;
        lock (this._lock)
        {
            if (this._disposed)
            {
                return;
            }

            watching = this._watcher != null;
        }

        if (watching == false)
        {
            this.TryStartWatching();
            lock (this._lock)
            {
                watching = this._watcher != null;
            }

            // The folder just appeared: files may already be in it.
            if (watching)
            {
                this.Raise([]);
            }

            return;
        }

        var due = this._debouncer.TakeDue(DateTime.UtcNow);
        if (due.Count > 0)
        {
            this.Raise(due);
        }

        if (this._debouncer.HasPending)
        {
            lock (this._lock)
            {
                if (this._disposed == false)
                {
                    this._timer.Change(TickInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    private void Raise(IReadOnlyList<uint> appIds)
    {
        try
        {
            this.Changed?.Invoke(this, appIds);
        }
        catch (Exception e)
        {
            Diagnostics.Log("A stats file change handler failed", e);
        }
    }
}
