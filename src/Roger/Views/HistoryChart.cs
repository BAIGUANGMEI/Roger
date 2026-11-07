using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Roger.Views;
public sealed class HistoryChart : FrameworkElement
{
    private readonly Queue<(double? Cpu, double? Gpu, double Ram)> _samples = new();
    public void Add(double? cpu, double? gpu, double ram) { _samples.Enqueue((cpu, gpu, ram)); while (_samples.Count > 60) _samples.Dequeue(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = Math.Max(0, ActualWidth - 34), h = Math.Max(0, ActualHeight - 20);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(39, 49, 61)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = h * i / 4;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            dc.DrawText(new FormattedText((100 - i * 25).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(w + 7, y - 5));
        }
        var samples = _samples.ToArray();
        DrawLine(dc, samples.Select(s => s.Cpu).ToArray(), Color.FromRgb(108, 229, 177), w, h, true);
        DrawLine(dc, samples.Select(s => s.Gpu).ToArray(), Color.FromRgb(160, 148, 248), w, h, false);
        DrawLine(dc, samples.Select(s => (double?)s.Ram).ToArray(), Color.FromRgb(105, 183, 243), w, h, false);
    }
    private static void DrawLine(DrawingContext dc, double?[] values, Color color, double w, double h, bool fill)
    {
        if (values.Length < 2 || w <= 0 || h <= 0) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool active = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is not double v || !double.IsFinite(v)) { active = false; continue; }
                var p = new Point(w * (60 - values.Length + i) / 59d, h * (1 - Math.Clamp(v, 0, 100) / 100));
                if (!active) { ctx.BeginFigure(p, false, false); active = true; } else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2), geo);
    }
}
