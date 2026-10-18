using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SAM.App.Models;
using SAM.App.Services;
using SAM.App.ViewModels;
using SAM.App.Views.Library;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;

namespace SAM.App.Views;

/// <summary>
/// The game library (picker). State lives in <see cref="LibraryViewModel"/>; this class handles
/// window chrome, focus, search debouncing and lazy image loading for realized cards.
/// </summary>
public sealed partial class LibraryWindow : Window
{
    private readonly DispatcherQueueTimer _searchDebounce;
    private bool _isDialogOpen;

    private readonly AppServices _services;

    public LibraryWindow(AppServices services)
    {
        this._services = services;
        this.ViewModel = new LibraryViewModel(services);
        this.InitializeComponent();

        this.Title = "Steam Achievement Manager";
        this.SystemBackdrop = new MicaBackdrop();
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.AppTitleBar);
        this.AppWindow.SetIcon(AppIcon.Path);
        this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        this.SizeAndCenter(1440, 900);
        this.UpdateTitleBarTheme();

        this._searchDebounce = DispatcherQueue.GetForCurrentThread().CreateTimer();
        this._searchDebounce.Interval = TimeSpan.FromMilliseconds(150);
        this._searchDebounce.IsRepeating = false;
        this._searchDebounce.Tick += (_, _) => this.CommitSearch();

        this.ViewModel.PropertyChanged += this.OnViewModelPropertyChanged;
        this.Root.Loaded += this.OnRootLoaded;

