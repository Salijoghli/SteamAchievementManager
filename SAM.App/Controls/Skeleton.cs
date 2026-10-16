using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace SAM.App.Controls;

/// <summary>
/// A placeholder block with a moving shimmer highlight, for loading states.
/// Size it like the content it stands in for:
/// <code>&lt;controls:Skeleton Width="120" Height="14" CornerRadius="4" /&gt;</code>
/// The animation runs only while the control is in the visual tree.
/// </summary>
public sealed class Skeleton : UserControl
{
    private readonly Border _border;
    private readonly TranslateTransform _shift = new();
    private Storyboard? _storyboard;

    public Skeleton()
    {
        this.IsTabStop = false;
        this.IsHitTestVisible = false;

        this._border = new Border();
        this.Content = this._border;

        this.Loaded += (_, _) => this.Start();
        this.Unloaded += (_, _) => this.Stop();
        this.ActualThemeChanged += (_, _) => this.ApplyBrush();
        this.RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => this._border.CornerRadius = this.CornerRadius);
        this.CornerRadius = new CornerRadius(4);
    }

    private void ApplyBrush()
    {
        // Look up the colours for this element's actual theme (App.xaml ThemeDictionaries).
        var themeKey = this.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        var colors = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[themeKey];
        var baseColor = (Windows.UI.Color)colors["SkeletonBaseColor"];
        var highlight = (Windows.UI.Color)colors["SkeletonHighlightColor"];

        // A diagonal band of highlight that slides from left to right across the block.
        this._border.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = this._shift,
            GradientStops =
            {
                new GradientStop { Color = baseColor, Offset = 0.0 },
                new GradientStop { Color = baseColor, Offset = 0.35 },
                new GradientStop { Color = highlight, Offset = 0.5 },
                new GradientStop { Color = baseColor, Offset = 0.65 },
                new GradientStop { Color = baseColor, Offset = 1.0 },
            },
        };
    }

    private void Start()
    {
        this.ApplyBrush();

        var animation = new DoubleAnimation
        {
            From = -1.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, this._shift);
        Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));

        this._storyboard = new Storyboard { Children = { animation } };
        this._storyboard.Begin();
    }

    private void Stop()
    {
        this._storyboard?.Stop();
        this._storyboard = null;
    }
}
