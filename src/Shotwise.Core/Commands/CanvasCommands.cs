using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Commands;

/// <summary>Captured page canvas state used by raster/canvas commands for exact undo.</summary>
internal sealed record CanvasState(Size2 CanvasSize, CanvasBackground Background)
{
    public static CanvasState Of(Page p) => new(p.CanvasSize, p.Background);
    public void ApplyTo(Page p) { p.CanvasSize = CanvasSize; p.Background = Background; }
}

internal static class PageGeometry
{
    /// <summary>Translates every annotation, section and the base-image offset of a page.</summary>
    public static void TranslateContent(Page page, double dx, double dy, bool includeBaseImage)
    {
        if (dx == 0 && dy == 0) return;
        foreach (var a in page.Annotations) a.Translate(dx, dy);
        foreach (var s in page.Sections)
        {
            s.Bounds = s.Bounds.Offset(dx, dy);
            foreach (var g in s.Graphics) g.Translate(dx, dy);
        }
        if (includeBaseImage && page.Background.BaseImage is { } bi)
            page.Background = page.Background with { BaseImage = bi with { Offset = bi.Offset.Offset(dx, dy) } };
    }
}

/// <summary>Changes canvas size, placing existing content according to a 9-position anchor (non-destructive).</summary>
public sealed class ResizeCanvasCommand : PageCommand
{
    private readonly Size2 _newSize;
    private readonly Anchor9 _anchor;
    private readonly Argb? _fill;
    private CanvasState? _before;
    private Point2 _offset;

    public ResizeCanvasCommand(Guid pageId, Size2 newSize, Anchor9 anchor, Argb? fill, string label = "Resize canvas")
        : base(pageId, label)
    {
        if (newSize.Width < 1 || newSize.Height < 1) throw new ArgumentOutOfRangeException(nameof(newSize));
        _newSize = newSize;
        _anchor = anchor;
        _fill = fill;
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = CanvasState.Of(page);
        _offset = _anchor.PlacementOffset(page.CanvasSize, _newSize);
        PageGeometry.TranslateContent(page, _offset.X, _offset.Y, includeBaseImage: true);
        page.CanvasSize = _newSize;
        if (_fill is { } f) page.Background = page.Background with { Color = f };
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is null) return;
        var page = GetPage(doc);
        PageGeometry.TranslateContent(page, -_offset.X, -_offset.Y, includeBaseImage: false);
        _before.ApplyTo(page);
    }
}

/// <summary>Replaces the canvas background (colour / gradient / base image reference).</summary>
public sealed class SetCanvasBackgroundCommand : PageCommand
{
    private readonly CanvasBackground _after;
    private CanvasBackground? _before;

    public SetCanvasBackgroundCommand(Guid pageId, CanvasBackground after, string label = "Canvas color") : base(pageId, label) => _after = after;

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = page.Background;
        page.Background = _after;
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is not null) GetPage(doc).Background = _before;
    }

    public override bool TryMerge(IEditCommand next) => false;
}

/// <summary>Sets canvas corner radius and canvas drop shadow.</summary>
public sealed class SetCanvasStyleCommand : PageCommand
{
    private readonly double _cornerRadius;
    private readonly ShadowStyle? _shadow;
    private (double Radius, ShadowStyle? Shadow)? _before;

    public SetCanvasStyleCommand(Guid pageId, double cornerRadius, ShadowStyle? shadow, string label = "Canvas style")
        : base(pageId, label)
    {
        _cornerRadius = cornerRadius;
        _shadow = shadow;
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = (page.CornerRadius, page.CanvasShadow);
        page.CornerRadius = _cornerRadius;
        page.CanvasShadow = _shadow;
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is not { } b) return;
        var page = GetPage(doc);
        page.CornerRadius = b.Radius;
        page.CanvasShadow = b.Shadow;
    }
}

/// <summary>
/// Generic raster swap: replaces the page base image (and optionally canvas size / content offset).
/// Used by fill, eraser, pixel-selection moves/deletes, inpainting, Smart Move, Edit Text.
/// </summary>
public sealed class ReplaceBaseImageCommand : PageCommand, IAssetReferencingCommand
{
    private readonly BaseImageRef? _after;
    private readonly Size2? _canvasAfter;
    private readonly Point2 _contentOffset;
    private CanvasState? _before;

    public ReplaceBaseImageCommand(Guid pageId, BaseImageRef? after, string label, Size2? canvasAfter = null, Point2 contentOffset = default)
        : base(pageId, label)
    {
        _after = after;
        _canvasAfter = canvasAfter;
        _contentOffset = contentOffset;
    }

