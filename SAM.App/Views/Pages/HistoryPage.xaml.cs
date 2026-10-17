using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;
using SAM.App.ViewModels.Pages;

namespace SAM.App.Views.Pages;

/// <summary>Log of commits made with SAM, with links to each game (where its backups are).</summary>
public sealed partial class HistoryPage : UserControl
{
    /// <param name="library">The library's view model, for its game list, progress and playtime.</param>
    public HistoryPage(AppServices services, LibraryViewModel library)
    {
        this.ViewModel = new HistoryViewModel(services);
        this.InitializeComponent();

        // Listen for new commits only while shown, so a hidden page doesn't hold the event.
        this.Loaded += (_, _) => this.ViewModel.OnShown();
        this.Unloaded += (_, _) => this.ViewModel.OnHidden();
    }

    public HistoryViewModel ViewModel { get; }
}
