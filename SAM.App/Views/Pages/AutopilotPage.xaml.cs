using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;

namespace SAM.App.Views.Pages;

/// <summary>Autopilot: runs games in the background and unlocks achievements by the user's rules. TODO(autopilot-ui): placeholder.</summary>
public sealed partial class AutopilotPage : UserControl
{
    /// <param name="library">The library's view model, for its game list, progress and playtime.</param>
    public AutopilotPage(AppServices services, LibraryViewModel library)
    {
        this.InitializeComponent();
    }
}
