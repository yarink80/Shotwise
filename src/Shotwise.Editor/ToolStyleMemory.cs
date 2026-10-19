using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Serialization;
using Shotwise.Core.Styles;

namespace Shotwise.Editor;

/// <summary>
/// "Remember last style per tool": the quick style each tool used last, shared by every editor session of the app
/// (singleton) and optionally persisted to disk so it survives restarts.
/// </summary>
public interface IToolStyleMemory
{
    QuickStyle? Get(ToolKind tool);
    void Set(ToolKind tool, QuickStyle style);
    void Clear();
    event EventHandler<ToolKind>? Changed;
}

/// <summary>
/// Thread-safe <see cref="IToolStyleMemory"/>. With a file path the dictionary is loaded on construction and written
/// atomically (JSON, <see cref="ShotwiseJson"/>) shortly after changes (debounced so slider drags do not hit the disk
/// on every tick); <see cref="Flush"/> / <see cref="Dispose"/> write pending changes immediately. I/O failures are
/// logged and ignored.
/// </summary>
public sealed class ToolStyleMemory : IToolStyleMemory, IDisposable
{
    public const string FileName = "editor-tool-styles.json";

    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Dictionary<ToolKind, QuickStyle> _styles = new();
    private readonly string? _filePath;
    private readonly ILogger _log;
    private Timer? _timer;
    private bool _pending;
    private bool _disposed;

    public ToolStyleMemory(string? filePath = null, ILogger<ToolStyleMemory>? logger = null)
    {
        _filePath = filePath;
        _log = logger ?? (ILogger)NullLogger.Instance;
        Load();
    }

    /// <summary>Delay between the last change and the disk write.</summary>
    public TimeSpan SaveDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    public event EventHandler<ToolKind>? Changed;

    public QuickStyle? Get(ToolKind tool)
    {
        lock (_gate) return _styles.TryGetValue(tool, out var s) ? s.Clone() : null;
    }

    public void Set(ToolKind tool, QuickStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        lock (_gate) _styles[tool] = style.Clone();
        ScheduleSave();
        Changed?.Invoke(this, tool);
    }

    public void Clear()
    {
        lock (_gate) _styles.Clear();
        ScheduleSave();
        Flush();
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<ToolKind, QuickStyle>>(File.ReadAllText(_filePath), ShotwiseJson.Compact);
            if (dict is null) return;
            lock (_gate)
                foreach (var (k, v) in dict)
                    if (v is not null) _styles[k] = v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _log.LogWarning(ex, "Could not load remembered tool styles from {Path}", _filePath);
        }
    }

    private void ScheduleSave()
    {
        if (_filePath is null) return;
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes pending changes now (no-op when nothing changed or no file is configured).</summary>
    public void Flush()
    {
        if (_filePath is null) return;
        lock (_writeGate)
        {
            string json;
            lock (_gate)
            {
                if (!_pending) return;
                _pending = false;
                json = JsonSerializer.Serialize(_styles, ShotwiseJson.Indented);
            }
            try
            {
                AtomicFile.WriteAllText(_filePath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log.LogWarning(ex, "Could not persist remembered tool styles to {Path}", _filePath);
            }
        }
    }

    public void Dispose()
    {
        Timer? t;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            t = _timer;
            _timer = null;
        }
        t?.Dispose();
        Flush();
    }
}
