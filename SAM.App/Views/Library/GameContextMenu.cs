using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Services;
using SAM.App.ViewModels;

namespace SAM.App.Views.Library;

/// <summary>
/// The right-click menu for a game, shared by grid cards and list rows. It's rebuilt each time it
/// opens so labels (Play/Install, Hide/Unhide, favorite) and the collection checkmarks are current.
/// </summary>
internal static class GameContextMenu
{
    /// <summary>Creates a menu for <paramref name="owner"/> that acts on whatever game <paramref name="getCard"/> returns when it opens.</summary>
    public static MenuFlyout Create(FrameworkElement owner, Func<GameCardViewModel?> getCard)
    {
        var flyout = new MenuFlyout();
        Populate(flyout, owner, null);
        flyout.Opening += (_, _) => Populate(flyout, owner, getCard());
        return flyout;
    }

    private static void Populate(MenuFlyout flyout, FrameworkElement owner, GameCardViewModel? card)
    {
        flyout.Items.Clear();
        var items = flyout.Items;

        items.Add(Item(Strings.Get("Card_MenuOpen"), "", () => card?.OpenCommand.Execute(null)));
        items.Add(Item(card?.PlayLabel ?? Strings.Get("Card_Play"), card?.PlayGlyph ?? "", () => card?.PlayCommand.Execute(null)));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item(Strings.Get("Card_MenuUnlockAll"), "", () => card?.UnlockAllCommand.Execute(null)));
        items.Add(Item(Strings.Get("Card_MenuLockAll"), "", () => card?.LockAllCommand.Execute(null)));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item(card?.FavoriteLabel ?? Strings.Get("Card_AddToFavorites"), card?.FavoriteGlyph ?? "", () => card?.ToggleFavoriteCommand.Execute(null)));
        items.Add(CollectionsSubMenu(owner, card));
        items.Add(Item(card?.HiddenLabel ?? Strings.Get("Card_HideGame"), card?.HiddenGlyph ?? "", () => card?.ToggleHiddenCommand.Execute(null)));
        items.Add(Item(Strings.Get("Card_MenuCopyAppId"), "", () => card?.CopyAppIdCommand.Execute(null)));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item(Strings.Get("Card_MenuStorePage"), "", () => card?.OpenStorePageCommand.Execute(null)));
        items.Add(Item(Strings.Get("Card_MenuCommunityPage"), "", () => card?.OpenAchievementsPageCommand.Execute(null)));
    }

    private static MenuFlyoutSubItem CollectionsSubMenu(FrameworkElement owner, GameCardViewModel? card)
    {
        var sub = new MenuFlyoutSubItem { Text = Strings.Get("Card_MenuAddToCollection"), Icon = new FontIcon { Glyph = "" } };
        if (card == null)
        {
            return sub;
        }

        var library = card.Owner;
        foreach (var collection in library.CustomCollections)
        {
            var id = collection.Id;
            var item = new ToggleMenuFlyoutItem
            {
                Text = collection.Name,
                IsChecked = library.IsInCollection(card, id),
            };
            item.Click += (_, _) => library.SetInCollection(card, id, item.IsChecked);
            sub.Items.Add(item);
        }

        if (sub.Items.Count > 0)
        {
            sub.Items.Add(new MenuFlyoutSeparator());
        }

        sub.Items.Add(Item(Strings.Get("Card_MenuNewCollection"), "", async () =>
        {
            try
            {
                var name = await CollectionDialogs.PromptNameAsync(owner.XamlRoot, library.RequestedTheme, Strings.Get("Collections_NewTitle"), "", Strings.Get("Common_Create"));
                if (name != null)
                {
                    library.CreateCollection(name, card);
                }
            }
            catch (Exception e)
            {
                Diagnostics.Log("New collection dialog failed", e);
            }
        }));
        return sub;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => onClick();
        return item;
    }
}
