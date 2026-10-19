using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor;

// Templates (EDT-14) and image drag-in (EDT-15): drop zones (fill / pan / zoom / swap / clear) and sections
// (add / duplicate / move / remove). Sections are assumed to be stacked vertically for add/duplicate/remove/move:
// everything below the affected section shifts and the canvas height changes accordingly.
public sealed partial class EditorSession
{
    /// <summary>Topmost drop zone containing <paramref name="p"/> (section graphics or top-level).</summary>
    public DropZoneAnnotation? DropZoneAt(Point2 p)
        => HitCandidates().OfType<DropZoneAnnotation>().FirstOrDefault(d => d.Bounds.Contains(p));

    /// <summary>Puts an image into a drop zone (cover-fit, centred). One undo step.</summary>
    public void SetDropZoneImage(Guid zoneId, BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (ActivePage.Find(zoneId) is not DropZoneAnnotation) return;
        var asset = Document.Assets.AddImage(image);
        var cmd = ModifyAnnotationsCommand.Create(ActivePage, new[] { zoneId }, a =>
        {
            var d = (DropZoneAnnotation)a;
            d.Content = asset;
            d.ContentOffset = Point2.Zero;
            d.ContentZoom = 1;
        }, "Drop image");
        Execute(new AssetHoldingCommand(cmd, new[] { asset }));
        Selection.Select(zoneId);
    }

