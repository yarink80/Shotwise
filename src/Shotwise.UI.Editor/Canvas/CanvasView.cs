using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Editor;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using Orientation = System.Windows.Controls.Orientation;

namespace Shotwise.UI.Editor.Surface;

/// <summary>
/// The document canvas: SkiaSharp surface that draws <see cref="IDocumentRenderer.RenderPage"/> at the current zoom
/// (cached; re-rendered only when the session invalidates), a checkerboard for transparency, the session overlay
/// primitives, scroll bars, input forwarding in canvas coordinates, the inline text editor and drag/drop in.
/// All view maths is in device pixels; <see cref="Zoom"/> = device pixels per canvas pixel.
/// </summary>
internal sealed class CanvasView : Grid
{
    private static readonly double[] ZoomLevels = { 0.05, 0.1, 0.125, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64 };
    private new const double Margin = 40;
    private const int MaxCacheSide = 8192;

    private readonly EditorContext _ctx;
    private readonly CanvasSurface _surface;
    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal };
    private readonly InlineTextEditor _editor = new();
    private readonly ILogger _log;
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private SKShader? _checker;

    private ImageEditorDocument? _doc;
    private EditorSession? _s;
    private double _zoom = 1;
    private double _ox, _oy;
    private int _viewW, _viewH;
    private double _dpi = 1;
    private bool _fitPending;
    private bool _spaceDown;
    private bool _panning;
    private Point _panStart;
    private double _panOx, _panOy;
    private bool _updatingBars;
    private PointerButton _heldButton;

    // render cache
    private SKBitmap? _cacheBmp;
    private SKImage? _cacheImage;
    private Rect2 _cacheRegion;
    private double _cacheZoom;
    private long _cacheVersion = -1;
    private Guid? _cacheExclude;
    private long _version;
    private Rect2? _dirty;
    private bool _fullDirty = true;

    // overlay bitmap caches
    private readonly List<(Mask Mask, uint Tint, bool Invert, SKBitmap Bmp)> _tintCache = new();
    private (BgraImage Image, SKBitmap Bmp)? _overlayImage;

    public CanvasView(EditorContext ctx)
    {
        _ctx = ctx;
        _log = ctx.Log<CanvasView>();
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Advanced ▸ Hardware-accelerated rendering: GPU (OpenGL) surface, CPU surface otherwise / on failure.
        _surface = new CanvasSurface(ctx.Settings.Current.Advanced.HardwareAcceleratedRendering, _log);
        _surface.Paint += OnPaintSurface;
        var holder = new Grid { ClipToBounds = true };
        holder.Children.Add(_surface);
        holder.Children.Add(_editor);
        SetRow(holder, 0);
        SetColumn(holder, 0);
        Children.Add(holder);

        SetColumn(_vbar, 1);
        Children.Add(_vbar);
        SetRow(_hbar, 1);
        Children.Add(_hbar);
        // workspace token behind the image and the scroll bars (Light / Dark)
        SetResourceReference(BackgroundProperty, "Shotwise.CanvasBrush");
        _vbar.SetResourceReference(Control.BackgroundProperty, "Shotwise.CanvasBrush");
        _hbar.SetResourceReference(Control.BackgroundProperty, "Shotwise.CanvasBrush");
        // repaint the Skia workspace when the theme swaps the canvas token
        SetResourceReference(WorkspaceTokenProperty, "Shotwise.Color.Canvas");

        _vbar.Scroll += (_, _) => { if (!_updatingBars) { _oy = Margin - _vbar.Value; _fitPending = false; Redraw(); } };
        _hbar.Scroll += (_, _) => { if (!_updatingBars) { _ox = Margin - _hbar.Value; _fitPending = false; Redraw(); } };

        Focusable = true;
        FocusVisualStyle = null;
        AllowDrop = true;
        SizeChanged += (_, _) => OnViewResized();
        _editor.CommitRequested += text => CommitEditor(text);
        _editor.TextEdited += text => _s?.UpdateTextEdit(text);
        ctx.Settings.Changed += OnSettingsChanged;
        _editor.ApplyPreferences(ctx.Prefs, ctx.Settings.Current.General.Language);
        Cursor = Cursors.Arrow;
    }

    // ------------------------------------------------------------------ public surface

    public event Action<double>? ZoomChanged;
    public event Action<Point2?>? PointerMoved;
    public event Action<Point2>? ContextMenuRequested;
    public event Action<IReadOnlyList<string>, Point2>? NonImageFilesDropped;
    public event Action? EscapeUnhandled;
    public event Action<EditorMessage>? SessionMessage;

    public EditorSession? Session => _s;
    public double Zoom => _zoom;
    public bool FitToWindow => _doc?.FitToWindow ?? false;

    public bool ShowPixelGrid
    {
        get => _doc?.ShowPixelGrid ?? false;
        set
        {
            if (_doc is null) return;
            _doc.ShowPixelGrid = value;
            Redraw();
        }
    }

    public void SetDocument(ImageEditorDocument? doc)
    {
        if (ReferenceEquals(doc, _doc)) return;
        SaveViewState();
        Detach();
        _doc = doc;
        _s = doc?.Session;
        InvalidateCache();
        if (_s is not null)
        {
            _s.Invalidated += OnSessionInvalidated;
            _s.BeginTextEdit += OnBeginTextEdit;
            _s.EndTextEditRequested += OnEndTextEditRequested;
            _s.ActivePageChanged += OnPageChanged;
            _s.ActiveToolChanged += OnToolChanged;
            _s.Message += OnSessionMessage;
            _s.BusyChanged += OnToolChanged;
            _zoom = doc!.Zoom <= 0 ? 1 : doc.Zoom;
            _ox = _oy = 0;
            _fitPending = doc.FitToWindow;
            if (!_fitPending) { _ox = doc.Pan.X; _oy = doc.Pan.Y; }
            _centerPending = !_fitPending && doc.Pan == default;
            if (_s.ActiveTextEdit is { } pending) OnBeginTextEdit(_s, pending);
        }
        _editor.End();
        Redraw();
        ZoomChanged?.Invoke(_zoom);
    }

    private bool _centerPending;

    private void SaveViewState()
    {
        if (_doc is null) return;
        _doc.Zoom = _zoom;
        _doc.Pan = new Point2(_ox, _oy);
    }

    private void Detach()
    {
        if (_s is null) return;
        if (_s.ActiveTextEdit is not null) CommitEditor(_editor.IsEditing ? _editor.CurrentText : null);
        _s.Invalidated -= OnSessionInvalidated;
        _s.BeginTextEdit -= OnBeginTextEdit;
        _s.EndTextEditRequested -= OnEndTextEditRequested;
        _s.ActivePageChanged -= OnPageChanged;
        _s.ActiveToolChanged -= OnToolChanged;
        _s.Message -= OnSessionMessage;
        _s.BusyChanged -= OnToolChanged;
    }

    /// <summary>
    /// Real window closure (R5-#5): detaches the session, unsubscribes from the shared settings service and frees the
    /// native Skia caches so the closed editor's visual tree can be collected.
    /// </summary>
    public void ReleaseResources()
    {
        SetDocument(null);
        _ctx.Settings.Changed -= OnSettingsChanged;
        _surface.Release();
        _cacheImage?.Dispose();
        _cacheImage = null;
        _cacheBmp?.Dispose();
        _cacheBmp = null;
        foreach (var t in _tintCache) t.Bmp.Dispose();
        _tintCache.Clear();
        _overlayImage?.Bmp.Dispose();
        _overlayImage = null;
        _checker?.Dispose();
        _checker = null;
        _paint.Dispose();
    }

    public void Redraw() => _surface.Invalidate();

    public void RefreshWorkspace() => Redraw();

    public void ZoomIn() => StepZoom(+1);
    public void ZoomOut() => StepZoom(-1);

    public void ActualSize() => SetZoom(1, null, userAction: true);

    public void ZoomToFit(bool shrinkOnly = false)
    {
        if (_s is null || _viewW <= 0) { _fitPending = true; return; }
        var page = _s.ActivePage;
        var availW = Math.Max(50, _viewW - 2 * Margin);
        var availH = Math.Max(50, _viewH - 2 * Margin);
        var z = Math.Min(availW / Math.Max(1, page.CanvasSize.Width), availH / Math.Max(1, page.CanvasSize.Height));
        if (shrinkOnly) z = Math.Min(z, 1);
        z = Math.Clamp(z, ZoomLevels[0], ZoomLevels[^1]);
        _zoom = z;
        if (_doc is not null) _doc.FitToWindow = true;
        CenterContent();
        ApplyZoomSideEffects();
    }

    public void SetZoom(double zoom, Point? anchorPx, bool userAction)
    {
        zoom = Math.Clamp(zoom, ZoomLevels[0], ZoomLevels[^1]);
        if (_s is null) return;
        if (_doc is not null && userAction) _doc.FitToWindow = false;
        _fitPending = false;
        var a = anchorPx ?? new Point(_viewW / 2.0, _viewH / 2.0);
        var cx = (a.X - _ox) / _zoom;
        var cy = (a.Y - _oy) / _zoom;
        _zoom = zoom;
        _ox = a.X - cx * zoom;
        _oy = a.Y - cy * zoom;
        ClampOrigin();
        ApplyZoomSideEffects();
    }

    private void StepZoom(int dir)
    {
        var current = _zoom;
        double next = current;
        if (dir > 0) next = ZoomLevels.FirstOrDefault(z => z > current * 1.001, ZoomLevels[^1]);
        else next = ZoomLevels.LastOrDefault(z => z < current / 1.001, ZoomLevels[0]);
        SetZoom(next, null, userAction: true);
    }

    private void ApplyZoomSideEffects()
    {
        UpdateEditorPlacement();
        UpdateScrollBars();
        Redraw();
        ZoomChanged?.Invoke(_zoom);
    }

    private void CenterContent()
    {
        if (_s is null) return;
        var cw = _s.ActivePage.CanvasSize.Width * _zoom;
        var ch = _s.ActivePage.CanvasSize.Height * _zoom;
        _ox = (_viewW - cw) / 2;
        _oy = (_viewH - ch) / 2;
        ClampOrigin();
    }

    private void ClampOrigin()
    {
        if (_s is null) return;
        var cw = _s.ActivePage.CanvasSize.Width * _zoom;
        var ch = _s.ActivePage.CanvasSize.Height * _zoom;
        _ox = cw + 2 * Margin <= _viewW ? (_viewW - cw) / 2 : Math.Clamp(_ox, _viewW - cw - Margin, Margin);
        _oy = ch + 2 * Margin <= _viewH ? (_viewH - ch) / 2 : Math.Clamp(_oy, _viewH - ch - Margin, Margin);
    }

    private void UpdateScrollBars()
    {
        if (_s is null) return;
        _updatingBars = true;
        try
        {
            var cw = _s.ActivePage.CanvasSize.Width * _zoom + 2 * Margin;
            var ch = _s.ActivePage.CanvasSize.Height * _zoom + 2 * Margin;
            ConfigureBar(_hbar, cw, _viewW, Margin - _ox);
            ConfigureBar(_vbar, ch, _viewH, Margin - _oy);
        }
        finally { _updatingBars = false; }
    }

    private static void ConfigureBar(ScrollBar bar, double content, double viewport, double value)
    {
        var max = Math.Max(0, content - viewport);
        bar.IsEnabled = max > 0.5;
        // keep the layout stable but do not paint an idle track over the workspace
        bar.Opacity = max > 0.5 ? 1 : 0;
        bar.Minimum = 0;
        bar.Maximum = max;
        bar.ViewportSize = Math.Max(1, viewport);
        bar.LargeChange = Math.Max(20, viewport * 0.9);
        bar.SmallChange = 40;
        bar.Value = Math.Clamp(value, 0, max);
    }

    private void OnViewResized()
    {
        if (_s is null) return;
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _viewW = (int)Math.Round(_surface.ActualWidth * _dpi);
        _viewH = (int)Math.Round(_surface.ActualHeight * _dpi);
        if (_doc?.FitToWindow == true) ZoomToFit(shrinkOnly: true);
        else { ClampOrigin(); UpdateScrollBars(); UpdateEditorPlacement(); Redraw(); }
    }

    // ------------------------------------------------------------------ session events

    private void OnSessionInvalidated(object? sender, InvalidatedEventArgs e)
    {
        _version++;
        if (e.DirtyRect is { } r && !_fullDirty)
            _dirty = _dirty is { } d ? d.Union(r) : r;
        else
        {
            _fullDirty = true;
            _dirty = null;
        }
        if (_doc is not null) { _doc.LastChangeUtc = DateTime.UtcNow; _doc.ThumbnailDirty = true; }
        if (_s?.ActiveTextEdit is null && _editor.IsEditing) _editor.End();
        UpdateScrollBars();
        Redraw();
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        InvalidateCache();
        if (_doc?.FitToWindow == true) ZoomToFit(shrinkOnly: true); else { ClampOrigin(); UpdateScrollBars(); Redraw(); }
    }

    private void OnToolChanged(object? sender, EventArgs e) => UpdateCursor();

    private void OnSessionMessage(object? sender, EditorMessage e) => SessionMessage?.Invoke(e);

    private void OnBeginTextEdit(object? sender, TextEditRequest r)
    {
        InvalidateCache();
        _editor.Begin(r, _zoom, new Point(_ox, _oy), _dpi);
        Redraw();
    }

    private void OnEndTextEditRequested(object? sender, EventArgs e)
    {
        if (_editor.IsEditing) CommitEditor(_editor.CurrentText);
    }

    private void CommitEditor(RichText? text)
    {
        if (_s is null) { _editor.End(); return; }
        var had = _s.ActiveTextEdit is not null;
        _editor.End();
        if (had)
        {
            if (text is not null) _s.CommitTextEdit(text);
            else _s.CommitTextEdit();
        }
        InvalidateCache();
        Redraw();
        Focus();
    }

    public void CommitTextEditing()
    {
        if (_editor.IsEditing) CommitEditor(_editor.CurrentText);
    }

    private void UpdateEditorPlacement()
    {
        if (_editor.IsEditing) _editor.Reposition(_zoom, new Point(_ox, _oy), _dpi);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Affects(nameof(AppSettings.Editor))) Dispatcher.BeginInvoke(() => { _editor.ApplyPreferences(_ctx.Prefs, _ctx.Settings.Current.General.Language); Redraw(); });
        if (e.Affects(nameof(AppSettings.Advanced))) Dispatcher.BeginInvoke(() => _surface.SetHardware(_ctx.Settings.Current.Advanced.HardwareAcceleratedRendering));
    }

    private void InvalidateCache()
    {
        _fullDirty = true;
        _dirty = null;
        _version++;
    }

    // ------------------------------------------------------------------ painting

    private void OnPaintSurface(SKCanvas c, int width, int height)
    {
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _viewW = width;
        _viewH = height;
        c.Clear(WorkspaceSkColor());
        if (_s is null || _doc is null) return;
        try
        {
            if (_fitPending) { _fitPending = false; ZoomToFit(shrinkOnly: true); }
            else if (_centerPending) { _centerPending = false; CenterContent(); UpdateScrollBars(); }
            ClampOrigin();
            var page = _s.ActivePage;
            var cw = page.CanvasSize.Width;
            var ch = page.CanvasSize.Height;
            var dest = new SKRect((float)_ox, (float)_oy, (float)(_ox + cw * _zoom), (float)(_oy + ch * _zoom));

            DrawPageShadow(c, dest);
            c.Save();
            c.ClipRect(dest);
            DrawChecker(c, dest);
            c.Restore();

            EnsureCache();
            if (_cacheImage is not null)
            {
                var x = (float)(_ox + _cacheRegion.X * _zoom);
                var y = (float)(_oy + _cacheRegion.Y * _zoom);
                c.DrawImage(_cacheImage, new SKRect(x, y, x + _cacheImage.Width, y + _cacheImage.Height), new SKSamplingOptions(SKFilterMode.Linear));
            }
            using (var border = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0, 0, 0, 28), StrokeWidth = 1, IsAntialias = false })
                c.DrawRect(dest, border);

            if (_doc.ShowPixelGrid && _zoom >= 8) DrawPixelGrid(c, (int)cw, (int)ch);
            DrawOverlay(c);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Canvas paint failed");
        }
    }

    private static readonly DependencyProperty WorkspaceTokenProperty = DependencyProperty.Register("WorkspaceToken", typeof(object), typeof(CanvasView),
        new FrameworkPropertyMetadata(null, (d, _) => ((CanvasView)d).Redraw()));

    private SKColor WorkspaceSkColor()
    {
        var p = _ctx.Prefs;
        return p.WorkspaceColor switch
        {
            WorkspaceColor.White => SKColors.White,
            // the default follows the design token (Light #E9EBF2 / Dark #101116)
            WorkspaceColor.LightGray => TryFindResource("Shotwise.Color.Canvas") is System.Windows.Media.Color tc ? new SKColor(tc.R, tc.G, tc.B, tc.A) : new SKColor(0xE9, 0xEB, 0xF2),
            WorkspaceColor.Gray => new SKColor(0x9A, 0x9A, 0x9A),
            WorkspaceColor.DarkGray => new SKColor(0x4A, 0x4A, 0x4A),
            WorkspaceColor.Black => SKColors.Black,
            _ => p.CustomWorkspaceColor.ToSk(),
        };
    }

    /// <summary>Soft two-layer elevation under the image (ambient + key light), like the design system's card elevation.</summary>
    private void DrawPageShadow(SKCanvas c, SKRect dest)
    {
        var dark = TryFindResource("Shotwise.ThemeName") as string == "Dark";
        using var ambient = new SKPaint { Color = new SKColor(0x10, 0x14, 0x2A, (byte)(dark ? 120 : 30)), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3) };
        var r = dest;
        r.Offset(0, 1);
        c.DrawRect(r, ambient);
        using var key = new SKPaint { Color = new SKColor(0x10, 0x14, 0x2A, (byte)(dark ? 150 : 46)), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) };
        r = dest;
        r.Offset(0, 8);
        c.DrawRect(r, key);
    }

    private void DrawChecker(SKCanvas c, SKRect dest)
    {
        if (_checker is null)
        {
            using var bmp = new SKBitmap(16, 16);
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                    bmp.SetPixel(x, y, ((x / 8) + (y / 8)) % 2 == 0 ? new SKColor(0xFF, 0xFF, 0xFF) : new SKColor(0xCC, 0xCC, 0xCC));
            _checker = SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        }
        using var p = new SKPaint { Shader = _checker };
        c.DrawRect(dest, p);
    }

    private void EnsureCache()
    {
        if (_s is null || _doc is null) return;
        var page = _s.ActivePage;
        var cw = page.CanvasSize.Width;
        var ch = page.CanvasSize.Height;
        if (cw < 1 || ch < 1) return;
        var excl = _s.EditingAnnotationId;

        var vis = new Rect2(
            Math.Max(0, -_ox / _zoom), Math.Max(0, -_oy / _zoom), 0, 0);
        var x1 = Math.Min(cw, (_viewW - _ox) / _zoom);
        var y1 = Math.Min(ch, (_viewH - _oy) / _zoom);
        vis = Rect2.FromLTRB(vis.X, vis.Y, Math.Max(vis.X + 1, x1), Math.Max(vis.Y + 1, y1));

        var cacheValid = _cacheImage is not null && _cacheZoom == _zoom && !_fullDirty && _cacheExclude == excl
            && _cacheRegion.Contains(vis);

        if (cacheValid && _dirty is { } dirty)
        {
            // Partial refresh only when pixel grid aligned (integral zoom) so seams cannot appear.
            if (Math.Abs(_zoom - Math.Round(_zoom)) < 1e-9 && _zoom >= 1)
            {
                var d = dirty.Inflate(2).Intersect(_cacheRegion);
                if (!d.IsEmpty)
                {
                    var r = d.ToRectIOutward();
                    PartialRender(page, new Rect2(r.X, r.Y, r.Width, r.Height), excl);
                }
                _dirty = null;
                return;
            }
            cacheValid = false;
        }
        if (cacheValid) return;

        // choose region: whole canvas when small enough, otherwise visible area plus padding
        Rect2 region;
        if (cw * _zoom <= MaxCacheSide && ch * _zoom <= MaxCacheSide && cw * ch * _zoom * _zoom <= 36_000_000) region = new Rect2(0, 0, cw, ch);
        else
        {
            var padX = vis.Width * 0.25;
            var padY = vis.Height * 0.25;
            region = Rect2.FromLTRB(Math.Max(0, vis.X - padX), Math.Max(0, vis.Y - padY), Math.Min(cw, vis.Right + padX), Math.Min(ch, vis.Bottom + padY));
            var ri = region.ToRectIOutward();
            region = new Rect2(ri.X, ri.Y, Math.Min(ri.Width, cw - ri.X), Math.Min(ri.Height, ch - ri.Y));
            var maxSide = MaxCacheSide / _zoom;
            if (region.Width > maxSide) region = new Rect2(Math.Clamp(vis.X - 8, 0, Math.Max(0, cw - maxSide)), region.Y, maxSide, region.Height);
            if (region.Height > maxSide) region = new Rect2(region.X, Math.Clamp(vis.Y - 8, 0, Math.Max(0, ch - maxSide)), region.Width, maxSide);
            var rr = region.ToRectIOutward();
            region = new Rect2(rr.X, rr.Y, rr.Width, rr.Height);
        }
        FullRender(page, region, excl);
    }

    private RenderOptions OptionsFor(Rect2 region, Guid? excl)
        => new(_zoom, true, excl is { } g ? new HashSet<Guid> { g } : null) { Region = region, DrawEditorAdornments = true };

    private void FullRender(Page page, Rect2 region, Guid? excl)
    {
        var img = _ctx.Renderer.RenderPage(_doc!.Document, page, OptionsFor(region, excl));
        _cacheImage?.Dispose();
        _cacheBmp?.Dispose();
        _cacheBmp = SkiaConvert.ToBitmap(img);
        _cacheImage = SKImage.FromBitmap(_cacheBmp);
        _cacheRegion = region;
        _cacheZoom = _zoom;
        _cacheVersion = _version;
        _cacheExclude = excl;
        _fullDirty = false;
        _dirty = null;
    }

    private void PartialRender(Page page, Rect2 region, Guid? excl)
    {
        if (_cacheBmp is null) { FullRender(page, region, excl); return; }
        var img = _ctx.Renderer.RenderPage(_doc!.Document, page, OptionsFor(region, excl));
        using var patch = SkiaConvert.ToBitmap(img);
        using (var canvas = new SKCanvas(_cacheBmp))
        using (var p = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            var x = (float)((region.X - _cacheRegion.X) * _zoom);
            var y = (float)((region.Y - _cacheRegion.Y) * _zoom);
            canvas.DrawBitmap(patch, x, y, p);
        }
        _cacheImage?.Dispose();
        _cacheImage = SKImage.FromBitmap(_cacheBmp);
        _cacheVersion = _version;
    }

    private void DrawPixelGrid(SKCanvas c, int cw, int ch)
    {
        using var p = new SKPaint { Color = new SKColor(128, 128, 128, 70), StrokeWidth = 1, IsAntialias = false, Style = SKPaintStyle.Stroke };
        var x0 = Math.Max(0, (int)Math.Floor(-_ox / _zoom));
        var x1 = Math.Min(cw, (int)Math.Ceiling((_viewW - _ox) / _zoom));
        var y0 = Math.Max(0, (int)Math.Floor(-_oy / _zoom));
        var y1 = Math.Min(ch, (int)Math.Ceiling((_viewH - _oy) / _zoom));
        var top = (float)(_oy + y0 * _zoom);
        var bottom = (float)(_oy + y1 * _zoom);
        var left = (float)(_ox + x0 * _zoom);
        var right = (float)(_ox + x1 * _zoom);
        for (var x = x0; x <= x1; x++)
        {
            var sx = (float)(_ox + x * _zoom);
            c.DrawLine(sx, top, sx, bottom, p);
        }
        for (var y = y0; y <= y1; y++)
        {
            var sy = (float)(_oy + y * _zoom);
            c.DrawLine(left, sy, right, sy, p);
        }
    }

    // ------------------------------------------------------------------ overlay

    private SKPoint P(Point2 p) => new((float)(_ox + p.X * _zoom), (float)(_oy + p.Y * _zoom));

    private SKRect R(Rect2 r)
    {
        var a = P(r.TopLeft);
        return new SKRect(a.X, a.Y, a.X + (float)(r.Width * _zoom), a.Y + (float)(r.Height * _zoom));
    }

    private void DrawOverlay(SKCanvas c)
    {
        if (_s is null) return;
        IReadOnlyList<OverlayPrimitive> primitives;
        try { primitives = _s.GetOverlay(_zoom / _dpi); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Overlay failed");
            return;
        }
        var sc = (float)_dpi;
        foreach (var o in primitives)
        {
            switch (o)
            {
                case OverlayImageTint t: DrawTint(c, t); break;
                case OverlayImage img: DrawOverlayImage(c, img); break;
            }
        }
        foreach (var o in primitives)
        {
            switch (o)
            {
                case OverlayRect r: DrawShape(c, r.Style, sc, R(r.Rect), r.RotationDegrees, false); break;
                case OverlayEllipse el: DrawShape(c, el.Style, sc, R(el.Bounds), el.RotationDegrees, true); break;
                case OverlayPath path: DrawPath(c, path, sc); break;
                case OverlayLine line: DrawLine(c, line, sc); break;
            }
        }
        foreach (var o in primitives)
        {
            if (o is OverlayHandle h) DrawHandle(c, h, sc);
            else if (o is OverlayText t) DrawText(c, t, sc);
        }
    }

    private static SKPathEffect? Dash(OverlayStyle s, float sc) => s.Dashed ? SKPathEffect.CreateDash(new[] { 4f * sc, 3f * sc }, 0) : null;

    private static void DrawShape(SKCanvas c, OverlayStyle style, float sc, SKRect rect, double rotation, bool oval)
    {
        c.Save();
        if (Math.Abs(rotation) > 0.001) c.RotateDegrees((float)rotation, rect.MidX, rect.MidY);
        if (style.Fill is { } fill)
        {
            using var fp = new SKPaint { Color = fill.ToSk(), Style = SKPaintStyle.Fill, IsAntialias = true };
            if (oval) c.DrawOval(rect, fp); else c.DrawRect(rect, fp);
        }
        if (style.StrokeWidth > 0 && style.Stroke.A > 0)
        {
            using var sp = new SKPaint { Color = style.Stroke.ToSk(), Style = SKPaintStyle.Stroke, StrokeWidth = (float)style.StrokeWidth * sc, IsAntialias = true, PathEffect = Dash(style, sc) };
            if (oval) c.DrawOval(rect, sp); else c.DrawRect(rect, sp);
        }
        c.Restore();
    }

    private void DrawPath(SKCanvas c, OverlayPath path, float sc)
    {
        if (path.Points.Count < 2) return;
        using var sk = new SKPath();
        sk.MoveTo(P(path.Points[0]));
        for (var i = 1; i < path.Points.Count; i++) sk.LineTo(P(path.Points[i]));
        if (path.Closed) sk.Close();
        var s = path.Style;
        if (s.Fill is { } fill && path.Closed)
        {
            using var fp = new SKPaint { Color = fill.ToSk(), Style = SKPaintStyle.Fill, IsAntialias = true };
            c.DrawPath(sk, fp);
        }
        using var sp = new SKPaint { Color = s.Stroke.ToSk(), Style = SKPaintStyle.Stroke, StrokeWidth = (float)s.StrokeWidth * sc, IsAntialias = true, PathEffect = Dash(s, sc), StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
        c.DrawPath(sk, sp);
    }

    private void DrawLine(SKCanvas c, OverlayLine line, float sc)
    {
        using var sp = new SKPaint { Color = line.Style.Stroke.ToSk(), Style = SKPaintStyle.Stroke, StrokeWidth = (float)line.Style.StrokeWidth * sc, IsAntialias = true, PathEffect = Dash(line.Style, sc) };
        c.DrawLine(P(line.From), P(line.To), sp);
    }

    private void DrawHandle(SKCanvas c, OverlayHandle h, float sc)
    {
        var p = P(h.Center);
        var half = (float)OverlayHandle.ScreenSize * sc / 2f;
        var round = h.Kind is HandleKind.Rotate or HandleKind.TailTip or HandleKind.PointerTip or HandleKind.Vertex or HandleKind.LensSource or HandleKind.Control1 or HandleKind.Control2;
        using var fill = new SKPaint { Color = (h.Style.Fill ?? Argb.White).ToSk(), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Color = h.Style.Stroke.ToSk(), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, (float)h.Style.StrokeWidth) * sc, IsAntialias = true };
        if (round)
        {
            c.DrawCircle(p, half, fill);
            c.DrawCircle(p, half, stroke);
        }
        else
        {
            var r = new SKRect(p.X - half, p.Y - half, p.X + half, p.Y + half);
            c.DrawRect(r, fill);
            c.DrawRect(r, stroke);
        }
    }

    private void DrawText(SKCanvas c, OverlayText t, float sc)
    {
        var p = P(t.Anchor);
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), (float)t.FontSize * sc);
        var width = font.MeasureText(t.Text);
        var pad = 4f * sc;
        var rect = new SKRect(p.X + 10 * sc, p.Y + 10 * sc, p.X + 10 * sc + width + 2 * pad, p.Y + 10 * sc + font.Size + 2 * pad);
        using var bg = new SKPaint { Color = t.Background.ToSk(), IsAntialias = true };
        c.DrawRoundRect(rect, 3 * sc, 3 * sc, bg);
        using var fg = new SKPaint { Color = t.Foreground.ToSk(), IsAntialias = true };
        c.DrawText(t.Text, rect.Left + pad, rect.Bottom - pad - font.Size * 0.2f, font, fg);
    }

    private void DrawTint(SKCanvas c, OverlayImageTint t)
    {
        var rect = R(t.Rect);
        if (t.Mask is null)
        {
            using var p = new SKPaint { Color = t.Tint.ToSk(), Style = SKPaintStyle.Fill };
            c.DrawRect(rect, p);
            return;
        }
        var bmp = TintFor(t.Mask, t.Tint, t.InvertMask);
        c.Save();
        c.ClipRect(rect);
        using var img = SKImage.FromBitmap(bmp);
        var dest = new SKRect((float)_ox, (float)_oy, (float)(_ox + bmp.Width * _zoom), (float)(_oy + bmp.Height * _zoom));
        c.DrawImage(img, dest, new SKSamplingOptions(SKFilterMode.Nearest));
        c.Restore();
    }

    private SKBitmap TintFor(Mask mask, Argb tint, bool invert)
    {
        foreach (var e in _tintCache)
            if (ReferenceEquals(e.Mask, mask) && e.Tint == tint.Value && e.Invert == invert) return e.Bmp;
        if (_tintCache.Count >= 3)
        {
            _tintCache[0].Bmp.Dispose();
            _tintCache.RemoveAt(0);
        }
        var bmp = SkiaConvert.TintBitmap(mask, tint, invert);
        _tintCache.Add((mask, tint.Value, invert, bmp));
        return bmp;
    }

    private void DrawOverlayImage(SKCanvas c, OverlayImage o)
    {
        if (_overlayImage is not { } cached || !ReferenceEquals(cached.Image, o.Image))
        {
            _overlayImage?.Bmp.Dispose();
            _overlayImage = (o.Image, SkiaConvert.ToBitmap(o.Image));
        }
        using var img = SKImage.FromBitmap(_overlayImage!.Value.Bmp);
        using var p = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(o.Opacity * 255, 0, 255)) };
        c.DrawImage(img, R(o.Destination), new SKSamplingOptions(SKFilterMode.Linear), p);
    }

    // ------------------------------------------------------------------ input

    private Point2 ToCanvas(Point dip) => new((dip.X * _dpi - _ox) / _zoom, (dip.Y * _dpi - _oy) / _zoom);

    private PointerInput MakeInput(MouseEventArgs e, PointerButton button, int clickCount)
    {
        var pos = ToCanvas(e.GetPosition(_surface));
        return new PointerInput(pos, button, InputMap.ToCore(Keyboard.Modifiers), clickCount) { Zoom = _zoom / _dpi };
    }

    private PointerButton CurrentButton(MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) return PointerButton.Left;
        if (e.RightButton == MouseButtonState.Pressed) return PointerButton.Right;
        if (e.MiddleButton == MouseButtonState.Pressed) return PointerButton.Middle;
        return PointerButton.None;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_s is null) return;
        if (!ReferenceEquals(Keyboard.FocusedElement, this) && !_editor.IsKeyboardFocusWithin) Focus();
        if (_editor.IsEditing && !_editor.IsMouseOver) CommitEditor(_editor.CurrentText);
        else if (_editor.IsEditing) return;
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown))
        {
            _panning = true;
            _panStart = e.GetPosition(_surface);
            _panOx = _ox;
            _panOy = _oy;
            CaptureMouse();
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }
        var btn = InputMap.ToButton(e.ChangedButton);
        if (btn == PointerButton.None) return;
        _heldButton = btn;
        CaptureMouse();
        var input = MakeInput(e, btn, e.ClickCount);
        _s.PointerDown(input);
        if (e.ClickCount == 2 && btn == PointerButton.Left) _s.DoubleClick(input with { ClickCount = 2 });
        UpdateCursor();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_s is null) return;
        if (_panning)
        {
            var p = e.GetPosition(_surface);
            _ox = _panOx + (p.X - _panStart.X) * _dpi;
            _oy = _panOy + (p.Y - _panStart.Y) * _dpi;
            _fitPending = false;
            if (_doc is not null) _doc.FitToWindow = false;
            ClampOrigin();
            UpdateScrollBars();
            UpdateEditorPlacement();
            Redraw();
            return;
        }
        var canvasPos = ToCanvas(e.GetPosition(_surface));
        PointerMoved?.Invoke(canvasPos);
        if (_editor.IsEditing) return;
        _s.PointerMove(MakeInput(e, CurrentButton(e), 0));
        UpdateCursor();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_s is null) return;
        if (_panning && (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left))
        {
            _panning = false;
            ReleaseMouseCapture();
            UpdateCursor();
            return;
        }
        var btn = InputMap.ToButton(e.ChangedButton);
        if (btn == PointerButton.None || _editor.IsEditing) return;
        var input = MakeInput(e, btn, 1);
        _s.PointerUp(input);
        if (Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released) ReleaseMouseCapture();
        UpdateCursor();
        if (btn == PointerButton.Right) ContextMenuRequested?.Invoke(input.Position);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        PointerMoved?.Invoke(null);
        if (!_panning && !IsMouseCaptured) _s?.PointerLeave();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_s is null || _editor.IsEditing) return;
        var mods = Keyboard.Modifiers;
        var posDip = e.GetPosition(_surface);
        if ((mods & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            var anchor = new Point(posDip.X * _dpi, posDip.Y * _dpi);
            var factor = Math.Pow(1.0015, e.Delta);
            SetZoom(_zoom * factor, anchor, userAction: true);
            e.Handled = true;
            return;
        }
        var input = new PointerInput(ToCanvas(posDip), PointerButton.None, InputMap.ToCore(mods), 0) { Zoom = _zoom / _dpi };
        if (_s.MouseWheel(input, e.Delta)) { e.Handled = true; return; }
        var delta = e.Delta * _dpi;
        if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0) _ox += delta; else _oy += delta;
        _fitPending = false;
        if (_doc is not null) _doc.FitToWindow = false;
        ClampOrigin();
        UpdateScrollBars();
        Redraw();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_s is null || _editor.IsEditing) return;
        if (e.Key == Key.Space && !e.IsRepeat)
        {
            _spaceDown = true;
            Cursor = Cursors.Hand;
            e.Handled = true;
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Delete or Key.Back or Key.Escape or Key.Enter or Key.Return)
        {
            var input = new KeyInput(InputMap.ToVirtualKey(key), InputMap.ToCore(Keyboard.Modifiers), e.IsRepeat);
            if (key == Key.Escape && _s.IsBusy) _s.CancelBackgroundWork();
            _s.KeyDown(input);
            if (input.Handled) { e.Handled = true; UpdateCursor(); return; }
            if (key == Key.Escape) EscapeUnhandled?.Invoke();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            UpdateCursor();
        }
    }

    private void UpdateCursor()
    {
        if (_s is null) { Cursor = Cursors.Arrow; return; }
        if (_panning) { Cursor = Cursors.ScrollAll; return; }
        if (_spaceDown) { Cursor = Cursors.Hand; return; }
        Cursor = InputMap.ToCursor(_s.CurrentCursor);
    }

    // ------------------------------------------------------------------ drag & drop in

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) || e.Data.GetDataPresent(DataFormats.UnicodeText)
            || e.Data.GetDataPresent(ClipboardPayload.ClipboardFormat)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (_s is null) return;
        var pos = ToCanvas(e.GetPosition(_surface));
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var others = new List<string>();
                foreach (var f in files)
                {
                    if (ImageFormatExtensions.TryFromExtension(Path.GetExtension(f), out var fmt) && fmt != ImageFormat.Pdf && fmt != ImageFormat.Ico)
                    {
                        var img = _ctx.Codec.Load(f);
                        _s.DropImage(img, pos);
                        pos = pos.Offset(24, 24);
                    }
                    else others.Add(f);
                }
                if (others.Count > 0) NonImageFilesDropped?.Invoke(others, pos);
            }
            else if (e.Data.GetDataPresent(ClipboardPayload.ClipboardFormat) && e.Data.GetData(ClipboardPayload.ClipboardFormat) is string json && ClipboardPayload.FromJson(json) is { } payload)
                _s.Paste(payload, pos);
            else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bmp)
                _s.DropImage(bmp.ToBgraImage(), pos);
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText) && e.Data.GetData(DataFormats.UnicodeText) is string text)
                _s.Paste(new ClipboardPayload(Array.Empty<Shotwise.Core.Annotations.Annotation>(), Array.Empty<EmbeddedAsset>(), Rect2.Empty, null, text), pos);
        }
        catch (Exception ex) when (ex is ShotwiseException or IOException or UnauthorizedAccessException)
        {
            SessionMessage?.Invoke(new EditorMessage(EditorMessageKind.Error, ex is ShotwiseException se ? se.Code : ErrorCode.ImportFailed, ex.Message));
        }
        e.Handled = true;
    }

    /// <summary>Canvas position under the mouse (null when the pointer is outside the view).</summary>
    public Point2? PointerCanvasPosition()
    {
        if (!IsMouseOver || _s is null) return null;
        return ToCanvas(Mouse.GetPosition(_surface));
    }

    /// <summary>Canvas coordinates of the viewport centre (default insertion point).</summary>
    public Point2 ViewportCenterCanvas() => new((_viewW / 2.0 - _ox) / _zoom, (_viewH / 2.0 - _oy) / _zoom);
}
