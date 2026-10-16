using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SAM.App.Services;
using SAM.App.ViewModels;
using Windows.Globalization.NumberFormatting;
using Windows.Graphics;
using Windows.System;

namespace SAM.App.Views;

/// <summary>Achievement and stat manager for one game.</summary>
public sealed partial class GameWindow : Window, ILaunchPresetTarget
{
    /// <summary>A launch pre-set waiting for the next successful load: true = unlock all, false = lock all.</summary>
    private bool? _queuedPreset;
    private bool _loadStarted;
    private bool _dialogOpen;
    private bool _closeConfirmed;

    /// <param name="unlockAllOnOpen">
    /// After loading, check every unlockable achievement and prompt the user to commit.
    /// </param>
    /// <param name="lockAllOnOpen">
    /// After loading, uncheck every lockable achievement and prompt the user to commit.
    /// Ignored when <paramref name="unlockAllOnOpen"/> is also set.
    /// </param>
    public GameWindow(AppServices services, uint appId, bool unlockAllOnOpen = false, bool lockAllOnOpen = false)
    {
        this.ViewModel = new GameViewModel(services, appId);
        this._queuedPreset = ToPreset(unlockAllOnOpen, lockAllOnOpen);

        this.InitializeComponent();

        this.Title = this.ViewModel.GameName;
        this.SystemBackdrop = new MicaBackdrop();
        this.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "SAM.ico"));
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.AppTitleBar);
        this.ApplyTheme(services.Settings.Theme);
        this.ResizeAndCenter(1100, 800);

        this.AppWindow.Closing += this.OnAppWindowClosing;
        this.ViewModel.CommitRequested += async (_, _) => await this.ConfirmAndCommitAsync();
        this.ViewModel.PropertyChanged += this.OnViewModelPropertyChanged;
        this.ViewModel.LoadCompleted += this.OnLoadCompleted;
        this.AchievementList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(this.OnAchievementListKeyDown), true);

        this.Root.Loaded += async (_, _) =>
        {
            if (this._loadStarted == false)
            {
                this._loadStarted = true;
                await this.ViewModel.LoadAsync();
            }
        };
    }

    public GameViewModel ViewModel { get; }

    /// <summary>Placeholder rows shown while loading.</summary>
    public int[] SkeletonRows { get; } = Enumerable.Range(0, 8).ToArray();

    #region Launch pre-set
    /// <summary>
    /// Applies a --unlock-all / --lock-all request from another launch of this game: checks
    /// (or unchecks) every changeable achievement, ignoring search and filters, and opens the
    /// commit confirmation. Nothing is stored unless the user confirms. If the window is
    /// still loading (or the last load failed) the request is queued for the next successful
    /// load; if a dialog is open it's ignored. <paramref name="unlockAll"/> wins when both are set.
    /// </summary>
    public void ApplyLaunchPreset(bool unlockAll, bool lockAll)
    {
        if (ToPreset(unlockAll, lockAll) is not { } preset || this._dialogOpen)
        {
            return;
        }

        // Runs now if loaded and idle, otherwise after the next successful load.
        this._queuedPreset = preset;
        this.TryRunQueuedPreset();
    }

    private static bool? ToPreset(bool unlockAll, bool lockAll) => unlockAll ? true : lockAll ? false : null;

    private void OnLoadCompleted(object? sender, bool success)
    {
        if (success)
        {
            // Let the load (and whoever awaited it) finish before opening the dialog.
            this.DispatcherQueue.TryEnqueue(this.TryRunQueuedPreset);
        }
    }

    /// <summary>
    /// Runs the queued pre-set once the content is loaded and nothing is in progress, so it
    /// survives a failed first load (Retry / F5) and requests that arrive mid-load.
    /// </summary>
    private async void TryRunQueuedPreset()
    {
        if (this._queuedPreset is not { } unlockAll || this.ViewModel.IsLoaded == false || this.ViewModel.IsBusy)
        {
            return; // Stays queued for the next successful load.
        }

        this._queuedPreset = null;
        if (this._dialogOpen || this.ViewModel.ApplyLaunchPreset(unlockAll) == false)
        {
            return;
        }

        // Nothing is stored unless the user confirms.
        if (this.ViewModel.HasPending)
        {
            await this.ConfirmAndCommitAsync();
        }
    }
    #endregion

    #region Window setup
    private void ApplyTheme(string theme)
    {
        var (elementTheme, titleBarTheme) = theme switch
        {
            "Light" => (ElementTheme.Light, TitleBarTheme.Light),
            "Dark" => (ElementTheme.Dark, TitleBarTheme.Dark),
            _ => (ElementTheme.Default, TitleBarTheme.UseDefaultAppMode),
        };
        this.Root.RequestedTheme = elementTheme;
        this.AppWindow.TitleBar.PreferredTheme = titleBarTheme;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ResizeAndCenter(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var size = new SizeInt32((int)(width * scale), (int)(height * scale));

        var area = DisplayArea.GetFromWindowId(this.AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        if (area is { } work)
        {
            size.Width = Math.Min(size.Width, work.Width);
            size.Height = Math.Min(size.Height, work.Height);
            this.AppWindow.MoveAndResize(new RectInt32(
                work.X + ((work.Width - size.Width) / 2),
                work.Y + ((work.Height - size.Height) / 2),
                size.Width,
                size.Height));
        }
        else
        {
            this.AppWindow.Resize(size);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.IsLoaded))
        {
            this.UpdatePanels();
        }
        else if (e.PropertyName == nameof(GameViewModel.IsBusy) && this._queuedPreset != null && this.ViewModel.IsBusy == false)
        {
            // E.g. a pre-set requested while a commit was being stored.
            this.DispatcherQueue.TryEnqueue(this.TryRunQueuedPreset);
        }
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => this.UpdatePanels();

    private void UpdatePanels()
    {
        var loaded = this.ViewModel.IsLoaded;
        var stats = this.Tabs.SelectedItem == this.StatisticsTab;
        this.AchievementsPanel.Visibility = loaded && stats == false ? Visibility.Visible : Visibility.Collapsed;
        this.StatisticsPanel.Visibility = loaded && stats ? Visibility.Visible : Visibility.Collapsed;
    }
    #endregion

    #region Achievement list
    private void OnAchievementContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue == false && args.Item is AchievementViewModel achievement)
        {
            achievement.EnsureIcon();
        }
    }

    private void OnAchievementListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Space on a focused row toggles it (the switch itself handles Space when it has focus).
        if (e.Key == VirtualKey.Space &&
            e.OriginalSource is ListViewItem { Content: AchievementViewModel achievement })
        {
            this.ViewModel.Toggle(achievement);
            e.Handled = true;
        }
    }

    private void OnDescriptionPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AchievementViewModel { IsHidden: true } achievement)
        {
            achievement.IsHovered = true;
        }
    }

    private void OnDescriptionPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AchievementViewModel achievement)
        {
            achievement.IsHovered = false;
        }
    }

    private void OnDescriptionTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AchievementViewModel { IsHidden: true } achievement)
        {
            achievement.IsRevealed = !achievement.IsRevealed;
            e.Handled = true;
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        this.ViewModel.SearchText = sender.Text;
    #endregion

    #region Keyboard accelerators
    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.ViewModel.IsLoaded)
        {
            this.Tabs.SelectedItem = this.AchievementsTab;
            this.SearchBox.Focus(FocusState.Keyboard);
        }

        args.Handled = true;
    }

    private void OnCommitAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // A value typed into a stat box only counts once committed; it may enable (or block) the commit.
        this.CommitFocusedNumberBox();
        if (this.ViewModel.CommitCommand.CanExecute(null))
        {
            this.ViewModel.CommitCommand.Execute(null);
        }

        args.Handled = true;
    }

    private void OnReloadAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this._dialogOpen == false)
        {
            _ = this.ReloadAsync();
        }

        args.Handled = true;
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e) => await this.ReloadAsync();

    private async Task ReloadAsync()
    {
        this.CommitFocusedNumberBox();
        if (this.ViewModel.HasUnsavedChanges &&
            await this.ShowDialogAsync(
                "Discard pending changes?",
                "Reloading replaces your pending changes with the current state from Steam.",
                "Discard and reload",
                closeText: "Cancel") != ContentDialogResult.Primary)
        {
            return;
        }

        if (this.ViewModel.ReloadCommand.CanExecute(null))
        {
            await this.ViewModel.ReloadCommand.ExecuteAsync(null);
        }
    }
    #endregion

    #region Dialogs
    private async Task<ContentDialogResult> ShowDialogAsync(
        string title,
        object content,
        string primaryText,
        string? secondaryText = null,
        string closeText = "Cancel",
        bool destructive = false,
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        if (this._dialogOpen || this.Content.XamlRoot == null)
        {
            return ContentDialogResult.None;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = this.Root.ActualTheme,
            Title = title,
            Content = content is string text ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } : content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = defaultButton,
        };
        if (secondaryText != null)
        {
            dialog.SecondaryButtonText = secondaryText;
        }

        if (destructive)
        {
            // Lightweight styling: the accent (default) button becomes red.
            var red = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
            var redHover = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xB0, 0x26, 0x1A));
            var redPressed = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x9C, 0x22, 0x18));
            var white = new SolidColorBrush(Colors.White);
            dialog.Resources["AccentButtonBackground"] = red;
            dialog.Resources["AccentButtonBackgroundPointerOver"] = redHover;
            dialog.Resources["AccentButtonBackgroundPressed"] = redPressed;
            dialog.Resources["AccentButtonBorderBrush"] = red;
            dialog.Resources["AccentButtonBorderBrushPointerOver"] = redHover;
            dialog.Resources["AccentButtonBorderBrushPressed"] = redPressed;
            dialog.Resources["AccentButtonForeground"] = white;
            dialog.Resources["AccentButtonForegroundPointerOver"] = white;
            dialog.Resources["AccentButtonForegroundPressed"] = white;
        }

        this._dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            this._dialogOpen = false;
        }
    }

    /// <summary>
    /// A NumberBox only commits typed text on focus loss or Enter, which for Ctrl+S happens
    /// after the commit summary is built. Parse the focused box's text now, with the box's own
    /// parser, and push it into Value (and so through the TwoWay binding into the stat's
    /// validation) so the summary matches what gets written. Unparsable text is left alone:
    /// the box reverts it on focus loss, so the stored value doesn't change.
    /// </summary>
    private void CommitFocusedNumberBox()
    {
        if (this.Content.XamlRoot is not { } root ||
            FocusManager.GetFocusedElement(root) is not TextBox textBox)
        {
            return;
        }

        NumberBox? numberBox = null;
        for (DependencyObject? element = textBox; element != null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is NumberBox box)
            {
                numberBox = box;
                break;
            }
        }

        if (numberBox == null)
        {
            return;
        }

        var text = textBox.Text.Trim();
        double value;
        if (text.Length == 0)
        {
            value = double.NaN; // What NumberBox does with an empty box.
        }
        else if (numberBox.NumberFormatter is INumberParser parser && parser.ParseDouble(text) is { } parsed)
        {
            value = parsed;
        }
        else
        {
            return;
        }

        if (value.Equals(numberBox.Value) == false)
        {
            numberBox.Value = value;
        }
    }

    private async Task ConfirmAndCommitAsync()
    {
        this.CommitFocusedNumberBox();
        if (this.ViewModel.CanCommit() == false)
        {
            return;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = this.ViewModel.GetCommitSummary(),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        content.Children.Add(new TextBlock
        {
            Text = "The changes are written to your Steam account right away.",
            TextWrapping = TextWrapping.Wrap,
        });

        if (await this.ShowDialogAsync("Commit changes?", content, "Commit") == ContentDialogResult.Primary)
        {
            await this.ViewModel.StoreAsync();
        }
    }

    private async void OnResetAllStats(object sender, RoutedEventArgs e)
    {
        if (await this.ShowDialogAsync(
                "Reset all stats?",
                "This resets every statistic for this game on your Steam account — not just achievements. " +
                "Game progress tracked by stats will be lost. Are you sure you want to reset stats?",
                "Continue",
                closeText: "Cancel",
                defaultButton: ContentDialogButton.Close) != ContentDialogResult.Primary)
        {
            return;
        }

        var answer = await this.ShowDialogAsync(
            "Reset achievements too?",
            "Do you want to reset this game's achievements as well? Choosing \"Only stats\" leaves achievements as they are.",
            "Reset achievements too",
            secondaryText: "Only stats",
            closeText: "Cancel",
            defaultButton: ContentDialogButton.Close);
        if (answer == ContentDialogResult.None)
        {
            return;
        }

        var achievementsToo = answer == ContentDialogResult.Primary;
        if (await this.ShowDialogAsync(
                "Are you absolutely sure?",
                achievementsToo
                    ? "All stats AND all achievements for this game will be reset. This can't be undone."
                    : "All stats for this game will be reset. This can't be undone.",
                achievementsToo ? "Reset stats and achievements" : "Reset stats",
                closeText: "Cancel",
                destructive: true) != ContentDialogResult.Primary)
        {
            return;
        }

        await this.ViewModel.ResetAllStatsAsync(achievementsToo);
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        this.CommitFocusedNumberBox();
        if (this._closeConfirmed || this.ViewModel.HasUnsavedChanges == false)
        {
            return;
        }

        args.Cancel = true;
        if (await this.ShowDialogAsync(
                "Discard uncommitted changes?",
                this.ViewModel.UnsavedChangesText,
                "Discard",
                closeText: "Cancel",
                defaultButton: ContentDialogButton.Close) == ContentDialogResult.Primary)
        {
            this._closeConfirmed = true;
            this.Close();
        }
    }
    #endregion
}