    public IEnumerable<AssetId> ReferencedAssets
    {
        get
        {
            if (_after is { } a) yield return a.Asset;
            if (_before?.Background.BaseImage is { } b) yield return b.Asset;
        }
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = CanvasState.Of(page);
        PageGeometry.TranslateContent(page, _contentOffset.X, _contentOffset.Y, includeBaseImage: false);
        page.Background = page.Background with { BaseImage = _after };
        if (_canvasAfter is { } s) page.CanvasSize = s;
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is null) return;
        var page = GetPage(doc);
        PageGeometry.TranslateContent(page, -_contentOffset.X, -_contentOffset.Y, includeBaseImage: false);
        _before.ApplyTo(page);
    }
}

/// <summary>
/// Applies a raster effect result: the effect was computed by the caller via IEffectProcessor on the flattened base
/// (background + base image, no annotations) and stored as <paramref name="afterAsset"/> covering the whole new canvas.
/// Annotations are shifted by the result's content offset.
/// </summary>
public sealed class ApplyEffectCommand : PageCommand, IAssetReferencingCommand
{
    private readonly AssetId _afterAsset;
    private readonly Size2 _afterSize;
    private readonly Point2 _contentOffset;
    private CanvasState? _before;

    public ApplyEffectCommand(Guid pageId, EffectSpec effect, AssetId afterAsset, Size2 afterCanvasSize, Point2 contentOffset, string? label = null)
        : base(pageId, label ?? $"Effect: {effect.GetType().Name.Replace("Effect", "")}")
    {
        Effect = effect;
        _afterAsset = afterAsset;
        _afterSize = afterCanvasSize;
        _contentOffset = contentOffset;
    }

    public EffectSpec Effect { get; }

    public IEnumerable<AssetId> ReferencedAssets
    {
        get
        {
            yield return _afterAsset;
            if (_before?.Background.BaseImage is { } b) yield return b.Asset;
        }
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = CanvasState.Of(page);
        PageGeometry.TranslateContent(page, _contentOffset.X, _contentOffset.Y, includeBaseImage: false);
        page.Background = page.Background with { BaseImage = new BaseImageRef(_afterAsset, Point2.Zero) };
        page.CanvasSize = _afterSize;
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is null) return;
        var page = GetPage(doc);
        PageGeometry.TranslateContent(page, -_contentOffset.X, -_contentOffset.Y, includeBaseImage: false);
        _before.ApplyTo(page);
    }
}

/// <summary>
/// Crops the canvas to <c>rect</c>. Non-destructive by default (base image offset moves, pixels kept);
/// destructive when <c>croppedAsset</c> is given (caller produced the cropped raster — "Delete cropped content").
/// </summary>
public sealed class CropCommand : PageCommand, IAssetReferencingCommand
{
    private readonly Rect2 _rect;
    private readonly AssetId? _croppedAsset;
    private CanvasState? _before;

    public CropCommand(Guid pageId, Rect2 rect, AssetId? croppedAsset = null, string label = "Crop") : base(pageId, label)
    {
        var r = rect.Normalize();
        if (r.Width < 1 || r.Height < 1) throw new ArgumentOutOfRangeException(nameof(rect));
        _rect = r;
        _croppedAsset = croppedAsset;
    }

    public IEnumerable<AssetId> ReferencedAssets
    {
        get
        {
            if (_croppedAsset is { } c) yield return c;
            if (_before?.Background.BaseImage is { } b) yield return b.Asset;
        }
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = CanvasState.Of(page);
        PageGeometry.TranslateContent(page, -_rect.X, -_rect.Y, includeBaseImage: _croppedAsset is null);
        if (_croppedAsset is { } c) page.Background = page.Background with { BaseImage = new BaseImageRef(c, Point2.Zero) };
        page.CanvasSize = _rect.Size;
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is null) return;
        var page = GetPage(doc);
        PageGeometry.TranslateContent(page, _rect.X, _rect.Y, includeBaseImage: false);
        _before.ApplyTo(page);
    }
}

public enum CutOutOrientation
{
    /// <summary>Removes a horizontal band (rows start..end) and joins the parts above and below.</summary>
    Horizontal,
    /// <summary>Removes a vertical band (columns start..end) and joins the parts left and right.</summary>
    Vertical,
}

/// <summary>Edge treatment where the two parts join after a cut-out.</summary>
public enum CutOutEdgeStyle { None, Fade, Tear, Wave, Saw, Sharktooth }

