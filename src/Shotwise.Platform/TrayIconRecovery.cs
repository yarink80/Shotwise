using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Shotwise.Platform;

/// <summary>
/// Creates the notification-area icon through a factory without ever failing the caller, and retries when Explorer broadcasts
/// <c>TaskbarCreated</c> (Explorer restarted after a crash, or the shell started after Shotwise - e.g. a session without an
/// interactive desktop at launch). The App feeds window messages from a hidden top-level window into <see cref="OnWindowMessage"/>.
/// </summary>
public sealed class TrayIconRecovery
{
    private readonly Func<bool> _create;
    private readonly Func<bool> _isAlive;
    private readonly ILogger _log;
    private static readonly Lazy<uint> Message = new(() => { try { return RegisterWindowMessage("TaskbarCreated"); } catch (Exception) { return 0; } });

    /// <param name="create">Creates (or re-creates) the icon; returns true when it exists afterwards. May throw.</param>
    /// <param name="isAlive">True while the icon currently exists in the notification area.</param>
    public TrayIconRecovery(Func<bool> create, Func<bool> isAlive, ILogger log)
    {
        _create = create;
        _isAlive = isAlive;
        _log = log;
    }

    /// <summary>The registered <c>TaskbarCreated</c> message id (0 when it could not be registered).</summary>
    public static uint TaskbarCreatedMessage => Message.Value;

    /// <summary>True after the last attempt created the icon.</summary>
    public bool IsCreated { get; private set; }

    /// <summary>Number of creation attempts so far (diagnostics/tests).</summary>
    public int Attempts { get; private set; }

    /// <summary>Last creation failure, or null.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Attempts creation; never throws. A failure is logged once as a warning and retried on <c>TaskbarCreated</c>.</summary>
    public bool TryCreate()
    {
        Attempts++;
        try
        {
            IsCreated = _create();
            LastError = null;
            if (!IsCreated) _log.LogWarning("Tray icon not created (attempt {Attempt}); Shotwise keeps running and retries when the taskbar starts", Attempts);
        }
        catch (Exception e)
        {
            IsCreated = false;
            LastError = e;
            _log.LogWarning(e, "Tray icon unavailable (attempt {Attempt}); Shotwise keeps running and retries when the taskbar starts", Attempts);
        }
        return IsCreated;
    }

    /// <summary>
    /// Handles a window message; returns true when it was <c>TaskbarCreated</c>. A missing icon is created again; an existing one
    /// is left to its own re-add logic.
    /// </summary>
    public bool OnWindowMessage(uint message)
    {
        if (message == 0 || message != TaskbarCreatedMessage) return false;
        bool alive;
        try { alive = _isAlive(); } catch (Exception) { alive = false; }
        if (alive) { IsCreated = true; return true; }
        _log.LogInformation("Taskbar (re)started; creating the tray icon again");
        TryCreate();
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);
}
