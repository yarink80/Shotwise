using System.Windows;
using System.Windows.Interop;
using Shotwise.UI.Capture.Infrastructure;

namespace Shotwise.UI.Capture.Coordinator;

/// <summary>
/// Keeps the application's own windows out of captures ("Hide app during capture and remove its controls from completed captures").
/// On Windows 10 2004+ the windows get WDA_EXCLUDEFROMCAPTURE (instant, no flicker, covers recordings too); on older builds they are
/// hidden for the duration. Windows created afterwards by the capture UI exclude themselves.
/// </summary>
internal sealed class OwnWindowShield : IDisposable
{
    private readonly List<(Window Window, IntPtr Hwnd, uint Previous)> _shielded = new();
    private readonly List<Window> _hidden = new();

    private OwnWindowShield() { }

    public bool HidSomething => _hidden.Count > 0;

    public static OwnWindowShield Apply(bool enabled)
    {
        var s = new OwnWindowShield();
        if (!enabled || Application.Current is null) return s;
        bool canExclude = Environment.OSVersion.Version.Build >= 19041;
        foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
        {
            if (!w.IsVisible) continue;
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) continue;
            if (canExclude)
            {
                NativeUi.GetWindowDisplayAffinity(h, out var prev);
                if (prev == NativeUi.WDA_EXCLUDEFROMCAPTURE) continue;
                if (NativeUi.SetWindowDisplayAffinity(h, NativeUi.WDA_EXCLUDEFROMCAPTURE)) { s._shielded.Add((w, h, prev)); continue; }
            }
            w.Hide();
            s._hidden.Add(w);
        }
        return s;
    }

    public void Dispose()
    {
        foreach (var (w, h, prev) in _shielded)
        {
            if (NativeUi.IsWindow(h)) NativeUi.SetWindowDisplayAffinity(h, prev);
        }
        _shielded.Clear();
        foreach (var w in _hidden)
        {
            try { w.ShowActivated = false; w.Show(); } catch (InvalidOperationException) { }
        }
        _hidden.Clear();
    }
}
