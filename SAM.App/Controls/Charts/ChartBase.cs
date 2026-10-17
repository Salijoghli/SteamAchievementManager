using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace SAM.App.Controls.Charts;

/// <summary>
/// Shared plumbing for the single-series charts: brushes, a value axis with clean ticks and
/// hairline gridlines, selective x-axis labels, and a hover/keyboard tooltip that reads out the
/// point under the pointer (or the one chosen with the arrow keys). Derived classes draw the marks.
/// </summary>
/// <remarks>
/// The chart redraws itself on a Canvas whenever its size, data or brushes change. Bind the
/// brushes with ThemeResource so the chart follows the light/dark theme.
/// </remarks>
public abstract class ChartBase : UserControl
{
    /// <summary>Space between the plot and the x-axis labels' baseline area.</summary>
    protected const double XAxisHeight = 22;

    /// <summary>Space above the plot for a direct label on the tallest mark.</summary>
    protected const double TopPadding = 22;

    protected const double AxisFontSize = 11;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(ChartBase), new PropertyMetadata(null, OnChartPropertyChanged));

    public static readonly DependencyProperty SeriesBrushProperty = DependencyProperty.Register(
        nameof(SeriesBrush), typeof(Brush), typeof(ChartBase), new PropertyMetadata(null, OnChartPropertyChanged));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(ChartBase), new PropertyMetadata(null, OnChartPropertyChanged));

    public static readonly DependencyProperty BaselineBrushProperty = DependencyProperty.Register(
        nameof(BaselineBrush), typeof(Brush), typeof(ChartBase), new PropertyMetadata(null, OnChartPropertyChanged));

    public static readonly DependencyProperty AxisLabelBrushProperty = DependencyProperty.Register(
        nameof(AxisLabelBrush), typeof(Brush), typeof(ChartBase), new PropertyMetadata(null, OnChartPropertyChanged));

    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBrush), typeof(Brush), typeof(ChartBase), new PropertyMetadata(null, OnChartPropertyChanged));

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(ChartBase), new PropertyMetadata(null, OnChartPropertyChanged));

    private readonly Border _tooltip;
    private readonly TextBlock _tooltipText;
    private int _activeIndex = -1;

    protected ChartBase()
    {
        this.Canvas = new Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        this.Content = this.Canvas;
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
        this.Canvas.PointerMoved += this.OnPointerMoved;
        this.Canvas.PointerExited += (_, _) =>
        {
            if (this.FocusState == FocusState.Unfocused)
            {
                this.SetActiveIndex(-1);
            }
        };
        this.GotFocus += (_, _) =>
        {
            if (this._activeIndex < 0 && this.MarkCenters.Count > 0)
            {
                this.SetActiveIndex(this.MarkCenters.Count - 1);
            }
        };
        this.LostFocus += (_, _) => this.SetActiveIndex(-1);
    }

    /// <summary>The data, in x order. Null or empty draws nothing.</summary>
    public IReadOnlyList<ChartPoint>? Points
    {
        get => (IReadOnlyList<ChartPoint>?)this.GetValue(PointsProperty);
        set => this.SetValue(PointsProperty, value);
    }

    /// <summary>Fill/stroke of the data marks.</summary>
    public Brush? SeriesBrush
    {
        get => (Brush?)this.GetValue(SeriesBrushProperty);
        set => this.SetValue(SeriesBrushProperty, value);
    }

    /// <summary>Hairline gridlines.</summary>
    public Brush? GridBrush
    {
        get => (Brush?)this.GetValue(GridBrushProperty);
        set => this.SetValue(GridBrushProperty, value);
    }

    /// <summary>The zero baseline and the hover crosshair.</summary>
    public Brush? BaselineBrush
    {
        get => (Brush?)this.GetValue(BaselineBrushProperty);
        set => this.SetValue(BaselineBrushProperty, value);
    }

    /// <summary>Axis tick labels (muted text).</summary>
    public Brush? AxisLabelBrush
    {
        get => (Brush?)this.GetValue(AxisLabelBrushProperty);
        set => this.SetValue(AxisLabelBrushProperty, value);
    }

    /// <summary>The surface the chart sits on: marker rings and the tooltip background.</summary>
    public Brush? SurfaceBrush
    {
        get => (Brush?)this.GetValue(SurfaceBrushProperty);
        set => this.SetValue(SurfaceBrushProperty, value);
    }

    /// <summary>Wash behind the hovered column.</summary>
    public Brush? HoverBrush
    {
        get => (Brush?)this.GetValue(HoverBrushProperty);
        set => this.SetValue(HoverBrushProperty, value);
    }

    protected Canvas Canvas { get; }

    /// <summary>Where each point's mark is, for the tooltip and hit testing. Set by <see cref="RenderCore"/>.</summary>
    protected List<Point> MarkCenters { get; } = [];

    /// <summary>Draws the marks for a non-empty <paramref name="points"/> inside <paramref name="plot"/>.</summary>
    protected abstract void RenderCore(IReadOnlyList<ChartPoint> points, Rect plot, double niceMax);

    /// <summary>Shows (index ≥ 0) or hides (-1) the derived chart's hover highlight.</summary>
    protected abstract void ShowHighlight(int index);

    /// <summary>Extra space to keep free on the right of the plot (e.g. for an end label).</summary>
    protected virtual double GetRightPadding(IReadOnlyList<ChartPoint> points) => 4;

    private static void OnChartPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ChartBase)d).Render();

    /// <summary>Rounds an axis maximum up to a clean value with about four ticks.</summary>
    internal static (double Max, double Step) NiceScale(double max)
    {
        if (double.IsFinite(max) == false || max <= 0)
        {
            return (4, 1);
        }

        var rough = max / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var normalized = rough / magnitude;
        var step = normalized switch
        {
            <= 1 => 1.0,
            <= 2 => 2.0,
            <= 2.5 when magnitude >= 10 => 2.5,
            <= 5 => 5.0,
            _ => 10.0,
        } * magnitude;
        step = Math.Max(1, step); // counts: no fractional ticks
        return (Math.Ceiling(max / step) * step, step);
    }

    /// <summary>Compact tick text: 0, 500, 1K, 2.5K, 1M.</summary>
    internal static string FormatTick(double value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 1_000 => (value / 1_000).ToString("0.#", CultureInfo.CurrentCulture) + "K",
        _ => value.ToString("0", CultureInfo.CurrentCulture),
    };

    protected static Size MeasureText(TextBlock text)
    {
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return text.DesiredSize;
    }

    protected TextBlock AddText(string text, double x, double y, Brush? brush, double fontSize = AxisFontSize)
    {
        var block = new TextBlock { Text = text, FontSize = fontSize, IsHitTestVisible = false };
        if (brush != null)
        {
            block.Foreground = brush;
        }

        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        this.Canvas.Children.Add(block);
        return block;
    }

    private void Render()
    {
        this.Canvas.Children.Clear();
        this.MarkCenters.Clear();
        this._activeIndex = -1;

        var points = this.Points;
        var width = this.ActualWidth;
        var height = this.ActualHeight;
        if (points == null || points.Count == 0 || width < 40 || height < 40)
        {
            return;
        }

        var (niceMax, step) = NiceScale(points.Max(p => p.Value));

        // Value axis: tick labels on the left, hairline gridlines across, a firmer zero baseline.
        var labels = new List<(TextBlock Text, double Value)>();
        var gutter = 0.0;
        for (var value = 0.0; value <= niceMax + (step / 2); value += step)
        {
            var text = new TextBlock { Text = FormatTick(value), FontSize = AxisFontSize, IsHitTestVisible = false };
            if (this.AxisLabelBrush != null)
            {
                text.Foreground = this.AxisLabelBrush;
            }

            gutter = Math.Max(gutter, MeasureText(text).Width);
            labels.Add((text, value));
        }

        gutter += 8;
        var right = this.GetRightPadding(points);
        var plot = new Rect(gutter, TopPadding, Math.Max(10, width - gutter - right), Math.Max(10, height - TopPadding - XAxisHeight));

        foreach (var (text, value) in labels)
        {
            var y = plot.Bottom - (value / niceMax * plot.Height);
            var line = new Line
            {
                X1 = plot.Left,
                X2 = plot.Right,
                Y1 = Math.Round(y) + 0.5,
                Y2 = Math.Round(y) + 0.5,
                StrokeThickness = 1,
                Stroke = value == 0 ? this.BaselineBrush : this.GridBrush,
                IsHitTestVisible = false,
            };
            this.Canvas.Children.Add(line);

            var size = MeasureText(text);
            Canvas.SetLeft(text, gutter - 8 - size.Width);
            Canvas.SetTop(text, y - (size.Height / 2));
            this.Canvas.Children.Add(text);
        }

        this.RenderCore(points, plot, niceMax);

        // Selective x labels: only the points that have one, skipping any that would collide.
        var lastRight = double.NegativeInfinity;
        for (var i = 0; i < points.Count && i < this.MarkCenters.Count; i++)
        {
            if (points[i].AxisLabel is not { Length: > 0 } label)
            {
                continue;
            }

            var text = new TextBlock { Text = label, FontSize = AxisFontSize, IsHitTestVisible = false };
            if (this.AxisLabelBrush != null)
            {
                text.Foreground = this.AxisLabelBrush;
            }

            var size = MeasureText(text);
            var x = Math.Clamp(this.MarkCenters[i].X - (size.Width / 2), 0, Math.Max(0, width - size.Width));
            if (x < lastRight + 6)
            {
                continue;
            }

            Canvas.SetLeft(text, x);
            Canvas.SetTop(text, plot.Bottom + 4);
            this.Canvas.Children.Add(text);
            lastRight = x + size.Width;
        }

        this._tooltip.Background = this.SurfaceBrush;
        this._tooltip.BorderBrush = this.GridBrush;
        this.Canvas.Children.Add(this._tooltip);
        this._tooltip.Visibility = Visibility.Collapsed;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (this.MarkCenters.Count == 0)
        {
            return;
        }

        var x = e.GetCurrentPoint(this.Canvas).Position.X;
        var nearest = 0;
        for (var i = 1; i < this.MarkCenters.Count; i++)
        {
            if (Math.Abs(this.MarkCenters[i].X - x) < Math.Abs(this.MarkCenters[nearest].X - x))
            {
                nearest = i;
            }
        }

        this.SetActiveIndex(nearest);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        var count = this.MarkCenters.Count;
        if (count > 0)
        {
            var index = e.Key switch
            {
                VirtualKey.Left => Math.Max(0, this._activeIndex - 1),
                VirtualKey.Right => Math.Min(count - 1, this._activeIndex + 1),
                VirtualKey.Home => 0,
                VirtualKey.End => count - 1,
                _ => -2,
            };
            if (index != -2)
            {
                this.SetActiveIndex(index);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    private void SetActiveIndex(int index)
    {
        if (index == this._activeIndex)
        {
            return;
        }

        this._activeIndex = index;
        this.ShowHighlight(index);

        var points = this.Points;
        if (index < 0 || points == null || index >= points.Count || index >= this.MarkCenters.Count)
        {
            this._tooltip.Visibility = Visibility.Collapsed;
            return;
        }

        this._tooltipText.Text = points[index].Description;
        this._tooltip.Visibility = Visibility.Visible;
        this._tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = this._tooltip.DesiredSize;
        var mark = this.MarkCenters[index];

        // Above the mark, centred and kept inside the chart; below it if there's no room above.
        var left = Math.Clamp(mark.X - (size.Width / 2), 0, Math.Max(0, this.ActualWidth - size.Width));
        var top = mark.Y - size.Height - 10;
        if (top < 0)
        {
            top = Math.Min(mark.Y + 12, Math.Max(0, this.ActualHeight - size.Height));
        }

        Canvas.SetLeft(this._tooltip, left);
        Canvas.SetTop(this._tooltip, top);

        if (this.FocusState != FocusState.Unfocused)
        {
            FrameworkElementAutomationPeer.FromElement(this._tooltipText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
