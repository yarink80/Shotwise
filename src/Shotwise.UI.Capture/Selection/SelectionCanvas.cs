using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.UI.Capture.Selection;

/// <summary>
/// Draws one monitor of the selection overlay in physical pixels: frozen snapshot, dimming with a clear hole for the selection or
/// hover target, crosshair, handles, size readout, guides, magnifier loupe, help overlay and transient messages.
/// </summary>
internal sealed class SelectionCanvas : FrameworkElement
{
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)));
    private static readonly Brush White = Brushes.White;
    private static readonly Brush WhiteSoft = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ShadowRing = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)));
    private static readonly Brush LabelBack = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x1B, 0x1C, 0x22)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Typeface UiFont = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiFontBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private const int Cells = 17;

    // Theme colours (Shotwise tokens) resolved once per selection: the overlay never touches resource lookups while painting.
    private readonly Brush Accent;
    private readonly Brush HoverFill;
    private readonly Brush Danger;
    private readonly Color _accentColor;

    private readonly SelectionController _ctl;
    private readonly MonitorInfo _monitor;
    private readonly BitmapSource _frozen;
    private readonly BgraImage _pixels;
    private readonly Func<bool> _isHelpHost;

    public SelectionCanvas(SelectionController ctl, MonitorInfo monitor, BitmapSource frozen, BgraImage pixels, Func<bool> isHelpHost)
    {
        _ctl = ctl; _monitor = monitor; _frozen = frozen; _pixels = pixels; _isHelpHost = isHelpHost;
        _accentColor = ResolveColor("Shotwise.Color.Accent", Color.FromRgb(0x5B, 0x4C, 0xF5));
        Accent = Freeze(new SolidColorBrush(_accentColor));
        HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x38, _accentColor.R, _accentColor.G, _accentColor.B)));
        Danger = Freeze(new SolidColorBrush(ResolveColor("Shotwise.Color.Danger", Color.FromRgb(0xE1, 0x1D, 0x48))));
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
        Focusable = false;
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static Color ResolveColor(string key, Color fallback) => Application.Current?.TryFindResource(key) is Color c ? c : fallback;

    private bool _magnifierReady;

    /// <summary>
    /// The loupe samples the full-resolution frozen image at 8× zoom; it is left out of the first overlay frame (lazy magnifier) and
    /// drawn from the next frame on, so the overlay's first frame only has to upload and draw the snapshot itself.
    /// </summary>
    public void EnableMagnifier()
    {
        if (_magnifierReady) return;
        _magnifierReady = true;
        if (_ctl.MagnifierOn) InvalidateVisual();
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    protected override void OnRender(DrawingContext dc)
    {
        var mb = _monitor.BoundsPx;
        double s = Scale;
        dc.PushTransform(new ScaleTransform(1 / s, 1 / s));
        var full = new Rect(0, 0, mb.Width, mb.Height);
        dc.DrawImage(_frozen, full);

        // ---- dim + clear hole
        var hole = BuildHole(mb);
        Geometry dimGeo = hole is null ? new RectangleGeometry(full) : new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), hole);
        dc.DrawGeometry(Dim, null, dimGeo);

        // ---- hover highlight (window / element)
        if (_ctl.HoverRect is { } hr && _ctl.Phase == SelectionPhase.Idle)
        {
            var r = Local(hr, mb);
            dc.DrawRectangle(HoverFill, new Pen(Accent, Math.Max(2, Math.Round(1.6 * s))), r);
            var caption = _ctl.Request.HoverElements ? _ctl.HoverElement?.Name : _ctl.HoverWindow?.Title;
            if (!string.IsNullOrWhiteSpace(caption)) DrawLabel(dc, Trim(caption!, 60) + $"  {hr.Width} × {hr.Height}", new Point(r.X, r.Y - 4), s, above: true, full);
        }

        // ---- committed multi areas
        foreach (var a in _ctl.Areas)
            dc.DrawRectangle(null, new Pen(Accent, 2), Local(a, mb));

        // ---- selection outline + handles
        var sel = _ctl.Selection ?? (_ctl.Request.Type == SelectionType.FixedRegion ? _ctl.FixedPreview : null);
        if (sel is { } sr && _ctl.Request.Type != SelectionType.Freehand)
        {
            var r = Local(sr, mb);
            double bw = Math.Max(2, Math.Round(1.6 * s));
            dc.DrawRectangle(null, new Pen(WhiteSoft, 1), new Rect(r.X - bw, r.Y - bw, r.Width + 2 * bw, r.Height + 2 * bw));
            dc.DrawRectangle(null, new Pen(Accent, bw), r);
            if (_ctl.Phase == SelectionPhase.Adjusting) DrawHandles(dc, r, s);
            DrawReadout(dc, sr, r, s, full);
        }
        if (_ctl.Request.Type == SelectionType.Freehand && _ctl.FreehandPoints.Count > 1)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                var pts = _ctl.FreehandPoints;
                c.BeginFigure(new Point(pts[0].X - mb.X, pts[0].Y - mb.Y), true, false);
                c.PolyLineTo(pts.Skip(1).Select(p => new Point(p.X - mb.X, p.Y - mb.Y)).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(HoverFill, new Pen(Accent, 2) { LineJoin = PenLineJoin.Round }, g);
        }

        // ---- crosshair
        if (_ctl.Phase != SelectionPhase.Adjusting && _ctl.Request.Type != SelectionType.FixedRegion) DrawCrosshair(dc, mb, s, full);

        // ---- guide label
        if (_ctl.IsDragging && _ctl.GuidesOn && _ctl.GuideSize is { } gs && _ctl.Selection is { } gsel && _monitor.BoundsPx.Contains(new PointI(gsel.Right - 1, gsel.Bottom - 1))
            && (_ctl.GuideSnapped || (Math.Abs(gs.Width - gsel.Width) <= 80 && Math.Abs(gs.Height - gsel.Height) <= 80)))
        {
            var r = Local(gsel, mb);
            string text = (_ctl.GuideSnapped ? "✔ " : "≈ ") + $"{gs.Width} × {gs.Height}";
            DrawLabel(dc, text, new Point(r.Right, r.Bottom + 30 * s), s, above: false, full, _ctl.GuideSnapped ? Accent : LabelBack, right: true);
        }

        // ---- magnifier
        if (_magnifierReady && _ctl.MagnifierOn && _ctl.Phase != SelectionPhase.Adjusting && mb.Contains(_ctl.Cursor)) DrawMagnifier(dc, mb, s, full);

        // ---- messages & hint & help
        if (_ctl.MessageKey is { } mk && mb.Contains(_ctl.Cursor))
            DrawLabel(dc, CS.Format(mk, SelectionController.MinSize), new Point(_ctl.Cursor.X - mb.X + 18 * s, _ctl.Cursor.Y - mb.Y + 18 * s), s, above: false, full, Danger);
        if (_ctl.Request.ShowHelpHint && !_ctl.HelpVisible && _isHelpHost() && _ctl.Phase == SelectionPhase.Idle)
            DrawLabel(dc, CS.Get("Selection_HelpHint"), new Point(mb.Width / 2.0 - 70 * s, 68 * s), s, above: false, full);
        if (_ctl.HelpVisible && _isHelpHost()) DrawHelp(dc, mb, s);

        dc.Pop();
    }

    private Geometry? BuildHole(RectI mb)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        bool any = false;
        foreach (var a in _ctl.Areas) { group.Children.Add(new RectangleGeometry(Local(a, mb))); any = true; }
        if (_ctl.Request.Type == SelectionType.Freehand && _ctl.FreehandPoints.Count > 2)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                var pts = _ctl.FreehandPoints;
                c.BeginFigure(new Point(pts[0].X - mb.X, pts[0].Y - mb.Y), true, true);
                c.PolyLineTo(pts.Skip(1).Select(p => new Point(p.X - mb.X, p.Y - mb.Y)).ToList(), true, true);
            }
            group.Children.Add(g); any = true;
        }
        else if (_ctl.DisplayRect is { } d && _ctl.Request.Type != SelectionType.Freehand)
        {
            group.Children.Add(new RectangleGeometry(Local(d, mb))); any = true;
        }
        return any ? group : null;
    }

    private static Rect Local(RectI r, RectI mb) => new(r.X - mb.X, r.Y - mb.Y, r.Width, r.Height);

    private void DrawHandles(DrawingContext dc, Rect r, double s)
    {
        // white rounded handles with an accent border and a soft dark ring so they read on any screenshot
        double h = 5.5 * s;
        var pen = new Pen(Accent, Math.Max(1.5, 1.4 * s));
        var corner = 2.5 * s;
        foreach (var p in new[]
        {
            new Point(r.Left, r.Top), new Point(r.Left + r.Width / 2, r.Top), new Point(r.Right, r.Top),
            new Point(r.Left, r.Top + r.Height / 2), new Point(r.Right, r.Top + r.Height / 2),
            new Point(r.Left, r.Bottom), new Point(r.Left + r.Width / 2, r.Bottom), new Point(r.Right, r.Bottom),
        })
        {
            dc.DrawRoundedRectangle(ShadowRing, null, new Rect(p.X - h - 1.5 * s, p.Y - h - 1.5 * s, 2 * h + 3 * s, 2 * h + 3 * s), corner + s, corner + s);
            dc.DrawRoundedRectangle(White, pen, new Rect(p.X - h, p.Y - h, 2 * h, 2 * h), corner, corner);
        }
    }

    private void DrawReadout(DrawingContext dc, RectI sel, Rect r, double s, Rect bounds)
    {
        // size chip: accent pill above the selection's top-left corner
        var text = $"{sel.Width} × {sel.Height}";
        DrawLabel(dc, text, new Point(r.X, r.Y - 10 * s), s, above: true, bounds, Accent);
    }

    private void DrawCrosshair(DrawingContext dc, RectI mb, double s, Rect full)
    {
        var c = _ctl.Cursor;
        double lx = c.X - mb.X + 0.5, ly = c.Y - mb.Y + 0.5;
        var dark = new Pen(Brushes.Black, 3 * 0.8);
        var light = new Pen(White, 1);
        bool inX = c.X >= mb.Left && c.X < mb.Right, inY = c.Y >= mb.Top && c.Y < mb.Bottom;
        if (_ctl.Request.SimplifiedCrosshair)
        {
            if (!(inX && inY)) return;
            double len = 14 * s, gap = 4 * s;
            foreach (var pen in new[] { dark, light })
            {
                dc.DrawLine(pen, new Point(lx - gap - len, ly), new Point(lx - gap, ly));
                dc.DrawLine(pen, new Point(lx + gap, ly), new Point(lx + gap + len, ly));
                dc.DrawLine(pen, new Point(lx, ly - gap - len), new Point(lx, ly - gap));
                dc.DrawLine(pen, new Point(lx, ly + gap), new Point(lx, ly + gap + len));
            }
            return;
        }
        foreach (var pen in new[] { dark, light })
        {
            if (inY) dc.DrawLine(pen, new Point(0, ly), new Point(full.Width, ly));
            if (inX) dc.DrawLine(pen, new Point(lx, 0), new Point(lx, full.Height));
        }
    }

    private void DrawMagnifier(DrawingContext dc, RectI mb, double s, Rect full)
    {
        int zoom = Math.Max(4, (int)Math.Round(8 * s));
        double box = Cells * zoom;
        double cx = _ctl.Cursor.X - mb.X, cy = _ctl.Cursor.Y - mb.Y;
        const double infoH = 38;
        double offset = 28 * s;
        double bx = cx + offset, by = cy + offset;
        if (bx + box > full.Width) bx = cx - offset - box;
        if (by + box + infoH * s > full.Height) by = cy - offset - box - infoH * s;
        bx = Math.Max(0, bx); by = Math.Max(0, by);
        var boxRect = new Rect(bx, by, box, box);

        double rr = 14 * s;
        dc.DrawRoundedRectangle(ShadowRing, null, new Rect(bx - 3 * s, by - 3 * s, box + 6 * s, box + 6 * s), rr + 3 * s, rr + 3 * s);
        dc.DrawRoundedRectangle(Brushes.Black, null, boxRect, rr, rr);
        dc.PushClip(new RectangleGeometry(boxRect, rr, rr));
        double destX = bx + box / 2 - (cx + 0.5) * zoom;
        double destY = by + box / 2 - (cy + 0.5) * zoom;
        dc.DrawImage(_frozen, new Rect(destX, destY, _frozen.PixelWidth * zoom, _frozen.PixelHeight * zoom));
        if (zoom >= 6)
        {
            var gridPen = new Pen(GridBrush, 1);
            for (int i = 1; i < Cells; i++)
            {
                dc.DrawLine(gridPen, new Point(bx + i * zoom, by), new Point(bx + i * zoom, by + box));
                dc.DrawLine(gridPen, new Point(bx, by + i * zoom), new Point(bx + box, by + i * zoom));
            }
        }
        var center = new Rect(bx + (Cells / 2) * zoom, by + (Cells / 2) * zoom, zoom, zoom);
        // crosshair through the centre pixel (accent, translucent) + the centre cell itself
        var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, _accentColor.R, _accentColor.G, _accentColor.B)), Math.Max(1, s));
        dc.DrawLine(crossPen, new Point(bx, center.Y + zoom / 2.0), new Point(bx + box, center.Y + zoom / 2.0));
        dc.DrawLine(crossPen, new Point(center.X + zoom / 2.0, by), new Point(center.X + zoom / 2.0, by + box));
        dc.DrawRectangle(null, new Pen(Brushes.Black, 3), center);
        dc.DrawRectangle(null, new Pen(White, 1), center);
        dc.Pop();
        // rounded border: white ring with an accent hairline outside
        dc.DrawRoundedRectangle(null, new Pen(White, Math.Max(2.5, 2.5 * s)), boxRect, rr, rr);
        dc.DrawRoundedRectangle(null, new Pen(Accent, Math.Max(1, s)), new Rect(bx - 1.5 * s, by - 1.5 * s, box + 3 * s, box + 3 * s), rr + 1.5 * s, rr + 1.5 * s);

        // info panel: coordinates + colour
        int px = Math.Clamp((int)cx, 0, _pixels.Width - 1), py = Math.Clamp((int)cy, 0, _pixels.Height - 1);
        var col = _pixels.GetPixel(px, py);
        var info = new Rect(bx, by + box + 6 * s, box, infoH * s);
        dc.DrawRoundedRectangle(LabelBack, null, info, 9 * s, 9 * s);
        dc.DrawText(Text($"{_ctl.Cursor.X}, {_ctl.Cursor.Y}", 11 * s, White, true), new Point(info.X + 10 * s, info.Y + 3 * s));
        var swatch = new Rect(info.X + 10 * s, info.Y + 20 * s, 12 * s, 12 * s);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(col.R, col.G, col.B)), new Pen(White, 1), swatch, 3 * s, 3 * s);
        dc.DrawText(Text($"#{col.R:X2}{col.G:X2}{col.B:X2}  ({col.R}, {col.G}, {col.B})", 11 * s, White, false), new Point(swatch.Right + 6 * s, info.Y + 19 * s));
    }

    private void DrawHelp(DrawingContext dc, RectI mb, double s)
    {
        var lines = new (string Key, string Text)[]
        {
            ("Esc", CS.Get("Help_Cancel")), ("F1", CS.Get("Help_ToggleHelp")), ("← ↑ → ↓", CS.Get("Help_Arrows")),
            ("Shift + ←↑→↓", CS.Get("Help_ArrowsFast")), ("Ctrl / Alt + ←↑→↓", CS.Get("Help_ArrowsResize")),
            ("Enter / Space", CS.Get("Help_Confirm")), ("Space (drag)", CS.Get("Help_MoveWhileDrag")), ("F / Shift+F", CS.Get("Help_Fullscreen")),
            ("1 / 4 / 6", CS.Get("Help_AspectLocks")), ("0", CS.Get("Help_AspectOff")), ("M", CS.Get("Help_Magnifier")), ("G", CS.Get("Help_Guides")),
            ("Del", CS.Get("Help_RemoveArea")), (CS.Get("Help_RightClickKey"), CS.Get("Help_RightClick")),
        };
        double w = 600 * s, lineH = 22 * s, pad = 16 * s;
        double h = pad * 2 + lineH * (lines.Length + 1);
        var rect = new Rect((mb.Width - w) / 2, (mb.Height - h) / 2, w, h);
        dc.DrawRoundedRectangle(ShadowRing, null, new Rect(rect.X - 4 * s, rect.Y - 4 * s, rect.Width + 8 * s, rect.Height + 8 * s), 18 * s, 18 * s);
        dc.DrawRoundedRectangle(LabelBack, new Pen(Accent, Math.Max(1, s)), rect, 14 * s, 14 * s);
        dc.DrawText(Text(CS.Get("Help_Title"), 15 * s, White, true), new Point(rect.X + pad, rect.Y + pad - 2 * s));
        double y = rect.Y + pad + lineH;
        foreach (var (k, t) in lines)
        {
            dc.DrawText(Text(k, 12 * s, Accent, true), new Point(rect.X + pad, y));
            dc.DrawText(Text(t, 12 * s, White, false), new Point(rect.X + pad + 190 * s, y));
            y += lineH;
        }
    }

    private void DrawLabel(DrawingContext dc, string text, Point anchor, double s, bool above, Rect bounds, Brush? back = null, bool right = false)
    {
        var ft = Text(text, 12 * s, White, true);
        double w = ft.Width + 20 * s, h = ft.Height + 8 * s;
        double x = right ? anchor.X - w : anchor.X;
        double y = above ? anchor.Y - h : anchor.Y;
        if (y < 0) y = anchor.Y + 8 * s;
        x = Math.Clamp(x, 0, Math.Max(0, bounds.Width - w));
        y = Math.Clamp(y, 0, Math.Max(0, bounds.Height - h));
        dc.DrawRoundedRectangle(back ?? LabelBack, null, new Rect(x, y, w, h), h / 2, h / 2);
        dc.DrawText(ft, new Point(x + 10 * s, y + 4 * s));
    }

    private static FormattedText Text(string text, double size, Brush brush, bool bold)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? UiFontBold : UiFont, size, brush, 1.0);

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
