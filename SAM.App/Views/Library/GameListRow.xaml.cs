using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.ViewModels;

namespace SAM.App.Views.Library;

/// <summary>
/// A game row in the library's list view: thumbnail, name, progress, playtime, last played,
/// install size and favorite star, with the same context menu as the grid cards.
/// </summary>
public sealed partial class GameListRow : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card),
        typeof(GameCardViewModel),
        typeof(GameListRow),
        new PropertyMetadata(null));

    public GameListRow()
    {
        this.InitializeComponent();
        this.ContextFlyout = GameContextMenu.Create(this, () => this.Card);
    }

    public GameCardViewModel? Card
    {
        get => (GameCardViewModel?)this.GetValue(CardProperty);
        set => this.SetValue(CardProperty, value);
    }
}
