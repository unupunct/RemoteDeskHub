using System.Windows;
using System.Windows.Media;

namespace RemoteDeskHub.Desktop.Controls;

public sealed class ChartSeries
{
    public string Name { get; init; } = string.Empty;
    public Color Color { get; init; } = Colors.DodgerBlue;
    public IReadOnlyList<double> Values { get; init; } = [];
}

/// <summary>Lightweight line chart used for CPU, memory, disk and network history.</summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<ChartSeries>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("No data yet", FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<ChartSeries>? Series
    {
        get => (IEnumerable<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20)
        {
            return;
        }

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(44, 52, 70)), 1);
        gridPen.Freeze();
        var textBrush = new SolidColorBrush(Color.FromRgb(102, 116, 140));
        textBrush.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        const double left = 38;
        const double top = 12;
        const double right = 12;
        const double bottom = 22;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);
        var max = Math.Max(1, Maximum);

        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotHeight * i / 4;
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + plotWidth, y));
            var label = max * (4 - i) / 4;
            var formatted = new FormattedText($"{label:0}", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, textBrush, dpi);
            dc.DrawText(formatted, new Point(left - formatted.Width - 6, y - formatted.Height / 2));
        }

        var series = Series?.Where(s => s.Values.Count > 0).ToList() ?? [];
        if (series.Count == 0)
        {
            var empty = new FormattedText(EmptyText, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, textBrush, dpi);
            dc.DrawText(empty, new Point(left + (plotWidth - empty.Width) / 2, top + (plotHeight - empty.Height) / 2));
            return;
        }

        var legendX = left + plotWidth;
        foreach (var item in series)
        {
            var pen = new Pen(new SolidColorBrush(item.Color), 2) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var values = item.Values;
            var step = values.Count == 1 ? plotWidth : plotWidth / (values.Count - 1);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (var i = 0; i < values.Count; i++)
                {
                    var x = left + step * i;
                    var normalized = Math.Clamp(values[i], 0, max) / max;
                    var y = top + plotHeight - normalized * plotHeight;
                    if (i == 0)
                    {
                        context.BeginFigure(new Point(x, y), false, false);
                    }
                    else
                    {
                        context.LineTo(new Point(x, y), true, false);
                    }
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);

            var legend = new FormattedText(item.Name, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10.5,
                new SolidColorBrush(item.Color), dpi);
            legendX -= legend.Width + 22;
            dc.DrawRectangle(new SolidColorBrush(item.Color), null, new Rect(legendX, top + 2, 8, 3));
            dc.DrawText(legend, new Point(legendX + 12, top - 3));
            legendX -= 6;
        }
    }
}

/// <summary>Radial gauge for a single percentage.</summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Gauge),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(Gauge),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 30)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2 + size * 0.05);
        var radius = size * 0.4;
        var thickness = Math.Max(4, size * 0.075);
        const double startAngle = 135;
        const double sweep = 270;

        var track = new Pen(new SolidColorBrush(Color.FromRgb(38, 45, 60)), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        track.Freeze();
        dc.DrawGeometry(null, track, BuildArc(center, radius, startAngle, sweep));

        var clamped = Math.Clamp(Value, 0, 100);
        if (clamped > 0.5)
        {
            var pen = new Pen(Accent, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, BuildArc(center, radius, startAngle, sweep * clamped / 100));
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var valueText = new FormattedText($"{clamped:0.#}%", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size * 0.19, new SolidColorBrush(Color.FromRgb(232, 236, 244)), dpi);
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2 - size * 0.03));

        if (!string.IsNullOrWhiteSpace(Label))
        {
            var labelText = new FormattedText(Label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), size * 0.1,
                new SolidColorBrush(Color.FromRgb(150, 163, 186)), dpi);
            dc.DrawText(labelText, new Point(center.X - labelText.Width / 2, center.Y + valueText.Height / 2 - size * 0.05));
        }
    }

    private static StreamGeometry BuildArc(Point center, double radius, double startAngle, double sweep)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweep);
        context.BeginFigure(start, false, false);
        context.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
