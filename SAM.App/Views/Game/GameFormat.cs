using Microsoft.UI.Xaml;
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

    public static double Opacity(bool visible) => visible ? 1.0 : 0.0;

    public static string ProtectedTooltip(bool isProtected) =>
        isProtected ? "Protected — only the game's servers can change this" : "Locked or unlocked state to commit";

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
