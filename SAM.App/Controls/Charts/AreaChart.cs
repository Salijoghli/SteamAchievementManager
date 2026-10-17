using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace SAM.App.Controls.Charts;

/// <summary>
/// A single-series line with a light area wash beneath it: a 2 px line with round joins, the
/// fill at about 10% opacity, an 8 px end dot with a 2 px surface ring and the last value
/// labelled beside it, plus a crosshair and tooltip on hover or arrow keys.
/// </summary>
public sealed class AreaChart : ChartBase
{
    private const double DotSize = 12; // 8 px dot inside a 2 px ring

    private Line? _crosshair;
    private Ellipse? _hoverDot;

    protected override double GetRightPadding(IReadOnlyList<ChartPoint> points)
    {
        var text = new TextBlock { Text = points[^1].ValueText, FontSize = AxisFontSize + 1 };
        return MeasureText(text).Width + 14;
    }

    protected override void RenderCore(IReadOnlyList<ChartPoint> points, Rect plot, double niceMax)
    {
        var step = points.Count > 1 ? plot.Width / (points.Count - 1) : 0;
        var line = new PointCollection();
        var area = new PointCollection { new Point(points.Count > 1 ? plot.Left : plot.Left + (plot.Width / 2), plot.Bottom) };
        for (var i = 0; i < points.Count; i++)
        {
            var x = points.Count > 1 ? plot.Left + (i * step) : plot.Left + (plot.Width / 2);
            var y = plot.Bottom - (Math.Max(0, points[i].Value) / niceMax * plot.Height);
            var p = new Point(x, y);
            this.MarkCenters.Add(p);
            line.Add(p);
            area.Add(p);
        }

        area.Add(new Point(this.MarkCenters[^1].X, plot.Bottom));

        this.Canvas.Children.Add(new Polygon { Points = area, Fill = this.SeriesBrush, Opacity = 0.1, IsHitTestVisible = false });
        this.Canvas.Children.Add(new Polyline
        {
            Points = line,
            Stroke = this.SeriesBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        });

        // Crosshair (behind the dots), hidden until hover/focus.
        this._crosshair = new Line
        {
            Y1 = plot.Top,
            Y2 = plot.Bottom,
            Stroke = this.BaselineBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        this.Canvas.Children.Add(this._crosshair);

        // End dot and the last value labelled beside it.
        var end = this.MarkCenters[^1];
        this.Canvas.Children.Add(this.CreateDot(end));
        var label = this.AddText(points[^1].ValueText, end.X + 8, 0, null, AxisFontSize + 1);
        Canvas.SetTop(label, end.Y - (MeasureText(label).Height / 2));

        this._hoverDot = this.CreateDot(end);
        this._hoverDot.Visibility = Visibility.Collapsed;
        this.Canvas.Children.Add(this._hoverDot);
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

    protected override void ShowHighlight(int index)
    {
        if (this._crosshair == null || this._hoverDot == null)
        {
            return;
        }

        if (index < 0 || index >= this.MarkCenters.Count)
        {
            this._crosshair.Visibility = Visibility.Collapsed;
            this._hoverDot.Visibility = Visibility.Collapsed;
            return;
        }

        var point = this.MarkCenters[index];
        this._crosshair.X1 = this._crosshair.X2 = Math.Round(point.X) + 0.5;
        this._crosshair.Visibility = Visibility.Visible;
        Canvas.SetLeft(this._hoverDot, point.X - (DotSize / 2));
        Canvas.SetTop(this._hoverDot, point.Y - (DotSize / 2));
        this._hoverDot.Visibility = Visibility.Visible;
    }
}
