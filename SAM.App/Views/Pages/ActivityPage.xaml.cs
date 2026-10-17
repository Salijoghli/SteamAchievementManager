using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;

namespace SAM.App.Views.Pages;

/// <summary>Recent unlocks across all games, newest first. TODO(pages): placeholder.</summary>
public sealed partial class ActivityPage : UserControl
{
    /// <param name="library">The library's view model, for its game list, progress and playtime.</param>
    public ActivityPage(AppServices services, LibraryViewModel library)
    {
        this.InitializeComponent();
    }
}
