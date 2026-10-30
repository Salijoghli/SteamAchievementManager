using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.Services;
using SAM.App.ViewModels.Pages;

namespace SAM.App.Views.Autopilot;

/// <summary>Per-game Autopilot overrides: profile, time target, rare achievements and an achievement checklist.</summary>
public sealed partial class OverridesDialog : ContentDialog
{
    public OverridesDialog(AppServices services, uint appId, string gameName, AutopilotGameOverride value, AutopilotConfig config, ElementTheme theme)
    {
        this.ViewModel = new AutopilotOverridesViewModel(services, appId, gameName, value, config);
        this.InitializeComponent();
        this.RequestedTheme = theme;
        this.Opened += async (_, _) => await this.ViewModel.LoadAsync();
        this.PrimaryButtonClick += (_, _) => this.ViewModel.Apply();
    }

    public AutopilotOverridesViewModel ViewModel { get; }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var index))
        {
            this.ViewModel.ModeIndex = index;
        }
    }
}
