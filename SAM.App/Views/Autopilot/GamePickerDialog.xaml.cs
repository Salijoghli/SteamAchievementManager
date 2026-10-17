using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.ViewModels;

namespace SAM.App.Views.Autopilot;

/// <summary>
/// Picks owned games with achievements for Autopilot: a searchable multi-select list with art
/// thumbnails and progress. Games already in the target list are left out. The selection
/// survives searching and filtering.
/// </summary>
public sealed partial class GamePickerDialog : ContentDialog
{
    private readonly LibraryViewModel _library;
    private readonly IReadOnlySet<uint> _alreadyListed;
    private readonly HashSet<uint> _selected = [];
    private bool _restoringSelection;

    public GamePickerDialog(LibraryViewModel library, string title, string primaryText, IReadOnlyCollection<uint> alreadyListed)
    {
        this._library = library;
        this._alreadyListed = alreadyListed.ToHashSet();
        this.InitializeComponent();
        this.Title = title;
        this.PrimaryButtonText = primaryText;
        this.CloseButtonText = Strings.Get("Common_Cancel");
        this.IsPrimaryButtonEnabled = false;
        this.RequestedTheme = library.RequestedTheme;

        library.PropertyChanged += this.OnLibraryPropertyChanged;
        this.Closed += (_, _) => library.PropertyChanged -= this.OnLibraryPropertyChanged;
        this.Opened += (_, _) =>
        {
            this.ApplyFilter();
            this.SearchBox.Focus(FocusState.Programmatic);
        };
    }

    /// <summary>The chosen App IDs, in list order (empty until the user confirms).</summary>
    public IReadOnlyList<uint> SelectedAppIds => this._library.AllGames
        .Where(c => this._selected.Contains(c.AppId))
        .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(c => c.AppId)
        .ToList();

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryViewModel.IsSummaryReady) or nameof(LibraryViewModel.IsLoading))
        {
            this.ApplyFilter();
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => this.ApplyFilter();

    private void OnFilterChanged(object sender, RoutedEventArgs e) => this.ApplyFilter();

    private void ApplyFilter()
    {
        var ready = this._library.IsLoading == false && this._library.AllGames.Any(c => c.IsProgressLoaded);
        this.Skeletons.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (ready == false)
        {
            this.GameList.Visibility = Visibility.Collapsed;
            this.EmptyText.Visibility = Visibility.Collapsed;
            return;
        }

        var query = this.SearchBox.Text.Trim();
        var lockedOnly = this.LockedOnlyBox.IsChecked == true;
        var games = this._library.AllGames
            .Where(c => c.HasAchievements && this._alreadyListed.Contains(c.AppId) == false)
            .Where(c => lockedOnly == false || c.IsPerfect == false)
            .Where(c => query.Length == 0 ||
                        c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        c.AppIdText.StartsWith(query, StringComparison.Ordinal))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        this._restoringSelection = true;
        try
        {
            this.GameList.ItemsSource = games;
            foreach (var card in games.Where(c => this._selected.Contains(c.AppId)))
            {
                this.GameList.SelectedItems.Add(card);
            }
        }
        finally
        {
            this._restoringSelection = false;
        }

        this.GameList.Visibility = games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.EmptyText.Visibility = games.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        this.UpdateCount(games.Count);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._restoringSelection)
        {
            return;
        }

        foreach (var card in e.AddedItems.OfType<GameCardViewModel>())
        {
            this._selected.Add(card.AppId);
        }

        foreach (var card in e.RemovedItems.OfType<GameCardViewModel>())
        {
            this._selected.Remove(card.AppId);
        }

        this.UpdateCount((this.GameList.ItemsSource as IReadOnlyCollection<GameCardViewModel>)?.Count ?? 0);
    }

    private void UpdateCount(int shown)
    {
        this.IsPrimaryButtonEnabled = this._selected.Count > 0;
        this.CountText.Text = Strings.Format(
            "Autopilot_PickerCount",
            Strings.Plural("Autopilot_PickerShown", shown),
            Strings.Plural("Autopilot_PickerSelected", this._selected.Count));
    }

    /// <summary>Loads a row's card image once it's realized (the list is virtualized).</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Not cancelled on recycle: the library's own views may be waiting for the same image.
        if (args.InRecycleQueue == false && args.Item is GameCardViewModel card)
        {
            this._library.RequestImage(card);
        }
    }
}
