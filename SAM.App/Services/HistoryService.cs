using System.Text;
using System.Text.Json;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Append-only log of commits, in data\users\&lt;steamid64&gt;\history.jsonl (one JSON object per line).
/// The file is only rewritten when it grows past <see cref="MaxLines"/> lines. Thread-safe.
/// </summary>
public sealed class HistoryService
{
    /// <summary>When the log grows past this many lines it's trimmed to the newest <see cref="TrimToLines"/>.</summary>
    internal const int MaxLines = 10_000;

    /// <summary>Lines kept after a trim (below <see cref="MaxLines"/> so trims don't happen on every append).</summary>
    internal const int TrimToLines = 9_000;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _path;
    private readonly object _lock = new();
    private int _lineCount = -1;

    /// <summary>Creates the service for the account that <paramref name="cache"/> belongs to.</summary>
    public HistoryService(AppCache cache)
        : this(Path.Combine(cache.UserDataDirectory, "history.jsonl"))
    {
    }

    /// <summary>Creates the service logging to <paramref name="path"/> (for tests).</summary>
    internal HistoryService(string path)
    {
        this._path = path;
    }

    /// <summary>Raised (on the calling thread) after a record is appended.</summary>
    public event EventHandler<CommitRecord>? Appended;

    /// <summary>Appends <paramref name="record"/> to the log. Failures are logged, not thrown.</summary>
    public void Append(CommitRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var line = JsonSerializer.Serialize(record, Options);
        lock (this._lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this._path)!);
                if (this._lineCount < 0)
                {
                    this._lineCount = File.Exists(this._path) ? this.ReadLines().Count : 0;
                }

                // If a previous write was cut off mid-line, start on a fresh line so this record stays readable.
                var prefix = EndsWithNewline(this._path) ? "" : "\n";
                File.AppendAllText(this._path, prefix + line + "\n", Utf8NoBom);
                this._lineCount++;

                if (this._lineCount > MaxLines)
                {
                    this.Trim();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Log($"Couldn't append to {this._path}", e);
                this._lineCount = -1;
                return;
            }
        }

        this.Appended?.Invoke(this, record);
    }

    /// <summary>Newest first, at most <paramref name="max"/> records. Tolerates corrupt lines.</summary>
    public IReadOnlyList<CommitRecord> Load(int max = 1000)
    {
        List<string> lines;
        lock (this._lock)
        {
            try
            {
                lines = File.Exists(this._path) ? this.ReadLines() : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Log($"Couldn't read {this._path}", e);
                return [];
            }
        }

        var result = new List<CommitRecord>(Math.Min(Math.Max(max, 0), lines.Count));
        var corrupt = 0;
        for (var i = lines.Count - 1; i >= 0 && result.Count < max; i--)
        {
            try
            {
                var record = JsonSerializer.Deserialize<CommitRecord>(lines[i], Options);
                if (record != null)
                {
                    result.Add(record);
                }
                else
                {
                    corrupt++;
                }
            }
            catch (JsonException)
            {
                corrupt++;
            }
        }

        if (corrupt > 0)
        {
            Diagnostics.Log($"Skipped {corrupt} corrupt line(s) in {this._path}");
        }

        return result;
    }

    /// <summary>Non-empty lines of the log, oldest first. Call under the lock.</summary>
    private List<string> ReadLines()
    {
        using var stream = new FileStream(this._path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Utf8NoBom);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) == false)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>True if the file is missing, empty, or ends with a line feed.</summary>
    private static bool EndsWithNewline(string path)
    {
        if (File.Exists(path) == false)
        {
            return true;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return true;
        }

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    /// <summary>Rewrites the log keeping the newest <see cref="TrimToLines"/> lines. Call under the lock.</summary>
    private void Trim()
    {
        var lines = this.ReadLines();
        var keep = lines.Skip(Math.Max(0, lines.Count - TrimToLines)).ToList();
        var temp = this._path + ".tmp";
        File.WriteAllText(temp, string.Join("\n", keep) + "\n", Utf8NoBom);
        File.Move(temp, this._path, overwrite: true);
        this._lineCount = keep.Count;
    }
}
