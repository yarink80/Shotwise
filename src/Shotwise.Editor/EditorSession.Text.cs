using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Geometry;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor;

// Inline text editing protocol (EDT-10).
//  1. Session raises BeginTextEdit(TextEditRequest) — new box (IsNew) or existing text/callout.
//  2. The view shows its rich-text overlay at request.Bounds/rotation and hides the annotation from rendering
//     (RenderOptions.Exclude = { EditingAnnotationId }).
//  3. Optional live updates: UpdateTextEdit(text, contentSize) keeps the document box in sync (no undo).
//  4. CommitTextEdit(text, contentSize) creates one undo step (Add / Modify / Remove-if-empty); CancelTextEdit()
//     discards. When the user clicks elsewhere the session raises EndTextEditRequested first; a view that does not
//     handle it gets an automatic commit of the last live text.
public sealed partial class EditorSession
{
    private TextEditRequest? _textEdit;
    private bool _textEditPendingNew;
    private int _textEditIndex = -1;
    private Annotation? _textEditOriginal;

    /// <summary>The text edit in progress, or null.</summary>
    public TextEditRequest? ActiveTextEdit => _textEdit;

    /// <summary>Annotation being edited inline — exclude it from rendering while the overlay editor is shown.</summary>
    public Guid? EditingAnnotationId => _textEdit?.AnnotationId;

    /// <summary>Raised before a pointer gesture / tool switch while editing: the view should call <see cref="CommitTextEdit(RichText, Size2?)"/>.</summary>
    public event EventHandler? EndTextEditRequested;

