using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SAM.App.Models;
using SAM.App.ViewModels;

namespace SAM.App.Views.Game;

/// <summary>
/// Lists a game's backups with Restore and Delete, plus "Create backup now". Choosing Restore
/// closes the dialog and sets <see cref="RestoreRequested"/>; the window runs the restore
/// after the dialog has closed (only one ContentDialog can be open at a time).
/// </summary>
public sealed partial class BackupsDialog : ContentDialog
{
    public BackupsDialog(GameBackupsViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
        this.Opened += (_, _) => this.ViewModel.Refresh();
    }

    public GameBackupsViewModel ViewModel { get; }

    /// <summary>The snapshot the user chose to restore, or null.</summary>
    public GameSnapshot? RestoreRequested { get; private set; }

    private void OnCreateClick(object sender, RoutedEventArgs e) => this.ViewModel.CreateNow();

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BackupItemViewModel item)
        {
            this.RestoreRequested = item.Snapshot;
            this.Hide();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BackupItemViewModel item } button)
        {
            return;
        }

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            FlyoutPresenterStyle = GameFormat.ThemedFlyoutPresenterStyle(this.ActualTheme),
        };
        var confirm = new Button
        {
            Content = "Delete",
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            this.ViewModel.Delete(item);
        };

        var panel = new StackPanel { Width = 240, Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Delete the backup from {item.DateText}? This can't be undone.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(confirm);
        flyout.Content = panel;
        flyout.ShowAt(button);
    }
}
