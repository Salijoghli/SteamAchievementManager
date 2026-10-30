// Partial-property [ObservableProperty] needs C# 13; this project uses C# 12 and isn't AOT-compiled.
#pragma warning disable MVVMTK0045

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels;

// CSV export, the update check and self-update.
public sealed partial class LibraryViewModel
{
    private static readonly TimeSpan ExportPercentTimeout = TimeSpan.FromSeconds(90);

    private UpdateInfo? _update;

    /// <summary>The release's SAM.exe and the running exe, when "Update now" can replace it in place.</summary>
    private (UpdateAsset Asset, string ExePath)? _selfUpdate;

    /// <summary>The update InfoBar's button: "Update now" when SAM can update itself, else "Download".</summary>
    [ObservableProperty]
    private string _updateActionText = Strings.Get("Update_Download");

    /// <summary>True while an update downloads and installs (the InfoBar's buttons are disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateIdle))]
    private bool _isUpdating;

    public bool IsUpdateIdle => this.IsUpdating == false;

    /// <summary>The newer version the last check found ("8.2.1"), for Settings; null until one is found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdate))]
    private string? _availableUpdateVersion;

    public bool HasAvailableUpdate => this.AvailableUpdateVersion != null;

    /// <summary>Notify about achievements unlocked by playing (Settings; saved at once).</summary>
    public bool NotifyOnUnlock
    {
        get => this._services.Settings.NotifyOnUnlock;
        set
        {
            if (value == this._services.Settings.NotifyOnUnlock)
            {
                return;
            }

            try
            {
                this._services.Settings.UpdateChanged(s => s.NotifyOnUnlock = value);
            }
            catch (Exception e)
            {
                this.LogOnce("SaveSettings", e);
            }

            this.OnPropertyChanged();
        }
    }

    /// <summary>The running version, e.g. "8.0.0".</summary>
    public static string CurrentVersionText => UpdateService.CurrentVersion is { Build: >= 0 } v ? v.ToString(3) : UpdateService.CurrentVersion.ToString();

