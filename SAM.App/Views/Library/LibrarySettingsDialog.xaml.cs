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
        this.CheckNowButton.IsEnabled = false;
        this.UpdateProgress.IsActive = true;
        this.UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            this.UpdateStatusText.Text = await this.ViewModel.CheckForUpdatesNowAsync();
            this.UpdateStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            this.UpdateProgress.IsActive = false;
            this.UpdateProgress.Visibility = Visibility.Collapsed;
            this.CheckNowButton.IsEnabled = true;
        }
    }

    private void OnConfirmClearCache(object sender, RoutedEventArgs e)
    {
        this.ConfirmFlyout.Hide();
        this.ClearCacheRequested = true;
        this.Hide();
    }
}
