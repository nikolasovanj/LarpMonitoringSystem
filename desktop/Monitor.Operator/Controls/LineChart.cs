using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Monitor.Operator.Controls;

/// <summary>
/// A small live line chart: auto-scaled Y axis, three grid lines with labels, and the newest value
/// marked. Points are spaced evenly, which is right for a steady publish interval. It draws a
/// snapshot, so the view model must hand it a new list when the data changes.
/// </summary>
public sealed class LineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<LineChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<LineChart, IBrush?>(nameof(LineBrush), Brushes.DeepSkyBlue);

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<LineChart, IBrush?>(nameof(GridBrush), new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<LineChart, IBrush?>(nameof(LabelBrush), Brushes.Gray);

    static LineChart()
    {
        AffectsRender<LineChart>(ValuesProperty, LineBrushProperty, GridBrushProperty, LabelBrushProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    private const double LeftGutter = 44;
    private const double Pad = 6;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var w = Bounds.Width;
        var h = Bounds.Height;
        // Transparent fill so the whole control takes part in hit testing.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var values = Values;
        if (values is null || values.Count == 0 || w <= LeftGutter + 2 * Pad || h <= 2 * Pad) return;

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var v in values)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (double.IsInfinity(min)) return;

        // Pad the range so a flat line sits mid-chart and spikes do not touch the edge.
        var span = max - min;
        if (span < 1e-9) span = Math.Max(Math.Abs(max) * 0.1, 1);
        min -= span * 0.1;
        max += span * 0.1;

        var plot = new Rect(LeftGutter, Pad, w - LeftGutter - Pad, h - 2 * Pad);
        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;
        double X(int i) => values.Count == 1 ? plot.Right : plot.Left + i * plot.Width / (values.Count - 1);

        var gridPen = new Pen(GridBrush, 1);
        var typeface = new Typeface(FontFamily.Default);
        for (var i = 0; i < 3; i++)
        {
            var value = min + (max - min) * i / 2.0;
            var y = Y(value);
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var label = new FormattedText(value.ToString("0.##", CultureInfo.CurrentCulture),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, LabelBrush);
            var ly = Math.Clamp(y - label.Height / 2, 0, h - label.Height);
            context.DrawText(label, new Point(LeftGutter - label.Width - 4, ly));
        }

        // A NaN or infinite sample breaks the line instead of drawing to nowhere.
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var open = false;
            for (var i = 0; i < values.Count; i++)
            {
                var v = values[i];
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    if (open) { g.EndFigure(false); open = false; }
                    continue;
                }
                var p = new Point(X(i), Y(v));
                if (!open) { g.BeginFigure(p, false); open = true; }
                else g.LineTo(p);
            }
            if (open) g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(LineBrush, 1.6, lineJoin: PenLineJoin.Round), geometry);

        var last = values[^1];
        if (!double.IsNaN(last) && !double.IsInfinity(last))
            context.DrawEllipse(LineBrush, null, new Point(X(values.Count - 1), Y(last)), 3, 3);
    }
}
