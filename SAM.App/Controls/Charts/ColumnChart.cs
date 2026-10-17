using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SAM.App.Controls.Charts;

/// <summary>
/// A single-series column chart: columns at most 24 px wide with 4 px rounded tops, square at
/// the zero baseline, a direct value label on the tallest column only, and a hover/keyboard
/// tooltip with a wash behind the active column.
/// </summary>
public sealed class ColumnChart : ChartBase
{
    private const double MaxColumnWidth = 24;

    private Border? _hoverBand;
    private Rect _plot;
    private double _slotWidth;

    protected override void RenderCore(IReadOnlyList<ChartPoint> points, Rect plot, double niceMax)
    {
        this._plot = plot;
        this._slotWidth = plot.Width / points.Count;
        var columnWidth = Math.Clamp(this._slotWidth * 0.62, 2, MaxColumnWidth);

        // The hover wash sits behind the columns.
        this._hoverBand = new Border
        {
            Width = this._slotWidth,
            Height = plot.Height,
            Background = this.HoverBrush,
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetTop(this._hoverBand, plot.Top);
        this.Canvas.Children.Add(this._hoverBand);

        var tallest = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Value > points[tallest].Value)
            {
                tallest = i;
            }
        }

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var centerX = plot.Left + ((i + 0.5) * this._slotWidth);
            var height = Math.Max(0, point.Value / niceMax * plot.Height);
            var top = plot.Bottom - height;
            this.MarkCenters.Add(new Point(centerX, top));

            if (height <= 0)
            {
                continue;
            }

            var radius = Math.Min(4, Math.Min(height, columnWidth / 2));
            var column = new Border
            {
                Width = columnWidth,
                Height = height,
                Background = this.SeriesBrush,
                CornerRadius = new CornerRadius(radius, radius, 0, 0),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(column, centerX - (columnWidth / 2));
            Canvas.SetTop(column, top);
            this.Canvas.Children.Add(column);
        }

        // Direct label: the peak only.
        if (points[tallest].Value > 0)
        {
            var label = new TextBlock { Text = points[tallest].ValueText, FontSize = AxisFontSize + 1, IsHitTestVisible = false };
            var size = MeasureText(label);
            var mark = this.MarkCenters[tallest];
            Canvas.SetLeft(label, mark.X - (size.Width / 2));
            Canvas.SetTop(label, mark.Y - size.Height - 2);
            this.Canvas.Children.Add(label);
        }
    }

    protected override void ShowHighlight(int index)
    {
        if (this._hoverBand == null)
        {
            return;
        }

        if (index < 0)
        {
            this._hoverBand.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(this._hoverBand, this._plot.Left + (index * this._slotWidth));
        this._hoverBand.Visibility = Visibility.Visible;
    }
}
