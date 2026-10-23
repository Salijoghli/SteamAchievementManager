namespace SAM.App.Services;

/// <summary>
/// Per-account favorite games, stored in data\users\&lt;steamid64&gt;\favorites.json. Each change
/// re-reads the file under a cross-process lock, so favorites set in other SAM processes are kept.
/// Thread-safe.
/// </summary>
public sealed class FavoritesStore
{
    private readonly SharedJsonFile<List<uint>> _file;
    private readonly object _lock = new();
    private HashSet<uint> _ids;

    /// <summary>Creates the store for the account that <paramref name="cache"/> belongs to.</summary>
    public FavoritesStore(AppCache cache)
        : this(Path.Combine(cache.UserDataDirectory, "favorites.json"))
    {
    }

    /// <summary>Creates the store backed by <paramref name="path"/> (for tests).</summary>
    internal FavoritesStore(string path)
    {
        this._file = new SharedJsonFile<List<uint>>(path);
        this._ids = new HashSet<uint>(this._file.Read() ?? []);
    }

    public event EventHandler<uint>? Changed;

    public bool Contains(uint appId)
    {
        lock (this._lock)
        {
            return this._ids.Contains(appId);
        }
    }

    public void Set(uint appId, bool favorite)
    {
        bool changed;
        lock (this._lock)
        {
            // Raise Changed when this process's view of the game changes.
            changed = this._ids.Contains(appId) != favorite;

            HashSet<uint>? latest = null;
            var saved = this._file.Update(current =>
            {
                latest = new HashSet<uint>(current ?? []);
                var fileChanged = favorite ? latest.Add(appId) : latest.Remove(appId);
                return fileChanged ? latest.Order().ToList() : null;
            });

            if (saved && latest != null)
            {
                // Also picks up favorites other processes saved since this one loaded.
                this._ids = latest;
            }
            else if (favorite)
            {
                this._ids.Add(appId);
            }
            else
            {
                this._ids.Remove(appId);
            }
        }

        if (changed)
        {
            this.Changed?.Invoke(this, appId);
        }
    }
}
