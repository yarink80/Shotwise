using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor;

// Menu / shortcut commands: selection, clipboard, arrange, flatten, styles, canvas, crop, cut-out, effects.
public sealed partial class EditorSession
{
    // ------------------------------------------------------------------ selection

    /// <summary>Ctrl+A: all objects; with the Selection / Magic Wand tool active, all pixels.</summary>
    public void SelectAll()
    {
        if (IsPixelTool(ActiveTool))
        {
            SelectAllPixels();
            return;
        }
        Selection.Set(ActivePage.Annotations.Select(a => a.Id));
    }

    /// <summary>"Select All Objects" regardless of tool.</summary>
    public void SelectAllObjects() => Selection.Set(ActivePage.Annotations.Select(a => a.Id));

    public void DeselectAll() => Selection.Clear();

    /// <summary>Ctrl+Shift+I: inverts the object selection, or the pixel mask when a pixel selection exists.</summary>
    public void InvertSelection()
    {
        if (Selection.PixelMask is { } m)
        {
            var inv = m.Invert();
            SetPixelSelection(inv, PixelSelectionShape.Freehand, null);
            return;
        }
        var sel = Selection.ToSet();
        Selection.Set(ActivePage.Annotations.Where(a => !sel.Contains(a.Id)).Select(a => a.Id));
    }

    /// <summary>Delete: removes selected objects (drop zones are emptied instead), or deletes selected pixels.</summary>
    public void DeleteSelection()
    {
        if (Selection.HasObjects)
        {
            var selected = SelectedAnnotations();
            var zones = selected.OfType<DropZoneAnnotation>().Where(d => d.Content is not null).Select(d => d.Id).ToList();
            var removable = selected.Where(a => a is not DropZoneAnnotation).Select(a => a.Id).ToList();
            var cmds = new List<IEditCommand>();
            if (removable.Count > 0) cmds.Add(new RemoveAnnotationsByIdCommand(ActivePage.Id, removable));
            if (zones.Count > 0) cmds.Add(ModifyAnnotationsCommand.Create(ActivePage, zones, a => { var d = (DropZoneAnnotation)a; d.Content = null; d.ContentOffset = Point2.Zero; d.ContentZoom = 1; }, "Clear drop zone"));
            Selection.ClearObjects();
            ExecuteComposite("Delete", cmds);
            return;
        }
        if (Selection.HasPixelSelection) _ = DeletePixelsAsync();
    }

    // ------------------------------------------------------------------ clipboard

    /// <summary>
    /// Ctrl+C. Objects: payload with deep-cloned annotations + referenced assets + a flattened bitmap of just those
    /// objects (for other apps). Pixel selection: the visible pixels under the mask as an image.
    /// </summary>
    public ClipboardPayload? Copy()
    {
        if (!Selection.HasObjects && Selection.PixelMask is { } mask && Selection.PixelBounds is { } pb)
            return CopyPixels(mask, pb);
        return CopyObjects(SelectedAnnotations(), includeImage: true);
    }

    /// <summary>Ctrl+Shift+C "Copy All": the whole flattened page plus every annotation.</summary>
    public ClipboardPayload CopyAll()
    {
        var all = ActivePage.Annotations.ToList();
        BgraImage? img = TryRender(new RenderOptions());
        var objects = CopyObjects(all, includeImage: false);
        return new ClipboardPayload(objects?.Annotations ?? Array.Empty<Annotation>(), objects?.Assets ?? Array.Empty<EmbeddedAsset>(),
            ActivePage.CanvasRect, img, objects?.Text);
    }

