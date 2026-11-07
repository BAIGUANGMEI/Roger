using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Roger.Services;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;
using Wpf = System.Windows;

namespace Roger.Views;

/// <summary>A themed native tray popup; WinForms retains keyboard, focus and outside-click dismissal.</summary>
public sealed class TrayContextMenu : Forms.ContextMenuStrip
{
    private readonly Forms.ToolStripMenuItem _open, _dock, _exit;
    private readonly BrandHeader _header;
    private readonly Func<bool> _isDockVisible;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Font _menuFont = new("Microsoft YaHei UI", 9.5f);
    private readonly Font _brandFont = new("Segoe UI", 11.5f, FontStyle.Bold);
    private readonly Font _detailFont = new("Microsoft YaHei UI", 8f);
    private readonly List<Bitmap> _images = new();
    private Palette _palette = ReadPalette();
    private int _renderDpi;
    private bool _ready;

    public TrayContextMenu(Action openMain, Action toggleDock, Action exitApp, Func<bool> isDockVisible)
    {
        ArgumentNullException.ThrowIfNull(openMain);
        ArgumentNullException.ThrowIfNull(toggleDock);
        ArgumentNullException.ThrowIfNull(exitApp);
        _isDockVisible = isDockVisible ?? throw new ArgumentNullException(nameof(isDockVisible));
        Font = _menuFont;
        AutoClose = true;
        AutoSize = true;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        _header = new BrandHeader(this);
        _open = new Forms.ToolStripMenuItem("打开 Roger", null, (_, _) => openMain());
        _dock = new Forms.ToolStripMenuItem("显示桌面监控条", null, (_, _) => toggleDock());
        _exit = new Forms.ToolStripMenuItem("退出 Roger", null, (_, _) => exitApp());
        Items.AddRange(new Forms.ToolStripItem[] { _header, new Forms.ToolStripSeparator(), _open, _dock, new Forms.ToolStripSeparator(), _exit });
        Renderer = new MenuRenderer(this);
        _ready = true;
        ThemeService.Changed += OnThemeChanged;
        RefreshAppearance();
    }

    public void RefreshAppearance()
    {
        if (IsDisposed || Disposing || !_ready) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(new Action(RefreshAppearance)); return; }
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        Palette next = ReadPalette();
        if (next != _palette || dpi != _renderDpi)
        {
            _palette = next;
            _renderDpi = dpi;
            ReplaceImages();
        }
        BackColor = _palette.Background;
        ForeColor = _palette.Text;
        Padding = new Forms.Padding(Scale(8));
        ImageScalingSize = new Size(Scale(20), Scale(20));
        int width = Scale(244);
        MinimumSize = new Size(width, 0);
        MaximumSize = new Size(width, 0);
        foreach (Forms.ToolStripItem item in Items)
        {
            item.AutoSize = false;
            item.Margin = Forms.Padding.Empty;
            item.Size = new Size(width - Padding.Horizontal, Scale(item == _header ? 54 : item is Forms.ToolStripSeparator ? 9 : 38));
        }
        Width = width;
        PerformLayout();
        UpdateRoundedRegion();
        Invalidate(true);
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        _dock.Checked = _isDockVisible();
        RefreshAppearance();
        base.OnOpening(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RefreshAppearance();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_ready) UpdateRoundedRegion();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => RefreshAppearance();
    private int Scale(int value) => (int)Math.Round(value * (DeviceDpi > 0 ? DeviceDpi : 96) / 96d);

    private void UpdateRoundedRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = Rounded(new Rectangle(0, 0, Width, Height), Scale(10));
        Region? old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    private void ReplaceImages()
    {
        foreach (Forms.ToolStripItem item in Items) item.Image = null;
        foreach (var image in _images) image.Dispose();
        _images.Clear();
        _images.Add(RenderAsset("RogerMark", 28, _palette.Accent));
        _open.Image = AddIcon("IconOverview", _palette.Muted);
        _dock.Image = AddIcon("IconHardware", _palette.Muted);
        _exit.Image = AddIcon("IconClose", _palette.Danger);
    }

    private Bitmap AddIcon(string key, Color color)
    {
        Bitmap bitmap = RenderAsset(key, 20, color);
        _images.Add(bitmap);
        return bitmap;
    }

