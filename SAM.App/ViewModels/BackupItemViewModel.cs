using System.Globalization;
using SAM.App.Models;

namespace SAM.App.ViewModels;

/// <summary>One row in the game window's backups dialog.</summary>
public sealed class BackupItemViewModel(GameSnapshot snapshot, bool canRestore)
{
    public GameSnapshot Snapshot { get; } = snapshot;

    /// <summary>False while the game isn't loaded (or is busy), so its state can't be changed.</summary>
    public bool CanRestore { get; } = canRestore;

    public string DateText => this.Snapshot.CreatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture);

    public string ReasonText => string.IsNullOrWhiteSpace(this.Snapshot.Reason) ? "Backup" : this.Snapshot.Reason;

    /// <summary>"12 / 40 unlocked · 3 stats".</summary>
    public string DetailsText
    {
        get
        {
            var stats = this.Snapshot.Stats.Count == 1 ? "1 stat" : $"{this.Snapshot.Stats.Count} stats";
            return $"{this.Snapshot.UnlockedCount} / {this.Snapshot.Achievements.Count} unlocked · {stats}";
        }
    }
}
