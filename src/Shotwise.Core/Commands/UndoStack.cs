using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;

namespace Shotwise.Core.Commands;

/// <summary>An undoable edit. <see cref="Do"/> must be repeatable after <see cref="Undo"/> (redo).</summary>
public interface IEditCommand
{
    string Label { get; }
    void Do(ShotDocument doc);
    void Undo(ShotDocument doc);
    /// <summary>Attempts to absorb <paramref name="next"/> (already executed) into this command; true = merged (next is discarded).</summary>
    bool TryMerge(IEditCommand next) => false;
}

/// <summary>Commands that keep raster assets alive while they sit in the undo history.</summary>
public interface IAssetReferencingCommand
{
    IEnumerable<AssetId> ReferencedAssets { get; }
}

/// <summary>
/// Linear undo/redo history for one document. Executing a command clears the redo branch.
/// Assets referenced by commands (<see cref="IAssetReferencingCommand"/>) are retained in the document's
/// <see cref="AssetStore"/> while the command is in history and released when it is dropped.
/// Not thread-safe: use from the UI thread.
/// </summary>
public sealed class UndoStack
{
    private readonly List<IEditCommand> _history = new();
    private ShotDocument? _doc;
    private int _cleanPosition;
    private bool _executing;

    public UndoStack(ShotDocument? document = null)
    {
        _doc = document;
    }

    /// <summary>Raised after every change of history or position.</summary>
    public event EventHandler? Changed;

    /// <summary>Maximum number of commands kept; oldest commands are dropped beyond this.</summary>
    public int MaxDepth { get; set; } = 500;

    /// <summary>Number of applied commands (index of the next redo).</summary>
    public int Position { get; private set; }

    public bool CanUndo => Position > 0 && !_executing;
    public bool CanRedo => Position < _history.Count && !_executing;

    /// <summary>Labels of all commands in history (applied ones are indices &lt; <see cref="Position"/>).</summary>
    public IReadOnlyList<string> History => _history.Select(c => c.Label).ToList();

    public IReadOnlyList<IEditCommand> Commands => _history;

    public string? UndoLabel => CanUndo ? _history[Position - 1].Label : null;
    public string? RedoLabel => CanRedo ? _history[Position].Label : null;

    /// <summary>The document this stack operates on (set by the first <see cref="Execute"/> if not given in the constructor).</summary>
    public ShotDocument? Document => _doc;

    /// <summary>True when the history position equals the last <see cref="MarkClean"/> point.</summary>
    public bool IsClean => Position == _cleanPosition;

    /// <summary>Applies <paramref name="cmd"/> and records it (merging with the previous command when possible).</summary>
    public void Execute(IEditCommand cmd, ShotDocument doc)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(doc);
        if (_executing) throw new InvalidOperationException("Re-entrant UndoStack.Execute.");
        if (_doc is not null && !ReferenceEquals(_doc, doc)) throw new InvalidOperationException("UndoStack is bound to a different document.");
        _doc = doc;
        _executing = true;
        try
        {
            cmd.Do(doc);
        }
        finally
        {
            _executing = false;
        }
        DropRedoBranch();

