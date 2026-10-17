namespace SAM.App.Services;

/// <summary>Personal notes on achievements, in data\users\&lt;steamid64&gt;\notes.json.</summary>
public sealed class NotesStore(AppCache cache)
{
    /// <summary>The note, or null if none.</summary>
    public string? Get(uint appId, string achievementId) => throw new NotImplementedException("TODO(data-a)");

    /// <summary>Saves the note; null or whitespace deletes it.</summary>
    public void Set(uint appId, string achievementId, string? note) => throw new NotImplementedException("TODO(data-a)");

    /// <summary>Achievement IDs with notes for a game (for showing a note indicator).</summary>
    public IReadOnlySet<string> GetIds(uint appId) => throw new NotImplementedException("TODO(data-a)");
}