    private Bitmap RenderAsset(string key, int logicalSize, Color color)
    {
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        int pixels = Math.Max(1, Scale(logicalSize));
        var visual = new Media.DrawingVisual();
        object? resource = Wpf.Application.Current?.TryFindResource(key);
        using (var drawing = visual.RenderOpen())
        {
            if (resource is Media.ImageSource image)
                drawing.DrawImage(image, new Wpf.Rect(0, 0, logicalSize, logicalSize));
            else if (resource is Media.Geometry geometry)
            {
                drawing.PushTransform(new Media.ScaleTransform(logicalSize / 24d, logicalSize / 24d));
                var pen = new Media.Pen(new Media.SolidColorBrush(Media.Color.FromArgb(color.A, color.R, color.G, color.B)), 1.8)
                    { StartLineCap = Media.PenLineCap.Round, EndLineCap = Media.PenLineCap.Round, LineJoin = Media.PenLineJoin.Round };
                drawing.DrawGeometry(null, pen, geometry);
                drawing.Pop();
            }
        }
        var rendered = new RenderTargetBitmap(pixels, pixels, dpi, dpi, Media.PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var decoded = Image.FromStream(stream);
        return new Bitmap(decoded);
    }

    private void PaintBrand(Graphics graphics, Size size)
    {
        if (_images.Count > 0) graphics.DrawImage(_images[0], new Rectangle(Scale(9), Scale(12), Scale(28), Scale(28)));
        var title = new Rectangle(Scale(48), Scale(7), size.Width - Scale(55), Scale(23));
        Forms.TextRenderer.DrawText(graphics, "Roger", _brandFont, title, _palette.Text, Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter);
        title.Y = Scale(29); title.Height = Scale(18);
        Forms.TextRenderer.DrawText(graphics, "电脑状态监控 · v" + UpdateService.CurrentVersion, _detailFont, title, _palette.Muted,
            Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeService.Changed -= OnThemeChanged;
            foreach (Forms.ToolStripItem item in Items) item.Image = null;
            foreach (var image in _images) image.Dispose();
            _images.Clear();
        }
        base.Dispose(disposing);
        if (disposing) { _menuFont.Dispose(); _brandFont.Dispose(); _detailFont.Dispose(); }
    }

    private static Palette ReadPalette() => new(
        ReadColor("Panel", "#141D2A", "#FFFFFF"), ReadColor("TextPrimary", "#EDF3FC", "#18283D"),
        ReadColor("Muted", "#9AAAC0", "#60728B"), ReadColor("Hover", "#202E41", "#EDF2F8"),
        ReadColor("Line", "#263346", "#E1E7EF"), ReadColor("Accent", "#65DEB1", "#087D5C"),
        ReadColor("Danger", "#F47D8F", "#C52C49"));
    private static Color ReadColor(string key, string dark, string light) =>
        Wpf.Application.Current?.TryFindResource(key) is Media.SolidColorBrush brush
            ? Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B)
            : ColorTranslator.FromHtml(ThemeService.IsDark ? dark : light);
    private sealed record Palette(Color Background, Color Text, Color Muted, Color Hover, Color Line, Color Accent, Color Danger);

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class BrandHeader(TrayContextMenu owner) : Forms.ToolStripItem
    {
        public override bool CanSelect => false;
        protected override void OnPaint(Forms.PaintEventArgs e) => owner.PaintBrand(e.Graphics, Size);
    }

    private sealed class MenuRenderer(TrayContextMenu owner) : Forms.ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var fill = new SolidBrush(owner._palette.Background);
            e.Graphics.FillRectangle(fill, e.ToolStrip.ClientRectangle);
        }
        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = new Pen(owner._palette.Line);
            using var path = Rounded(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), owner.Scale(10));
            e.Graphics.DrawPath(outline, path);
        }
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }
        protected override void OnRenderItemImage(Forms.ToolStripItemImageRenderEventArgs e)
        { }
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
            {
                Color fillColor = owner._palette.Hover;
                if (ReferenceEquals(e.Item, owner._exit))
                {
                    Color a = owner._palette.Background, b = owner._palette.Danger;
                    fillColor = Color.FromArgb((a.R * 7 + b.R) / 8, (a.G * 7 + b.G) / 8, (a.B * 7 + b.B) / 8);
                }
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var fill = new SolidBrush(fillColor);
                using var path = Rounded(new Rectangle(owner.Scale(4), owner.Scale(1), e.Item.Width - owner.Scale(8), e.Item.Height - owner.Scale(2)), owner.Scale(7));
                e.Graphics.FillPath(fill, path);
            }
            // The native menu gutter reserves a separate check/image column. Draw both here
            // instead so the row highlight fills the menu and checked icons have no blue frame.
            if (e.Item.Image is Image image)
                e.Graphics.DrawImage(image, new Rectangle(owner.Scale(12), (e.Item.Height - owner.Scale(20)) / 2, owner.Scale(20), owner.Scale(20)));
            if (e.Item is Forms.ToolStripMenuItem { Checked: true })
            {
                float x = e.Item.Width - owner.Scale(19), y = e.Item.Height / 2f;
                using var pen = new Pen(owner._palette.Accent, Math.Max(1.5f, owner.Scale(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, new[] { new PointF(x - owner.Scale(4), y), new PointF(x - owner.Scale(1), y + owner.Scale(3)), new PointF(x + owner.Scale(5), y - owner.Scale(3)) });
            }
        }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            Color text = !e.Item.Enabled ? owner._palette.Muted : ReferenceEquals(e.Item, owner._exit) ? owner._palette.Danger : owner._palette.Text;
            var bounds = new Rectangle(owner.Scale(44), 0, Math.Max(0, e.Item.Width - owner.Scale(72)), e.Item.Height);
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, owner._menuFont, bounds, text,
                Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.EndEllipsis);
        }
        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(owner._palette.Line);
            e.Graphics.DrawLine(pen, owner.Scale(8), e.Item.Height / 2, e.Item.Width - owner.Scale(8), e.Item.Height / 2);
        }
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        { }
    }
}