    #region Export
    /// <summary>Writes one row per owned game to <paramref name="path"/>.</summary>
    public async Task ExportLibraryAsync(string path)
    {
        if (this.IsExporting)
        {
            return;
        }

        this.BeginExport(Strings.Get("Export_ExportingLibrary"));
        try
        {
            var rows = this._master.Values
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new LibraryExportRow(
                    c.AppId,
                    c.Name,
                    c.Type,
                    c.Progress?.Unlocked,
                    c.Progress?.Total,
                    c.Playtime is { } p ? Math.Round(p.Playtime.TotalHours, 2) : null,
                    c.LastPlayed,
                    c.IsInstalled,
                    c.IsFavorite))
                .ToList();
            await Task.Run(() => CsvExport.WriteLibraryAsync(path, rows));
            this.EndExport(path, Strings.Plural("Export_LibraryDone", rows.Count, Path.GetFileName(path)));
        }
        catch (Exception e)
        {
            this.FailExport(e);
        }
    }

    /// <summary>
    /// Writes every achievement of every owned game to <paramref name="path"/>, with global unlock
    /// percentages where they can be fetched (cached for a day; gives up after a timeout).
    /// </summary>
    public async Task ExportAchievementsAsync(string path)
    {
        if (this.IsExporting)
        {
            return;
        }

        this.BeginExport(Strings.Get("Export_ReadingAchievements"));
        try
        {
            var entries = this._master.Values.Select(c => new GameEntry(c.AppId, c.Name, c.Type)).ToList();
            var rows = await Task.Run(() => this._services.Timeline.LoadAllAchievements(entries));

            var appIds = rows.Select(r => r.AppId).Distinct().ToList();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token))
            {
                timeout.CancelAfter(ExportPercentTimeout);
                var done = 0;
                this.ExportProgressText = Strings.Plural("Export_FetchingPercentages", appIds.Count, LibraryFormat.Count(0));
                await this.FetchPercentsAsync(appIds, timeout.Token, _ =>
                {
                    done++;
                    this.ExportProgressText = Strings.Plural("Export_FetchingPercentages", appIds.Count, LibraryFormat.Count(done));
                });
            }

            this._lifetime.Token.ThrowIfCancellationRequested();
            var filled = rows
                .Select(r => r.GlobalPercent == null &&
                             this._percents.TryGetValue(r.AppId, out var percents) &&
                             percents != null &&
                             percents.TryGetValue(r.AchievementId, out var percent)
                    ? r with { GlobalPercent = percent }
                    : r)
                .ToList();

            this.ExportProgressText = Strings.Get("Export_WritingFile");
            await Task.Run(() => CsvExport.WriteAchievementsAsync(path, filled));
            this.EndExport(
                path,
                Strings.Plural("Export_AchievementsDone", filled.Count, Strings.Plural("Export_FromGames", appIds.Count), Path.GetFileName(path)));
        }
        catch (Exception e)
        {
            this.FailExport(e);
        }
    }

    private void BeginExport(string text)
    {
        this.IsExportDoneOpen = false;
        this.ExportProgressText = text;
        this.IsExporting = true;
    }

    private void EndExport(string path, string message)
    {
        this.IsExporting = false;
        this.ExportedPath = path;
        this.ExportDoneMessage = message;
        this.IsExportDoneOpen = true;
    }

    private void FailExport(Exception e)
    {
        this.IsExporting = false;
        if (this._isShutDown)
        {
            return;
        }

        Diagnostics.Log("CSV export failed", e);
        this.ShowNotice(Strings.Get("Export_Failed"), e.Message, InfoBarSeverity.Error);
    }

    [RelayCommand]
    private void OpenExportedFile()
    {
        if (this.ExportedPath is { } path)
        {
            this.OpenPath(path);
        }
    }

    [RelayCommand]
    private void OpenExportedFolder()
    {
        if (this.ExportedPath is not { } path)
        {
            return;
        }

        try
        {
            // Select the file in Explorer.
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't show {path} in Explorer", e);
            if (Path.GetDirectoryName(path) is { } folder)
            {
                this.OpenPath(folder);
            }
        }
    }
    #endregion

    #region Updates
    private async void CheckForUpdatesOnStartup()
    {
        if (this.CheckForUpdates == false)
        {
            return;
        }

        try
        {
            var update = await this._services.Updates.CheckAsync(this._lifetime.Token);
            if (update != null && this._isShutDown == false)
            {
                this.ShowUpdate(update);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            this.LogOnce("UpdateCheck", e);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Checks for a newer version now (from Settings). Returns a status line for the user.</summary>
    public async Task<string> CheckForUpdatesNowAsync()
    {
        try
        {
            var update = await this._services.Updates.CheckAsync(this._lifetime.Token);
            if (update == null)
            {
                return Strings.Format("Update_UpToDate", CurrentVersionText);
            }

            this.ShowUpdate(update);
            return Strings.Format("Update_AvailableStatus", update.Version);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Update check failed", e);
            return Strings.Get("Update_CheckFailed");
        }
    }

    private void ShowUpdate(UpdateInfo update)
    {
        if (this.IsUpdating)
        {
            return;
        }

        this._update = update;
        this._selfUpdate = null;
        this.AvailableUpdateVersion = update.Version;
        this.UpdateActionText = Strings.Get("Update_Download");
        this.ResolveSelfUpdate(update);
        this.UpdateTitle = Strings.Format("Update_AvailableTitle", update.Version);
        this.UpdateMessage = string.IsNullOrWhiteSpace(update.Title) || update.Title == update.Version
            ? Strings.Format("Update_YouHave", CurrentVersionText)
            : Strings.Format("Update_YouHaveWithTitle", update.Title, CurrentVersionText);
        this.IsUpdateOpen = true;
    }

    /// <summary>Offers "Update now" when the release has a SAM.exe and the running exe can be replaced.</summary>
    private async void ResolveSelfUpdate(UpdateInfo update)
    {
        try
        {
            var exePath = await Task.Run(SelfUpdater.GetReplaceableExecutable);
            var asset = exePath == null ? null : await this._services.Updates.GetExecutableAsync(update, this._lifetime.Token);
            if (asset != null && exePath != null && ReferenceEquals(this._update, update) && this.IsUpdating == false)
            {
                this._selfUpdate = (asset, exePath);
                this.UpdateActionText = Strings.Get("Update_UpdateNow");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            this.LogOnce("SelfUpdateCheck", e);
        }
    }

    [RelayCommand]
    private async Task DownloadUpdate()
    {
        if (this._update is not { } update || this.IsUpdating)
        {
            return;
        }

        if (this._selfUpdate is { } selfUpdate)
        {
            await this.InstallUpdateAsync(update, selfUpdate.Asset, selfUpdate.ExePath);
            return;
        }

        this.OpenDownloadPage(update);
    }

    private void OpenDownloadPage(UpdateInfo update)
    {
        this.OpenUrl((update.DownloadUrl ?? update.ReleasePage).ToString());
        this.IsUpdateOpen = false;
    }

    /// <summary>
    /// "Update now": downloads the release's SAM.exe next to the running one, verifies it, swaps
    /// it in and restarts SAM. On any failure the download page opens instead, with a message.
    /// </summary>
    private async Task InstallUpdateAsync(UpdateInfo update, UpdateAsset asset, string exePath)
    {
        var download = exePath + SelfUpdater.DownloadSuffix;
        var message = this.UpdateMessage;
        this.IsUpdating = true;
        try
        {
            this.UpdateMessage = Strings.Format("Update_Downloading", update.Version, 0);
            var progress = new Progress<double>(fraction =>
            {
                if (this.IsUpdating)
                {
                    this.UpdateMessage = Strings.Format("Update_Downloading", update.Version, fraction * 100);
                }
            });
            await SelfUpdater.DownloadAsync(SelfUpdater.Http, asset, download, progress, this._lifetime.Token);

            this.UpdateMessage = Strings.Get("Update_Verifying");
            var verification = await Task.Run(() => SelfUpdater.Verify(download, update.Version, asset.Size));
            if (verification != UpdateVerification.Ok)
            {
                throw new InvalidDataException(verification switch
                {
                    UpdateVerification.Incomplete => Strings.Get("Update_ErrorIncomplete"),
                    UpdateVerification.WrongVersion => Strings.Format("Update_ErrorWrongVersion", update.Version),
                    _ => Strings.Get("Update_ErrorNotExecutable"),
                });
            }

            this.UpdateMessage = Strings.Get("Update_Installing");
            await Task.Run(() => SelfUpdater.Swap(exePath, download));
            Diagnostics.Log($"Updated SAM.exe to {update.Version}; restarting");
            if (App.RestartForUpdate() == false)
            {
                this.IsUpdating = false;
                this.IsUpdateOpen = false;
                this.ShowNotice(Strings.Get("Update_InstalledTitle"), Strings.Format("Update_RestartManually", update.Version), InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException) when (this._isShutDown)
        {
            SelfUpdater.CleanUp(exePath);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Self-update to {update.Version} failed", e);
            SelfUpdater.CleanUp(exePath);
            this.IsUpdating = false;
            this.UpdateMessage = message;
            if (this._isShutDown == false)
            {
                this.ShowNotice(Strings.Get("Update_FailedTitle"), Strings.Format("Update_FailedMessage", e.Message), InfoBarSeverity.Warning);
                this.OpenDownloadPage(update);
            }
        }
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        if (this._update is not { } update || this.IsUpdating)
        {
            return;
        }

        try
        {
            this._services.Settings.Update(s => s.SkippedUpdateVersion = update.Version);
        }
        catch (Exception e)
        {
            this.LogOnce("SaveSettings", e);
        }

        this.IsUpdateOpen = false;
    }
    #endregion
}
