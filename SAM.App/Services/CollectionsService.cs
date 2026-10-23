using System.Globalization;
using System.Text.Json;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Game collections: the user's Steam collections (read-only, from Steam's
/// userdata\&lt;accountid&gt;\config\cloudstorage\cloud-storage-namespace-1.json), SAM's own
/// collections and the hidden-games list (data\users\&lt;steamid64&gt;\collections.json). Each change
/// re-reads collections.json under a cross-process lock, so changes made in other SAM processes
/// are kept. Thread-safe.
/// </summary>
public sealed class CollectionsService
{
    private const string SteamCollectionPrefix = "user-collections.";

    private readonly Func<string?> _steamCollectionsPath;
    private readonly SharedJsonFile<StoredCollections> _file;
    private readonly object _lock = new();
    private StoredCollections? _data;

    /// <summary>Creates the service for the signed-in account.</summary>
    public CollectionsService(SteamSession steam, AppCache cache)
        : this(
            () => GetSteamCollectionsPath(steam),
            Path.Combine(cache.UserDataDirectory, "collections.json"))
    {
    }

    /// <summary>Creates the service from explicit file paths (for tests).</summary>
    internal CollectionsService(Func<string?> steamCollectionsPath, string path)
    {
        this._steamCollectionsPath = steamCollectionsPath;
        this._file = new SharedJsonFile<StoredCollections>(path);
    }

    /// <summary>Raised (on the calling thread) after any change to SAM's collections or the hidden list.</summary>
    public event EventHandler? Changed;

    /// <summary>Hidden games (a snapshot; changes after the call aren't reflected).</summary>
    public IReadOnlySet<uint> HiddenAppIds
    {
        get
        {
            lock (this._lock)
            {
                return new HashSet<uint>(this.Data.Hidden);
            }
        }
    }

    /// <summary>Steam collections with explicit app lists (dynamic/filter collections are skipped). Empty if unreadable.</summary>
    public IReadOnlyList<GameCollection> LoadSteamCollections()
    {
        string? path;
        try
        {
            path = this._steamCollectionsPath();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't locate Steam collections", e);
            return [];
        }

        return path == null ? [] : ParseSteamCollections(path);
    }

