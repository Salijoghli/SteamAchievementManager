using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SAM.App.Controls.Charts;
using SAM.App.Models;
using SAM.App.ViewModels.Pages;
using Windows.Foundation;
using Windows.System;

namespace SAM.App.Views.Autopilot;

/// <summary>
/// The Autopilot playtime curve: hours needed (y, linear) against the share of players who have
/// the achievement (x, log scale, common on the left, rare on the right) — the same log-scale
/// interpolation the planner uses, so straight segments here are exactly what it applies.
/// </summary>
/// <remarks>
/// One series, one hue: a 2 px line with round joins, 8 px dots with a 2 px surface ring,
/// recessive hairline gridlines, only the rarest point labelled directly, and a tooltip for the
/// point under the pointer (or chosen with the arrow keys). The editable table beside it is the
/// chart's table view.
/// </remarks>
public sealed class CurveChart : UserControl
{
    private const double AxisFontSize = 11;
    private const double DotSize = 12; // 8 px dot inside a 2 px ring
    private const double LeftGutterMin = 28;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<AutopilotCurvePoint>), typeof(CurveChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SeriesBrushProperty = DependencyProperty.Register(
        nameof(SeriesBrush), typeof(Brush), typeof(CurveChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(CurveChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty BaselineBrushProperty = DependencyProperty.Register(
        nameof(BaselineBrush), typeof(Brush), typeof(CurveChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty AxisLabelBrushProperty = DependencyProperty.Register(
        nameof(AxisLabelBrush), typeof(Brush), typeof(CurveChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBrush), typeof(Brush), typeof(CurveChart), new PropertyMetadata(null, OnChanged));

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Border _tooltip;
    private readonly TextBlock _tooltipText;
    private readonly List<(Point Center, AutopilotCurvePoint Point)> _marks = [];
    private Ellipse? _hoverDot;
    private int _active = -1;

    public CurveChart()
    {
        this.Content = this._canvas;
        this.IsTabStop = true;
        this.UseSystemFocusVisuals = true;

        this._tooltipText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        AutomationProperties.SetLiveSetting(this._tooltipText, AutomationLiveSetting.Polite);
        this._tooltip = new Border
        {
            Padding = new Thickness(8, 4, 8, 5),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = this._tooltipText,
        };

        this.SizeChanged += (_, _) => this.Render();
        this._canvas.PointerMoved += this.OnPointerMoved;
        this._canvas.PointerExited += (_, _) =>
        {
            if (this.FocusState == FocusState.Unfocused)
            {
                this.SetActive(-1);
            }
        };
        this.GotFocus += (_, _) => this.SetActive(this._marks.Count > 0 ? 0 : -1);
        this.LostFocus += (_, _) => this.SetActive(-1);
    }

    /// <summary>Curve points (any order; drawn most common first).</summary>
    public IReadOnlyList<AutopilotCurvePoint>? Points
    {
        get => (IReadOnlyList<AutopilotCurvePoint>?)this.GetValue(PointsProperty);
        set => this.SetValue(PointsProperty, value);
    }

    public Brush? SeriesBrush
    {
        get => (Brush?)this.GetValue(SeriesBrushProperty);
        set => this.SetValue(SeriesBrushProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)this.GetValue(GridBrushProperty);
        set => this.SetValue(GridBrushProperty, value);
    }

    public Brush? BaselineBrush
    {
        get => (Brush?)this.GetValue(BaselineBrushProperty);
        set => this.SetValue(BaselineBrushProperty, value);
    }

    public Brush? AxisLabelBrush
    {
        get => (Brush?)this.GetValue(AxisLabelBrushProperty);
        set => this.SetValue(AxisLabelBrushProperty, value);
    }

    public Brush? SurfaceBrush
    {
        get => (Brush?)this.GetValue(SurfaceBrushProperty);
        set => this.SetValue(SurfaceBrushProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CurveChart)d).Render();

    private static string Describe(AutopilotCurvePoint point) =>
        Strings.Format("Autopilot_CurvePointDescription", AutopilotFormat.Percent(point.Percent), AutopilotFormat.Hours(point.Hours));

    private TextBlock Text(string text)
    {
        var block = new TextBlock { Text = text, FontSize = AxisFontSize, IsHitTestVisible = false };
        if (this.AxisLabelBrush != null)
        {
            block.Foreground = this.AxisLabelBrush;
        }

        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block;
    }

    private void Render()
    {
        this._canvas.Children.Clear();
        this._marks.Clear();
        this._active = -1;

        var points = this.Points?.Where(p => p.Percent > 0 && p.Percent <= 100 && double.IsFinite(p.Hours)).OrderByDescending(p => p.Percent).ToList();
        var width = this.ActualWidth;
        var height = this.ActualHeight;
        if (points == null || points.Count == 0 || width < 80 || height < 60)
        {
            return;
        }

        // X: log10(percent) from 100% down to the decade below the rarest point (at least 1%).
        var minDecade = Math.Min(0, Math.Floor(Math.Log10(points.Min(p => p.Percent))));
        var maxLog = 2.0;
        var (niceMax, step) = ChartBase.NiceScale(Math.Max(1, points.Max(p => p.Hours)));

        // Y axis labels decide the left gutter.
        var yLabels = new List<(TextBlock Text, double Value)>();
        var gutter = LeftGutterMin;
        for (var value = 0.0; value <= niceMax + (step / 2); value += step)
        {
            var text = this.Text(AutopilotFormat.Hours(value));
            gutter = Math.Max(gutter, text.DesiredSize.Width + 8);
            yLabels.Add((text, value));
        }

        const double top = 20;
        const double bottom = 22;
        // The direct label wears primary text (not the series or axis colour).
        var rightLabel = new TextBlock { Text = AutopilotFormat.Hours(points[^1].Hours), FontSize = AxisFontSize + 1, IsHitTestVisible = false };
        rightLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var right = Math.Max(16, (rightLabel.DesiredSize.Width / 2) + 4);
        var plot = new Rect(gutter, top, Math.Max(10, width - gutter - right), Math.Max(10, height - top - bottom));

        double X(double percent) => plot.Left + ((maxLog - Math.Log10(percent)) / (maxLog - minDecade) * plot.Width);
        double Y(double hours) => plot.Bottom - (Math.Max(0, hours) / niceMax * plot.Height);

        // Hairline gridlines with hour labels; a firmer zero baseline.
        foreach (var (text, value) in yLabels)
        {
            var y = Math.Round(Y(value)) + 0.5;
            this._canvas.Children.Add(new Line
            {
                X1 = plot.Left,
                X2 = plot.Right,
                Y1 = y,
                Y2 = y,
                StrokeThickness = 1,
                Stroke = value == 0 ? this.BaselineBrush : this.GridBrush,
                IsHitTestVisible = false,
            });
            Canvas.SetLeft(text, gutter - 8 - text.DesiredSize.Width);
            Canvas.SetTop(text, y - (text.DesiredSize.Height / 2));
            this._canvas.Children.Add(text);
        }

        // Decade ticks on the x axis: 100%, 10%, 1%, 0.1%…
        for (var decade = maxLog; decade >= minDecade - 0.001; decade--)
        {
            var percent = Math.Pow(10, decade);
            var x = Math.Round(X(percent)) + 0.5;
            this._canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = plot.Top, Y2 = plot.Bottom, StrokeThickness = 1, Stroke = this.GridBrush, IsHitTestVisible = false });
            var label = this.Text(AutopilotFormat.Percent(percent));
            Canvas.SetLeft(label, Math.Clamp(x - (label.DesiredSize.Width / 2), 0, Math.Max(0, width - label.DesiredSize.Width)));
            Canvas.SetTop(label, plot.Bottom + 4);
            this._canvas.Children.Add(label);
        }

        // The curve (flat beyond its ends, as the planner clamps to them).
        var line = new PointCollection { new Point(plot.Left, Y(points[0].Hours)) };
        foreach (var point in points)
        {
            var center = new Point(X(point.Percent), Y(point.Hours));
            line.Add(center);
            this._marks.Add((center, point));
        }

        line.Add(new Point(plot.Right, Y(points[^1].Hours)));
        this._canvas.Children.Add(new Polyline
        {
            Points = line,
            Stroke = this.SeriesBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        });

        foreach (var (center, _) in this._marks)
        {
            this._canvas.Children.Add(this.CreateDot(center));
        }

        // Direct label on the rarest point only.
        var last = this._marks[^1].Center;
        Canvas.SetLeft(rightLabel, Math.Clamp(last.X - (rightLabel.DesiredSize.Width / 2), 0, Math.Max(0, width - rightLabel.DesiredSize.Width)));
        Canvas.SetTop(rightLabel, Math.Max(0, last.Y - rightLabel.DesiredSize.Height - 6));
        this._canvas.Children.Add(rightLabel);

        this._hoverDot = this.CreateDot(last);
        this._hoverDot.Width = this._hoverDot.Height = DotSize + 4;
        this._hoverDot.Visibility = Visibility.Collapsed;
        this._canvas.Children.Add(this._hoverDot);

        this._tooltip.Background = this.SurfaceBrush;
        this._tooltip.BorderBrush = this.GridBrush;
        this._tooltip.Visibility = Visibility.Collapsed;
        this._canvas.Children.Add(this._tooltip);

        AutomationProperties.SetHelpText(this, string.Join(", ", points.Select(Describe)));
    }

    private Ellipse CreateDot(Point center)
    {
        var dot = new Ellipse
        {
            Width = DotSize,
            Height = DotSize,
            Fill = this.SeriesBrush,
            Stroke = this.SurfaceBrush,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, center.X - (DotSize / 2));
        Canvas.SetTop(dot, center.Y - (DotSize / 2));
        return dot;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (this._marks.Count == 0)
        {
            return;
        }

        var position = e.GetCurrentPoint(this._canvas).Position;
        var nearest = 0;
        for (var i = 1; i < this._marks.Count; i++)
        {
            if (Distance(this._marks[i].Center, position) < Distance(this._marks[nearest].Center, position))
            {
                nearest = i;
            }
        }

        this.SetActive(nearest);
    }

    private static double Distance(Point a, Point b) => Math.Abs(a.X - b.X) + (Math.Abs(a.Y - b.Y) / 4);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (this._marks.Count > 0)
        {
            var index = e.Key switch
            {
                VirtualKey.Left => Math.Max(0, this._active - 1),
                VirtualKey.Right => Math.Min(this._marks.Count - 1, this._active + 1),
                VirtualKey.Home => 0,
                VirtualKey.End => this._marks.Count - 1,
                _ => -2,
            };
            if (index != -2)
            {
                this.SetActive(index);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    private void SetActive(int index)
    {
        if (index == this._active)
        {
            return;
        }

        this._active = index;
        if (index < 0 || index >= this._marks.Count || this._hoverDot == null)
        {
            this._tooltip.Visibility = Visibility.Collapsed;
            if (this._hoverDot != null)
            {
                this._hoverDot.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var (center, point) = this._marks[index];
        Canvas.SetLeft(this._hoverDot, center.X - (this._hoverDot.Width / 2));
        Canvas.SetTop(this._hoverDot, center.Y - (this._hoverDot.Height / 2));
        this._hoverDot.Visibility = Visibility.Visible;

        this._tooltipText.Text = Describe(point);
        this._tooltip.Visibility = Visibility.Visible;
        this._tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = this._tooltip.DesiredSize;
        var left = Math.Clamp(center.X - (size.Width / 2), 0, Math.Max(0, this.ActualWidth - size.Width));
        var topEdge = center.Y - size.Height - 12;
        if (topEdge < 0)
        {
            topEdge = Math.Min(center.Y + 12, Math.Max(0, this.ActualHeight - size.Height));
        }

        Canvas.SetLeft(this._tooltip, left);
        Canvas.SetTop(this._tooltip, topEdge);

        if (this.FocusState != FocusState.Unfocused)
        {
            FrameworkElementAutomationPeer.FromElement(this._tooltipText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
