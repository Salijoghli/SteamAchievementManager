using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels;

// CSV export and the update check.
public sealed partial class LibraryViewModel
{
    private static readonly TimeSpan ExportPercentTimeout = TimeSpan.FromSeconds(90);

    private UpdateInfo? _update;

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

        this.BeginExport("Exporting your library…");
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
            this.EndExport(path, $"Exported {LibraryFormat.Count(rows.Count)} games to {Path.GetFileName(path)}.");
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

        this.BeginExport("Reading achievements…");
        try
        {
            var entries = this._master.Values.Select(c => new GameEntry(c.AppId, c.Name, c.Type)).ToList();
            var rows = await Task.Run(() => this._services.Timeline.LoadAllAchievements(entries));

            var appIds = rows.Select(r => r.AppId).Distinct().ToList();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token))
            {
                timeout.CancelAfter(ExportPercentTimeout);
                var done = 0;
                this.ExportProgressText = $"Fetching global unlock percentages (0 of {LibraryFormat.Count(appIds.Count)} games)…";
                await this.FetchPercentsAsync(appIds, timeout.Token, _ =>
                {
                    done++;
                    this.ExportProgressText = $"Fetching global unlock percentages ({LibraryFormat.Count(done)} of {LibraryFormat.Count(appIds.Count)} games)…";
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

            this.ExportProgressText = "Writing the file…";
            await Task.Run(() => CsvExport.WriteAchievementsAsync(path, filled));
            this.EndExport(
                path,
                $"Exported {LibraryFormat.Count(filled.Count)} achievements from {LibraryFormat.Count(appIds.Count)} games to {Path.GetFileName(path)}.");
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
        this.ShowNotice("Couldn't export", e.Message, InfoBarSeverity.Error);
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
                return $"You're up to date (version {CurrentVersionText}).";
            }

            this.ShowUpdate(update);
            return $"Version {update.Version} is available.";
        }
        catch (Exception e)
        {
            Diagnostics.Log("Update check failed", e);
            return "Couldn't check for updates.";
        }
    }

    private void ShowUpdate(UpdateInfo update)
    {
        this._update = update;
        this.UpdateTitle = $"Version {update.Version} is available";
        this.UpdateMessage = string.IsNullOrWhiteSpace(update.Title) || update.Title == update.Version
            ? $"You have version {CurrentVersionText}."
            : $"{update.Title}. You have version {CurrentVersionText}.";
        this.IsUpdateOpen = true;
    }

    [RelayCommand]
    private void DownloadUpdate()
    {
        if (this._update is { } update)
        {
            this.OpenUrl((update.DownloadUrl ?? update.ReleasePage).ToString());
            this.IsUpdateOpen = false;
        }
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        if (this._update is not { } update)
        {
            return;
        }

        this._services.Settings.SkippedUpdateVersion = update.Version;
        try
        {
            this._services.Settings.Save();
        }
        catch (Exception e)
        {
            this.LogOnce("SaveSettings", e);
        }

        this.IsUpdateOpen = false;
    }
    #endregion
}
