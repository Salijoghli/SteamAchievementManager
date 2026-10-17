using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;
using SAM.App.ViewModels.Pages;

namespace SAM.App.Views.Pages;

/// <summary>Recent unlocks across all games, newest first, grouped by day.</summary>
public sealed partial class ActivityPage : UserControl
{
    /// <param name="library">The library's view model, for its game list, progress and playtime.</param>
    public ActivityPage(AppServices services, LibraryViewModel library)
    {
        this.ViewModel = new ActivityViewModel(services, library);
        this.InitializeComponent();
        this.ViewModel.PropertyChanged += this.OnViewModelPropertyChanged;
        this.Loaded += (_, _) => this.ViewModel.OnShown();
    }

    public ActivityViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityViewModel.Groups))
        {
            this.DaysSource.Source = this.ViewModel.Groups;
            this.Feed.ItemsSource = this.DaysSource.View;
        }
    }

    /// <summary>Loads a row's icon and rarity once it's realized (the list is virtualized).</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue == false && args.Item is UnlockItemViewModel item)
        {
            item.EnsureLoaded();
        }
    }
}
