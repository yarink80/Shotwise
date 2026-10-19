using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Styles;
using Shotwise.Editor.Internal;
using Shotwise.Editor.Tools;

namespace Shotwise.Editor;

/// <summary>
/// UI-agnostic editing engine for one open document: active page/tool, selection, undo, input routing, overlay
/// generation and the menu/shortcut commands. The WPF canvas view forwards input in canvas coordinates and draws
/// <see cref="GetOverlay"/> on top of the rendered document. Use from the UI thread; long operations (vision, raster
/// tools) run on the thread pool and apply their result back through <see cref="Execute"/> on the calling context.
/// <para>Every document mutation goes through an <see cref="IEditCommand"/> on <see cref="Undo"/>. Gestures show a
/// live preview by editing the page directly and replace it with one command when the gesture ends.</para>
/// </summary>
public sealed partial class EditorSession : IDisposable
{
    private static readonly EditorSettings DefaultPreferences = new();

    private readonly Dictionary<ToolKind, QuickStyle> _toolStyles = new();
    private readonly IToolFactory _toolFactory;
    private readonly ILogger _log;
    private readonly object _workGate = new();
    private readonly HashSet<Task> _running = new();
    private CancellationTokenSource _workCts = new();
    private Page _activePage;
    private ToolKind _activeTool = ToolKind.Select;
    private ITool _currentTool;
    private bool? _autoExpandOverride;
    private bool _disposed;

