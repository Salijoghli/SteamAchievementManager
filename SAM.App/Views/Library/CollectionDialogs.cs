using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.ViewModels;

namespace SAM.App.Views.Library;

/// <summary>Dialogs for SAM's own collections: naming one, and managing (create, rename, delete) them all.</summary>
internal static class CollectionDialogs
{
    /// <summary>Asks for a collection name. Returns the trimmed name, or null if cancelled or empty.</summary>
    public static async Task<string?> PromptNameAsync(XamlRoot root, ElementTheme theme, string title, string initial, string primaryText)
    {
        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = "Collection name",
            MaxLength = 100,
            Width = 320,
        };
        box.SelectAll();

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = title,
            Content = box,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = initial.Trim().Length > 0,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && box.Text.Trim().Length > 0)
            {
                e.Handled = true;
                dialog.Tag = "ok";
                dialog.Hide();
            }
        };

        var result = await dialog.ShowAsync();
        var name = box.Text.Trim();
        return (result == ContentDialogResult.Primary || Equals(dialog.Tag, "ok")) && name.Length > 0 ? name : null;
    }

    /// <summary>Lists SAM collections with rename and delete buttons, plus a box to create a new one.</summary>
    public static async Task ShowManageAsync(XamlRoot root, LibraryViewModel library)
    {
        var list = new StackPanel { Spacing = 4 };
        var newName = new TextBox { PlaceholderText = "New collection name", MaxLength = 100 };
        var create = new Button { Content = "Create", IsEnabled = false };
        newName.TextChanged += (_, _) => create.IsEnabled = newName.Text.Trim().Length > 0;

        var content = new StackPanel { Width = 420, Spacing = 16 };
        var createRow = new Grid { ColumnSpacing = 8 };
        createRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        createRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(create, 1);
        createRow.Children.Add(newName);
        createRow.Children.Add(create);
        content.Children.Add(createRow);
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });
        content.Children.Add(new TextBlock
        {
            Text = "Steam collections are read-only here; change them in Steam.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = library.RequestedTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Manage collections",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        void Rebuild()
        {
            list.Children.Clear();
            if (library.CustomCollections.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "You haven't made any collections yet.",
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                return;
            }

            foreach (var collection in library.CustomCollections)
            {
                list.Children.Add(CreateRow(collection));
            }
        }

        FrameworkElement CreateRow(GameCollection collection)
        {
            var row = new Grid { ColumnSpacing = 4, MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = collection.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock
            {
                Text = collection.AppIds.Count == 1 ? "1 game" : $"{LibraryFormat.Count(collection.AppIds.Count)} games",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            row.Children.Add(label);

            // Rename: an inline flyout (a second ContentDialog can't open while this one is shown).
            var renameBox = new TextBox { Text = collection.Name, MaxLength = 100, Width = 240 };
            var renameSave = new Button { Content = "Rename", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
            var renamePanel = new StackPanel { Spacing = 8 };
            renamePanel.Children.Add(renameBox);
            renamePanel.Children.Add(renameSave);
            var renameFlyout = new Flyout { Content = renamePanel };
            renameBox.TextChanged += (_, _) => renameSave.IsEnabled = renameBox.Text.Trim().Length > 0;
            renameSave.Click += (_, _) =>
            {
                renameFlyout.Hide();
                library.RenameCollection(collection.Id, renameBox.Text);
                Rebuild();
            };
            var rename = IconButton("", $"Rename {collection.Name}");
            rename.Flyout = renameFlyout;
            Grid.SetColumn(rename, 1);
            row.Children.Add(rename);

            var confirm = new Button { Content = "Delete", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
            var confirmPanel = new StackPanel { Spacing = 12, Width = 260 };
            confirmPanel.Children.Add(new TextBlock { Text = $"Delete \"{collection.Name}\"? The games stay in your library.", TextWrapping = TextWrapping.Wrap });
            confirmPanel.Children.Add(confirm);
            var confirmFlyout = new Flyout { Content = confirmPanel };
            confirm.Click += (_, _) =>
            {
                confirmFlyout.Hide();
                library.DeleteCollection(collection.Id);
                Rebuild();
            };
            var delete = IconButton("", $"Delete {collection.Name}");
            delete.Flyout = confirmFlyout;
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);
            return row;
        }

        void Create()
        {
            if (newName.Text.Trim().Length == 0)
            {
                return;
            }

            library.CreateCollection(newName.Text);
            newName.Text = "";
            Rebuild();
        }

        create.Click += (_, _) => Create();
        newName.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Create();
            }
        };

        Rebuild();
        await dialog.ShowAsync();
    }

    private static Button IconButton(string glyph, string name)
    {
        var button = new Button
        {
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }
}
