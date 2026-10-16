using Microsoft.UI.Xaml;
using SAM.App.Services;

namespace SAM.App.Views;

/// <summary>Achievement and stat manager for one game. TODO(game-ui): placeholder.</summary>
public sealed partial class GameWindow : Window
{
    /// <param name="unlockAllOnOpen">
    /// After loading, check every unlockable achievement and prompt the user to commit.
    /// </param>
    public GameWindow(AppServices services, uint appId, bool unlockAllOnOpen = false)
    {
        this.InitializeComponent();
        this.Title = $"Steam Achievement Manager | {appId}";
    }
}
