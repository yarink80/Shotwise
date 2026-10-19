using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor;

// Raster ("flattened pixel") operations: pixel selection, move/copy/cut/delete pixels, fill, eraser, eyedropper.
// Every operation reads the page base raster (base image at canvas size, no annotations), computes on the thread pool
// and commits a ReplaceBaseImageCommand. A result is dropped when the page changed meanwhile (stale snapshot).
public sealed partial class EditorSession
{
    // ------------------------------------------------------------------ pixel selection model

    /// <summary>Selects every canvas pixel (Ctrl+A with a pixel tool).</summary>
    public void SelectAllPixels()
    {
        var size = RasterOps.CanvasPixels(ActivePage);
        var m = Mask.FromRect(size.Width, size.Height, new RectI(0, 0, size.Width, size.Height));
        Selection.ClearObjects();
        Selection.SetPixelSelection(m, new Rect2(0, 0, size.Width, size.Height), PixelSelectionShape.Rectangle);
    }

    /// <summary>Sets the pixel selection from a canvas-sized mask (an empty mask clears it). Object selection is cleared.</summary>
    public void SetPixelSelection(Mask mask, PixelSelectionShape shape, IReadOnlyList<Point2>? outline)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var size = RasterOps.CanvasPixels(ActivePage);
        var m = mask.Width == size.Width && mask.Height == size.Height ? mask : ResizeMaskToCanvas(mask, size.Width, size.Height);
        var b = m.PixelBounds;
        Selection.ClearObjects();
        if (b.IsEmpty)
        {
            Selection.ClearPixelSelection();
            return;
        }
        Selection.SetPixelSelection(m, b.ToRect2(), shape, outline);
    }

    /// <summary>Canvas-sized mask for a selection shape: rectangle/ellipse use <paramref name="rect"/>; polygon/freehand use <paramref name="points"/>.</summary>
    public Mask CreateSelectionMask(PixelSelectionShape shape, Rect2 rect, IReadOnlyList<Point2>? points = null)
    {
        var size = RasterOps.CanvasPixels(ActivePage);
        return shape switch
        {
            PixelSelectionShape.Ellipse => Mask.FromEllipse(size.Width, size.Height, rect.Normalize()),
            PixelSelectionShape.Polygon or PixelSelectionShape.Freehand when points is { Count: >= 3 } => Mask.FromPolygon(size.Width, size.Height, points),
            _ => Mask.FromRect(size.Width, size.Height, rect.Normalize().ToRectIRounded()),
        };
    }

    internal static Mask ResizeMaskToCanvas(Mask m, int w, int h)
    {
        var outp = new Mask(w, h);
        var cw = Math.Min(w, m.Width);
        for (var y = 0; y < Math.Min(h, m.Height); y++) Buffer.BlockCopy(m.Data, y * m.Width, outp.Data, y * w, cw);
        return outp;
    }

    private (SelectionBackground Background, bool HighQuality) VacatedPixelOptions()
        => ActiveTool == ToolKind.MagicWand
            ? (ToolOptions.MagicWand.Background, ToolOptions.MagicWand.HighQuality)
            : (ToolOptions.Selection.Background, ToolOptions.Selection.HighQuality);

    // ------------------------------------------------------------------ pixel edits

    /// <summary>Deletes the selected pixels: transparent or auto-filled (inpainting) per the active selection options.</summary>
    public Task DeletePixelsAsync(CancellationToken ct = default)
    {
        if (Selection.PixelMask is not { } mask) return Task.CompletedTask;
        var (bg, hq) = VacatedPixelOptions();
        var page = ActivePage;
        var snap = Snapshot(page);
        return RunBackground("Delete pixels", async token =>
        {
            var baseRaster = RasterOps.ComposeBase(Document, page);
            var result = await FillHoleAsync(baseRaster, page, mask, bg == SelectionBackground.AutoFill, hq, token).ConfigureAwait(true);
            if (!CommitBaseRaster(page, snap, result, "Delete pixels")) return;
            Selection.ClearPixelSelection();
        }, ct);
    }

    /// <summary>
    /// Moves (or copies with <paramref name="copy"/>) the selected pixels by an integer offset. The vacated area becomes
    /// transparent or auto-filled; the selection follows the pixels.
    /// </summary>
    public Task MovePixelsAsync(int dx, int dy, bool copy, CancellationToken ct = default)
    {
        if (Selection.PixelMask is not { } mask || Selection.PixelBounds is not { } pb || (dx == 0 && dy == 0 && !copy)) return Task.CompletedTask;
        var (bg, hq) = VacatedPixelOptions();
        var page = ActivePage;
        var snap = Snapshot(page);
        var shape = Selection.PixelShape ?? PixelSelectionShape.Freehand;
        var outline = Selection.PixelOutline;
        return RunBackground(copy ? "Copy pixels" : "Move pixels", async token =>
        {
            var baseRaster = RasterOps.ComposeBase(Document, page);
            var region = pb.ToRectIOutward();
            var lifted = RasterOps.ExtractMasked(baseRaster, mask, region);
            var target = copy ? baseRaster : await FillHoleAsync(baseRaster, page, mask, bg == SelectionBackground.AutoFill, hq, token).ConfigureAwait(true);
            target = target.Clone();
            var ri = region.Intersect(baseRaster.Bounds);
            target.DrawOver(lifted, ri.X + dx, ri.Y + dy);
            if (!CommitBaseRaster(page, snap, target, copy ? "Copy pixels" : "Move pixels")) return;
            var moved = RasterOps.Translate(mask, dx, dy);
            var newOutline = outline?.Select(p => p.Offset(dx, dy)).ToList();
            if (moved.PixelBounds.IsEmpty) Selection.ClearPixelSelection();
            else Selection.SetPixelSelection(moved, moved.PixelBounds.ToRect2(), shape, newOutline);
        }, ct);
    }

    /// <summary>Fills the vacated <paramref name="hole"/>: transparent, inpainting service, or the classical fallback fill.</summary>
    internal async Task<BgraImage> FillHoleAsync(BgraImage baseRaster, Page page, Mask hole, bool autoFill, bool highQuality, CancellationToken ct)
    {
        if (!autoFill)
        {
            var cleared = baseRaster.Clone();
            RasterOps.ClearMasked(cleared, hole);
            return cleared;
        }
        var flattened = RasterOps.ComposeFlattenedBase(Document, page);
        var mask = hole.Width == flattened.Width && hole.Height == flattened.Height ? hole : ResizeMaskToCanvas(hole, flattened.Width, flattened.Height);
        BgraImage filled;
        if (Services.Inpainting is { } inpaint)
        {
            var q = highQuality && inpaint.Available.Contains(InpaintQuality.Quality) ? InpaintQuality.Quality : InpaintQuality.Fast;
            try
            {
                filled = await inpaint.InpaintAsync(flattened, mask, q, ct).ConfigureAwait(true);
            }
            catch (ShotwiseException ex) when (ex.Code is ErrorCode.InpaintFailed or ErrorCode.ModelUnavailable)
            {
                _log.LogWarning(ex, "Inpainting failed; using fallback fill");
                filled = await Task.Run(() => RasterOps.FillFromSurroundings(flattened, mask), ct).ConfigureAwait(true);
            }
        }
        else
        {
            filled = await Task.Run(() => RasterOps.FillFromSurroundings(flattened, mask), ct).ConfigureAwait(true);
        }
        if (filled.Width != baseRaster.Width || filled.Height != baseRaster.Height)
            throw new ShotwiseException(ErrorCode.InpaintFailed, "Inpainting returned an image of unexpected size.");
        var outp = baseRaster.Clone();
        MergeMasked(outp, filled, mask);
        return outp;
    }

    /// <summary>dst = lerp(dst, src, coverage) for every masked pixel.</summary>
    internal static void MergeMasked(BgraImage dst, BgraImage src, Mask m)
    {
        var d = dst.Pixels;
        var s = src.Pixels;
        for (var i = 0; i < m.Data.Length; i++)
        {
            var cov = m.Data[i];
            if (cov == 0) continue;
            var o = i * 4;
            if (cov == 255)
            {
                d[o] = s[o]; d[o + 1] = s[o + 1]; d[o + 2] = s[o + 2]; d[o + 3] = s[o + 3];
                continue;
            }
            var k = cov / 255.0;
            for (var c = 0; c < 4; c++) d[o + c] = (byte)Math.Round(d[o + c] + (s[o + c] - d[o + c]) * k);
        }
    }

    // ------------------------------------------------------------------ fill / eraser / eyedropper

    /// <summary>
    /// Fill tool: flood-fills the flattened base pixels around <paramref name="p"/> (tolerance, contiguous/global,
    /// colour or transparent per <see cref="EditorToolOptions.Fill"/>), limited to the pixel selection when present.
    /// </summary>
    public Task FillAtAsync(Point2 p, CancellationToken ct = default)
    {
        var o = ToolOptions.Fill;
        var page = ActivePage;
        var snap = Snapshot(page);
        var selection = Selection.PixelMask;
        var seed = new PointI((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        var size = RasterOps.CanvasPixels(page);
        if ((uint)seed.X >= (uint)size.Width || (uint)seed.Y >= (uint)size.Height) return Task.CompletedTask;
        var color = o.Transparent ? Argb.Transparent : o.Color;
        var tolerance = Math.Clamp(o.Tolerance, 0, 100) / 100.0;
        var opacity = o.Opacity;
        var global = o.Global;
        return RunBackground("Fill", async token =>
        {
            var baseRaster = RasterOps.ComposeBase(Document, page);
            var visible = RasterOps.ComposeFlattenedBase(Document, page);
            var result = await Task.Run(() =>
            {
                var region = RasterOps.ColorRegion(visible, seed, tolerance, contiguous: !global);
                if (selection is not null && selection.Width == region.Width && selection.Height == region.Height) region = region.Intersect(selection);
                token.ThrowIfCancellationRequested();
                var outp = baseRaster.Clone();
                RasterOps.PaintMasked(outp, region, color, opacity, replace: color.A == 0);
                return outp;
            }, token).ConfigureAwait(true);
            CommitBaseRaster(page, snap, result, "Fill");
        }, ct);
    }

    /// <summary>Eraser: erases flattened pixels along <paramref name="stroke"/> to the canvas colour or transparency.</summary>
    public Task EraseAsync(IReadOnlyList<Point2> stroke, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        if (stroke.Count == 0) return Task.CompletedTask;
        var o = ToolOptions.Eraser;
        var page = ActivePage;
        var snap = Snapshot(page);
        var points = stroke.ToArray();
        var width = Math.Max(1, o.Width);
        var aa = o.Antialias;
        var opacity = o.Opacity;
        var color = o.Mode == EraseMode.Transparent ? Argb.Transparent : page.Background.Color;
        return RunBackground("Erase", async token =>
        {
            var baseRaster = RasterOps.ComposeBase(Document, page);
            var result = await Task.Run(() =>
            {
                var mask = RasterOps.StrokeMask(baseRaster.Width, baseRaster.Height, points, width, aa);
                var outp = baseRaster.Clone();
                RasterOps.PaintMasked(outp, mask, color, opacity, replace: true);
                return outp;
            }, token).ConfigureAwait(true);
            CommitBaseRaster(page, snap, result, "Erase");
        }, ct);
    }

    /// <summary>Eyedropper: colour of the rendered page (with annotations) at <paramref name="p"/>; also stored as the Fill colour.</summary>
    public Argb? SampleColor(Point2 p)
    {
        var x = Math.Floor(p.X);
        var y = Math.Floor(p.Y);
        if (!ActivePage.CanvasRect.Contains(new Point2(x + 0.5, y + 0.5))) return null;
        var img = TryRender(new RenderOptions { Region = new Rect2(x, y, 1, 1) });
        if (img is null) return null;
        var c = img.GetPixel(0, 0);
        ToolOptions.Fill.Color = c;
        RaiseColorSampled(new ColorSampledEventArgs(c, p));
        ToolOptionsChanged?.Invoke(this, EventArgs.Empty);
        return c;
    }

    // ------------------------------------------------------------------ base raster commits

    internal readonly record struct PageSnapshot(BaseImageRef? BaseImage, Size2 CanvasSize);

    internal static PageSnapshot Snapshot(Page page) => new(page.Background.BaseImage, page.CanvasSize);

    internal bool IsCurrent(Page page, PageSnapshot snap)
        => ReferenceEquals(page, ActivePage) && Equals(page.Background.BaseImage, snap.BaseImage) && page.CanvasSize == snap.CanvasSize;

    /// <summary>Stores <paramref name="newBase"/> as the page base image (one undo step, optionally with extra commands). False when stale.</summary>
    internal bool CommitBaseRaster(Page page, PageSnapshot snap, BgraImage newBase, string label, IEnumerable<IEditCommand>? extra = null)
    {
        if (!IsCurrent(page, snap))
        {
            _log.LogInformation("{Label} result discarded: page changed while it was computed", label);
            return false;
        }
        var asset = AddEditorRaster(newBase);
        var cmds = new List<IEditCommand> { new ReplaceBaseImageCommand(page.Id, new BaseImageRef(asset, Point2.Zero), label) };
        if (extra is not null) cmds.AddRange(extra);
        ExecuteComposite(label, cmds);
        return true;
    }

    /// <summary>Draws <paramref name="img"/> into the base raster at <paramref name="rect"/> (auto-flatten pasted images).</summary>
    private void FlattenImageIntoBase(BgraImage img, Rect2 rect, string label)
    {
        var page = ActivePage;
        var snap = Snapshot(page);
        var raster = RasterOps.ComposeBase(Document, page);
        raster.DrawOver(img, (int)Math.Round(rect.X), (int)Math.Round(rect.Y));
        CommitBaseRaster(page, snap, raster, label);
    }
}
