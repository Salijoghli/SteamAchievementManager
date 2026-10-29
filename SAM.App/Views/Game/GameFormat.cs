using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SAM.App.ViewModels;

namespace SAM.App.Views.Game;

/// <summary>Static helpers for x:Bind function bindings in the game window.</summary>
public static class GameFormat
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAny(bool a, bool b) => a || b ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    /// <summary>The chart shows when there's data and the table toggle is off.</summary>
    public static Visibility ChartVisible(bool hasData, bool? showTable) =>
        hasData && showTable != true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The table shows when there's data and the table toggle is on.</summary>
    public static Visibility TableVisible(bool hasData, bool? showTable) =>
        hasData && showTable == true ? Visibility.Visible : Visibility.Collapsed;

    public static double Opacity(bool visible) => visible ? 1.0 : 0.0;

    public static string ProtectedTooltip(bool isProtected) =>
        Strings.Get(isProtected ? "Achievement_ProtectedTooltip" : "Achievement_ToggleTooltip");

    /// <summary>
    /// A flyout presenter style that follows <paramref name="theme"/>: flyouts open in a popup,
    /// so they don't inherit the window root's (or a dialog's) requested theme.
    /// </summary>
    public static Style ThemedFlyoutPresenterStyle(ElementTheme theme) =>
        ThemedStyle(typeof(FlyoutPresenter), "DefaultFlyoutPresenterStyle", theme);

    /// <summary>Like <see cref="ThemedFlyoutPresenterStyle"/>, for a MenuFlyout.</summary>
    public static Style ThemedMenuFlyoutPresenterStyle(ElementTheme theme) =>
        ThemedStyle(typeof(MenuFlyoutPresenter), "DefaultMenuFlyoutPresenterStyle", theme);

    private static Style ThemedStyle(Type targetType, string basedOnKey, ElementTheme theme)
    {
        var style = new Style(targetType);
        if (Application.Current.Resources.TryGetValue(basedOnKey, out var basedOn) && basedOn is Style b)
        {
            style.BasedOn = b;
        }

        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, theme));
        return style;
    }

    /// <summary>True when the window's state can be changed (loaded and not loading or storing).</summary>
    public static bool IsIdle(bool isLoaded, bool isBusy) => isLoaded && isBusy == false;

    /// <summary>The App.xaml rarity tier brush.</summary>
    public static Brush RarityBrush(AchievementRarity rarity)
    {
        var key = rarity switch
        {
            AchievementRarity.Common => "RarityCommonBrush",
            AchievementRarity.Uncommon => "RarityUncommonBrush",
            AchievementRarity.Rare => "RarityRareBrush",
            AchievementRarity.Epic => "RarityEpicBrush",
            AchievementRarity.Legendary => "RarityLegendaryBrush",
            _ => null,
        };

        return key != null && Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
