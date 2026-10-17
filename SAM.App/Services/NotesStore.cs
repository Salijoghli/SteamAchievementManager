namespace SAM.App.Services;

/// <summary>
/// Personal notes on achievements, in data\users\&lt;steamid64&gt;\notes.json
/// (app ID → achievement ID → note). Loaded on first use; thread-safe.
/// </summary>
public sealed class NotesStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<uint, Dictionary<string, string>>? _notes;

    /// <summary>Creates the store for the account that <paramref name="cache"/> belongs to.</summary>
    public NotesStore(AppCache cache)
        : this(Path.Combine(cache.UserDataDirectory, "notes.json"))
    {
    }

    /// <summary>Creates the store backed by <paramref name="path"/> (for tests).</summary>
    internal NotesStore(string path)
    {
        this._path = path;
    }

    /// <summary>The note, or null if none.</summary>
    public string? Get(uint appId, string achievementId)
    {
        lock (this._lock)
        {
            return this.Notes.TryGetValue(appId, out var game) && game.TryGetValue(achievementId, out var note)
                ? note
                : null;
        }
    }

    /// <summary>Saves the note; null or whitespace deletes it.</summary>
    public void Set(uint appId, string achievementId, string? note)
    {
        ArgumentException.ThrowIfNullOrEmpty(achievementId);

        lock (this._lock)
        {
            var notes = this.Notes;
            bool changed;
            if (string.IsNullOrWhiteSpace(note))
            {
                changed = notes.TryGetValue(appId, out var game) && game.Remove(achievementId);
                if (changed && game!.Count == 0)
                {
                    notes.Remove(appId);
                }
            }
            else
            {
                if (notes.TryGetValue(appId, out var game) == false)
                {
                    notes[appId] = game = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                changed = game.TryGetValue(achievementId, out var old) == false || old != note;
                game[achievementId] = note;
            }

            if (changed)
            {
                JsonFile.Write(this._path, notes);
            }
        }
    }

    /// <summary>Achievement IDs with notes for a game (for showing a note indicator).</summary>
    public IReadOnlySet<string> GetIds(uint appId)
    {
        lock (this._lock)
        {
            return this.Notes.TryGetValue(appId, out var game)
                ? new HashSet<string>(game.Keys, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>The notes, loaded from disk on first access. Call under the lock.</summary>
    private Dictionary<uint, Dictionary<string, string>> Notes
    {
        get
        {
            if (this._notes == null)
            {
                var loaded = JsonFile.Read<Dictionary<uint, Dictionary<string, string>>>(this._path);
                this._notes = [];
                foreach (var (appId, game) in loaded ?? [])
                {
                    if (game == null)
                    {
                        continue;
                    }

                    var copy = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (id, note) in game)
                    {
                        if (string.IsNullOrWhiteSpace(note) == false)
                        {
                            copy[id] = note;
                        }
                    }

                    if (copy.Count > 0)
                    {
                        this._notes[appId] = copy;
                    }
                }
            }

            return this._notes;
        }
    }
}
