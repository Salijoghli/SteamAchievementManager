using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
            // A refresh keeps the reader's place; a filter change starts from the top.
            var scroller = this.ViewModel.KeepScrollPosition ? FindScrollViewer(this.Feed) : null;
            var offset = scroller?.VerticalOffset ?? 0;

            this.DaysSource.Source = this.ViewModel.Groups;
            this.Feed.ItemsSource = this.DaysSource.View;

            if (scroller != null && offset > 0)
            {
                this.DispatcherQueue.TryEnqueue(() => scroller.ChangeView(null, offset, null, disableAnimation: true));
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(Microsoft.UI.Xaml.DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
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