    internal ClipboardPayload? CopyObjects(IReadOnlyList<Annotation> selected, bool includeImage)
    {
        if (selected.Count == 0) return null;
        var clones = selected.Select(a => a.DeepClone()).ToList();
        var assets = new List<EmbeddedAsset>();
        foreach (var id in clones.SelectMany(ReferencedAssets).Distinct())
            if (Document.Assets.TryGet(id, out var data, out var mt)) assets.Add(new EmbeddedAsset(id, mt, data));
        var text = string.Join("\n", clones.Select(a => a switch { TextAnnotation t => t.Text.PlainText, CalloutAnnotation c => c.Text.PlainText, _ => null }).Where(s => !string.IsNullOrEmpty(s)));
        var bounds = AnnotationGeometry.Union(clones);
        BgraImage? image = null;
        if (includeImage)
        {
            var visual = Rect2.FromLTRB(Math.Floor(bounds.Left), Math.Floor(bounds.Top), Math.Ceiling(bounds.Right), Math.Ceiling(bounds.Bottom));
            foreach (var a in selected)
            {
                try { visual = visual.Union(Services.Renderer.MeasureBounds(a)); }
                catch (ShotwiseException ex) { _log.LogDebug(ex, "MeasureBounds failed"); }
            }
            if (visual.Width >= 1 && visual.Height >= 1)
                image = TryRender(new RenderOptions(TransparentBackground: true) { Region = visual.ToRectIOutward().ToRect2(), Only = selected.Select(a => a.Id).ToHashSet() });
        }
        return new ClipboardPayload(clones, assets, bounds, image, text.Length > 0 ? text : null);
    }

    private ClipboardPayload CopyPixels(Mask mask, Rect2 bounds)
    {
        var region = bounds.ToRectIOutward();
        var visible = TryRender(new RenderOptions()) ?? RasterOps.ComposeFlattenedBase(Document, ActivePage);
        var fullMask = mask.Width == visible.Width && mask.Height == visible.Height ? mask : ResizeMaskToCanvas(mask, visible.Width, visible.Height);
        var img = RasterOps.ExtractMasked(visible, fullMask, region);
        return new ClipboardPayload(Array.Empty<Annotation>(), Array.Empty<EmbeddedAsset>(), region.Intersect(visible.Bounds).ToRect2(), img);
    }

    internal BgraImage? TryRender(RenderOptions o)
    {
        try
        {
            return Services.Renderer.RenderPage(Document, ActivePage, o);
        }
        catch (ShotwiseException ex)
        {
            _log.LogWarning(ex, "Render for clipboard failed");
            return null;
        }
    }

    /// <summary>Ctrl+X: copy, then delete (objects, or pixels with the selection background option).</summary>
    public ClipboardPayload? Cut()
    {
        var p = Copy();
        if (p is not null) DeleteSelection();
        return p;
    }

