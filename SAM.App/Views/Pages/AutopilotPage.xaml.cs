using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.Services;
using SAM.App.ViewModels;
using SAM.App.ViewModels.Pages;
using SAM.App.Views.Autopilot;

namespace SAM.App.Views.Pages;

/// <summary>
/// Autopilot: runs games in the background and unlocks achievements by the user's rules. A header
/// with Start/Stop and the overall state, live per-game status cards, and sections for every
/// setting (games, rules, schedule, behaviour) that are edited as a copy and saved together.
/// </summary>
public sealed partial class AutopilotPage : UserControl, IAutopilotDialogs
{
    private readonly AppServices _services;
    private readonly LibraryViewModel _library;
    private bool _isDialogOpen;

    /// <param name="library">The library's view model, for its game list, progress and playtime.</param>
    public AutopilotPage(AppServices services, LibraryViewModel library)
    {
        this._services = services;
        this._library = library;
        this.ViewModel = new AutopilotViewModel(services, library) { Dialogs = this };
        this.InitializeComponent();
        this.Loaded += (_, _) => this.ViewModel.OnShown();
        this.Unloaded += (_, _) => this.ViewModel.OnHidden();
    }

    public AutopilotViewModel ViewModel { get; }

    /// <summary>Two columns per section on wide windows, one below about 1100 px.</summary>
    private void OnColumnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var columns = width >= 1100 ? 2 : 1;
        PageLayout.Reflow(this.GamesColumns, columns);
        PageLayout.Reflow(this.RulesColumns, columns);
        PageLayout.Reflow(this.ScheduleColumns, columns);
        PageLayout.Reflow(this.BehaviourColumns, columns);

        // The curve table and chart sit side by side only when the pacing panel is wide enough.
        PageLayout.Reflow(this.CurveColumns, width >= 1600 || (width >= 760 && width < 1100) ? 2 : 1);
    }

    #region IAutopilotDialogs
    public async Task<IReadOnlyList<uint>?> PickGamesAsync(string title, string primaryText, IReadOnlyCollection<uint> alreadyListed)
    {
        var dialog = new GamePickerDialog(this._library, title, primaryText, alreadyListed);
        return await this.ShowAsync(dialog) == ContentDialogResult.Primary ? dialog.SelectedAppIds : null;
    }

    public async Task<bool> EditOverridesAsync(uint appId, string name, AutopilotGameOverride value, AutopilotConfig config)
    {
        var dialog = new OverridesDialog(this._services, appId, name, value, config, this._library.RequestedTheme);
        return await this.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task ShowPreviewAsync(IReadOnlyList<(uint AppId, string Name)> games, AutopilotConfig config) =>
        await this.ShowAsync(new PreviewDialog(this._services, games, config, this._library.RequestedTheme));

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = this._library.RequestedTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = primaryText,
            CloseButtonText = Strings.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await this.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<string?> PromptNameAsync(string title, string initial, string primaryText, Func<string, string?> validate)
    {
        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = Strings.Get("Autopilot_ProfileNamePlaceholder"),
            MaxLength = 60,
            Width = 320,
        };
        box.SelectAll();
        var error = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(box);
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            RequestedTheme = this._library.RequestedTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = Strings.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        void Check()
        {
            var name = box.Text.Trim();
            var message = name.Length == 0 ? null : validate(name);
            error.Text = message ?? "";
            error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && message == null;
        }

        box.TextChanged += (_, _) => Check();
        Check();

        var result = await this.ShowAsync(dialog);
        var chosen = box.Text.Trim();
        return result == ContentDialogResult.Primary && chosen.Length > 0 && validate(chosen) == null ? chosen : null;
    }
    #endregion

    /// <summary>Shows one dialog at a time (WinUI allows only one open ContentDialog).</summary>
    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (this._isDialogOpen || this.XamlRoot == null)
        {
            return ContentDialogResult.None;
        }

        this._isDialogOpen = true;
        try
        {
            dialog.XamlRoot = this.XamlRoot;
            return await dialog.ShowAsync();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Autopilot: couldn't show a dialog", e);
            return ContentDialogResult.None;
        }
        finally
        {
            this._isDialogOpen = false;
        }
    }
}