    /// <summary>SAM's own collections, sorted by name.</summary>
    public IReadOnlyList<GameCollection> GetCustomCollections()
    {
        lock (this._lock)
        {
            return this.Data.Collections
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new GameCollection(c.Id, c.Name, false, new HashSet<uint>(c.AppIds)))
                .ToList();
        }
    }

    /// <summary>Creates an empty collection named <paramref name="name"/> and returns it.</summary>
    public GameCollection CreateCustomCollection(string name)
    {
        name = ValidateName(name);
        GameCollection result;
        lock (this._lock)
        {
            string? id = null;
            this.Mutate(data =>
            {
                // A fresh ID per attempt: the file may have changed since the last one.
                do
                {
                    id = Guid.NewGuid().ToString("N")[..8];
                }
                while (data.Collections.Any(c => c.Id == id));

                data.Collections.Add(new StoredCollection { Id = id, Name = name });
                return true;
            });
            result = new GameCollection(id!, name, false, new HashSet<uint>());
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Renames a collection. Does nothing if <paramref name="id"/> is unknown.</summary>
    public void RenameCustomCollection(string id, string name)
    {
        name = ValidateName(name);
        lock (this._lock)
        {
            var changed = this.Mutate(data =>
            {
                var collection = Find(data, id);
                if (collection == null || collection.Name == name)
                {
                    return false;
                }

                collection.Name = name;
                return true;
            });
            if (changed == false)
            {
                return;
            }
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes a collection. Does nothing if <paramref name="id"/> is unknown.</summary>
    public void DeleteCustomCollection(string id)
    {
        lock (this._lock)
        {
            if (this.Mutate(data => data.Collections.RemoveAll(c => c.Id == id) > 0) == false)
            {
                return;
            }
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds or removes a game from a collection. Does nothing if <paramref name="id"/> is unknown.</summary>
    public void SetInCustomCollection(string id, uint appId, bool included)
    {
        lock (this._lock)
        {
            var changed = this.Mutate(data =>
            {
                var collection = Find(data, id);
                if (collection == null)
                {
                    return false;
                }

                var added = included && collection.AppIds.Contains(appId) == false;
                if (added)
                {
                    collection.AppIds.Add(appId);
                    collection.AppIds.Sort();
                }

                return added || (included == false && collection.AppIds.Remove(appId));
            });
            if (changed == false)
            {
                return;
            }
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hides or unhides a game.</summary>
    public void SetHidden(uint appId, bool hidden)
    {
        lock (this._lock)
        {
            var changed = this.Mutate(data =>
            {
                var list = data.Hidden;
                var added = hidden && list.Contains(appId) == false;
                if (added)
                {
                    list.Add(appId);
                    list.Sort();
                }

                return added || (hidden == false && list.Remove(appId));
            });
            if (changed == false)
            {
                return;
            }
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads Steam's cloud-storage-namespace-1.json (plus pending local edits in the matching
    /// .modified.json, if any) and returns the collections that have explicit app lists.
    /// Favorites first, then the user's collections by name, then Hidden. Empty if unreadable.
    /// </summary>
    internal static IReadOnlyList<GameCollection> ParseSteamCollections(string path)
    {
        var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (ReadEntries(path, entries) == false)
        {
            return [];
        }

        var modified = Path.ChangeExtension(path, ".modified.json");
        ReadEntries(modified, entries);

        var result = new List<GameCollection>();
        foreach (var (key, entry) in entries)
        {
            if (key.StartsWith(SteamCollectionPrefix, StringComparison.Ordinal) == false)
            {
                continue;
            }

            try
            {
                var collection = ParseSteamCollection(key, entry);
                if (collection != null)
                {
                    result.Add(collection);
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                Diagnostics.Log($"Skipping unreadable Steam collection {key}", e);
            }
        }

        static int Rank(GameCollection c) => c.Id switch { "favorite" => 0, "hidden" => 2, _ => 1 };
        return result
            .OrderBy(Rank)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static GameCollection? ParseSteamCollection(string key, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            (entry.TryGetProperty("is_deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True) ||
            entry.TryGetProperty("value", out var valueElement) == false ||
            valueElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        using var document = JsonDocument.Parse(valueElement.GetString()!);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = value.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : key[SteamCollectionPrefix.Length..];
        var isBuiltIn = id is "favorite" or "hidden";

        var hasFilter = value.TryGetProperty("filterSpec", out var filter) &&
            filter.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        var hasAdded = value.TryGetProperty("added", out var added) && added.ValueKind == JsonValueKind.Array;
        if (isBuiltIn == false && (hasFilter || hasAdded == false))
        {
            // Dynamic collection: membership is computed by Steam from the filter.
            return null;
        }

        var appIds = hasAdded ? ReadAppIds(added) : [];
        if (value.TryGetProperty("removed", out var removed) && removed.ValueKind == JsonValueKind.Array)
        {
            appIds.ExceptWith(ReadAppIds(removed));
        }

        var name = id switch
        {
            "favorite" => Strings.Get("Collections_SteamFavorites"),
            "hidden" => Strings.Get("Collections_SteamHidden"),
            _ => value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String &&
                 string.IsNullOrWhiteSpace(n.GetString()) == false
                ? n.GetString()!
                : id,
        };

        return new GameCollection(id, name, true, appIds);
    }

    private static HashSet<uint> ReadAppIds(JsonElement array)
    {
        var ids = new HashSet<uint>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out var id))
            {
                ids.Add(id);
            }
            else if (item.ValueKind == JsonValueKind.String &&
                     uint.TryParse(item.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Adds the [key, entry] pairs of a cloud storage file to <paramref name="entries"/>, later
    /// files overriding earlier ones. Also accepts an object keyed by entry key. False if unreadable.
    /// </summary>
    private static bool ReadEntries(string path, Dictionary<string, JsonElement> entries)
    {
        try
        {
            if (File.Exists(path) == false)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var pair in root.EnumerateArray())
                {
                    if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2 &&
                        pair[0].ValueKind == JsonValueKind.String)
                    {
                        entries[pair[0].GetString()!] = pair[1].Clone();
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    entries[property.Name] = property.Value.Clone();
                }
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Diagnostics.Log($"Couldn't read Steam collections from {path}", e);
            return false;
        }
    }

    private static string? GetSteamCollectionsPath(SteamSession steam)
    {
        if (string.IsNullOrEmpty(steam.SteamInstallPath))
        {
            return null;
        }

        return Path.Combine(
            steam.SteamInstallPath,
            "userdata",
            steam.AccountId.ToString(CultureInfo.InvariantCulture),
            "config",
            "cloudstorage",
            "cloud-storage-namespace-1.json");
    }

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("Collection name can't be empty.", nameof(name));
        }

        return trimmed;
    }

    /// <summary>The stored data, loaded from disk on first access. Call under the lock.</summary>
    private StoredCollections Data => this._data ??= Normalize(this._file.Read());

    private static StoredCollection? Find(StoredCollections data, string id) => data.Collections.FirstOrDefault(c => c.Id == id);

    /// <summary>Cleans up loaded data: drops entries without IDs and duplicates, sorts app IDs.</summary>
    private static StoredCollections Normalize(StoredCollections? loaded)
    {
        loaded ??= new StoredCollections();
        loaded.Collections = (loaded.Collections ?? [])
            .Where(c => c != null && string.IsNullOrEmpty(c.Id) == false)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();
        foreach (var c in loaded.Collections)
        {
            c.Name ??= c.Id;
            c.AppIds = (c.AppIds ?? []).Distinct().Order().ToList();
        }

        loaded.Hidden = (loaded.Hidden ?? []).Distinct().Order().ToList();
        return loaded;
    }

    /// <summary>
    /// Applies <paramref name="change"/> (which returns true if it changed anything) to the latest
    /// collections.json under the cross-process lock and saves it; when the file can't be saved
    /// to, applies it in memory only. Returns false only when nothing changed, in memory or on
    /// disk (the latest file replaces the in-memory copy). Call under the lock.
    /// </summary>
    private bool Mutate(Func<StoredCollections, bool> change)
    {
        StoredCollections? latest = null;
        var saved = this._file.Update(current =>
        {
            latest = Normalize(current);
            return change(latest) ? latest : null;
        });

        if (saved && latest != null)
        {
            // Also picks up changes other processes saved since this one loaded, so report a
            // change even when this one was already on disk: listeners need to refresh either way.
            this._data = latest;
            return true;
        }

        return change(this.Data);
    }

    /// <summary>On-disk format of collections.json.</summary>
    private sealed class StoredCollections
    {
        public List<StoredCollection> Collections { get; set; } = [];

        public List<uint> Hidden { get; set; } = [];
    }

    /// <summary>One SAM collection in collections.json.</summary>
    private sealed class StoredCollection
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public List<uint> AppIds { get; set; } = [];
    }
}
