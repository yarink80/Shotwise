using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;

namespace Shotwise.Editor;

public enum PointerButton { None, Left, Middle, Right, X1, X2 }

/// <summary>Pointer event in canvas coordinates (already un-zoomed / un-scrolled by the view).</summary>
public readonly record struct PointerInput(Point2 Position, PointerButton Button, ModifierKeys Modifiers, int ClickCount, double Pressure = 1)
{
    public bool Shift => (Modifiers & ModifierKeys.Shift) != 0;
    public bool Control => (Modifiers & ModifierKeys.Control) != 0;
    public bool Alt => (Modifiers & ModifierKeys.Alt) != 0;
    /// <summary>Current view zoom (1 = 100%) so tools can compute screen-constant tolerances. 0 = unknown (treated as 1).</summary>
    public double Zoom { get; init; } = 1;
    public double EffectiveZoom => Zoom > 0 ? Zoom : 1;
}

/// <summary>Key event routed to the session/tool. Set <see cref="Handled"/> to stop the view from processing it further.</summary>
public sealed class KeyInput
{
    public KeyInput(VirtualKey key, ModifierKeys modifiers, bool isRepeat = false)
    {
        Key = key;
        Modifiers = modifiers;
        IsRepeat = isRepeat;
    }

    public VirtualKey Key { get; }
    public ModifierKeys Modifiers { get; }
    public bool IsRepeat { get; }
    public bool Handled { get; set; }

    public bool Shift => (Modifiers & ModifierKeys.Shift) != 0;
    public bool Control => (Modifiers & ModifierKeys.Control) != 0;
    public bool Alt => (Modifiers & ModifierKeys.Alt) != 0;

    public override string ToString() => new Hotkey(Key, Modifiers).ToDisplayString();
}

/// <summary>Cursor the view should show; mapped to WPF cursors (or custom cursors) by UI.Editor.</summary>
public enum CursorHint
{
    Arrow, Crosshair, Move, SizeNS, SizeWE, SizeNWSE, SizeNESW, Rotate, Hand, HandGrab, IBeam,
    Pen, Highlighter, Eraser, Fill, Eyedropper, MagicWand, Stamp, ZoomIn, NotAllowed, Wait,
}

public enum AlignKind { Left, Center, Right, Top, Middle, Bottom }
