using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SAM.App.Views;

/// <summary>A small window showing a startup error (e.g. Steam not running) with a Close button.</summary>
public sealed class ErrorWindow : Window
{
    public ErrorWindow(string title, string message)
    {
        this.Title = "Steam Achievement Manager";
        this.SystemBackdrop = new MicaBackdrop();
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 320));
        this.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "SAM.ico"));

        var close = new Button
        {
            Content = "Close",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => this.Close();

        this.Content = new Grid
        {
            Padding = new Thickness(24),
            RowSpacing = 16,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
            Children =
            {
                new InfoBar
                {
                    Title = title,
                    Severity = InfoBarSeverity.Error,
                    IsOpen = true,
                    IsClosable = false,
                },
                WithRow(new ScrollViewer
                {
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                }, 1),
                WithRow(close, 2),
            },
        };
    }

    private static FrameworkElement WithRow(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        return element;
    }
}
