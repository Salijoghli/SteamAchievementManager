namespace SAM.App.Models;

#region Backups and history
public sealed record AchievementSnapshotEntry(string Id, bool Achieved, DateTimeOffset? UnlockTime);

public sealed record StatSnapshotEntry(string Id, StatKind Kind, double Value);

/// <summary>A saved copy of one game's achievement and stat state.</summary>
public sealed class GameSnapshot
{
    /// <summary>Unique ID (also the file name stem). Assigned by BackupService.Save when empty.</summary>
    public string Id { get; set; } = "";

    public uint AppId { get; set; }

    public string GameName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Why it was taken, e.g. "Before commit", "Manual", "Before restore".</summary>
    public string Reason { get; set; } = "";

    public List<AchievementSnapshotEntry> Achievements { get; set; } = [];

    public List<StatSnapshotEntry> Stats { get; set; } = [];

    public int UnlockedCount => this.Achievements.Count(a => a.Achieved);
}

public sealed record StatChange(string Id, double From, double To);

/// <summary>Where a commit came from.</summary>
public static class CommitSource
{
    public const string Manual = "Manual";
    public const string Restore = "Restore";
    public const string CommandLine = "Command line";
    public const string Farm = "Farm";
}

/// <summary>One entry in the commit history log.</summary>
public sealed record CommitRecord(
    DateTimeOffset Time,
    uint AppId,
    string GameName,
    IReadOnlyList<string> Unlocked,
    IReadOnlyList<string> Locked,
    IReadOnlyList<StatChange> Stats,
    string Source,
    bool Succeeded,
    string? Message);
#endregion

#region Library data
/// <summary>An installed game, from Steam's appmanifest_&lt;appid&gt;.acf.</summary>
public sealed record InstalledGame(uint AppId, string InstallDirectory, long SizeOnDisk);

/// <summary>A group of games: a collection from Steam, or one made in SAM.</summary>
public sealed record GameCollection(string Id, string Name, bool IsSteamCollection, IReadOnlySet<uint> AppIds)
{
    /// <summary>Value stored in AppSettings.CollectionFilter for this collection.</summary>
    public string FilterKey => (this.IsSteamCollection ? "steam:" : "custom:") + this.Id;
}

/// <summary>An unlocked achievement with its unlock time, from Steam's local stats cache.</summary>
public sealed record UnlockEvent(
    uint AppId,
    string GameName,
    string AchievementId,
    string AchievementName,
    string Description,
    string? IconFile,
    DateTimeOffset Time);
#endregion

#region Export
public sealed record LibraryExportRow(
    uint AppId,
    string Name,
    string Type,
    int? Unlocked,
    int? Total,
    double? PlaytimeHours,
    DateTimeOffset? LastPlayed,
    bool Installed,
    bool Favorite);

public sealed record AchievementExportRow(
    uint AppId,
    string GameName,
    string AchievementId,
    string Name,
    string Description,
    bool Unlocked,
    DateTimeOffset? UnlockTime,
    double? GlobalPercent);
#endregion

#region Updates
public sealed record UpdateInfo(string Version, string Title, string Notes, Uri ReleasePage, Uri? DownloadUrl);
#endregion
