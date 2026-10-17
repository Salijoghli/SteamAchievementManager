using SAM.App.Models;

namespace SAM.App.Services;

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
