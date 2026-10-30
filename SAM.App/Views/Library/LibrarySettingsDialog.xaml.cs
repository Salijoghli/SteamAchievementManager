using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.ViewModels;

namespace SAM.App.Views.Library;

/// <summary>Settings: appearance, library and cache, game window, app behavior and updates, and the logged-in account.</summary>
public sealed partial class LibrarySettingsDialog : ContentDialog
{
    public LibrarySettingsDialog(LibraryViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
        this.RequestedTheme = viewModel.RequestedTheme;

        viewModel.PropertyChanged += this.OnViewModelPropertyChanged;
        this.Closed += (_, _) => viewModel.PropertyChanged -= this.OnViewModelPropertyChanged;
        this.Opened += async (_, _) => await this.UpdateCacheSizeAsync();
        this.VersionText.Text = Strings.Format("Settings_Version", LibraryViewModel.CurrentVersionText);
        this.RefreshUpdateRow();
    }

    public LibraryViewModel ViewModel { get; }

    /// <summary>True if the user confirmed "Clear cache"; the caller reloads after the dialog closes.</summary>
    public bool ClearCacheRequested { get; private set; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.RequestedTheme))
        {
            // The dialog lives in a popup, so it doesn't inherit the window root's theme.
            this.RequestedTheme = this.ViewModel.RequestedTheme;
        }
        else if (e.PropertyName is nameof(LibraryViewModel.AvailableUpdateVersion) or nameof(LibraryViewModel.IsUpdating) or nameof(LibraryViewModel.UpdateMessage))
        {
            this.RefreshUpdateRow();
        }
    }

    /// <summary>The last "Check now" result, shown until an update is found.</summary>
    private string? _checkResult;

    private bool _checking;

    /// <summary>
    /// The version card: "Check now" until a newer release is known, then "Update now" (or "Download")
    /// right here, with the download progress while it installs.
    /// </summary>
    private void RefreshUpdateRow()
    {
        var available = this.ViewModel.AvailableUpdateVersion;
        var updating = this.ViewModel.IsUpdating;
        string? status = updating ? this.ViewModel.UpdateMessage
            : available != null ? Strings.Format("Update_AvailableStatus", available)
            : this._checkResult;

        this.UpdateStatusText.Text = status ?? "";
        this.UpdateStatusText.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        this.CheckNowButton.Visibility = available == null ? Visibility.Visible : Visibility.Collapsed;
        this.CheckNowButton.IsEnabled = this._checking == false;
        this.UpdateNowButton.Visibility = available != null ? Visibility.Visible : Visibility.Collapsed;
        var busy = this._checking || updating;
        this.UpdateProgress.IsActive = busy;
        this.UpdateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        this.UpdateIcon.Glyph = available != null ? "" : "";
    }

    private async Task UpdateCacheSizeAsync()
    {
        this.CacheSizeSkeleton.Visibility = Visibility.Visible;
        this.CacheSizeText.Visibility = Visibility.Collapsed;
        var size = await LibraryViewModel.GetCacheSizeAsync();
        this.CacheSizeText.Text = LibraryFormat.Bytes(size);
        this.CacheSizeSkeleton.Visibility = Visibility.Collapsed;
        this.CacheSizeText.Visibility = Visibility.Visible;
    }

    private async void OnCheckNowClick(object sender, RoutedEventArgs e)
    {
        this._checking = true;
        this.RefreshUpdateRow();
        try
        {
            this._checkResult = await this.ViewModel.CheckForUpdatesNowAsync();
        }
        finally
        {
            this._checking = false;
            this.RefreshUpdateRow();
        }
    }

    private void OnConfirmClearCache(object sender, RoutedEventArgs e)
    {
        this.ConfirmFlyout.Hide();
        this.ClearCacheRequested = true;
        this.Hide();
    }
}
