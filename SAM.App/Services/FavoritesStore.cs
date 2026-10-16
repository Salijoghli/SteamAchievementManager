namespace SAM.App.Services;

/// <summary>Per-account favorite games, stored in data\users\&lt;steamid64&gt;\favorites.json.</summary>
public sealed class FavoritesStore
{
    private readonly string _path;
    private readonly HashSet<uint> _ids;

    public FavoritesStore(AppCache cache)
    {
        this._path = Path.Combine(cache.UserDataDirectory, "favorites.json");
        this._ids = new HashSet<uint>(JsonFile.Read<List<uint>>(this._path) ?? []);
    }

    public event EventHandler<uint>? Changed;

    public bool Contains(uint appId) => this._ids.Contains(appId);

    public void Set(uint appId, bool favorite)
    {
        var changed = favorite ? this._ids.Add(appId) : this._ids.Remove(appId);
        if (changed)
        {
            JsonFile.Write(this._path, this._ids.Order().ToList());
            this.Changed?.Invoke(this, appId);
        }
    }
}