    public EditorSession(ShotDocument document, EditorServices services)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        if (document.Pages.Count == 0) throw new ArgumentException("Document has no pages.", nameof(document));
        document.Assets.Codec ??= services.Codec;
        _log = services.LoggerFactory?.CreateLogger<EditorSession>() ?? (ILogger)NullLogger.Instance;
        _toolFactory = services.Tools ?? new DefaultToolFactory();
        _activePage = document.Pages[0];
        Undo = new UndoStack(document);
        Undo.Changed += OnUndoChanged;
        Selection = new SelectionModel();
        Selection.Changed += OnSelectionChanged;
        ToolOptions = new EditorToolOptions();
        SnappingEnabled = services.Settings?.Current.Editor.CanvasSnapping ?? true;
        _currentTool = _toolFactory.Create(ToolKind.Select);
        _currentTool.Activate(this);
    }

    public ShotDocument Document { get; }
    public EditorServices Services { get; }
    public UndoStack Undo { get; }
    public SelectionModel Selection { get; }

    /// <summary>Canvas snapping (Ctrl+;): drags snap to canvas edges/centre and other objects' edges/centres.</summary>
    public bool SnappingEnabled { get; set; }

    /// <summary>Options of non-annotation tools (fill, eraser, selection, wand, crop, cut-out, move, simplify, grab text, pen, highlighter, stamp).</summary>
    public EditorToolOptions ToolOptions { get; }

    /// <summary>Editor preferences in effect (live settings, or defaults when no settings service is available).</summary>
    public EditorSettings Preferences => Services.Settings?.Current.Editor ?? DefaultPreferences;

    /// <summary>Grow the canvas when objects are dragged/pasted beyond its edge (defaults to the preference).</summary>
    public bool AutoExpandCanvas
    {
        get => _autoExpandOverride ?? Preferences.AutoExpandCanvas;
        set => _autoExpandOverride = value;
    }

    /// <summary>Zoom of the most recent pointer event (used for screen-constant tolerances by commands).</summary>
    public double LastZoom { get; private set; } = 1;

    public Page ActivePage
    {
        get => _activePage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Document.Pages.Contains(value)) throw new ArgumentException("Page does not belong to the document.", nameof(value));
            if (ReferenceEquals(value, _activePage)) return;
            EndTextEditForGesture();
            _currentTool.Cancel();
            _activePage = value;
            Selection.Clear();
            ResetPageScopedState();
            ActivePageChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public ToolKind ActiveTool
    {
        get => _activeTool;
        set
        {
            if (value == _activeTool) return;
            EndTextEditForGesture();
            _currentTool.Cancel();
            _currentTool.Deactivate();
            _activeTool = value;
            _currentTool = _toolFactory.Create(value);
            if (!IsPixelTool(value)) Selection.ClearPixelSelection();
            _currentTool.Activate(this);
            ActiveToolChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public ITool CurrentTool => _currentTool;

    public CursorHint CurrentCursor => IsBusy && _currentTool is ToolBase { ShowsBusyCursor: true } ? CursorHint.Wait : _currentTool.Cursor;

    internal static bool IsPixelTool(ToolKind k) => k is ToolKind.Selection or ToolKind.MagicWand;

    // ------------------------------------------------------------------ events

    /// <summary>Redraw request (dirty rect in canvas pixels, null = all).</summary>
    public event EventHandler<InvalidatedEventArgs>? Invalidated;
    public event EventHandler? SelectionChanged;
    public event EventHandler<TextEditRequest>? BeginTextEdit;
    public event EventHandler? ActiveToolChanged;
    public event EventHandler? ActivePageChanged;
    /// <summary>User-facing notifications (no objects found, feature unavailable…).</summary>
    public event EventHandler<EditorMessage>? Message;
    /// <summary>Raised when tool options or tool styles changed (properties panel refresh).</summary>
    public event EventHandler? ToolOptionsChanged;
    /// <summary>Raised when <see cref="IsBusy"/> changes (show progress / wait cursor).</summary>
    public event EventHandler? BusyChanged;
    /// <summary>Grab Text result ready (show the OCR result dialog).</summary>
    public event EventHandler<TextRecognizedEventArgs>? TextRecognized;
    /// <summary>Smart Redact detection result (review pane counts).</summary>
    public event EventHandler<RedactionsFoundEventArgs>? RedactionsFound;
    /// <summary>Smart Move detection finished.</summary>
    public event EventHandler<SmartComponentsDetectedEventArgs>? SmartComponentsDetected;
    /// <summary>An eyedropper sampled a colour (the colour is also stored in the Fill options).</summary>
    public event EventHandler<ColorSampledEventArgs>? ColorSampled;

    public void Invalidate(Rect2? dirty = null) => Invalidated?.Invoke(this, dirty is null ? InvalidatedEventArgs.Empty : new InvalidatedEventArgs(dirty));
    public void Report(EditorMessage message) => Message?.Invoke(this, message);

    internal void ReportUnavailable(string feature)
    {
        _log.LogInformation("{Feature} unavailable: service not registered", feature);
        Report(new EditorMessage(EditorMessageKind.FeatureUnavailable, ErrorCode.NotSupported, $"{feature} is not available."));
    }

    /// <summary>Call after changing <see cref="ToolOptions"/> from a panel; the active tool re-reads them.</summary>
    public void NotifyToolOptionsChanged()
    {
        if (_currentTool is ToolBase tb) tb.OnOptionsChanged();
        ToolOptionsChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    internal void RaiseTextRecognized(TextRecognizedEventArgs e) => TextRecognized?.Invoke(this, e);
    internal void RaiseRedactionsFound(RedactionsFoundEventArgs e) => RedactionsFound?.Invoke(this, e);
    internal void RaiseSmartComponentsDetected(SmartComponentsDetectedEventArgs e) => SmartComponentsDetected?.Invoke(this, e);
    internal void RaiseColorSampled(ColorSampledEventArgs e) => ColorSampled?.Invoke(this, e);

    // ------------------------------------------------------------------ styles

    /// <summary>
    /// Current quick style for a tool: explicitly set one, else the remembered last style (shared across sessions),
    /// else the active theme's first style for the tool.
    /// </summary>
    public QuickStyle? ActiveStyle(ToolKind t)
    {
        if (_toolStyles.TryGetValue(t, out var s)) return s;
        if (Services.StyleMemory?.Get(t) is { } remembered) return remembered;
        return Services.Styles?.ActiveTheme.StylesFor(t).FirstOrDefault();
    }

    /// <summary>Sets the tool's current style (remembered as "last style" for the tool). Option-only styles load <see cref="ToolOptions"/>.</summary>
    public void SetToolStyle(ToolKind t, QuickStyle s)
    {
        ArgumentNullException.ThrowIfNull(s);
        _toolStyles[t] = s;
        Services.StyleMemory?.Set(t, s);
        if (s.Options is { } json) ToolOptions.Apply(t, json);
        if (_currentTool is ToolBase tb && tb.Kind == t) tb.OnOptionsChanged();
        ToolOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Picking a quick style in the gallery: becomes the tool's style and is applied to selected objects it fits.
    /// </summary>
    public void UseQuickStyle(QuickStyle s)
    {
        ArgumentNullException.ThrowIfNull(s);
        SetToolStyle(s.Tool, s);
        if (s.Prototype is not null) ApplyStyleToSelection(s);
    }

    /// <summary>Favorites tool: switches to the style's tool and uses the style.</summary>
    public void UseFavorite(QuickStyle s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Tool != ToolKind.Favorites) ActiveTool = s.Tool;
        UseQuickStyle(s);
    }

    /// <summary>
    /// Properties-panel edit: applies <paramref name="mutate"/> to the selected objects of the tool's kind (one undo
    /// step; consecutive calls with the same <paramref name="mergeKey"/> merge, e.g. slider drags) and to the tool's
    /// current style so the next object drawn with the tool uses it ("remember last style").
    /// </summary>
    public void ChangeStyleProperty(ToolKind tool, Action<Annotation> mutate, string label, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var proto = CreatePrototype(tool);
        if (proto is not null)
        {
            mutate(proto);
            var current = ActiveStyle(tool);
            var style = current?.Clone() ?? new QuickStyle { Tool = tool, Name = "Custom" };
            style.Prototype = proto;
            style.IsBuiltIn = false;
            SetToolStyle(tool, style);
        }
        var kind = proto?.Kind;
        var targets = SelectedAnnotations().Where(a => kind is null || a.Kind == kind).Select(a => a.Id).ToList();
        if (targets.Count > 0) ModifyWhere(targets, mutate, label, mergeKey);
    }

    /// <summary>Creates a new annotation for <paramref name="tool"/> from its current style (fresh id, ungrouped, unlocked).</summary>
    public Annotation? CreatePrototype(ToolKind tool)
    {
        var kind = AnnotationKindFor(tool);
        if (kind is null) return null;
        var style = ActiveStyle(tool);
        Annotation a = style?.Prototype is { } p && p.Kind == kind ? p.DeepClone() : kind.Value.CreateDefault();
        a.Id = Guid.NewGuid();
        a.GroupId = null;
        a.IsLocked = false;
        a.IgnoresUndoAll = false;
        a.RotationDegrees = 0;
        return a;
    }

    internal T CreateFromStyle<T>(ToolKind tool) where T : Annotation, new()
        => CreatePrototype(tool) as T ?? new T { Id = Guid.NewGuid() };

    /// <summary>Annotation kind drawn by a tool, or null for non-annotation tools.</summary>
    public static AnnotationKind? AnnotationKindFor(ToolKind tool) => tool switch
    {
        ToolKind.Arrow => AnnotationKind.Arrow,
        ToolKind.Line => AnnotationKind.Line,
        ToolKind.Shape => AnnotationKind.Shape,
        ToolKind.Callout => AnnotationKind.Callout,
        ToolKind.Text => AnnotationKind.Text,
        ToolKind.Stamp => AnnotationKind.Stamp,
        ToolKind.Step => AnnotationKind.Step,
        ToolKind.Pen => AnnotationKind.Pen,
        ToolKind.Highlighter => AnnotationKind.Highlight,
        ToolKind.Magnify => AnnotationKind.Magnify,
        ToolKind.Blur => AnnotationKind.Blur,
        ToolKind.Cursor => AnnotationKind.Cursor,
        ToolKind.Simplify => AnnotationKind.SimplifyShape,
        _ => null,
    };

    // ------------------------------------------------------------------ input

    private void Track(PointerInput e) => LastZoom = e.EffectiveZoom;

    public void PointerDown(PointerInput e)
    {
        Track(e);
        EndTextEditForGesture();
        _currentTool.PointerDown(e);
    }

    /// <summary>Pointer move; <see cref="PointerInput.Button"/> = pressed button while dragging, <see cref="PointerButton.None"/> while hovering.</summary>
    public void PointerMove(PointerInput e)
    {
        Track(e);
        _currentTool.PointerMove(e);
    }

    public void PointerUp(PointerInput e)
    {
        Track(e);
        _currentTool.PointerUp(e);
    }

    public void DoubleClick(PointerInput e)
    {
        Track(e);
        _currentTool.DoubleClick(e);
    }

    public void PointerLeave() => _currentTool.PointerLeave();

    /// <summary>Mouse wheel over the canvas; returns true when consumed (e.g. zooming the content of a selected drop zone).</summary>
    public bool MouseWheel(PointerInput e, double delta)
    {
        Track(e);
        if (_currentTool is ToolBase tb && tb.Wheel(e, delta)) return true;
        return WheelDropZone(e, delta);
    }

    /// <summary>
    /// Tool-level keys: arrows nudge the selection 1 px (Shift: 10 px), Delete/Backspace delete objects or pixels,
    /// Esc cancels the gesture then deselects, Enter commits the tool (crop, polygon) or edits the selected text.
    /// Menu shortcuts (Ctrl+…) are the view's responsibility.
    /// </summary>
    public void KeyDown(KeyInput e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (ActiveTextEdit is not null)
        {
            if (e.Key == VirtualKey.Escape) { CommitTextEdit(); e.Handled = true; }
            return;
        }
        _currentTool.KeyDown(e);
        if (e.Handled) return;
        switch (e.Key)
        {
            case VirtualKey.Delete or VirtualKey.Back when e.Modifiers == ModifierKeys.None:
                if (Selection.HasObjects || Selection.HasPixelSelection) { DeleteSelection(); e.Handled = true; }
                break;
            case VirtualKey.Escape:
                _currentTool.Cancel();
                if (!Selection.IsEmpty) DeselectAll();
                e.Handled = true;
                break;
            case VirtualKey.Enter when e.Modifiers == ModifierKeys.None:
                if (Selection.Count == 1 && SelectedAnnotations().FirstOrDefault() is { } a and (TextAnnotation or CalloutAnnotation) && !a.IsLocked)
                {
                    BeginEditText(a.Id);
                    e.Handled = true;
                }
                break;
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down when Selection.HasObjects && !e.Control && !e.Alt:
                {
                    var step = e.Shift ? 10 : 1;
                    var (dx, dy) = e.Key switch
                    {
                        VirtualKey.Left => (-step, 0),
                        VirtualKey.Right => (step, 0),
                        VirtualKey.Up => (0, -step),
                        _ => (0, step),
                    };
                    MoveSelection(dx, dy, mergeKey: "nudge");
                    e.Handled = true;
                    break;
                }
        }
    }

    // ------------------------------------------------------------------ overlay

    /// <summary>
    /// Overlay primitives for the current zoom: selection outlines + handles (when the tool manipulates objects),
    /// pixel-selection tint/outline, the text-edit frame and the tool's own primitives (marquee, guides, crop frame,
    /// cut-out band, previews, readouts). Coordinates are canvas pixels; stroke widths are screen pixels.
    /// </summary>
    public IReadOnlyList<OverlayPrimitive> GetOverlay(double zoom)
    {
        var list = new List<OverlayPrimitive>();
        var z = zoom > 0 ? zoom : 1;
        if (Selection.HasObjects)
        {
            var selected = SelectedAnnotations();
            foreach (var a in selected)
            {
                if (AnnotationGeometry.IsLineLike(a))
                    list.Add(new OverlayLine(a.Start, a.End) { Style = OverlayStyle.Selection });
                else
                    list.Add(new OverlayRect(SelectionHandles.ManipulationBox(a), a.RotationDegrees) { Style = a.IsLocked ? OverlayStyle.Hover : OverlayStyle.Selection });
            }
            if (selected.Count > 1) list.Add(new OverlayRect(AnnotationGeometry.Union(selected)) { Style = OverlayStyle.Hover });
            if (_currentTool is ToolBase { ShowsObjectHandles: true })
            {
                var handles = SelectionHandles.For(selected, z);
                var rotHandle = handles.FirstOrDefault(h => h.Kind == HandleKind.Rotate);
                if (rotHandle is not null)
                {
                    var (box, r) = SelectionHandles.Frame(selected);
                    var top = AnnotationGeometry.ToWorld(new Point2(box.Center.X, box.Y), box.Center, r);
                    list.Add(new OverlayLine(top, rotHandle.Position) { Style = OverlayStyle.Selection });
                }
                foreach (var h in handles)
                    list.Add(new OverlayHandle(h.Id, h.Position, h.Kind) { Style = OverlayStyle.Handle });
            }
        }
        if (Selection.PixelMask is { } mask && Selection.PixelBounds is { } pb)
        {
            list.Add(new OverlayImageTint(pb, new Argb(0x331E88E5), mask) { Style = OverlayStyle.Marquee });
            if (Selection.PixelOutline is { Count: > 1 } outline)
                list.Add(new OverlayPath(outline, true) { Style = OverlayStyle.Marquee });
            else
                list.Add(new OverlayRect(pb) { Style = OverlayStyle.Marquee });
        }
        if (ActiveTextEdit is { } te)
            list.Add(new OverlayRect(te.Bounds, te.RotationDegrees) { Style = OverlayStyle.Hover });
        list.AddRange(_currentTool.Overlay(z));
        return list;
    }

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// Topmost annotation under <paramref name="p"/> using renderer outlines; tolerance max(4/zoom, strokeWidth/2).
    /// Top-level annotations are tested first (topmost = last), then template section graphics.
    /// </summary>
    public Annotation? HitTest(Point2 p, double zoom)
    {
        foreach (var a in HitCandidates())
            if (HitsAnnotation(a, p, zoom)) return a;
        return null;
    }

    /// <summary>All annotations under <paramref name="p"/>, topmost first.</summary>
    public IReadOnlyList<Annotation> HitTestAll(Point2 p, double zoom) => HitCandidates().Where(a => HitsAnnotation(a, p, zoom)).ToList();

    internal bool HitsAnnotation(Annotation a, Point2 p, double zoom)
    {
        Geometry2 outline;
        try { outline = Services.Renderer.GetOutline(a); }
        catch (ShotwiseException ex)
        {
            _log.LogDebug(ex, "Outline failed for {Id}", a.Id);
            outline = Geometry2.Empty;
        }
        return HitTesting.Hit(a, outline, p, HitTesting.Tolerance(zoom));
    }

    private IEnumerable<Annotation> HitCandidates()
    {
        var page = ActivePage;
        for (var i = page.Annotations.Count - 1; i >= 0; i--) yield return page.Annotations[i];
        for (var s = page.Sections.Count - 1; s >= 0; s--)
        {
            var g = page.Sections[s].Graphics;
            for (var i = g.Count - 1; i >= 0; i--) yield return g[i];
        }
    }

    /// <summary>Top-level annotations whose outline intersects <paramref name="marquee"/> (canvas pixels).</summary>
    public IReadOnlyList<Annotation> HitTestRect(Rect2 marquee)
    {
        var r = marquee.Normalize();
        return ActivePage.Annotations.Where(a =>
        {
            var o = Services.Renderer.GetOutline(a);
            return o.Figures.Count == 0 ? a.RotatedBounds.IntersectsWith(r) : o.IntersectsRect(r);
        }).ToList();
    }

    public IReadOnlyList<Annotation> SelectedAnnotations()
        => Selection.Ids.Select(id => ActivePage.Find(id)).Where(a => a is not null).Cast<Annotation>().ToList();

    /// <summary>Ids of <paramref name="ids"/> plus every member of the groups they belong to.</summary>
    public IReadOnlyList<Guid> ExpandToGroups(IEnumerable<Guid> ids)
    {
        var set = ids.ToList();
        var groups = set.Select(id => ActivePage.Find(id)?.GroupId).Where(g => g is not null).ToHashSet();
        if (groups.Count == 0) return set;
        var result = new List<Guid>(set);
        foreach (var a in ActivePage.AllAnnotations())
            if (a.GroupId is { } g && groups.Contains(g) && !result.Contains(a.Id)) result.Add(a.Id);
        return result;
    }

    // ------------------------------------------------------------------ command execution

    /// <summary>Executes a command on the undo stack and requests a redraw.</summary>
    public void Execute(IEditCommand cmd)
    {
        Undo.Execute(cmd, Document);
        Invalidate();
    }

    /// <summary>Executes several commands as one undo step (skips when empty).</summary>
    public void ExecuteComposite(string label, IEnumerable<IEditCommand> commands)
    {
        var list = commands.Where(c => c is not null).ToList();
        if (list.Count == 0) return;
        Execute(list.Count == 1 ? list[0] : new CompositeCommand(label, list));
    }

    // ------------------------------------------------------------------ background work

    /// <summary>True while a background operation (vision, raster tool, effect) is running.</summary>
    public bool IsBusy { get { lock (_workGate) return _running.Count > 0; } }

    /// <summary>Completes when every background operation started so far has finished.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_workGate) snapshot = _running.ToArray();
            if (snapshot.Length == 0) return;
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels running background operations (Esc on a long operation, document close).</summary>
    public void CancelBackgroundWork()
    {
        CancellationTokenSource old;
        lock (_workGate)
        {
            old = _workCts;
            _workCts = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="work"/> as tracked background work: exceptions are logged and reported as
    /// <see cref="Message"/>; cancellation is silent. The continuation of <paramref name="work"/> resumes on the
    /// caller's synchronization context (UI thread) so it may execute commands.
    /// </summary>
    internal Task RunBackground(string operation, Func<CancellationToken, Task> work, CancellationToken external = default)
    {
        CancellationToken token;
        lock (_workGate) token = _workCts.Token;
        var linked = external.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token, external) : null;
        var ct = linked?.Token ?? token;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_workGate) _running.Add(tcs.Task);
        BusyChanged?.Invoke(this, EventArgs.Empty);
        _ = RunCore();
        return tcs.Task;

        async Task RunCore()
        {
            try
            {
                await work(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _log.LogDebug("{Operation} cancelled", operation);
            }
            catch (ShotwiseException ex)
            {
                _log.LogWarning(ex, "{Operation} failed", operation);
                Report(new EditorMessage(EditorMessageKind.Error, ex.Code, ex.Message));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or OutOfMemoryException or NotSupportedException)
            {
                _log.LogError(ex, "{Operation} failed", operation);
                Report(new EditorMessage(EditorMessageKind.Error, ex is OutOfMemoryException ? ErrorCode.OutOfMemory : ErrorCode.Unknown, ex.Message));
            }
            finally
            {
                linked?.Dispose();
                lock (_workGate) _running.Remove(tcs.Task);
                // Notify before completing the task, so whoever awaits it already sees the final busy state.
                try
                {
                    BusyChanged?.Invoke(this, EventArgs.Empty);
                    Invalidate();
                }
                finally
                {
                    tcs.TrySetResult();
                }
            }
        }
    }

    // ------------------------------------------------------------------ save revisions (R5-#1)

    private bool _markingSaved;
    private bool _saveStale;

    /// <summary>
    /// Monotonic content revision: incremented by every undo-history change (execute, merge, undo, redo, jump, clear) except
    /// marking the document saved. Capture it right before snapshotting the document for a save and pass it to
    /// <see cref="CompleteSave"/> when the write finished, so edits made while the write was in flight stay dirty.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>True when the document differs from the last completed save (undo position moved, or a save completed after newer edits).</summary>
    public bool HasUnsavedChanges => _saveStale || !Undo.IsClean;

    /// <summary>
    /// Call after a save of the snapshot taken at <paramref name="savedRevision"/> completed. Marks the current history
    /// position clean only when nothing changed since that snapshot; otherwise the document stays dirty (even if the
    /// history position later returns to the old clean point) until a newer save completes. Returns true when marked clean.
    /// </summary>
    public bool CompleteSave(long savedRevision)
    {
        if (savedRevision != Revision)
        {
            _saveStale = true;
            Document.IsDirty = true;
            return false;
        }
        _saveStale = false;
        _markingSaved = true;
        try { Undo.MarkClean(); }
        finally { _markingSaved = false; }
        Document.IsDirty = false;
        return true;
    }

    // ------------------------------------------------------------------ housekeeping

    private void OnUndoChanged(object? sender, EventArgs e)
    {
        if (!_markingSaved) Revision++;
        PruneReleasedRasters();
        var existing = ActivePage.AllAnnotations().Select(a => a.Id).ToHashSet();
        Selection.Retain(existing);
        if (ActiveTextEdit is { } te && !existing.Contains(te.AnnotationId) && !_textEditPendingNew) ClearTextEditState();
        InvalidateDerivedCaches();
        if (_currentTool is ToolBase tb) tb.OnDocumentChanged();
        Invalidate();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void ResetPageScopedState()
    {
        ClearTextEditState();
        InvalidateDerivedCaches();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelBackgroundWork();
        try { _currentTool.Deactivate(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _log.LogDebug(ex, "Tool deactivate failed"); }
        Undo.Changed -= OnUndoChanged;
        Selection.Changed -= OnSelectionChanged;
        lock (_workGate) _workCts.Dispose();
    }
}