    /// <summary>
    /// Pastes annotations (new ids, groups remapped, assets imported). <paramref name="at"/> = centre of the pasted
    /// content; null = same place (+10 px if the originals are still on the page). Image-only payloads become an image
    /// object (or are flattened into the base when "Automatically flatten pasted images" is on); text-only payloads
    /// become a text box. The canvas auto-expands when the content falls outside and the preference is on.
    /// </summary>
    public void Paste(ClipboardPayload p, Point2? at)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (!p.HasAnnotations)
        {
            if (p.Image is { } img) PasteImage(img, at);
            else if (!string.IsNullOrEmpty(p.Text)) PasteText(p.Text!, at);
            return;
        }
        var assetMap = new Dictionary<AssetId, AssetId>();
        foreach (var a in p.Assets) assetMap[a.Id] = Document.Assets.Add(a.Data, a.MediaType);
        var groupMap = new Dictionary<Guid, Guid>();
        double dx = 0, dy = 0;
        if (at is { } target)
        {
            dx = target.X - p.Bounds.Center.X;
            dy = target.Y - p.Bounds.Center.Y;
        }
        else if (p.Annotations.Any(a => ActivePage.Find(a.Id) is not null))
        {
            dx = dy = 10;
        }
        var pasted = new List<Annotation>();
        foreach (var src in p.Annotations)
        {
            var a = src.DeepClone();
            a.Id = Guid.NewGuid();
            if (a.GroupId is { } g)
            {
                if (!groupMap.TryGetValue(g, out var ng)) groupMap[g] = ng = Guid.NewGuid();
                a.GroupId = ng;
            }
            RemapAssets(a, assetMap);
            a.Translate(dx, dy);
            pasted.Add(a);
        }
        var cmds = new List<IEditCommand> { new AssetHoldingCommand(new AddAnnotationsCommand(ActivePage.Id, pasted, "Paste"), assetMap.Values) };
        if (ExpandCanvasCommandFor(pasted) is { } expand) cmds.Add(expand);
        ExecuteComposite("Paste", cmds);
        Selection.Set(pasted.Select(a => a.Id));
    }

    private void PasteImage(BgraImage img, Point2? at)
    {
        var page = ActivePage;
        var (w, h) = ((double)img.Width, (double)img.Height);
        if (!Preferences.KeepOriginalImageDimensions && (w > page.CanvasSize.Width || h > page.CanvasSize.Height))
        {
            var s = Math.Min(page.CanvasSize.Width / w, page.CanvasSize.Height / h);
            w *= s;
            h *= s;
        }
        var center = at ?? page.CanvasRect.Center;
        var rect = new Rect2(Math.Round(center.X - w / 2), Math.Round(center.Y - h / 2), w, h);
        if (Preferences.AutoFlattenPastedImages && Math.Abs(w - img.Width) < 0.5)
        {
            FlattenImageIntoBase(img, rect, "Paste image");
            return;
        }
        var asset = Document.Assets.AddImage(img);
        var a = new ImageAnnotation { Asset = asset, Start = rect.TopLeft, End = rect.BottomRight };
        var cmds = new List<IEditCommand> { new AssetHoldingCommand(new AddAnnotationCommand(page.Id, a, label: "Paste image"), new[] { asset }) };
        if (ExpandCanvasCommandFor(new[] { a }) is { } expand) cmds.Add(expand);
        ExecuteComposite("Paste image", cmds);
        Selection.Select(a.Id);
    }

    private void PasteText(string text, Point2? at)
    {
        var t = CreateFromStyle<TextAnnotation>(ToolKind.Text);
        var fmt = t.Text.Runs.FirstOrDefault() ?? new TextRun();
        t.Text = new RichText(new[] { fmt with { Text = text } });
        var lines = text.Split('\n');
        var w = Math.Clamp(lines.Max(l => l.Length) * fmt.FontSize * 0.6 + t.TextStyle.Padding.Horizontal, 40, Math.Max(40, ActivePage.CanvasSize.Width));
        var h = lines.Length * fmt.FontSize * 1.35 * t.TextStyle.LineSpacing + t.TextStyle.Padding.Vertical;
        var c = at ?? ActivePage.CanvasRect.Center;
        t.Start = new Point2(c.X - w / 2, c.Y - h / 2);
        t.End = new Point2(c.X + w / 2, c.Y + h / 2);
        Execute(new AddAnnotationCommand(ActivePage.Id, t, label: "Paste text"));
        Selection.Select(t.Id);
    }

    /// <summary>Duplicate (objects): copies the selection offset by 10 px.</summary>
    public void Duplicate()
    {
        var p = CopyObjects(SelectedAnnotations(), includeImage: false);
        if (p is null) return;
        Paste(p, new Point2(p.Bounds.Center.X + 10, p.Bounds.Center.Y + 10));
    }

    // ------------------------------------------------------------------ arrange

    public void Group()
    {
        var ids = SelectedAnnotations().Where(a => ActivePage.IndexOf(a.Id) >= 0).Select(a => a.Id).ToList();
        if (ids.Count < 2) return;
        Execute(SetGroupCommand.Group(ActivePage.Id, ids));
    }

    public void Ungroup()
    {
        var ids = SelectedAnnotations().Where(a => a.GroupId is not null).Select(a => a.Id).ToList();
        if (ids.Count == 0) return;
        Execute(SetGroupCommand.Ungroup(ActivePage.Id, ids));
    }

    public void BringForward() => Reorder(ZOrder.BringForward, "Bring forward");
    public void SendBackward() => Reorder(ZOrder.SendBackward, "Send backward");
    public void BringToFront() => Reorder(ZOrder.BringToFront, "Bring to front");
    public void SendToBack() => Reorder(ZOrder.SendToBack, "Send to back");

    private void Reorder(Func<IReadOnlyList<Guid>, IReadOnlySet<Guid>, IReadOnlyList<Guid>> op, string label)
    {
        if (!Selection.HasObjects) return;
        var order = ActivePage.Annotations.Select(a => a.Id).ToList();
        var next = op(order, Selection.ToSet());
        if (next.SequenceEqual(order)) return;
        Execute(new ReorderAnnotationsCommand(ActivePage.Id, next, label));
    }

    /// <summary>Aligns the selection (rotated bounds) to its union bounds; a single object aligns to the canvas.</summary>
    public void Align(AlignKind k)
    {
        var sel = SelectedAnnotations().Where(a => !a.IsLocked).ToList();
        if (sel.Count == 0) return;
        var reference = sel.Count == 1 ? ActivePage.CanvasRect : AnnotationGeometry.Union(sel);
        ModifyWhere(sel.Select(a => a.Id), a =>
        {
            var b = a.RotatedBounds;
            var (dx, dy) = k switch
            {
                AlignKind.Left => (reference.X - b.X, 0.0),
                AlignKind.Center => (reference.Center.X - b.Center.X, 0.0),
                AlignKind.Right => (reference.Right - b.Right, 0.0),
                AlignKind.Top => (0.0, reference.Y - b.Y),
                AlignKind.Middle => (0.0, reference.Center.Y - b.Center.Y),
                _ => (0.0, reference.Bottom - b.Bottom),
            };
            a.Translate(dx, dy);
        }, $"Align {k}");
    }

    /// <summary>Evenly distributes the centres of 3+ selected objects between the outermost two.</summary>
    public void Distribute(bool horizontal)
    {
        var sel = SelectedAnnotations().Where(a => !a.IsLocked).ToList();
        if (sel.Count < 3) return;
        var ordered = sel.OrderBy(a => horizontal ? a.RotatedBounds.Center.X : a.RotatedBounds.Center.Y).ToList();
        var first = horizontal ? ordered[0].RotatedBounds.Center.X : ordered[0].RotatedBounds.Center.Y;
        var last = horizontal ? ordered[^1].RotatedBounds.Center.X : ordered[^1].RotatedBounds.Center.Y;
        var step = (last - first) / (ordered.Count - 1);
        var targets = ordered.Select((a, i) => (a.Id, Pos: first + i * step)).ToDictionary(t => t.Id, t => t.Pos);
        ModifyWhere(targets.Keys, a =>
        {
            var c = a.RotatedBounds.Center;
            if (horizontal) a.Translate(targets[a.Id] - c.X, 0); else a.Translate(0, targets[a.Id] - c.Y);
        }, horizontal ? "Distribute horizontally" : "Distribute vertically");
    }

    /// <summary>Flip Horizontal / Vertical of the selected objects about their common centre.</summary>
    public void FlipSelection(bool horizontal)
    {
        var sel = SelectedAnnotations().Where(a => !a.IsLocked).ToList();
        if (sel.Count == 0) return;
        var c = AnnotationGeometry.Union(sel).Center;
        ModifyWhere(sel.Select(a => a.Id), a => AnnotationGeometry.Flip(a, c, horizontal), horizontal ? "Flip horizontal" : "Flip vertical");
    }

    /// <summary>Rotates the selected objects by <paramref name="degrees"/> around their common centre.</summary>
    public void RotateSelection(double degrees)
    {
        var sel = SelectedAnnotations().Where(a => !a.IsLocked).ToList();
        if (sel.Count == 0 || degrees == 0) return;
        var c = AnnotationGeometry.Union(sel).Center;
        ModifyWhere(sel.Select(a => a.Id), a => AnnotationGeometry.RotateAround(a, sel.Count == 1 ? a.Bounds.Center : c, degrees), "Rotate");
    }

    /// <summary>Ctrl+T / Ctrl+Shift+T: merges the selection (or all annotations) into the base raster via the renderer.</summary>
    public void Flatten(bool all)
    {
        if (!all && !Selection.HasObjects) return;
        if (all && ActivePage.Annotations.Count == 0) return;
        IReadOnlySet<Guid>? only = all ? null : SelectedAnnotations().Where(a => ActivePage.IndexOf(a.Id) >= 0).Select(a => a.Id).ToHashSet();
        if (only is { Count: 0 }) return;
        var doc = Document;
        var renderer = Services.Renderer;
        Selection.ClearObjects();
        Execute(new FlattenAnnotationsCommand(ActivePage.Id, only, page => renderer.RenderPage(doc, page, new RenderOptions { Only = only })));
    }

    /// <summary>Flatten All (Ctrl+Shift+T).</summary>
    public void FlattenAll() => Flatten(all: true);

    public void Lock(bool locked)
    {
        if (!Selection.HasObjects) return;
        ModifySelection(a => a.IsLocked = locked, locked ? "Lock" : "Unlock");
    }

    // ------------------------------------------------------------------ styles

    /// <summary>Applies a quick style's prototype to the selected objects of the same kind.</summary>
    public void ApplyStyleToSelection(QuickStyle s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Prototype is not { } proto) return;
        var targets = SelectedAnnotations().Where(a => a.Kind == proto.Kind).Select(a => a.Id).ToList();
        if (targets.Count == 0) return;
        ModifyWhere(targets, a => StyleApplier.Apply(proto, a), "Apply style");
    }

    /// <summary>"Add to Quick Styles": a new quick style whose prototype is a copy of the (single) selected object.</summary>
    public QuickStyle? CreateQuickStyleFromSelection(string name)
    {
        if (SelectedAnnotations() is not { Count: 1 } sel) return null;
        var a = sel[0];
        var tool = StyleApplier.ToolFor(a.Kind);
        if (tool == ToolKind.Select) return null;
        var proto = a.DeepClone();
        proto.Id = Guid.NewGuid();
        proto.GroupId = null;
        proto.IsLocked = false;
        return new QuickStyle { Name = name, Tool = tool, Prototype = proto };
    }

    /// <summary>Applies <paramref name="mutate"/> to every selected annotation as one undoable step.</summary>
    public void ModifySelection(Action<Annotation> mutate, string label) => ModifySelection(mutate, label, mergeKey: null);

    /// <summary>As <see cref="ModifySelection(Action{Annotation}, string)"/>; consecutive calls with the same <paramref name="mergeKey"/> merge (slider drags).</summary>
    public void ModifySelection(Action<Annotation> mutate, string label, string? mergeKey)
    {
        if (!Selection.HasObjects) return;
        ModifyWhere(Selection.Ids, mutate, label, mergeKey);
    }

    /// <summary>Modifies the given annotations (by id) as one undo step.</summary>
    public void ModifyAnnotations(IEnumerable<Guid> ids, Action<Annotation> mutate, string label, string? mergeKey = null) => ModifyWhere(ids, mutate, label, mergeKey);

    internal void ModifyWhere(IEnumerable<Guid> ids, Action<Annotation> mutate, string label, string? mergeKey = null)
    {
        var list = ids.Where(id => ActivePage.Find(id) is not null).Distinct().ToList();
        if (list.Count == 0) return;
        Execute(ModifyAnnotationsCommand.Create(ActivePage, list, mutate, label, mergeKey));
    }

    /// <summary>Moves the selected (unlocked) annotations.</summary>
    public void MoveSelection(double dx, double dy, string? mergeKey = null)
    {
        var ids = SelectedAnnotations().Where(a => !a.IsLocked).Select(a => a.Id).ToList();
        if (ids.Count == 0 || (dx == 0 && dy == 0)) return;
        ModifyWhere(ids, a => a.Translate(dx, dy), "Move", mergeKey);
    }

    // ------------------------------------------------------------------ canvas / effects

    /// <summary>Applies a raster effect to the page base (background + base image; annotations untouched but offset).</summary>
    public Task ApplyEffectAsync(EffectSpec e) => ApplyEffectAsync(e, CancellationToken.None);

    /// <summary>
    /// As <see cref="ApplyEffectAsync(EffectSpec)"/> with cancellation. A Spotlight &amp; Magnify effect with an empty
    /// region uses the pixel-selection bounds (or the single selected object's bounds).
    /// </summary>
    public Task ApplyEffectAsync(EffectSpec e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e is SpotlightMagnifyEffect { Region.IsEmpty: true } spot)
        {
            Rect2? region = Selection.PixelBounds ?? (SelectedAnnotations() is { Count: > 0 } sel ? AnnotationGeometry.Union(sel) : null);
            if (region is null)
            {
                Report(new EditorMessage(EditorMessageKind.Warning, ErrorCode.InvalidArgument, "Select an area first."));
                return Task.CompletedTask;
            }
            e = spot with { Region = region.Value };
        }
        var page = ActivePage;
        var doc = Document;
        var renderer = Services.Renderer;
        var effects = Services.Effects;
        var effect = e;
        var snap = Snapshot(page);
        var baseImage = renderer.RenderPage(doc, page, new RenderOptions(IncludeAnnotations: false));
        return RunBackground("Apply effect", async token =>
        {
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return effects.Apply(baseImage, effect, doc.Metadata);
            }, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(page, snap))
            {
                _log.LogInformation("Effect result discarded: page changed while it was computed");
                return;
            }
            var asset = AddEditorRaster(result.Image);
            Execute(new ApplyEffectCommand(page.Id, effect, asset, new Size2(result.Image.Width, result.Image.Height), result.ContentOffset));
        }, ct);
    }

    public void ResizeCanvas(Size2 size, Anchor9 anchor, Argb? fill)
    {
        if (size.Width < 1 || size.Height < 1) return;
        Execute(new ResizeCanvasCommand(ActivePage.Id, size, anchor, fill));
    }

    /// <summary>Canvas colour (right-click with Fill/Eraser, Image ▸ Canvas Color).</summary>
    public void SetCanvasColor(Argb color)
    {
        var bg = ActivePage.Background;
        if (bg.Color == color && bg.Gradient is null) return;
        Execute(new SetCanvasBackgroundCommand(ActivePage.Id, bg with { Color = color, Gradient = null }));
    }

    /// <summary>Canvas corner radius and drop shadow (EDT-04).</summary>
    public void SetCanvasStyle(double cornerRadius, ShadowStyle? shadow)
        => Execute(new SetCanvasStyleCommand(ActivePage.Id, Math.Max(0, cornerRadius), shadow));

    /// <summary>
    /// Crops the active page to <paramref name="r"/> (clipped to the canvas). Non-destructive by default (the base image
    /// keeps its pixels, offset); destructive when the "Delete cropped content" preference is on.
    /// </summary>
    public void Crop(Rect2 r)
    {
        var rect = r.Normalize().Intersect(ActivePage.CanvasRect);
        rect = Rect2.FromLTRB(Math.Round(rect.Left), Math.Round(rect.Top), Math.Round(rect.Right), Math.Round(rect.Bottom));
        if (rect.Width < 1 || rect.Height < 1) return;
        if (rect == ActivePage.CanvasRect) return;
        AssetId? cropped = null;
        if (Preferences.DeleteCroppedContent)
        {
            var baseImage = Services.Renderer.RenderPage(Document, ActivePage, new RenderOptions(IncludeAnnotations: false));
            cropped = AddEditorRaster(baseImage.Crop(rect.ToRectIRounded()));
        }
        Selection.ClearPixelSelection();
        Execute(new CropCommand(ActivePage.Id, rect, cropped));
    }

    /// <summary>True when the page base image extends beyond the canvas (a non-destructive crop can be reset).</summary>
    public bool CanResetCrop
        => ActivePage.Background.BaseImage is { } bi && BaseImageRect(bi) is { } r && !r.IsEmpty && r != ActivePage.CanvasRect && !ActivePage.CanvasRect.Contains(r);

    /// <summary>"Reset crop": grows the canvas back to the full base image (undoes non-destructive crops in one step).</summary>
    public void ResetCrop()
    {
        if (ActivePage.Background.BaseImage is not { } bi || BaseImageRect(bi) is not { } r || r.IsEmpty) return;
        var target = ActivePage.CanvasRect.Union(r);
        if (target == ActivePage.CanvasRect) return;
        Execute(new CropCommand(ActivePage.Id, target, null, "Reset crop"));
    }

    private Rect2? BaseImageRect(BaseImageRef bi)
    {
        try
        {
            var img = Document.Assets.GetImage(bi.Asset);
            return new Rect2(bi.Offset.X, bi.Offset.Y, img.Width, img.Height);
        }
        catch (ShotwiseException ex)
        {
            _log.LogDebug(ex, "Base image unavailable");
            return null;
        }
    }

    /// <summary>
    /// Cut Out: removes the band [start, end) (rows for <see cref="CutOutOrientation.Horizontal"/>, columns for vertical)
    /// and joins the remaining parts with the given seam edge style. Annotations beyond the band move with the content.
    /// </summary>
    public void CutOut(CutOutOrientation orientation, double start, double end, CutOutEdgeStyle edge = CutOutEdgeStyle.None)
    {
        var size = ActivePage.CanvasSize;
        var extent = orientation == CutOutOrientation.Horizontal ? size.Height : size.Width;
        var s = (int)Math.Round(Math.Clamp(Math.Min(start, end), 0, extent));
        var e = (int)Math.Round(Math.Clamp(Math.Max(start, end), 0, extent));
        if (e - s < 1 || e - s >= (int)extent) return;
        var src = Services.Renderer.RenderPage(Document, ActivePage, new RenderOptions(IncludeAnnotations: false));
        var joined = CutOutRaster.Join(src, orientation, s, e, edge);
        var asset = AddEditorRaster(joined);
        Selection.ClearPixelSelection();
        Execute(new CutOutCommand(ActivePage.Id, orientation, s, e, asset, edge));
    }

    internal static BgraImage JoinWithoutBand(BgraImage src, CutOutOrientation orientation, int start, int end)
        => CutOutRaster.JoinPlain(src, orientation, start, end);

    // ------------------------------------------------------------------ auto-expand canvas

    /// <summary>
    /// When auto-expand is on and <paramref name="items"/> (their rotated bounds) extend beyond the canvas, a command
    /// that grows the canvas (shifting everything when growing left/up) to include them; otherwise null.
    /// </summary>
    internal IEditCommand? ExpandCanvasCommandFor(IEnumerable<Annotation> items)
    {
        if (!AutoExpandCanvas) return null;
        var list = items.ToList();
        if (list.Count == 0) return null;
        var canvas = ActivePage.CanvasRect;
        var u = AnnotationGeometry.Union(list);
        var target = Rect2.FromLTRB(Math.Floor(Math.Min(canvas.Left, u.Left)), Math.Floor(Math.Min(canvas.Top, u.Top)),
            Math.Ceiling(Math.Max(canvas.Right, u.Right)), Math.Ceiling(Math.Max(canvas.Bottom, u.Bottom)));
        if (target.Left >= canvas.Left && target.Top >= canvas.Top && target.Right <= canvas.Right && target.Bottom <= canvas.Bottom) return null;
        return new CropCommand(ActivePage.Id, target, null, "Expand canvas");
    }

    // ------------------------------------------------------------------ helpers

    internal static IEnumerable<AssetId> ReferencedAssets(Annotation a) => a switch
    {
        StampAnnotation s when !s.Asset.IsEmpty => new[] { s.Asset },
        ImageAnnotation i when !i.Asset.IsEmpty => new[] { i.Asset },
        DropZoneAnnotation { Content: { IsEmpty: false } c } => new[] { c },
        _ => Array.Empty<AssetId>(),
    };

    private static void RemapAssets(Annotation a, IReadOnlyDictionary<AssetId, AssetId> map)
    {
        switch (a)
        {
            case StampAnnotation s when map.TryGetValue(s.Asset, out var n): s.Asset = n; break;
            case ImageAnnotation i when map.TryGetValue(i.Asset, out var n): i.Asset = n; break;
            case DropZoneAnnotation { Content: { } c } d when map.TryGetValue(c, out var n): d.Content = n; break;
        }
    }
}
