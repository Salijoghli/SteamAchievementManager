using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAM.App.Services;

/// <summary>What <see cref="JsonFile.TryRead{T}"/> found.</summary>
public enum JsonReadStatus
{
    /// <summary>The file was read and parsed.</summary>
    Ok,

    /// <summary>There's no file.</summary>
    Missing,

    /// <summary>The file wasn't valid JSON; it was renamed to <c>&lt;name&gt;.corrupt-&lt;timestamp&gt;</c> and kept.</summary>
    Corrupt,

    /// <summary>The file exists but couldn't be opened (e.g. locked), even after retrying. It must not be overwritten.</summary>
    Unavailable,
}

/// <summary>
/// Helpers for reading and writing JSON files that several SAM processes (the library, game
/// windows, Autopilot workers, command-line runs) may use at the same time. Writes go to a
/// uniquely named temporary file that is then moved into place, so readers never see a partial
/// file; reads share the file for writing and deletion so they don't block those moves.
/// </summary>
public static class JsonFile
{
    private const int ReadAttempts = 3;
    private const int MoveAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Reads <paramref name="path"/>. Returns default when the file is missing or can't be read or parsed (logged).</summary>
    public static T? Read<T>(string path)
    {
        try
        {
            if (File.Exists(path) == false)
            {
                return default;
            }

            return Deserialize<T>(ReadAllBytesWithRetry(path));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't read {path}", e);
            return default;
        }
    }

    /// <summary>
    /// Reads <paramref name="path"/> and says what was found, so callers can tell a missing file
    /// from one they must not overwrite. A file that isn't valid JSON is renamed to
    /// <c>&lt;name&gt;.corrupt-&lt;yyyyMMddHHmmss&gt;</c> (kept for the user) and reported as
    /// <see cref="JsonReadStatus.Corrupt"/>; one that stays locked after a few retries is
    /// <see cref="JsonReadStatus.Unavailable"/>. <paramref name="value"/> is default unless the status is Ok.
    /// </summary>
    public static JsonReadStatus TryRead<T>(string path, out T? value)
    {
        value = default;
        byte[] bytes;
        try
        {
            if (File.Exists(path) == false)
            {
                return JsonReadStatus.Missing;
            }

            bytes = ReadAllBytesWithRetry(path);
        }
        catch (FileNotFoundException)
        {
            return JsonReadStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return JsonReadStatus.Missing;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't read {path}", e);
            return JsonReadStatus.Unavailable;
        }

        try
        {
            value = Deserialize<T>(bytes);
            return JsonReadStatus.Ok;
        }
        catch (JsonException e)
        {
            var corrupt = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Move(path, corrupt, overwrite: false);
                Diagnostics.Log($"{path} isn't valid JSON; moved it to {corrupt}", e);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // Can't set it aside: don't let anything overwrite it.
                Diagnostics.Log($"{path} isn't valid JSON and couldn't be moved aside", moveError);
                return JsonReadStatus.Unavailable;
            }

            return JsonReadStatus.Corrupt;
        }
    }

    /// <summary>Writes <paramref name="value"/> to <paramref name="path"/> atomically. Failures are logged, not thrown.</summary>
    public static void Write<T>(string path, T value)
    {
        try
        {
            WriteOrThrow(path, value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't write {path}", e);
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="path"/> atomically. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if it couldn't be written.
    /// </summary>
    public static void WriteOrThrow<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            MoveWithRetry(temp, path);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        if (File.Exists(path) == false)
        {
            throw new IOException($"{path} wasn't written.");
        }
    }

    /// <summary>
    /// Takes the cross-process lock for <paramref name="path"/> (a named mutex derived from the
    /// full path). Dispose the result to release it, on the same thread. Throws
    /// <see cref="TimeoutException"/> if another process holds it for too long.
    /// </summary>
    public static IDisposable Lock(string path)
    {
        var mutex = new Mutex(false, LockName(path));
        try
        {
            if (mutex.WaitOne(LockTimeout) == false)
            {
                throw new TimeoutException($"Another SAM process is holding {path}.");
            }
        }
        catch (AbandonedMutexException)
        {
            // A process died while holding it; the lock is ours now.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new MutexLock(mutex);
    }

    /// <summary>Session-local mutex name for <paramref name="path"/>: same file, same name, in every process.</summary>
    internal static string LockName(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return "SteamAchievementManager.File." + Convert.ToHexString(hash, 0, 16);
    }

    /// <summary>Moves <paramref name="source"/> over <paramref name="destination"/>, retrying briefly while another process has it open.</summary>
    internal static void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < MoveAttempts && e is IOException or UnauthorizedAccessException &&
                                      e is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static byte[] ReadAllBytesWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception e) when (attempt < ReadAttempts && e is IOException or UnauthorizedAccessException &&
                                      e is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static T? Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options);

    private sealed class MutexLock(Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
