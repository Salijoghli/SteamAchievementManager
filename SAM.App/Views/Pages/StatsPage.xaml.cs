using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;

namespace SAM.App.Views.Pages;

/// <summary>Library-wide statistics: unlocks per month, rarest unlocks, completion over time, hunter score. TODO(pages): placeholder.</summary>
public sealed partial class StatsPage : UserControl
{
    /// <param name="library">The library's view model, for its game list, progress and playtime.</param>
    public StatsPage(AppServices services, LibraryViewModel library)
    {
        this.InitializeComponent();
    }
}
