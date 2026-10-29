using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SAM.App.Services;
using SAM.App.ViewModels;
using VirtualKey = Windows.System.VirtualKey;

namespace SAM.App.Views;

/// <summary>
/// The library's Ctrl+K quick switcher: a centered search popup over the owned games. Typing
/// filters by name, initials or App ID (<see cref="QuickSwitcherMatcher"/>), the arrow keys move
/// the selection, Enter opens the game's window and Esc (or clicking outside) closes it.
/// </summary>
public sealed partial class QuickSwitcher : UserControl
{
    private const int MaxResults = 50;
    private const double PanelMaxWidth = 640;
    private const double TopOffset = 72;

    private readonly LibraryViewModel _library;
    private readonly List<GameCardViewModel> _games;
    private Popup? _popup;

    public QuickSwitcher(LibraryViewModel library)
    {
        this._library = library;

        // Shown for an empty query: most recently played first.
        this._games = library.AllGames
            .OrderByDescending(c => c.LastPlayed ?? DateTimeOffset.MinValue)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        this.InitializeComponent();
        this.Refresh();
    }

    /// <summary>
    /// Opens a switcher over <paramref name="root"/> (centered near the top) and returns its popup;
    /// it closes itself when a game is opened, on Esc or when clicking outside.
    /// </summary>
    public static Popup Show(FrameworkElement root, LibraryViewModel library)
    {
        var width = Math.Max(320, Math.Min(PanelMaxWidth, root.ActualWidth - 32));
        var switcher = new QuickSwitcher(library)
        {
            Width = width,
            RequestedTheme = root.ActualTheme,
        };
        var popup = new Popup
        {
            XamlRoot = root.XamlRoot,
            Child = switcher,
            IsLightDismissEnabled = true,
            HorizontalOffset = Math.Max(0, (root.ActualWidth - width) / 2),
            VerticalOffset = TopOffset,
        };
        switcher._popup = popup;
        popup.IsOpen = true;
        switcher.QueryBox.Focus(FocusState.Programmatic);
        return popup;
    }

    /// <summary>Puts the keyboard focus back in the search box (e.g. Ctrl+K while already open).</summary>
    public void FocusQuery()
    {
        this.QueryBox.Focus(FocusState.Programmatic);
        this.QueryBox.SelectAll();
    }

    private void Close()
    {
        if (this._popup != null)
        {
            this._popup.IsOpen = false;
        }
    }

    private void Refresh()
    {
        var results = QuickSwitcherMatcher.Filter(this._games, this.QueryBox.Text, c => c.Name, c => c.AppIdText, MaxResults);
        this.ResultsList.ItemsSource = results;
        this.ResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.NoMatchesText.Visibility = results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (results.Count > 0)
        {
            this.ResultsList.SelectedIndex = 0;
            this.ResultsList.ScrollIntoView(results[0]);
        }
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => this.Refresh();

    private void OnQueryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = this.ResultsList.Items.Count;
        switch (e.Key)
        {
            case VirtualKey.Down:
            case VirtualKey.Up:
                if (count > 0)
                {
                    var step = e.Key == VirtualKey.Down ? 1 : -1;
                    var index = (Math.Max(this.ResultsList.SelectedIndex, 0) + step + count) % count;
                    this.ResultsList.SelectedIndex = index;
                    this.ResultsList.ScrollIntoView(this.ResultsList.SelectedItem);
                }

                e.Handled = true;
                break;

            case VirtualKey.Enter:
                this.Open(this.ResultsList.SelectedItem as GameCardViewModel);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                this.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnResultClick(object sender, ItemClickEventArgs e) => this.Open(e.ClickedItem as GameCardViewModel);

    private void OnResultContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue == false && args.Item is GameCardViewModel card)
        {
            this._library.RequestImage(card);
        }
    }

    private void Open(GameCardViewModel? card)
    {
        if (card == null)
        {
            return;
        }

        this.Close();
        card.OpenCommand.Execute(null);
    }
}
