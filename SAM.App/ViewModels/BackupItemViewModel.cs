using System.Globalization;
using SAM.App.Models;

namespace SAM.App.ViewModels;

/// <summary>One row in the game window's backups dialog.</summary>
public sealed class BackupItemViewModel(GameSnapshot snapshot, bool canRestore)
{
    public GameSnapshot Snapshot { get; } = snapshot;

    /// <summary>False while the game isn't loaded (or is busy), so its state can't be changed.</summary>
    public bool CanRestore { get; } = canRestore;

    /// <summary>The reason CliRunner stores (kept in English in the snapshot file).</summary>
    private const string BeforeCommandLineCommitReason = "Before command-line commit";

    public string DateText => this.Snapshot.CreatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>The stored reason (English, see <see cref="GameViewModel.BeforeCommitReason"/>) in the UI language.</summary>
    public string ReasonText => this.Snapshot.Reason switch
    {
        null or "" => Strings.Get("Backups_ReasonDefault"),
        GameViewModel.BeforeCommitReason => Strings.Get("Backups_ReasonBeforeCommit"),
        GameViewModel.BeforeRestoreReason => Strings.Get("Backups_ReasonBeforeRestore"),
        GameViewModel.BeforeResetReason => Strings.Get("Game_BackupReasonBeforeReset"),
        GameViewModel.ManualReason => Strings.Get("Backups_ReasonManual"),
        BeforeCommandLineCommitReason => Strings.Get("Backups_ReasonBeforeCli"),
        var reason when string.IsNullOrWhiteSpace(reason) => Strings.Get("Backups_ReasonDefault"),
        var reason => reason,
    };

    /// <summary>"12 / 40 unlocked · 3 stats".</summary>
    public string DetailsText
    {
        get
        {
            var stats = Strings.Plural("Backups_Stats", this.Snapshot.Stats.Count);
            return Strings.Format("Backups_Details", this.Snapshot.UnlockedCount, this.Snapshot.Achievements.Count, stats);
        }
    }
}