    /// <summary>Raises <see cref="BeginTextEdit"/> for an arbitrary request (the annotation must exist on the active page).</summary>
    public void RequestTextEdit(TextEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_textEdit is not null && _textEdit.AnnotationId != request.AnnotationId) EndTextEditForGesture();
        var a = ActivePage.Find(request.AnnotationId);
        if (a is null) return;
        _textEdit = request;
        _textEditOriginal ??= a.DeepClone();
        BeginTextEdit?.Invoke(this, request);
        Invalidate();
    }

    /// <summary>Starts editing an existing text or callout annotation (double-click, Enter, Text tool click).</summary>
    public bool BeginEditText(Guid annotationId, int? caretIndex = null)
    {
        var a = ActivePage.Find(annotationId);
        if (a is null || a.IsLocked) return false;
        if (!TryGetText(a, out var text, out var style)) return false;
        if (_textEdit?.AnnotationId == annotationId) return true;
        EndTextEditForGesture();
        _textEditPendingNew = false;
        _textEditOriginal = a.DeepClone();
        Selection.Select(annotationId);
        var req = new TextEditRequest(annotationId, a.Bounds, a.RotationDegrees, text.Clone(), style, IsNew: false, caretIndex);
        _textEdit = req;
        BeginTextEdit?.Invoke(this, req);
        Invalidate();
        return true;
    }

    /// <summary>Creates a new text/callout box (not yet in history) and starts editing it.</summary>
    internal void BeginNewTextEdit(Annotation a)
    {
        if (!TryGetText(a, out var text, out var style)) throw new ArgumentException("Annotation has no text.", nameof(a));
        EndTextEditForGesture();
        ActivePage.Annotations.Add(a);
        _textEditIndex = ActivePage.Annotations.Count - 1;
        _textEditPendingNew = true;
        _textEditOriginal = null;
        Selection.Select(a.Id);
        var req = new TextEditRequest(a.Id, a.Bounds, a.RotationDegrees, text.Clone(), style, IsNew: true);
        _textEdit = req;
        BeginTextEdit?.Invoke(this, req);
        Invalidate();
    }

    /// <summary>Live update while typing: keeps the document text/box in sync (not undoable; the commit is).</summary>
    public void UpdateTextEdit(RichText text, Size2? contentSize = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_textEdit is not { } te || ActivePage.Find(te.AnnotationId) is not { } a) return;
        var copy = a.DeepClone();
        SetText(copy, text.Normalize(), contentSize);
        LiveEdit.Replace(ActivePage, copy);
        _textEdit = te with { Text = text.Clone(), Bounds = copy.Bounds };
        Invalidate();
    }

    /// <summary>Commits with the last live text (used when the view did not handle <see cref="EndTextEditRequested"/>).</summary>
    public void CommitTextEdit()
    {
        if (_textEdit is not { } te) return;
        var a = ActivePage.Find(te.AnnotationId);
        if (a is null || !TryGetText(a, out var text, out _)) { ClearTextEditState(); return; }
        CommitTextEdit(text, null);
    }

    /// <summary>
    /// Ends editing with <paramref name="text"/>. New boxes are added (one undo step) unless empty text boxes are set to
    /// auto-delete; existing boxes are modified, or removed when emptied and auto-delete applies. AutoSize boxes take
    /// <paramref name="contentSize"/> (or the renderer's measurement) plus padding.
    /// </summary>
    public void CommitTextEdit(RichText text, Size2? contentSize = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_textEdit is not { } te) return;
        var page = ActivePage;
        var current = page.Find(te.AnnotationId);
        var pendingNew = _textEditPendingNew;
        var original = _textEditOriginal;
        var index = _textEditIndex;
        ClearTextEditState();
        if (current is null) { Invalidate(); return; }
        var final = current.DeepClone();
        var normalized = text.Normalize();
        SetText(final, normalized, contentSize);
        var deleteIfEmpty = final is TextAnnotation { DeselectBehavior: TextDeselectBehavior.DeleteIfEmpty } && normalized.IsEmpty;

        if (pendingNew)
        {
            LiveEdit.Remove(page, final.Id);
            if (deleteIfEmpty)
            {
                Selection.Remove(final.Id);
                Invalidate();
                return;
            }
            var cmds = new List<IEditCommand> { new AddAnnotationCommand(page.Id, final, index >= 0 ? index : null, final is CalloutAnnotation ? "Add callout" : "Add text") };
            if (ExpandCanvasCommandFor(new[] { final }) is { } expand) cmds.Add(expand);
            ExecuteComposite(cmds[0].Label, cmds);
            Selection.Select(final.Id);
            return;
        }

        if (original is not null) LiveEdit.Replace(page, original.DeepClone());
        if (deleteIfEmpty)
        {
            Selection.Remove(final.Id);
            Execute(new RemoveAnnotationsByIdCommand(page.Id, new[] { final.Id }, "Delete empty text"));
            return;
        }
        if (original is not null && SameText(original, final) && original.Bounds == final.Bounds) { Invalidate(); return; }
        Execute(new ModifyAnnotationsCommand(page.Id, new[] { original ?? current }, new[] { final }, "Edit text"));
    }

    /// <summary>Discards the edit: a new box disappears, an existing one reverts to its text before editing.</summary>
    public void CancelTextEdit()
    {
        if (_textEdit is not { } te) return;
        var page = ActivePage;
        if (_textEditPendingNew) { LiveEdit.Remove(page, te.AnnotationId); Selection.Remove(te.AnnotationId); }
        else if (_textEditOriginal is not null) LiveEdit.Replace(page, _textEditOriginal.DeepClone());
        ClearTextEditState();
        Invalidate();
    }

    internal void EndTextEditForGesture()
    {
        if (_textEdit is null) return;
        EndTextEditRequested?.Invoke(this, EventArgs.Empty);
        if (_textEdit is not null) CommitTextEdit();
    }

    private void ClearTextEditState()
    {
        _textEdit = null;
        _textEditPendingNew = false;
        _textEditOriginal = null;
        _textEditIndex = -1;
    }

    internal static bool TryGetText(Annotation a, out RichText text, out TextLayoutStyle style)
    {
        switch (a)
        {
            case TextAnnotation t: text = t.Text; style = t.TextStyle; return true;
            case CalloutAnnotation c: text = c.Text; style = c.TextStyle; return true;
            default: text = new RichText(); style = TextLayoutStyle.Default; return false;
        }
    }

    private static bool SameText(Annotation a, Annotation b)
        => TryGetText(a, out var ta, out _) && TryGetText(b, out var tb, out _) && ta.Runs.SequenceEqual(tb.Runs);

    /// <summary>Sets the text and, for auto-size boxes, grows the box to the measured content plus padding.</summary>
    private void SetText(Annotation a, RichText text, Size2? contentSize)
    {
        if (!TryGetText(a, out _, out var style)) return;
        switch (a)
        {
            case TextAnnotation t: t.Text = text; break;
            case CalloutAnnotation c: c.Text = text; break;
        }
        if (!style.AutoSize) return;
        var box = a.Bounds;
        var size = contentSize;
        if (size is null)
        {
            try
            {
                var maxW = a is TextAnnotation ? 0 : Math.Max(1, box.Width - style.Padding.Horizontal);
                size = Services.Renderer.LayoutText(text, style, maxW).ContentSize;
            }
            catch (ShotwiseException ex)
            {
                _log.LogDebug(ex, "Text layout failed");
            }
        }
        if (size is not { } s || s.IsEmpty) return;
        var w = Math.Max(box.Width, s.Width + style.Padding.Horizontal);
        var h = a is CalloutAnnotation ? Math.Max(box.Height, s.Height + style.Padding.Vertical) : s.Height + style.Padding.Vertical;
        a.Start = box.TopLeft;
        a.End = new Point2(box.X + w, box.Y + h);
    }
}
