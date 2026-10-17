// [ObservableProperty] on fields: partial properties need C# 13 and the project uses C# 12;
// the app isn't AOT-compiled, so this WinRT AOT warning doesn't apply.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SAM.App.Services;

namespace SAM.App.ViewModels;

/// <summary>The game window's backups dialog: lists, creates and deletes snapshots of one game.</summary>
public sealed partial class GameBackupsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly GameViewModel _game;

    public GameBackupsViewModel(AppServices services, GameViewModel game)
    {
        this._services = services;
        this._game = game;
        this.Items.CollectionChanged += (_, _) => this.OnPropertyChanged(nameof(this.IsEmpty));
    }

    /// <summary>Snapshots, newest first.</summary>
    public ObservableCollection<BackupItemViewModel> Items { get; } = [];

    public bool IsEmpty => this.Items.Count == 0 && this.HasError == false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _error;

    public bool HasError => this.Error != null;

    /// <summary>Restoring and creating need the game's current state loaded.</summary>
    public bool CanUseGameState => this._game.IsLoaded && this._game.IsBusy == false;

    /// <summary>Reloads the list from disk.</summary>
    public void Refresh()
    {
        this.Items.Clear();
        try
        {
            foreach (var snapshot in this._services.Backups.List(this._game.AppId))
            {
                this.Items.Add(new BackupItemViewModel(snapshot, this.CanUseGameState));
            }

            this.Error = null;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't list the backups for app {this._game.AppId}", e);
            this.Error = Strings.Format("Backups_ReadFailed", e.Message);
        }
    }

    /// <summary>Saves a "Manual" snapshot of the current Steam state and refreshes the list.</summary>
    public void CreateNow()
    {
        try
        {
            this._game.SaveBackup(GameViewModel.ManualReason);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't create a backup for app {this._game.AppId}", e);
            this.Refresh();
            this.Error = Strings.Format("Backups_CreateFailed", e.Message);
            return;
        }

        this.Refresh();
    }

    public void Delete(BackupItemViewModel item)
    {
        try
        {
            this._services.Backups.Delete(this._game.AppId, item.Snapshot.Id);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't delete backup {item.Snapshot.Id} of app {this._game.AppId}", e);
            this.Refresh();
            this.Error = Strings.Format("Backups_DeleteFailed", e.Message);
            return;
        }

        this.Items.Remove(item);
    }
}
