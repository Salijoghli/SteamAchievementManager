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
/// The animation runs only while the control is in the visual tree and effectively visible.
/// </summary>
/// <remarks>
/// Skeletons are hidden by collapsing them or one of their ancestors, which doesn't unload
/// them, and the shimmer is a dependent (UI-thread) animation that would keep ticking. So on
/// Loaded the control listens for Visibility changes on itself and every ancestor (one walk
/// up the tree; the callbacks only fire when something is shown or hidden) and runs the
/// animation only while none of them is collapsed. The listeners are removed on Unloaded.
/// </remarks>
public sealed class Skeleton : UserControl
{
    private readonly Border _border;
    private readonly TranslateTransform _shift = new();
    private readonly List<(UIElement Element, long Token)> _visibilityListeners = [];
    private Storyboard? _storyboard;

    public Skeleton()
    {
        this.IsTabStop = false;
        this.IsHitTestVisible = false;

        this._border = new Border();
        this.Content = this._border;

        this.Loaded += (_, _) => this.OnLoaded();
        this.Unloaded += (_, _) => this.OnUnloaded();
        this.ActualThemeChanged += (_, _) => this.ApplyBrush();
        this.RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => this._border.CornerRadius = this.CornerRadius);
        this.CornerRadius = new CornerRadius(4);
    }

    private void OnLoaded()
    {
        // Loaded can fire again without an Unloaded in between; start from a clean slate.
        this.RemoveVisibilityListeners();
        for (DependencyObject? node = this; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement element)
            {
                var token = element.RegisterPropertyChangedCallback(VisibilityProperty, this.OnVisibilityChanged);
                this._visibilityListeners.Add((element, token));
            }
        }

        this.UpdateAnimation();
    }

    private void OnUnloaded()
    {
        this.RemoveVisibilityListeners();
        this.Stop();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property) => this.UpdateAnimation();

    private void RemoveVisibilityListeners()
    {
        foreach (var (element, token) in this._visibilityListeners)
        {
            element.UnregisterPropertyChangedCallback(VisibilityProperty, token);
        }

        this._visibilityListeners.Clear();
    }

    /// <summary>Runs the shimmer only while this control and all its ancestors are visible.</summary>
    private void UpdateAnimation()
    {
        var visible = this._visibilityListeners.Count > 0 &&
                      this._visibilityListeners.TrueForAll(l => l.Element.Visibility == Visibility.Visible);
        if (visible)
        {
            this.Start();
        }
        else
        {
            this.Stop();
        }
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
        if (this._storyboard != null)
        {
            return; // already running
        }

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
