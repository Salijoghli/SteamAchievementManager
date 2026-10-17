using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;
using SAM.App.ViewModels.Pages;

namespace SAM.App.Views.Pages;

/// <summary>Library-wide statistics: unlocks per month, rarest unlocks, completion over time, hunter score.</summary>
public sealed partial class StatsPage : UserControl
{
    /// <param name="library">The library's view model, for its game list, progress and playtime.</param>
    public StatsPage(AppServices services, LibraryViewModel library)
    {
        this.ViewModel = new StatsViewModel(services, library);
        this.InitializeComponent();
        this.Loaded += (_, _) => this.ViewModel.OnShown();
    }

    public StatsViewModel ViewModel { get; }

    /// <summary>Re-flows the tile, chart and list rows into fewer columns on narrow windows.</summary>
    private void OnColumnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var tiles = width >= 1100 ? 5 : width >= 700 ? 3 : 2;
        PageLayout.Reflow(this.Tiles, tiles);
        PageLayout.Reflow(this.SkeletonTiles, tiles);
        PageLayout.Reflow(this.Charts, width >= 1000 ? 2 : 1);
        PageLayout.Reflow(this.SkeletonCharts, width >= 1000 ? 2 : 1);
        PageLayout.Reflow(this.Lists, width >= 1150 ? 3 : 1);
    }
}

/// <summary>Layout helpers for the pages.</summary>
internal static class PageLayout
{
    /// <summary>Places a grid's children row by row in <paramref name="columns"/> equal columns.</summary>
    public static void Reflow(Grid grid, int columns)
    {
        var count = grid.Children.Count;
        var rows = (count + columns - 1) / columns;
        if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count == rows)
        {
            return;
        }

        grid.ColumnDefinitions.Clear();
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        grid.RowDefinitions.Clear();
        for (var i = 0; i < rows; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < count; i++)
        {
            var child = (FrameworkElement)grid.Children[i];
            Grid.SetRow(child, i / columns);
            Grid.SetColumn(child, i % columns);
        }
    }
}
