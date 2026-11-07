using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Roger.Views;
public sealed class HistoryChart : FrameworkElement
{
    private const int Capacity = 60;
    private readonly Queue<(double? Cpu, double? Gpu, double Ram)> _samples = new();

    public HistoryChart()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
    }

    public void Add(double? cpu, double? gpu, double ram)
    {
        _samples.Enqueue((cpu, gpu, ram));
        while (_samples.Count > Capacity) _samples.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var dpi = VisualTreeHelper.GetDpi(this);
        double left = PixelCenter(0, dpi.DpiScaleX);
        double right = PixelCenter(ActualWidth - 35, dpi.DpiScaleX);
        double top = PixelCenter(9, dpi.DpiScaleY);
        double bottom = PixelCenter(ActualHeight - 24, dpi.DpiScaleY);
        if (right <= left || bottom <= top) return;

        Brush muted = ThemeBrush("Muted", SystemColors.GrayTextBrush);
        var gridPen = new Pen(ThemeBrush("Line", SystemColors.ControlDarkBrush), 1 / dpi.DpiScaleY);
        for (int i = 0; i <= 4; i++)
        {
            double y = PixelCenter(top + (bottom - top) * i / 4, dpi.DpiScaleY);
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            var label = Label((100 - i * 25).ToString(CultureInfo.InvariantCulture), muted, dpi.PixelsPerDip);
            dc.DrawText(label, new Point(right + 8, y - label.Height / 2));
        }

        var earlier = Label("较早", muted, dpi.PixelsPerDip);
        var now = Label("现在", muted, dpi.PixelsPerDip);
        dc.DrawText(earlier, new Point(left, bottom + 7));
        dc.DrawText(now, new Point(Math.Max(left, right - now.Width), bottom + 7));

        var samples = _samples.ToArray();
        var plot = new Rect(new Point(left, top), new Point(right, bottom));
        DrawSeries(dc, samples.Select(s => (double?)s.Ram).ToArray(), ThemeBrush("MemoryAccent", SystemColors.HighlightBrush), plot);
        DrawSeries(dc, samples.Select(s => s.Gpu).ToArray(), ThemeBrush("GpuAccent", SystemColors.HotTrackBrush), plot);
        DrawSeries(dc, samples.Select(s => s.Cpu).ToArray(), ThemeBrush("CpuAccent", SystemColors.HighlightBrush), plot);

        if (samples.Length == 0)
        {
            var waiting = Label("等待真实采样", muted, dpi.PixelsPerDip);
            dc.DrawText(waiting, new Point(left + (plot.Width - waiting.Width) / 2, top + (plot.Height - waiting.Height) / 2));
        }
    }

    private Brush ThemeBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private static FormattedText Label(string value, Brush brush, double pixelsPerDip) =>
        new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, brush, pixelsPerDip);
    private static double PixelCenter(double coordinate, double scale) => (Math.Floor(coordinate * scale) + 0.5) / scale;

    private static void DrawSeries(DrawingContext dc, double?[] values, Brush brush, Rect plot)
    {
        if (values.Length == 0) return;
        var geo = new StreamGeometry();
        var isolated = new List<Point>();
        Point? newest = null;
        using (var ctx = geo.Open())
        {
            bool active = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (!Valid(values[i])) { active = false; continue; }
                var p = new Point(
                    plot.Left + plot.Width * (Capacity - values.Length + i) / (Capacity - 1d),
                    plot.Bottom - plot.Height * Math.Clamp(values[i]!.Value, 0, 100) / 100);
                // Missing readings end a segment; never bridge across unavailable samples.
                if (!active) { ctx.BeginFigure(p, false, false); active = true; }
                else ctx.LineTo(p, true, false);
                if ((i == 0 || !Valid(values[i - 1])) && (i == values.Length - 1 || !Valid(values[i + 1])))
                    isolated.Add(p);
                if (i == values.Length - 1) newest = p;
            }
        }
        geo.Freeze();
        var pen = new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geo);
        foreach (var point in isolated) dc.DrawEllipse(brush, null, point, 1.7, 1.7);
        if (newest is Point last) dc.DrawEllipse(brush, null, last, 2.4, 2.4);
    }

    private static bool Valid(double? value) => value is double reading && double.IsFinite(reading);
}