    /// <summary>
    /// Drag-drop of an image onto the canvas: fills the drop zone under <paramref name="at"/>, otherwise inserts it as an
    /// image object centred at <paramref name="at"/> (same rules as paste). Returns true when it went into a drop zone.
    /// </summary>
    public bool DropImage(BgraImage image, Point2 at)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (DropZoneAt(at) is { } zone)
        {
            SetDropZoneImage(zone.Id, image);
            return true;
        }
        PasteImage(image, at);
        return false;
    }

    /// <summary>Pans drop-zone content (drag inside a filled zone). Consecutive calls with the same merge key merge.</summary>
    public void PanDropZone(Guid zoneId, double dx, double dy, string? mergeKey = "dropzone-pan")
    {
        if (ActivePage.Find(zoneId) is not DropZoneAnnotation { Content: not null } || (dx == 0 && dy == 0)) return;
        ModifyWhere(new[] { zoneId }, a => { var d = (DropZoneAnnotation)a; d.ContentOffset = d.ContentOffset.Offset(dx, dy); }, "Pan image", mergeKey);
    }

    /// <summary>Zooms drop-zone content by <paramref name="factor"/> (clamped), keeping the zone centre fixed.</summary>
    public void ZoomDropZone(Guid zoneId, double factor, string? mergeKey = "dropzone-zoom")
    {
        if (ActivePage.Find(zoneId) is not DropZoneAnnotation { Content: not null } || factor <= 0 || Math.Abs(factor - 1) < 1e-9) return;
        ModifyWhere(new[] { zoneId }, a =>
        {
            var d = (DropZoneAnnotation)a;
            var nz = Math.Clamp(d.ContentZoom * factor, DropZoneLayout.MinZoom, DropZoneLayout.MaxZoom);
            var k = nz / d.ContentZoom;
            d.ContentOffset = new Point2(d.ContentOffset.X * k, d.ContentOffset.Y * k);
            d.ContentZoom = nz;
        }, "Zoom image", mergeKey);
    }

    /// <summary>Swaps the content (image, pan, zoom) of two drop zones (Combine Images "swap").</summary>
    public void SwapDropZoneContent(Guid a, Guid b)
    {
        if (ActivePage.Find(a) is not DropZoneAnnotation za || ActivePage.Find(b) is not DropZoneAnnotation zb || a == b) return;
        var (ca, oa, ka) = (za.Content, za.ContentOffset, za.ContentZoom);
        var (cb, ob, kb) = (zb.Content, zb.ContentOffset, zb.ContentZoom);
        ModifyWhere(new[] { a, b }, x =>
        {
            var d = (DropZoneAnnotation)x;
            if (d.Id == a) { d.Content = cb; d.ContentOffset = ob; d.ContentZoom = kb; }
            else { d.Content = ca; d.ContentOffset = oa; d.ContentZoom = ka; }
        }, "Swap images");
    }

    public void ClearDropZone(Guid zoneId)
    {
        if (ActivePage.Find(zoneId) is not DropZoneAnnotation { Content: not null }) return;
        ModifyWhere(new[] { zoneId }, a => { var d = (DropZoneAnnotation)a; d.Content = null; d.ContentOffset = Point2.Zero; d.ContentZoom = 1; }, "Clear drop zone");
    }

    private bool WheelDropZone(PointerInput e, double delta)
    {
        if (Selection.Count != 1 || SelectedAnnotations().FirstOrDefault() is not DropZoneAnnotation { Content: not null } zone) return false;
        if (!zone.Bounds.Contains(e.Position) || delta == 0) return false;
        ZoomDropZone(zone.Id, Math.Pow(1.1, delta / 120.0));
        return true;
    }

    // ------------------------------------------------------------------ sections

    /// <summary>Section containing <paramref name="p"/>.</summary>
    public TemplateSection? SectionAt(Point2 p) => ActivePage.Sections.LastOrDefault(s => s.Bounds.Contains(p));

    /// <summary>
    /// Adds a section below <paramref name="after"/> (default: the last body/repeatable section, else the last section).
    /// The new section copies the reference section's layout (empty drop zones); without sections a 200 px body is added.
    /// </summary>
    public Guid? AddSection(SectionType type = SectionType.Body, Guid? after = null)
    {
        var page = ActivePage;
        var reference = after is { } id ? page.Sections.FirstOrDefault(s => s.Id == id)
            : page.Sections.LastOrDefault(s => s.Type is SectionType.Body or SectionType.Repeatable) ?? page.Sections.LastOrDefault();
        TemplateSection section;
        if (reference is not null)
        {
            section = reference.CloneWithNewIds();
            section.Type = type;
        }
        else
        {
            section = new TemplateSection { Type = type, Bounds = new Rect2(0, page.CanvasSize.Height, page.CanvasSize.Width, 200) };
        }
        InsertSection(section, reference, "Add section");
        return section.Id;
    }

    /// <summary>Duplicates a section (graphics with new ids, drop zones emptied) directly below it.</summary>
    public Guid? DuplicateSection(Guid sectionId)
    {
        var reference = ActivePage.Sections.FirstOrDefault(s => s.Id == sectionId);
        if (reference is null) return null;
        var copy = reference.CloneWithNewIds();
        InsertSection(copy, reference, "Duplicate section");
        return copy.Id;
    }

    private void InsertSection(TemplateSection section, TemplateSection? below, string label)
    {
        var page = ActivePage;
        var before = PageLayoutState.Of(page);
        var height = Math.Max(1, (below?.Bounds.Height ?? section.Bounds.Height));
        var insertY = below?.Bounds.Bottom ?? page.CanvasSize.Height;
        var scratch = page.DeepClone();
        ShiftBelow(scratch, insertY, height);
        var dy = insertY - section.Bounds.Y;
        section.Bounds = section.Bounds.Offset(0, dy);
        foreach (var g in section.Graphics) g.Translate(0, dy);
        var index = below is null ? scratch.Sections.Count : scratch.Sections.FindIndex(s => s.Id == below.Id) + 1;
        scratch.Sections.Insert(Math.Clamp(index, 0, scratch.Sections.Count), section);
        scratch.CanvasSize = scratch.CanvasSize with { Height = scratch.CanvasSize.Height + height };
        Selection.Clear();
        Execute(new PageLayoutCommand(page.Id, before, PageLayoutState.Of(scratch), label));
    }

    /// <summary>Removes a section (and its graphics); content below moves up and the canvas shrinks.</summary>
    public void RemoveSection(Guid sectionId)
    {
        var page = ActivePage;
        var section = page.Sections.FirstOrDefault(s => s.Id == sectionId);
        if (section is null) return;
        var before = PageLayoutState.Of(page);
        var scratch = page.DeepClone();
        scratch.Sections.RemoveAll(s => s.Id == sectionId);
        var h = section.Bounds.Height;
        var canShrink = page.CanvasSize.Height - h >= 1 && !page.Sections.Any(s => s.Id != sectionId && s.Bounds.Top < section.Bounds.Bottom && s.Bounds.Bottom > section.Bounds.Top);
        if (canShrink)
        {
            ShiftBelow(scratch, section.Bounds.Bottom, -h);
            scratch.CanvasSize = scratch.CanvasSize with { Height = scratch.CanvasSize.Height - h };
        }
        Selection.Clear();
        Execute(new PageLayoutCommand(page.Id, before, PageLayoutState.Of(scratch), "Remove section"));
    }

    /// <summary>Moves a section one place up (<paramref name="direction"/> &lt; 0) or down in the vertical stack.</summary>
    public void MoveSection(Guid sectionId, int direction)
    {
        if (direction == 0) return;
        var page = ActivePage;
        var ordered = page.Sections.OrderBy(s => s.Bounds.Top).ToList();
        var i = ordered.FindIndex(s => s.Id == sectionId);
        var j = i + Math.Sign(direction);
        if (i < 0 || j < 0 || j >= ordered.Count) return;
        var before = PageLayoutState.Of(page);
        var scratch = page.DeepClone();
        var upper = scratch.Sections.First(s => s.Id == ordered[Math.Min(i, j)].Id);
        var lower = scratch.Sections.First(s => s.Id == ordered[Math.Max(i, j)].Id);
        var top = upper.Bounds.Top;
        var gap = lower.Bounds.Top - upper.Bounds.Bottom;
        var lowerDy = top - lower.Bounds.Top;
        var upperDy = top + lower.Bounds.Height + gap - upper.Bounds.Top;
        MoveSectionBy(lower, lowerDy);
        MoveSectionBy(upper, upperDy);
        Execute(new PageLayoutCommand(page.Id, before, PageLayoutState.Of(scratch), direction < 0 ? "Move section up" : "Move section down"));
    }

    private static void MoveSectionBy(TemplateSection s, double dy)
    {
        s.Bounds = s.Bounds.Offset(0, dy);
        foreach (var g in s.Graphics) g.Translate(0, dy);
    }

    /// <summary>Shifts sections and top-level annotations at/below <paramref name="y"/> by <paramref name="dy"/>.</summary>
    private static void ShiftBelow(Page page, double y, double dy)
    {
        foreach (var s in page.Sections)
            if (s.Bounds.Top >= y - 0.5) MoveSectionBy(s, dy);
        foreach (var a in page.Annotations)
            if (a.Bounds.Top >= y - 0.5) a.Translate(0, dy);
    }
}
