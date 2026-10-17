using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Snapshots of a game's achievements and stats, stored as JSON under
/// data\users\&lt;steamid64&gt;\backups\&lt;appid&gt;\&lt;id&gt;.json. Keeps the newest 50 per game.
/// </summary>
public sealed class BackupService(AppCache cache)
{
    /// <summary>Saves <paramref name="snapshot"/> (assigning Id and CreatedAt if unset) and prunes old ones. Returns it.</summary>
    public GameSnapshot Save(GameSnapshot snapshot) => throw new NotImplementedException("TODO(data-a)");

    /// <summary>Snapshots for a game, newest first. Empty if none.</summary>
    public IReadOnlyList<GameSnapshot> List(uint appId) => throw new NotImplementedException("TODO(data-a)");

    public void Delete(uint appId, string snapshotId) => throw new NotImplementedException("TODO(data-a)");
}