        var merged = Position > 0 && Position != _cleanPosition && _history[Position - 1].TryMerge(cmd);
        if (!merged)
        {
            _history.Add(cmd);
            RetainAssets(cmd);
            Position++;
            TrimToMaxDepth();
        }
        UpdateDirty();
        RaiseChanged();
    }

    /// <summary>Records a command whose effect has already been applied (e.g. live drag); does not call <see cref="IEditCommand.Do"/>.</summary>
    public void Push(IEditCommand alreadyApplied, ShotDocument doc)
    {
        ArgumentNullException.ThrowIfNull(alreadyApplied);
        _doc ??= doc;
        DropRedoBranch();
        var merged = Position > 0 && Position != _cleanPosition && _history[Position - 1].TryMerge(alreadyApplied);
        if (!merged)
        {
            _history.Add(alreadyApplied);
            RetainAssets(alreadyApplied);
            Position++;
            TrimToMaxDepth();
        }
        doc.IsDirty = true;
        RaiseChanged();
    }

    public void Undo()
    {
        if (!CanUndo || _doc is null) return;
        _executing = true;
        try { _history[Position - 1].Undo(_doc); Position--; }
        finally { _executing = false; }
        UpdateDirty();
        RaiseChanged();
    }

    public void Redo()
    {
        if (!CanRedo || _doc is null) return;
        _executing = true;
        try { _history[Position].Do(_doc); Position++; }
        finally { _executing = false; }
        UpdateDirty();
        RaiseChanged();
    }

    /// <summary>Moves to an arbitrary history position (Undo Stack window click).</summary>
    public void GoTo(int position)
    {
        position = Math.Clamp(position, 0, _history.Count);
        if (_doc is null) return;
        _executing = true;
        try
        {
            while (Position > position) { _history[Position - 1].Undo(_doc); Position--; }
            while (Position < position) { _history[Position].Do(_doc); Position++; }
        }
        finally { _executing = false; }
        UpdateDirty();
        RaiseChanged();
    }

    /// <summary>
    /// Undoes every applied command. Annotations flagged <see cref="Annotation.IgnoresUndoAll"/> that existed before
    /// and would disappear are restored afterwards; in that case the redo branch is discarded (it would be inconsistent).
    /// </summary>
    public void UndoAll()
    {
        if (_doc is null || Position == 0) return;
        var keep = _doc.Pages.ToDictionary(p => p.Id, p => p.Annotations.Where(a => a.IgnoresUndoAll).Select(a => a.DeepClone()).ToList());
        _executing = true;
        try
        {
            while (Position > 0) { _history[Position - 1].Undo(_doc); Position--; }
        }
        finally { _executing = false; }

        var restored = false;
        foreach (var page in _doc.Pages)
        {
            if (!keep.TryGetValue(page.Id, out var kept)) continue;
            foreach (var a in kept)
            {
                if (page.IndexOf(a.Id) >= 0) continue;
                page.Annotations.Add(a);
                restored = true;
            }
        }
        if (restored)
        {
            DropRedoBranch();
            _doc.IsDirty = true;
        }
        else UpdateDirty();
        RaiseChanged();
    }

    /// <summary>Clears all history (releases retained assets).</summary>
    public void Clear()
    {
        foreach (var c in _history) ReleaseAssets(c);
        _history.Clear();
        Position = 0;
        _cleanPosition = 0;
        RaiseChanged();
    }

    /// <summary>Marks the current position as saved (document not dirty).</summary>
    public void MarkClean()
    {
        _cleanPosition = Position;
        if (_doc is not null) _doc.IsDirty = false;
        RaiseChanged();
    }

    /// <summary>All asset ids held by commands in history (for diagnostics / pruning).</summary>
    public IEnumerable<AssetId> ReferencedAssets() => _history.OfType<IAssetReferencingCommand>().SelectMany(c => c.ReferencedAssets);

    private void DropRedoBranch()
    {
        if (Position >= _history.Count) return;
        for (var i = Position; i < _history.Count; i++) ReleaseAssets(_history[i]);
        _history.RemoveRange(Position, _history.Count - Position);
        if (_cleanPosition > Position) _cleanPosition = -1; // saved state no longer reachable
    }

    private void TrimToMaxDepth()
    {
        var excess = _history.Count - Math.Max(1, MaxDepth);
        if (excess <= 0) return;
        for (var i = 0; i < excess; i++) ReleaseAssets(_history[i]);
        _history.RemoveRange(0, excess);
        Position -= excess;
        _cleanPosition = _cleanPosition >= excess ? _cleanPosition - excess : -1;
    }

    private void RetainAssets(IEditCommand c)
    {
        if (_doc is null || c is not IAssetReferencingCommand r) return;
        foreach (var id in r.ReferencedAssets) _doc.Assets.Retain(id);
    }

    private void ReleaseAssets(IEditCommand c)
    {
        if (_doc is null || c is not IAssetReferencingCommand r) return;
        foreach (var id in r.ReferencedAssets) _doc.Assets.Release(id);
    }

    private void UpdateDirty()
    {
        if (_doc is not null) _doc.IsDirty = !IsClean;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Executes child commands in order; undoes in reverse order.</summary>
public sealed class CompositeCommand : IEditCommand, IAssetReferencingCommand
{
    private readonly List<IEditCommand> _children;

    public CompositeCommand(string label, IEnumerable<IEditCommand> children)
    {
        Label = label;
        _children = children.ToList();
    }

    public string Label { get; }
    public IReadOnlyList<IEditCommand> Children => _children;
    public bool IsEmpty => _children.Count == 0;

    public void Do(ShotDocument doc)
    {
        foreach (var c in _children) c.Do(doc);
    }

    public void Undo(ShotDocument doc)
    {
        for (var i = _children.Count - 1; i >= 0; i--) _children[i].Undo(doc);
    }

    public IEnumerable<AssetId> ReferencedAssets => _children.OfType<IAssetReferencingCommand>().SelectMany(c => c.ReferencedAssets);
}
