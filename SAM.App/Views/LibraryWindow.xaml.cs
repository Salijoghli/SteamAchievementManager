using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SAM.App.Services;
using SAM.App.ViewModels;
using SAM.App.Views.Library;
using VirtualKey = Windows.System.VirtualKey;

namespace SAM.App.Views;

/// <summary>
/// The game library (picker). State lives in <see cref="LibraryViewModel"/>; this class handles
/// window chrome, focus, search debouncing and lazy image loading for realized cards.
/// </summary>
public sealed partial class LibraryWindow : Window
{
    private readonly DispatcherQueueTimer _searchDebounce;
    private bool _isSettingsOpen;

    public LibraryWindow(AppServices services)
    {
        this.ViewModel = new LibraryViewModel(services);
        this.InitializeComponent();

        this.Title = "Steam Achievement Manager";
        this.SystemBackdrop = new MicaBackdrop();
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.AppTitleBar);
        this.AppWindow.SetIcon("Assets/SAM.ico");
        this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        this.SizeAndCenter(1440, 900);
        this.UpdateTitleBarTheme();

        this._searchDebounce = DispatcherQueue.GetForCurrentThread().CreateTimer();
        this._searchDebounce.Interval = TimeSpan.FromMilliseconds(150);
        this._searchDebounce.IsRepeating = false;
        this._searchDebounce.Tick += (_, _) => this.CommitSearch();

        this.ViewModel.PropertyChanged += this.OnViewModelPropertyChanged;
        this.Root.Loaded += this.OnRootLoaded;
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

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (this._isSettingsOpen)
        {
            return;
        }

        this._isSettingsOpen = true;
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
            this._isSettingsOpen = false;
        }
    }
}
