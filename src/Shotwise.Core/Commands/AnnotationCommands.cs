using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;

namespace Shotwise.Core.Commands;

/// <summary>Base for commands that target one page by id.</summary>
public abstract class PageCommand : IEditCommand
{
    protected PageCommand(Guid pageId, string label)
    {
        PageId = pageId;
        Label = label;
    }

    public Guid PageId { get; }
    public string Label { get; }

    public abstract void Do(ShotDocument doc);
    public abstract void Undo(ShotDocument doc);
    public virtual bool TryMerge(IEditCommand next) => false;

    protected Page GetPage(ShotDocument doc) => doc.GetPage(PageId);

    /// <summary>Finds the list (top-level or section graphics) containing an annotation id.</summary>
    protected static List<Annotation>? OwnerList(Page page, Guid id)
    {
        if (page.IndexOf(id) >= 0) return page.Annotations;
        foreach (var s in page.Sections)
            if (s.Graphics.Any(g => g.Id == id)) return s.Graphics;
        return null;
    }

    protected static void ReplaceById(Page page, Annotation replacement)
    {
        var list = OwnerList(page, replacement.Id) ?? throw new ShotwiseException(ErrorCode.InvalidArgument, $"Annotation {replacement.Id} not found.");
        var i = list.FindIndex(a => a.Id == replacement.Id);
        list[i] = replacement;
    }
}

/// <summary>Inserts an annotation (a private snapshot; each Do inserts a fresh deep clone with the same id).</summary>
public sealed class AddAnnotationCommand : PageCommand
{
    private readonly Annotation _snapshot;
    private readonly int? _index;

    public AddAnnotationCommand(Guid pageId, Annotation annotation, int? index = null, string? label = null)
        : base(pageId, label ?? $"Add {annotation.Kind}")
    {
        _snapshot = annotation.DeepClone();
        _index = index;
    }

    public Guid AnnotationId => _snapshot.Id;

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        var a = _snapshot.DeepClone();
        if (_index is { } i && i >= 0 && i <= page.Annotations.Count) page.Annotations.Insert(i, a);
        else page.Annotations.Add(a);
    }

    public override void Undo(ShotDocument doc)
    {
        var page = GetPage(doc);
        page.Annotations.RemoveAll(a => a.Id == _snapshot.Id);
    }
}

/// <summary>Inserts several annotations at the top of the z-order (paste, duplicate, smart redact, simplify).</summary>
public sealed class AddAnnotationsCommand : PageCommand
{
    private readonly List<Annotation> _snapshots;

    public AddAnnotationsCommand(Guid pageId, IEnumerable<Annotation> annotations, string label)
        : base(pageId, label) => _snapshots = annotations.Select(a => a.DeepClone()).ToList();

    public IReadOnlyList<Guid> AnnotationIds => _snapshots.Select(a => a.Id).ToList();

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        foreach (var s in _snapshots) page.Annotations.Add(s.DeepClone());
    }

    public override void Undo(ShotDocument doc)
    {
        var page = GetPage(doc);
        var ids = _snapshots.Select(s => s.Id).ToHashSet();
        page.Annotations.RemoveAll(a => ids.Contains(a.Id));
    }
}

/// <summary>Removes annotations (top-level or section graphics); undo restores them at their original indices.</summary>
public sealed class RemoveAnnotationsCommand : PageCommand
{
    private readonly HashSet<Guid> _ids;
    private readonly List<(List<Annotation> List, int Index, Annotation Item)> _removed = new();

    public RemoveAnnotationsCommand(Guid pageId, IEnumerable<Guid> ids, string label = "Delete")
        : base(pageId, label) => _ids = ids.ToHashSet();

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _removed.Clear();
        foreach (var list in new[] { page.Annotations }.Concat(page.Sections.Select(s => s.Graphics)))
        {
            for (var i = 0; i < list.Count; i++)
                if (_ids.Contains(list[i].Id)) _removed.Add((list, i, list[i]));
        }
        foreach (var grp in _removed.GroupBy(r => r.List))
            grp.Key.RemoveAll(a => _ids.Contains(a.Id));
    }

    public override void Undo(ShotDocument doc)
    {
        foreach (var (list, index, item) in _removed.OrderBy(r => r.Index))
        {
            var i = Math.Min(index, list.Count);
            list.Insert(i, item);
        }
        _removed.Clear();
    }
}

/// <summary>
/// Replaces annotations with "after" snapshots (Do) or "before" snapshots (Undo), matched by id.
/// Consecutive commands with the same non-null <see cref="MergeKey"/> and id set merge (slider drags, nudges, typing).
/// </summary>
public sealed class ModifyAnnotationsCommand : PageCommand
{
    private readonly List<Annotation> _before;
    private List<Annotation> _after;

    public ModifyAnnotationsCommand(Guid pageId, IReadOnlyList<Annotation> before, IReadOnlyList<Annotation> after, string label, string? mergeKey = null)
        : base(pageId, label)
    {
        if (before.Count != after.Count) throw new ArgumentException("before/after counts differ.");
        _before = before.Select(a => a.DeepClone()).ToList();
        _after = after.Select(a => a.DeepClone()).ToList();
        MergeKey = mergeKey;
    }

    public string? MergeKey { get; }
    public IReadOnlyList<Guid> AnnotationIds => _before.Select(a => a.Id).ToList();

