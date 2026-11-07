using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Roger.Views;

public sealed record HistoryTrendPoint(DateTimeOffset Timestamp, double? First, double? Second, double? Third, bool BreakBefore = false);

public sealed class HistoryTrendChart : FrameworkElement
{
    private IReadOnlyList<HistoryTrendPoint> _points = Array.Empty<HistoryTrendPoint>();
    private string _unit = "%";
    public void SetData(IReadOnlyList<HistoryTrendPoint> points, string unit)
    { _points = points; _unit = unit; InvalidateVisual(); }
    private Brush Color(string key) => TryFindResource(key) as Brush ?? Brushes.SlateGray;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth - 68, height = ActualHeight - 38;
        if (width < 30 || height < 30) return;
        var values = _points.SelectMany(p => new[] { p.First, p.Second, p.Third }).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
        double lower = _unit == "%" ? 0 : Math.Min(0, values.DefaultIfEmpty().Min() * 1.1);
        double upper = _unit == "%" ? 100 : Math.Max(1, values.DefaultIfEmpty().Max() * 1.1);
        for (int i = 0; i <= 4; i++)
        {
            double y = 4 + height * i / 4;
            dc.DrawLine(new Pen(Color("Line"), 1), new Point(0, y), new Point(width, y));
            dc.DrawText(Label((upper - (upper - lower) * i / 4).ToString("0.#", CultureInfo.CurrentCulture)), new Point(width + 8, y - 6));
        }
        dc.DrawText(Label(_unit), new Point(width + 8, height + 18));
        if (_points.Count == 0)
        {
            var waiting = Label("此时间范围内尚无历史数据");
            dc.DrawText(waiting, new Point(Math.Max(0, (width - waiting.Width) / 2), height / 2)); return;
        }
        DateTimeOffset first = _points[0].Timestamp, last = _points[^1].Timestamp;
        double span = Math.Max(60, (last - first).TotalSeconds);
        Draw(dc, p => p.First, Color("CpuAccent"));
        Draw(dc, p => p.Second, Color("GpuAccent"));
        Draw(dc, p => p.Third, Color("MemoryAccent"));
        dc.DrawText(Label(first.ToLocalTime().ToString("MM-dd HH:mm")), new Point(0, height + 17));
        var end = Label(last.ToLocalTime().ToString("MM-dd HH:mm"));
        dc.DrawText(end, new Point(Math.Max(0, width - end.Width), height + 17));

        void Draw(DrawingContext context, Func<HistoryTrendPoint, double?> get, Brush brush)
        {
            var geometry = new StreamGeometry(); Point? newest = null;
            var segmentStarts = new List<Point>();
            using (var path = geometry.Open())
            {
                bool started = false; DateTimeOffset? previousTime = null;
                foreach (var point in _points)
                {
                    if (point.BreakBefore) started = false;
                    double? value = get(point);
                    if (value is not double number || !double.IsFinite(number)) { started = false; previousTime = null; continue; }
                    if (previousTime.HasValue && point.Timestamp - previousTime.Value > TimeSpan.FromMinutes(3)) started = false;
                    var position = new Point(Math.Clamp((point.Timestamp - first).TotalSeconds / span, 0, 1) * width, 4 + height * (upper - number) / (upper - lower));
                    if (started) path.LineTo(position, true, false); else { path.BeginFigure(position, false, false); segmentStarts.Add(position); started = true; }
                    newest = position; previousTime = point.Timestamp;
                }
            }
            context.DrawGeometry(null, new Pen(brush, 1.7), geometry);
            foreach (var point in segmentStarts) context.DrawEllipse(brush, null, point, 1.5, 1.5);
            if (newest is Point final) context.DrawEllipse(brush, null, final, 2, 2);
        }
    }
    private FormattedText Label(string value) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Microsoft YaHei UI"), 10, Color("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