        // Coming back from a game window: pick up achievements committed there.
        this.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                this.ViewModel.RefreshProgress();
            }
        };
        this.Closed += (_, _) =>
        {
            this._searchDebounce.Stop();
            this.ViewModel.Shutdown();
        };
    }

    public LibraryViewModel ViewModel { get; }

    /// <summary>Placeholder items for the loading grid.</summary>
    public int[] SkeletonItems { get; } = Enumerable.Range(0, 24).ToArray();

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        this.Root.Loaded -= this.OnRootLoaded;
        this.SyncViewModeButtons();
        try
        {
            await this.ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Library initialization failed", ex);
        }
    }

    #region Window chrome
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SizeAndCenter(int width, int height)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            if (scale <= 0)
            {
                scale = 1;
            }

            var area = DisplayArea.GetFromWindowId(this.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var w = Math.Min((int)(width * scale), area.Width);
            var h = Math.Min((int)(height * scale), area.Height);
            this.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                area.X + ((area.Width - w) / 2),
                area.Y + ((area.Height - h) / 2),
                w,
                h));
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't size the library window", e);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.RequestedTheme))
        {
            this.UpdateTitleBarTheme();
        }
    }

    private void UpdateTitleBarTheme()
    {
        // Caption buttons follow the app's theme, not the root element's; keep them in sync.
        this.AppWindow.TitleBar.PreferredTheme = this.ViewModel.RequestedTheme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }
    #endregion

    #region Search
    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        this.SearchBox.Focus(FocusState.Keyboard);
    }

    private void OnRefreshAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (this.ViewModel.RefreshCommand.CanExecute(null))
        {
            this.ViewModel.RefreshCommand.Execute(null);
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            this._searchDebounce.Stop();
            this._searchDebounce.Start();
        }
    }

    private void CommitSearch()
    {
        this._searchDebounce.Stop();
        var text = this.SearchBox.Text;
        this.ViewModel.SearchText = text;
        var suggestions = this.ViewModel.GetSuggestions(text);
        this.SearchBox.ItemsSource = suggestions.Count > 0 ? suggestions : null;
        this.SearchBox.IsSuggestionListOpen = suggestions.Count > 0 && this.SearchBox.FocusState != FocusState.Unfocused;
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        this.CommitSearch();
        if (this.ViewModel.Submit(args.ChosenSuggestion as LibrarySuggestion, args.QueryText))
        {
            sender.IsSuggestionListOpen = false;
        }
    }

    private void OnSearchPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && this.SearchBox.Text.Length > 0)
        {
            e.Handled = true;
            this._searchDebounce.Stop();
            this.SearchBox.Text = "";
            this.SearchBox.ItemsSource = null;
            this.SearchBox.IsSuggestionListOpen = false;
            this.ViewModel.SearchText = "";
        }
    }
    #endregion

    #region Grid
    // Card sizing: as many columns as fit at a minimum card width, then cards stretch to fill
    // the row, so the grid edges line up with the header. The minimum grows with the window
    // (~1/6.5 of it, between 300 and 380), giving about 4 columns on a 1440-wide window and
    // 6 large ones on a 2K screen instead of many small ones.
    private const double MinCardWidth = 300;
    private const double MaxMinCardWidth = 380;
    private const double CardMargin = 12;            // CardContainerStyle margin (6) on both sides
    private const double GridHorizontalPadding = 36; // GridView padding (18) on both sides
    private const double CardInfoHeight = 106;       // text area below the image, incl. border
    private const double ImageAspect = 215.0 / 460.0;

    private double _slotWidth = MinCardWidth + CardMargin;
    private double _slotHeight = Math.Round((MinCardWidth * ImageAspect) + CardInfoHeight + CardMargin);

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = e.NewSize.Width - GridHorizontalPadding;
        if (available <= 0)
        {
            return;
        }

        var minCardWidth = Math.Clamp(available / 6.5, MinCardWidth, MaxMinCardWidth);
        var columns = Math.Max(1, (int)Math.Floor(available / (minCardWidth + CardMargin)));
        // Floor so rounding never pushes the last column onto a new row.
        this._slotWidth = Math.Floor(available / columns);
        var cardWidth = this._slotWidth - CardMargin;
        this._slotHeight = Math.Round((cardWidth * ImageAspect) + CardInfoHeight + CardMargin);

        foreach (var grid in new[] { this.GamesGrid, this.SkeletonGrid })
        {
            if (grid.ItemsPanelRoot is ItemsWrapGrid panel)
            {
                this.ApplyCardSize(panel);
            }
        }
    }

    /// <summary>Wrap panels are created lazily (e.g. when a grid first becomes visible); size them on load.</summary>
    private void OnCardPanelLoaded(object sender, RoutedEventArgs e) => this.ApplyCardSize((ItemsWrapGrid)sender);

    private void ApplyCardSize(ItemsWrapGrid panel)
    {
        panel.ItemWidth = this._slotWidth;
        panel.ItemHeight = this._slotHeight;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not GameCardViewModel card)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            this.ViewModel.CancelImage(card);
            return;
        }

        // Defer the image request to a later phase so fast scrolling skips cards that fly past.
        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1, this.OnContainerContentChanging);
        }
        else
        {
            this.ViewModel.RequestImage(card);
        }
    }

    private void OnGameClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameCardViewModel card)
        {
            card.OpenCommand.Execute(null);
        }
    }
    #endregion

    #region View mode, collections and smart lists
    private void OnGridModeClick(object sender, RoutedEventArgs e)
    {
        this.ViewModel.IsListView = false;
        this.SyncViewModeButtons();
    }

    private void OnListModeClick(object sender, RoutedEventArgs e)
    {
        this.ViewModel.IsListView = true;
        this.SyncViewModeButtons();
    }

    /// <summary>The two view buttons act as a segmented control: exactly one is checked.</summary>
    private void SyncViewModeButtons()
    {
        this.GridModeButton.IsChecked = this.ViewModel.IsListView == false;
        this.ListModeButton.IsChecked = this.ViewModel.IsListView;
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string column })
        {
            this.ViewModel.SortByColumn(column);
        }
    }

    /// <summary>Rebuilds the collection picker: All games, Installed games, Steam collections, SAM collections, Manage.</summary>
    private void OnCollectionMenuOpening(object? sender, object e)
    {
        var items = this.CollectionMenu.Items;
        items.Clear();

        var current = this.ViewModel.CollectionFilter;
        var all = this.CollectionItem(Strings.Get("Library_CollectionsAllGames"), null);
        items.Add(all);
        var installed = this.CollectionItem(Strings.Get("Library_CollectionsInstalled"), LibraryViewModel.InstalledCollectionKey);
        installed.Icon = new FontIcon { Glyph = "" };
        installed.IsChecked = current == LibraryViewModel.InstalledCollectionKey;
        items.Add(installed);
        var anyChecked = installed.IsChecked;

        void AddGroup(string title, IReadOnlyList<GameCollection> collections)
        {
            if (collections.Count == 0)
            {
                return;
            }

            items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = title, IsEnabled = false });
            foreach (var collection in collections)
            {
                var item = this.CollectionItem(collection.Name, collection.FilterKey);
                item.IsChecked = collection.FilterKey == current;
                anyChecked |= item.IsChecked;
                items.Add(item);
            }
        }

        AddGroup(Strings.Get("Library_CollectionsSteamGroup"), this.ViewModel.SteamCollections);
        AddGroup(Strings.Get("Library_CollectionsSamGroup"), this.ViewModel.CustomCollections);
        all.IsChecked = anyChecked == false;

        items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = Strings.Get("Library_CollectionsManage"), Icon = new FontIcon { Glyph = "" } };
        manage.Click += this.OnManageCollectionsClick;
        items.Add(manage);
    }

    private RadioMenuFlyoutItem CollectionItem(string text, string? filterKey)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "LibraryCollection" };
        item.Click += (_, _) => this.ViewModel.CollectionFilter = filterKey;
        return item;
    }

    private void OnSmartListMenuOpening(object? sender, object e)
    {
        var items = this.SmartListMenu.Items;
        items.Clear();

        var none = new RadioMenuFlyoutItem { Text = Strings.Get("Library_SmartListNone"), GroupName = "LibrarySmartList", IsChecked = this.ViewModel.SmartList == null };
        none.Click += (_, _) => this.ViewModel.SmartList = null;
        items.Add(none);
        items.Add(new MenuFlyoutSeparator());

        foreach (var list in LibraryViewModel.SmartLists)
        {
            var key = list.Key;
            var item = new RadioMenuFlyoutItem
            {
                Text = list.Name,
                GroupName = "LibrarySmartList",
                IsChecked = this.ViewModel.SmartList == key,
            };
            ToolTipService.SetToolTip(item, list.Description);
            item.Click += (_, _) => this.ViewModel.SmartList = key;
            items.Add(item);
        }
    }

    private async void OnManageCollectionsClick(object sender, RoutedEventArgs e)
    {
        if (this._isDialogOpen)
        {
            return;
        }

        this._isDialogOpen = true;
        try
        {
            await CollectionDialogs.ShowManageAsync(this.Root.XamlRoot, this.ViewModel);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Manage collections dialog failed", ex);
        }
        finally
        {
            this._isDialogOpen = false;
        }
    }
    #endregion

    #region Export
    private async void OnExportLibraryClick(object sender, RoutedEventArgs e)
    {
        var path = await this.PickCsvPathAsync(Strings.Format("Library_ExportLibraryFileName", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (path != null)
        {
            await this.ViewModel.ExportLibraryAsync(path);
        }
    }

    private async void OnExportAchievementsClick(object sender, RoutedEventArgs e)
    {
        var path = await this.PickCsvPathAsync(Strings.Format("Library_ExportAchievementsFileName", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (path != null)
        {
            await this.ViewModel.ExportAchievementsAsync(path);
        }
    }

    /// <summary>Asks where to save a CSV file. Returns null if cancelled.</summary>
    private async Task<string?> PickCsvPathAsync(string suggestedName)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
            };
            picker.FileTypeChoices.Add(Strings.Get("Library_CsvFileType"), new List<string> { ".csv" });

            // Unpackaged apps must tell the picker which window owns it.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Save picker failed", ex);
            this.ViewModel.ShowNotice(Strings.Get("Library_ChooseFileFailed"), ex.Message, InfoBarSeverity.Error);
            return null;
        }
    }
    #endregion

    #region Pages
    private readonly Dictionary<string, UIElement> _pages = [];

    /// <summary>Switches between the library and the Stats/Activity/History pages (created on first use).</summary>
    private void OnPageSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // The initially selected item raises this during InitializeComponent, before the
        // elements declared after the SelectorBar exist; the library is shown by default anyway.
        if (this.LibraryHeader == null || this.PageHost == null)
        {
            return;
        }

        var tag = sender.SelectedItem?.Tag as string ?? "library";
        var isLibrary = tag == "library";

        this.LibraryHeader.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        this.LibraryNotices.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        this.LibraryContent.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        this.PageHost.Visibility = isLibrary ? Visibility.Collapsed : Visibility.Visible;

        if (isLibrary)
        {
            this.PageHost.Content = null;
            return;
        }

        if (this._pages.TryGetValue(tag, out var page) == false)
        {
            page = tag switch
            {
                "stats" => new Pages.StatsPage(this._services, this.ViewModel),
                "activity" => new Pages.ActivityPage(this._services, this.ViewModel),
                "autopilot" => new Pages.AutopilotPage(this._services, this.ViewModel),
                _ => new Pages.HistoryPage(this._services, this.ViewModel),
            };
            this._pages[tag] = page;
        }

        this.PageHost.Content = page;
    }
    #endregion

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (this._isDialogOpen)
        {
            return;
        }

        this._isDialogOpen = true;
        try
        {
            var dialog = new LibrarySettingsDialog(this.ViewModel) { XamlRoot = this.Root.XamlRoot };
            await dialog.ShowAsync();
            if (dialog.ClearCacheRequested)
            {
                await this.ViewModel.ClearCacheAndReloadAsync();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Settings dialog failed", ex);
        }
        finally
        {
            this._isDialogOpen = false;
        }
    }
}
