using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.Services;
using SAM.App.ViewModels.Pages;

namespace SAM.App.Views.Autopilot;

/// <summary>
/// The Autopilot dry run: for each achievement, when it would unlock (playtime and estimated
/// date) or why it's skipped, with totals. Uses the configuration it's given, which is the page's
/// edited (unsaved) copy, so rules can be tried before saving. Nothing is written to Steam.
/// </summary>
public sealed partial class PreviewDialog : ContentDialog
{
    public PreviewDialog(AppServices services, IReadOnlyList<(uint AppId, string Name)> games, AutopilotConfig config, ElementTheme theme)
    {
        this.ViewModel = new AutopilotPreviewViewModel(services, games, config);
        this.InitializeComponent();
        this.RequestedTheme = theme;
        this.CloseButtonText = Strings.Get("Common_Close");
        this.Opened += async (_, _) => await this.ViewModel.ShowAsync();
        this.Closed += (_, _) => this.ViewModel.Cancel();
    }

    public AutopilotPreviewViewModel ViewModel { get; }
}
