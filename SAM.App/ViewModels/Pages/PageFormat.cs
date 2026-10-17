using Microsoft.UI.Xaml;

namespace SAM.App.ViewModels.Pages;

/// <summary>x:Bind helper functions for the Stats, Activity and History pages.</summary>
public static class PageFormat
{
    public static Visibility VisibleIfEmpty(IEnumerable<object>? items) =>
        items == null || items.Any() == false ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>A chart shows when it has data and its table toggle is off.</summary>
    public static Visibility ChartVisible(bool hasData, bool? tableChecked) =>
        hasData && tableChecked != true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A chart's table view shows when it has data and its table toggle is on.</summary>
    public static Visibility TableVisible(bool hasData, bool? tableChecked) =>
        hasData && tableChecked == true ? Visibility.Visible : Visibility.Collapsed;
}