/// <summary>
/// Removes a strip and joins the remainder. The joined raster (<c>afterAsset</c>) is produced by the caller.
/// Annotations beyond the strip move by the strip length; annotations inside move to the seam.
/// </summary>
public sealed class CutOutCommand : PageCommand, IAssetReferencingCommand
{
    private readonly CutOutOrientation _orientation;
    private readonly double _start;
    private readonly double _end;
    private readonly AssetId _afterAsset;
    private CanvasState? _before;
    private readonly Dictionary<Guid, double> _shifts = new();

    public CutOutCommand(Guid pageId, CutOutOrientation orientation, double start, double end, AssetId afterAsset, CutOutEdgeStyle edge = CutOutEdgeStyle.None, string label = "Cut out")
        : base(pageId, label)
    {
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        _orientation = orientation;
        _start = start;
        _end = end;
        _afterAsset = afterAsset;
        Edge = edge;
    }

    public CutOutEdgeStyle Edge { get; }

    public IEnumerable<AssetId> ReferencedAssets
    {
        get
        {
            yield return _afterAsset;
            if (_before?.Background.BaseImage is { } b) yield return b.Asset;
        }
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = CanvasState.Of(page);
        var len = _end - _start;
        var horizontal = _orientation == CutOutOrientation.Horizontal;
        _shifts.Clear();
        foreach (var a in page.AllAnnotations())
        {
            var c = horizontal ? a.Bounds.Center.Y : a.Bounds.Center.X;
            var shift = c >= _end ? -len : c > _start ? -(c - _start) : 0;
            if (shift == 0) continue;
            _shifts[a.Id] = shift;
            if (horizontal) a.Translate(0, shift); else a.Translate(shift, 0);
        }
        var size = page.CanvasSize;
        page.CanvasSize = horizontal ? size with { Height = Math.Max(1, size.Height - len) } : size with { Width = Math.Max(1, size.Width - len) };
        page.Background = page.Background with { BaseImage = new BaseImageRef(_afterAsset, Point2.Zero) };
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is null) return;
        var page = GetPage(doc);
        var horizontal = _orientation == CutOutOrientation.Horizontal;
        foreach (var a in page.AllAnnotations())
            if (_shifts.TryGetValue(a.Id, out var s))
            {
                if (horizontal) a.Translate(0, -s); else a.Translate(-s, 0);
            }
        _before.ApplyTo(page);
    }
}

/// <summary>
/// Merges annotations into the base raster. The render delegate must return the page as it should look after
/// flattening (canvas size, background + base image + the annotations being flattened). Rendered once; redo reuses it.
/// </summary>
public sealed class FlattenAnnotationsCommand : PageCommand, IAssetReferencingCommand
{
    private readonly Func<Page, BgraImage> _render;
    private readonly HashSet<Guid>? _ids;
    private AssetId? _afterAsset;
    private CanvasState? _before;
    private readonly List<(int Index, Annotation Item)> _removed = new();

    /// <param name="ids">Annotations to flatten; null = all top-level annotations.</param>
    public FlattenAnnotationsCommand(Guid pageId, IEnumerable<Guid>? ids, Func<Page, BgraImage> render, string? label = null)
        : base(pageId, label ?? (ids is null ? "Flatten all" : "Flatten"))
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _ids = ids?.ToHashSet();
    }

    public IEnumerable<AssetId> ReferencedAssets
    {
        get
        {
            if (_afterAsset is { } a) yield return a;
            if (_before?.Background.BaseImage is { } b) yield return b.Asset;
        }
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = CanvasState.Of(page);
        if (_afterAsset is null)
        {
            var img = _render(page);
            _afterAsset = doc.Assets.AddImage(img);
        }
        _removed.Clear();
        for (var i = 0; i < page.Annotations.Count; i++)
        {
            var a = page.Annotations[i];
            if (_ids is null || _ids.Contains(a.Id)) _removed.Add((i, a));
        }
        page.Annotations.RemoveAll(a => _ids is null || _ids.Contains(a.Id));
        page.Background = page.Background with { BaseImage = new BaseImageRef(_afterAsset.Value, Point2.Zero) };
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is null) return;
        var page = GetPage(doc);
        foreach (var (index, item) in _removed.OrderBy(r => r.Index)) page.Annotations.Insert(Math.Min(index, page.Annotations.Count), item);
        _removed.Clear();
        _before.ApplyTo(page);
    }
}
