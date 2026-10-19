using System.Windows.Input;
using Shotwise.Core.Platform;
using Shotwise.Editor;
using CoreModifiers = Shotwise.Core.Platform.ModifierKeys;
using WpfModifiers = System.Windows.Input.ModifierKeys;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Maps WPF input to the editor session's input model.</summary>
internal static class InputMap
{
    public static CoreModifiers ToCore(WpfModifiers m)
    {
        var r = CoreModifiers.None;
        if ((m & WpfModifiers.Alt) != 0) r |= CoreModifiers.Alt;
        if ((m & WpfModifiers.Control) != 0) r |= CoreModifiers.Control;
        if ((m & WpfModifiers.Shift) != 0) r |= CoreModifiers.Shift;
        if ((m & WpfModifiers.Windows) != 0) r |= CoreModifiers.Windows;
        return r;
    }

    public static VirtualKey ToVirtualKey(Key key)
    {
        var vk = KeyInterop.VirtualKeyFromKey(key);
        return Enum.IsDefined(typeof(VirtualKey), (ushort)vk) ? (VirtualKey)(ushort)vk : VirtualKey.None;
    }

    public static PointerButton ToButton(MouseButton b) => b switch
    {
        MouseButton.Left => PointerButton.Left,
        MouseButton.Middle => PointerButton.Middle,
        MouseButton.Right => PointerButton.Right,
        MouseButton.XButton1 => PointerButton.X1,
        MouseButton.XButton2 => PointerButton.X2,
        _ => PointerButton.None,
    };

    public static Cursor ToCursor(CursorHint hint) => hint switch
    {
        CursorHint.Arrow => Cursors.Arrow,
        CursorHint.Crosshair => Cursors.Cross,
        CursorHint.Move => Cursors.SizeAll,
        CursorHint.SizeNS => Cursors.SizeNS,
        CursorHint.SizeWE => Cursors.SizeWE,
        CursorHint.SizeNWSE => Cursors.SizeNWSE,
        CursorHint.SizeNESW => Cursors.SizeNESW,
        CursorHint.Rotate => Cursors.Hand,
        CursorHint.Hand => Cursors.Hand,
        CursorHint.HandGrab => Cursors.ScrollAll,
        CursorHint.IBeam => Cursors.IBeam,
        CursorHint.Pen => Cursors.Pen,
        CursorHint.Highlighter => Cursors.Cross,
        CursorHint.Eraser => Cursors.Cross,
        CursorHint.Fill => Cursors.Cross,
        CursorHint.Eyedropper => Cursors.Cross,
        CursorHint.MagicWand => Cursors.Cross,
        CursorHint.Stamp => Cursors.Cross,
        CursorHint.ZoomIn => Cursors.Cross,
        CursorHint.NotAllowed => Cursors.No,
        CursorHint.Wait => Cursors.Wait,
        _ => Cursors.Arrow,
    };
}
