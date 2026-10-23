namespace SAM.App.Services;

/// <summary>
/// A JSON file that several SAM processes change (favorites, notes, collections, settings).
/// Every change is a read-modify-write under a cross-process lock (<see cref="JsonFile.Lock"/>),
/// so one process's change never overwrites another's. A file that isn't valid JSON is set aside
/// and replaced; a file that can't be read (e.g. locked) makes this instance read-only for the
/// rest of the session, so it's never overwritten with partial data. Not thread-safe; callers
/// serialize access.
/// </summary>
/// <typeparam name="T">The file's content.</typeparam>
public sealed class SharedJsonFile<T>
    where T : class
{
    /// <summary>Creates the wrapper for <paramref name="path"/>.</summary>
    public SharedJsonFile(string path)
    {
        this.Path = path;
    }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>True once the file couldn't be read: changes then stay in memory and nothing is written.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>
    /// Reads the file under the lock. Null when it's missing, was corrupt (set aside), or
    /// couldn't be read (then <see cref="IsReadOnly"/> becomes true).
    /// </summary>
    public T? Read()
    {
        try
        {
            using (JsonFile.Lock(this.Path))
            {
                return this.ReadCore(out _);
            }
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Diagnostics.Log($"Couldn't lock {this.Path} for reading", e);
            return JsonFile.Read<T>(this.Path);
        }
    }

    /// <summary>
    /// Re-reads the file, passes its content (null when missing or corrupt) to
    /// <paramref name="change"/> and writes what that returns (nothing when it returns null), all
    /// under the lock. Returns false, without calling <paramref name="change"/>, when the file is
    /// read-only or the lock couldn't be taken; the caller should then apply the change in memory only.
    /// </summary>
    public bool Update(Func<T?, T?> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (this.IsReadOnly)
        {
            Diagnostics.Log($"Not saving {this.Path}: it couldn't be read earlier in this session");
            return false;
        }

        try
        {
            using (JsonFile.Lock(this.Path))
            {
                var current = this.ReadCore(out var readable);
                if (readable == false)
                {
                    return false;
                }

                var updated = change(current);
                if (updated != null)
                {
                    JsonFile.Write(this.Path, updated);
                }

                return true;
            }
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Diagnostics.Log($"Couldn't lock {this.Path} for saving", e);
            return false;
        }
    }

    /// <summary>Reads the file; call under the lock. <paramref name="readable"/> is false if it must not be overwritten.</summary>
    private T? ReadCore(out bool readable)
    {
        var status = JsonFile.TryRead<T>(this.Path, out var value);
        readable = status != JsonReadStatus.Unavailable;
        if (readable == false)
        {
            this.IsReadOnly = true;
            Diagnostics.Log($"{this.Path} couldn't be read; SAM won't save changes to it until it restarts");
        }

        return value;
    }
}
