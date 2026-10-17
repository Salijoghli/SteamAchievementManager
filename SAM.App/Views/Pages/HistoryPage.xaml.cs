using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;

namespace SAM.App.Views.Pages;

/// <summary>Log of commits made with SAM, with links to each game's backups. TODO(pages): placeholder.</summary>
public sealed partial class HistoryPage : UserControl
{
    /// <param name="library">The library's view model, for its game list, progress and playtime.</param>
    public HistoryPage(AppServices services, LibraryViewModel library)
    {
        this.InitializeComponent();
    }
}
