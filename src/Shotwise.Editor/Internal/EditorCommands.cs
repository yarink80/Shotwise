using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;

namespace Shotwise.Editor.Internal;

/// <summary>
/// Wraps a command that introduces assets (pasted images, stamps, drop-zone content) so the assets stay retained in the
/// document store while the command sits in the undo history (redo after undo must still find them).
/// </summary>
internal sealed class AssetHoldingCommand : IEditCommand, IAssetReferencingCommand
{
    private readonly IEditCommand _inner;
    private readonly List<AssetId> _assets;

    public AssetHoldingCommand(IEditCommand inner, IEnumerable<AssetId> assets)
    {
        _inner = inner;
        _assets = assets.Where(a => !a.IsEmpty).Distinct().ToList();
    }

    public string Label => _inner.Label;
    public IEditCommand Inner => _inner;
    public void Do(ShotDocument doc) => _inner.Do(doc);
    public void Undo(ShotDocument doc) => _inner.Undo(doc);

    public IEnumerable<AssetId> ReferencedAssets
        => _inner is IAssetReferencingCommand r ? _assets.Concat(r.ReferencedAssets) : _assets;
}

/// <summary>Sets one page-level value (Simplify / Smart Redact settings, view flags) with exact undo.</summary>
internal sealed class SetPageValueCommand<T> : PageCommand
{
    private readonly Func<Page, T> _get;
    private readonly Action<Page, T> _set;
    private readonly T _after;
    private T? _before;
    private bool _captured;

    public SetPageValueCommand(Guid pageId, Func<Page, T> get, Action<Page, T> set, T after, string label) : base(pageId, label)
    {
        _get = get;
        _set = set;
        _after = after;
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = _get(page);
        _captured = true;
        _set(page, _after);
    }

    public override void Undo(ShotDocument doc)
    {
        if (!_captured) return;
        _set(GetPage(doc), _before!);
    }
}

/// <summary>
/// Snapshot of a page's layout (canvas size, top-level annotations, template sections). Used for template section
/// add / duplicate / move / remove where many objects shift at once.
/// </summary>
internal sealed record PageLayoutState(Size2 CanvasSize, IReadOnlyList<Annotation> Annotations, IReadOnlyList<TemplateSection> Sections)
{
    public static PageLayoutState Of(Page p) => new(p.CanvasSize, p.Annotations.Select(a => a.DeepClone()).ToList(), p.Sections.Select(s => s.DeepClone()).ToList());

    public void ApplyTo(Page p)
    {
        p.CanvasSize = CanvasSize;
        p.Annotations.Clear();
        p.Annotations.AddRange(Annotations.Select(a => a.DeepClone()));
        p.Sections.Clear();
        p.Sections.AddRange(Sections.Select(s => s.DeepClone()));
    }

    public IEnumerable<AssetId> Assets()
    {
        foreach (var a in Annotations.Concat(Sections.SelectMany(s => s.Graphics)))
            switch (a)
            {
                case StampAnnotation s when !s.Asset.IsEmpty: yield return s.Asset; break;
                case ImageAnnotation i when !i.Asset.IsEmpty: yield return i.Asset; break;
                case DropZoneAnnotation { Content: { IsEmpty: false } c }: yield return c; break;
            }
    }
}

/// <summary>Replaces the page layout state (before → after) with exact undo.</summary>
internal sealed class PageLayoutCommand : PageCommand, IAssetReferencingCommand
{
    private readonly PageLayoutState _before;
    private readonly PageLayoutState _after;

    public PageLayoutCommand(Guid pageId, PageLayoutState before, PageLayoutState after, string label) : base(pageId, label)
    {
        _before = before;
        _after = after;
    }

    public override void Do(ShotDocument doc) => _after.ApplyTo(GetPage(doc));
    public override void Undo(ShotDocument doc) => _before.ApplyTo(GetPage(doc));

    public IEnumerable<AssetId> ReferencedAssets => _before.Assets().Concat(_after.Assets()).Distinct();
}

/// <summary>
/// Removes annotations (top-level or template-section graphics). Unlike holding on to the owning list instance, the undo
/// records each item's owner by id (null = top-level, else the section id) and resolves the live list on every undo/redo,
/// so section graphics are restored into the document even after layout commands replaced the sections with clones (R5-#3).
/// </summary>
internal sealed class RemoveAnnotationsByIdCommand : PageCommand
{
    private readonly HashSet<Guid> _ids;
    private readonly List<(Guid? SectionId, int Index, Annotation Item)> _removed = new();

    public RemoveAnnotationsByIdCommand(Guid pageId, IEnumerable<Guid> ids, string label = "Delete") : base(pageId, label) => _ids = ids.ToHashSet();

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _removed.Clear();
        Collect(page.Annotations, null);
        foreach (var s in page.Sections) Collect(s.Graphics, s.Id);
        page.Annotations.RemoveAll(a => _ids.Contains(a.Id));
        foreach (var s in page.Sections) s.Graphics.RemoveAll(a => _ids.Contains(a.Id));

        void Collect(List<Annotation> list, Guid? owner)
        {
            for (var i = 0; i < list.Count; i++)
                if (_ids.Contains(list[i].Id)) _removed.Add((owner, i, list[i].DeepClone()));
        }
    }

    public override void Undo(ShotDocument doc)
    {
        var page = GetPage(doc);
        foreach (var (sectionId, index, item) in _removed.OrderBy(r => r.Index))
        {
            List<Annotation> list;
            if (sectionId is { } sid)
            {
                // The section may have been replaced by an equal clone (layout undo); if it no longer exists keep the
                // object visible as a top-level annotation rather than silently dropping it.
                list = page.Sections.FirstOrDefault(s => s.Id == sid)?.Graphics ?? page.Annotations;
            }
            else list = page.Annotations;
            if (list.Any(a => a.Id == item.Id)) continue;
            list.Insert(Math.Min(index, list.Count), item.DeepClone());
        }
    }
}

/// <summary>Direct (non-undoable) page edits used for live previews while a gesture is in progress.</summary>
internal static class LiveEdit
{
    /// <summary>Replaces the annotation with the same id (top-level or section graphic) in place.</summary>
    public static bool Replace(Page page, Annotation replacement)
    {
        var i = page.IndexOf(replacement.Id);
        if (i >= 0) { page.Annotations[i] = replacement; return true; }
        foreach (var s in page.Sections)
        {
            var j = s.Graphics.FindIndex(g => g.Id == replacement.Id);
            if (j >= 0) { s.Graphics[j] = replacement; return true; }
        }
        return false;
    }

    public static void Remove(Page page, Guid id)
    {
        page.Annotations.RemoveAll(a => a.Id == id);
        foreach (var s in page.Sections) s.Graphics.RemoveAll(g => g.Id == id);
    }
}
