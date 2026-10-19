using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace Shotwise.UI.Common.Controls;

/// <summary>
/// Base class for modal dialogs (code-only so it can be subclassed from XAML in other assemblies):
/// Fluent window chrome, centre-on-owner, Esc = cancel, Enter on default button, <see cref="ShowDialogAsync"/>.
/// </summary>
public class DialogWindow : FluentWindow
{
    public DialogWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        // Dialogs keep the native caption (drag/close) and a solid theme background. WPF-UI 4 throws
        // "Cannot apply backdrop effect if ExtendsContentIntoTitleBar is false" for Mica without a custom title bar,
        // which made every dialog fail to open in the integrated app (P3 P0 fix).
        ExtendsContentIntoTitleBar = false;
        WindowBackdropType = WindowBackdropType.None;
        MinWidth = 320;
        UseLayoutRounding = true;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Set to false for dialogs that must not close on Esc (e.g. running operations).</summary>
    public bool CloseOnEscape { get; set; } = true;

    /// <summary>Closes with DialogResult = true.</summary>
    protected void Accept() => CloseWith(true);

    /// <summary>Closes with DialogResult = false.</summary>
    protected void CancelDialog() => CloseWith(false);

    private void CloseWith(bool result)
    {
        try
        {
            DialogResult = result; // only valid when shown with ShowDialog
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && CloseOnEscape)
        {
            e.Handled = true;
            CancelDialog();
        }
    }

    /// <summary>Attaches an owner given as Window, HWND (IntPtr) or null (current active window).</summary>
    public void SetOwner(object? owner)
    {
        switch (owner)
        {
            case Window w when !ReferenceEquals(w, this) && w.IsLoaded:
                Owner = w;
                break;
            case IntPtr hwnd when hwnd != IntPtr.Zero:
                new WindowInteropHelper(this).Owner = hwnd;
                break;
            default:
                var active = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive && !ReferenceEquals(x, this));
                if (active is not null) Owner = active;
                else WindowStartupLocation = WindowStartupLocation.CenterScreen;
                break;
        }
    }

    /// <summary>Shows modally after yielding to the dispatcher (so callers can await from event handlers).</summary>
    public async Task<bool?> ShowDialogAsync(object? owner = null)
    {
        SetOwner(owner);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        return ShowDialog();
    }
}
