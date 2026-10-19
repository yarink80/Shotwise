using System.Windows;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Documents;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor;

/// <summary>
/// <see cref="IEditorHost"/> implementation: owns the (single) editor window, creates it on demand on the UI thread and
/// forwards open/show requests. When "keep editor running in background" is on the window is hidden instead of closed, so
/// documents survive; otherwise closing the window closes every document and the next request builds a fresh window.
/// </summary>
public sealed class EditorHost : IEditorHost
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EditorHost> _log;
    private EditorContext? _context;
    private EditorWindow? _window;

    public EditorHost(IServiceProvider services, ILogger<EditorHost>? logger = null)
    {
        _services = services;
        _log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EditorHost>.Instance;
    }

    public bool IsVisible
    {
        get
        {
            var d = Application.Current?.Dispatcher;
            if (d is null) return false;
            return d.CheckAccess() ? _window is { IsVisible: true } : d.Invoke(() => _window is { IsVisible: true });
        }
    }

    public Task OpenAsync(ShotDocument doc, bool activate = true)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return OnUiAsync(async () =>
        {
            var w = EnsureWindow();
            if (activate) w.ShowAndActivate(); else if (!w.IsVisible) w.Show();
            await w.OpenDocumentAsync(doc, activate);
        });
    }

    public Task OpenFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return OnUiAsync(async () =>
        {
            var w = EnsureWindow();
            w.ShowAndActivate();
            await w.OpenFilesAsync(new[] { path });
        });
    }

    public Task OpenVideoAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return OnUiAsync(async () =>
        {
            var w = EnsureWindow();
            w.ShowAndActivate();
            try { await w.OpenVideoAsync(path); }
            catch (Exception ex) when (ex is ShotwiseException or IOException)
            {
                _log.LogWarning(ex, "Opening video {Path} failed", path);
                await w.ReportAsync(ex);
            }
        });
    }

    public void ShowEditor()
    {
        _ = OnUiAsync(() =>
        {
            var w = EnsureWindow();
            w.ShowAndActivate();
            return Task.CompletedTask;
        });
    }

    public void ShowLibrary()
    {
        _ = OnUiAsync(() =>
        {
            var w = EnsureWindow();
            w.ShowAndActivate();
            w.ShowLibraryView();
            return Task.CompletedTask;
        });
    }

    public Task<bool> CloseForExitAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(true);
        return dispatcher.InvokeAsync(async () =>
        {
            if (_window is not { } w) return true;
            try { return await w.CloseForRealAsync(); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Closing the editor before an update restart failed");
                return false;
            }
        }).Task.Unwrap();
    }

    private EditorWindow EnsureWindow()
    {
        if (_window is { } existing) return existing;
        _context ??= new EditorContext(_services);
        var w = new EditorWindow(_context);
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, w)) _window = null;
        };
        _window = w;
        return w;
    }

    private Task OnUiAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("The editor requires a running WPF application.");
        if (dispatcher.CheckAccess()) return RunSafe(action);
        return dispatcher.InvokeAsync(() => RunSafe(action)).Task.Unwrap();
    }

    private async Task RunSafe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Editor host operation failed");
            if (_window is { } w) await w.ReportAsync(ex);
            else throw;
        }
    }
}
