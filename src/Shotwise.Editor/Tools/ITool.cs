using Shotwise.Core.Styles;

namespace Shotwise.Editor.Tools;

/// <summary>
/// Interaction controller for one tool. The session forwards canvas input to the active tool; tools mutate the
/// document only through <see cref="EditorSession.Undo"/> commands (or live-preview then one command at gesture end).
/// </summary>
public interface ITool
{
    ToolKind Kind { get; }
    void Activate(EditorSession s);
    void Deactivate();
    void PointerDown(PointerInput e);
    void PointerMove(PointerInput e);
    void PointerUp(PointerInput e);
    void KeyDown(KeyInput e);
    IEnumerable<OverlayPrimitive> Overlay(double zoom);
    CursorHint Cursor { get; }

    void DoubleClick(PointerInput e) { }
    void PointerLeave() { }
    /// <summary>Esc / tool switch while a gesture is in progress.</summary>
    void Cancel() { }
}

/// <summary>Creates tool instances for the session.</summary>
public interface IToolFactory
{
    ITool Create(ToolKind kind);
}

/// <summary>Convenience base: stores the session, no-op handlers, optional capabilities used by the session.</summary>
public abstract class ToolBase : ITool
{
    protected ToolBase(ToolKind kind) => Kind = kind;

    public ToolKind Kind { get; }
    protected EditorSession? Session { get; private set; }

    /// <summary>The active session (throws when the tool is not active).</summary>
    protected EditorSession S => Session ?? throw new InvalidOperationException($"{Kind} tool is not active.");

    public virtual CursorHint Cursor => CursorHint.Arrow;

    /// <summary>True when the session should draw interactive selection handles while this tool is active.</summary>
    public virtual bool ShowsObjectHandles => false;

    /// <summary>True when the wait cursor should replace the tool cursor while background work runs.</summary>
    public virtual bool ShowsBusyCursor => false;

    public virtual void Activate(EditorSession s) => Session = s;
    public virtual void Deactivate() => Session = null;
    public virtual void PointerDown(PointerInput e) { }
    public virtual void PointerMove(PointerInput e) { }
    public virtual void PointerUp(PointerInput e) { }
    public virtual void KeyDown(KeyInput e) { }
    public virtual void DoubleClick(PointerInput e) { }
    public virtual void PointerLeave() { }
    public virtual void Cancel() { }
    public virtual IEnumerable<OverlayPrimitive> Overlay(double zoom) => Array.Empty<OverlayPrimitive>();

    /// <summary>Tool options / style changed (panel edit, quick style).</summary>
    public virtual void OnOptionsChanged() { }

    /// <summary>Document changed through the undo stack (execute / undo / redo).</summary>
    public virtual void OnDocumentChanged() { }

    /// <summary>Mouse wheel; return true when consumed.</summary>
    public virtual bool Wheel(PointerInput e, double delta) => false;
}

/// <summary>
/// No-op tool: accepts input and does nothing. Hosts can return it from a custom <see cref="IToolFactory"/> to disable
/// a tool (e.g. by policy); the default factory never uses it.
/// </summary>
public sealed class StubTool : ToolBase
{
    public StubTool(ToolKind kind) : base(kind) { }

    public override CursorHint Cursor => Kind switch
    {
        ToolKind.Select or ToolKind.Move => CursorHint.Arrow,
        ToolKind.Text or ToolKind.Callout => CursorHint.IBeam,
        ToolKind.Pen => CursorHint.Pen,
        ToolKind.Highlighter => CursorHint.Highlighter,
        ToolKind.Eraser => CursorHint.Eraser,
        ToolKind.Fill => CursorHint.Fill,
        ToolKind.MagicWand => CursorHint.MagicWand,
        ToolKind.Stamp => CursorHint.Stamp,
        _ => CursorHint.NotAllowed,
    };
}

/// <summary>Default factory: one real tool controller per <see cref="ToolKind"/>.</summary>
public sealed class DefaultToolFactory : IToolFactory
{
    public ITool Create(ToolKind kind) => kind switch
    {
        ToolKind.Select => new SelectTool(),
        ToolKind.Move => new MoveTool(),
        ToolKind.Favorites => new FavoritesTool(),
        ToolKind.Arrow => new ArrowTool(),
        ToolKind.Line => new LineTool(),
        ToolKind.Shape => new ShapeTool(),
        ToolKind.Callout => new CalloutTool(),
        ToolKind.Text => new TextTool(),
        ToolKind.Stamp => new StampTool(),
        ToolKind.Step => new StepTool(),
        ToolKind.Pen => new PenTool(),
        ToolKind.Highlighter => new HighlighterTool(),
        ToolKind.Fill => new FillTool(),
        ToolKind.Eraser => new EraserTool(),
        ToolKind.Magnify => new MagnifyTool(),
        ToolKind.Blur => new BlurTool(),
        ToolKind.Crop => new CropTool(),
        ToolKind.CutOut => new CutOutTool(),
        ToolKind.Selection => new SelectionTool(),
        ToolKind.MagicWand => new MagicWandTool(),
        ToolKind.Simplify => new SimplifyTool(),
        ToolKind.GrabText => new GrabTextTool(),
        ToolKind.Cursor => new CursorTool(),
        _ => new StubTool(kind),
    };
}