    /// <summary>Snapshots the current state of <paramref name="ids"/>, applies <paramref name="mutate"/> to copies and returns the command (not executed).</summary>
    public static ModifyAnnotationsCommand Create(Page page, IEnumerable<Guid> ids, Action<Annotation> mutate, string label, string? mergeKey = null)
    {
        var before = ids.Select(id => page.Find(id) ?? throw new ShotwiseException(ErrorCode.InvalidArgument, $"Annotation {id} not found.")).ToList();
        var after = before.Select(a => { var c = a.DeepClone(); mutate(c); return c; }).ToList();
        return new ModifyAnnotationsCommand(page.Id, before, after, label, mergeKey);
    }

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        foreach (var a in _after) ReplaceById(page, a.DeepClone());
    }

    public override void Undo(ShotDocument doc)
    {
        var page = GetPage(doc);
        foreach (var a in _before) ReplaceById(page, a.DeepClone());
    }

    public override bool TryMerge(IEditCommand next)
    {
        if (MergeKey is null || next is not ModifyAnnotationsCommand m || m.MergeKey != MergeKey || m.PageId != PageId) return false;
        if (!m._before.Select(a => a.Id).ToHashSet().SetEquals(_before.Select(a => a.Id))) return false;
        _after = m._after.Select(a => a.DeepClone()).ToList();
        return true;
    }
}

/// <summary>Sets a new full z-order of the page's top-level annotations.</summary>
public sealed class ReorderAnnotationsCommand : PageCommand
{
    private readonly List<Guid> _newOrder;
    private List<Guid>? _oldOrder;

    public ReorderAnnotationsCommand(Guid pageId, IReadOnlyList<Guid> newOrder, string label = "Arrange")
        : base(pageId, label) => _newOrder = newOrder.ToList();

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _oldOrder = page.Annotations.Select(a => a.Id).ToList();
        Apply(page, _newOrder);
    }

    public override void Undo(ShotDocument doc)
    {
        if (_oldOrder is null) return;
        Apply(GetPage(doc), _oldOrder);
    }

    private static void Apply(Page page, IReadOnlyList<Guid> order)
    {
        var byId = page.Annotations.ToDictionary(a => a.Id);
        var result = new List<Annotation>(page.Annotations.Count);
        foreach (var id in order)
            if (byId.Remove(id, out var a)) result.Add(a);
        // anything not mentioned keeps relative order at the top
        result.AddRange(page.Annotations.Where(a => byId.ContainsKey(a.Id)));
        page.Annotations.Clear();
        page.Annotations.AddRange(result);
    }
}

/// <summary>Z-order helpers producing the new id order for <see cref="ReorderAnnotationsCommand"/>.</summary>
public static class ZOrder
{
    public static IReadOnlyList<Guid> BringToFront(IReadOnlyList<Guid> order, IReadOnlySet<Guid> sel)
        => order.Where(id => !sel.Contains(id)).Concat(order.Where(sel.Contains)).ToList();

    public static IReadOnlyList<Guid> SendToBack(IReadOnlyList<Guid> order, IReadOnlySet<Guid> sel)
        => order.Where(sel.Contains).Concat(order.Where(id => !sel.Contains(id))).ToList();

    public static IReadOnlyList<Guid> BringForward(IReadOnlyList<Guid> order, IReadOnlySet<Guid> sel)
    {
        var list = order.ToList();
        for (var i = list.Count - 2; i >= 0; i--)
            if (sel.Contains(list[i]) && !sel.Contains(list[i + 1])) (list[i], list[i + 1]) = (list[i + 1], list[i]);
        return list;
    }

    public static IReadOnlyList<Guid> SendBackward(IReadOnlyList<Guid> order, IReadOnlySet<Guid> sel)
    {
        var list = order.ToList();
        for (var i = 1; i < list.Count; i++)
            if (sel.Contains(list[i]) && !sel.Contains(list[i - 1])) (list[i], list[i - 1]) = (list[i - 1], list[i]);
        return list;
    }
}

/// <summary>Assigns (group) or clears (ungroup) <see cref="Annotation.GroupId"/> for a set of annotations.</summary>
public sealed class SetGroupCommand : PageCommand
{
    private readonly List<Guid> _ids;
    private readonly Guid? _groupId;
    private Dictionary<Guid, Guid?>? _previous;

    public SetGroupCommand(Guid pageId, IEnumerable<Guid> ids, Guid? groupId, string? label = null)
        : base(pageId, label ?? (groupId is null ? "Ungroup" : "Group"))
    {
        _ids = ids.ToList();
        _groupId = groupId;
    }

    public static SetGroupCommand Group(Guid pageId, IEnumerable<Guid> ids) => new(pageId, ids, Guid.NewGuid(), "Group");
    public static SetGroupCommand Ungroup(Guid pageId, IEnumerable<Guid> ids) => new(pageId, ids, null, "Ungroup");

    public Guid? GroupId => _groupId;

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _previous = new();
        foreach (var id in _ids)
        {
            var a = page.Find(id);
            if (a is null) continue;
            _previous[id] = a.GroupId;
            a.GroupId = _groupId;
        }
    }

    public override void Undo(ShotDocument doc)
    {
        if (_previous is null) return;
        var page = GetPage(doc);
        foreach (var (id, gid) in _previous)
            if (page.Find(id) is { } a) a.GroupId = gid;
    }
}

/// <summary>Sets the page's next step-sequence values (restart sequence / edit value).</summary>
public sealed class SetStepCountersCommand : PageCommand
{
    private readonly StepCounters _after;
    private StepCounters? _before;

    public SetStepCountersCommand(Guid pageId, StepCounters after, string label = "Step sequence") : base(pageId, label) => _after = after;

    public override void Do(ShotDocument doc)
    {
        var page = GetPage(doc);
        _before = page.StepCounters;
        page.StepCounters = _after;
    }

    public override void Undo(ShotDocument doc)
    {
        if (_before is not null) GetPage(doc).StepCounters = _before;
    }
}
