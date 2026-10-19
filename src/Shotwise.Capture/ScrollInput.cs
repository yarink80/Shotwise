using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shotwise.Capture;

internal static class ScrollInput
{
    internal static unsafe void Wheel(Shotwise.Core.Geometry.RectI area, bool horizontal)
    {
        PInvoke.GetCursorPos(out var previous);
        int x = area.X + area.Width / 2, y = area.Y + area.Height / 2;
        PInvoke.SetCursorPos(x, y);
        try
        {
            var input = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
            input.Anonymous.mi.mouseData = unchecked((uint)(horizontal ? 480 : -480));
            input.Anonymous.mi.dwFlags = horizontal ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_HWHEEL : MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL;
            if (PInvoke.SendInput(1, &input, sizeof(INPUT)) != 1) throw new System.ComponentModel.Win32Exception("Scroll input was blocked.");
        }
        finally
        {
            if (PInvoke.GetCursorPos(out var now) && now.X == x && now.Y == y) PInvoke.SetCursorPos(previous.X, previous.Y);
        }
    }
    internal static unsafe void PageDown()
    {
        var inputs = stackalloc INPUT[2];
        inputs[0] = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD }; inputs[0].Anonymous.ki.wVk = VIRTUAL_KEY.VK_NEXT;
        inputs[1] = inputs[0]; inputs[1].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        if (PInvoke.SendInput(2, inputs, sizeof(INPUT)) != 2) throw new System.ComponentModel.Win32Exception("Page input was blocked.");
    }
}
