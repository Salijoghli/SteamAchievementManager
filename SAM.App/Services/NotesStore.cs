namespace SAM.App.Services;

/// <summary>
/// Personal notes on achievements, in data\users\&lt;steamid64&gt;\notes.json
/// (app ID → achievement ID → note). Loaded on first use; each change re-reads the file under a
/// cross-process lock so notes saved by other SAM processes are kept. Thread-safe.
/// </summary>
public sealed class NotesStore
{
    private readonly SharedJsonFile<Dictionary<uint, Dictionary<string, string>>> _file;
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
        this._file = new SharedJsonFile<Dictionary<uint, Dictionary<string, string>>>(path);
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
            Dictionary<uint, Dictionary<string, string>>? latest = null;
            var saved = this._file.Update(current =>
            {
                latest = Normalize(current);
                return Apply(latest, appId, achievementId, note) ? latest : null;
            });

            if (saved && latest != null)
            {
                // Also picks up notes other processes saved since this one loaded.
                this._notes = latest;
            }
            else
            {
                Apply(this.Notes, appId, achievementId, note);
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
    private Dictionary<uint, Dictionary<string, string>> Notes => this._notes ??= Normalize(this._file.Read());

    /// <summary>Sets or deletes one note in <paramref name="notes"/>. True if anything changed.</summary>
    private static bool Apply(Dictionary<uint, Dictionary<string, string>> notes, uint appId, string achievementId, string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            var removed = notes.TryGetValue(appId, out var game) && game.Remove(achievementId);
            if (removed && game!.Count == 0)
            {
                notes.Remove(appId);
            }

            return removed;
        }

        if (notes.TryGetValue(appId, out var existing) == false)
        {
            notes[appId] = existing = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var changed = existing.TryGetValue(achievementId, out var old) == false || old != note;
        existing[achievementId] = note;
        return changed;
    }

    /// <summary>A clean copy of the file's content: no empty notes or games, ordinal keys.</summary>
    private static Dictionary<uint, Dictionary<string, string>> Normalize(Dictionary<uint, Dictionary<string, string>>? loaded)
    {
        var result = new Dictionary<uint, Dictionary<string, string>>();
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
                result[appId] = copy;
            }
        }

        return result;
    }
}
