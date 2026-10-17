using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>Append-only log of commits, in data\users\&lt;steamid64&gt;\history.jsonl.</summary>
public sealed class HistoryService(AppCache cache)
{
    /// <summary>Raised (on the calling thread) after a record is appended.</summary>
    public event EventHandler<CommitRecord>? Appended;

    public void Append(CommitRecord record) => throw new NotImplementedException("TODO(data-a)");

    /// <summary>Newest first, at most <paramref name="max"/> records. Tolerates corrupt lines.</summary>
    public IReadOnlyList<CommitRecord> Load(int max = 1000) => throw new NotImplementedException("TODO(data-a)");
}
