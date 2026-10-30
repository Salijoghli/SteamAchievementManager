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
    private readonly List<PickerFilter> _filters;
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

        this._filters = BuildFilters(library);
        this.FilterBox.ItemsSource = this._filters;
        this.FilterBox.SelectedIndex = 0;

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

    /// <summary>One "Show" choice: a name and which library games it keeps.</summary>
    private sealed record PickerFilter(string Name, Func<GameCardViewModel, bool> Matches)
    {
        public override string ToString() => this.Name;
    }

    private static List<PickerFilter> BuildFilters(LibraryViewModel library)
    {
        var filters = new List<PickerFilter>
        {
            new(Strings.Get("Autopilot_PickerFilterAll"), _ => true),
            new(Strings.Get("Autopilot_PickerFilterInstalled"), c => c.IsInstalled),
            new(Strings.Get("Autopilot_PickerFilterFavorites"), c => c.IsFavorite),
        };

        foreach (var list in LibraryViewModel.SmartLists.Where(l => l.Key is LibraryViewModel.AlmostPerfectList or LibraryViewModel.NeverStartedList))
        {
            filters.Add(new(list.Name, c => LibraryViewModel.MatchesSmartList(c, list.Key)));
        }

        foreach (var collection in library.SteamCollections.Concat(library.CustomCollections))
        {
            filters.Add(new(collection.Name, c => collection.AppIds.Contains(c.AppId)));
        }

        return filters;
    }

    private void OnFilterSelectionChanged(object sender, SelectionChangedEventArgs e) => this.ApplyFilter();

    /// <summary>Adds every game the filter shows (e.g. a whole collection) to the selection.</summary>
    private void OnSelectAllClick(object sender, RoutedEventArgs e) => this.GameList.SelectAll();

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
        var filter = this.FilterBox.SelectedItem as PickerFilter ?? this._filters[0];
        var games = this._library.AllGames
            .Where(c => c.HasAchievements && this._alreadyListed.Contains(c.AppId) == false)
            .Where(filter.Matches)
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

        this.SelectAllButton.IsEnabled = games.Count > 0;
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
