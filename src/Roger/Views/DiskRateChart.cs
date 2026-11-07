using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Roger.Views;
public sealed class DiskRateChart : FrameworkElement
{
    private readonly Queue<(double? Read, double? Write)> _samples = new();
    public void Reset() { _samples.Clear(); InvalidateVisual(); }
    public void Add(double? read, double? write)
    {
        _samples.Enqueue((Valid(read), Valid(write))); while (_samples.Count > 60) _samples.Dequeue(); InvalidateVisual();
    }
    private static double? Valid(double? v) => v is >= 0 && double.IsFinite(v.Value) ? v : null;
    private Brush Color(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = Math.Max(0, ActualWidth - 58), height = Math.Max(0, ActualHeight - 22);
        if (width <= 0 || height <= 0) return;
        var data = _samples.ToArray();
        double maximum = Math.Max(1_000_000, data.SelectMany(s => new[] { s.Read ?? 0, s.Write ?? 0 }).DefaultIfEmpty().Max() * 1.15);
        for (int i = 0; i <= 4; i++)
        {
            double y = .5 + height * i / 4;
            dc.DrawLine(new Pen(Color("Line", Brushes.SlateGray), 1), new Point(0, y), new Point(width, y));
            var text = new FormattedText((maximum * (1 - i / 4d) / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Color("Muted", Brushes.SlateGray), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(width + 8, Math.Clamp(y - 6, 0, height - 10)));
        }
        Draw(dc, data.Select(d => d.Read).ToArray(), Color("MemoryAccent", Brushes.SkyBlue), maximum, width, height);
        Draw(dc, data.Select(d => d.Write).ToArray(), Color("GpuAccent", Brushes.MediumPurple), maximum, width, height);
        dc.DrawText(new FormattedText("MB/s · 最近60次采样", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10,
            Color("Muted", Brushes.SlateGray), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(0, height + 6));
    }
    private static void Draw(DrawingContext dc, double?[] values, Brush color, double max, double width, double height)
    {
        var path = new StreamGeometry(); Point? last = null;
        using (var context = path.Open())
        {
            bool started = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is not double value) { started = false; continue; }
                var point = new Point(width * (60 - values.Length + i) / 59, height * (1 - value / max));
                if (!started) { context.BeginFigure(point, false, false); started = true; } else context.LineTo(point, true, false);
                last = point;
            }
        }
        dc.DrawGeometry(null, new Pen(color, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, path);
        if (last is Point p) dc.DrawEllipse(color, null, p, 2, 2);
    }
}
