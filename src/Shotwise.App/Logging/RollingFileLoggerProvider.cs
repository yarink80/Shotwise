using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Shotwise.App.Logging;

/// <summary>
/// Minimal rolling file logger: one file per day (shotwise-yyyyMMdd.log, rolled at <see cref="MaxFileBytes"/>),
/// keeps the newest <c>maxFiles</c> files. Writes on a background thread; never throws into callers.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private readonly string _folder;
    private readonly int _maxFiles;
    private readonly LogLevel _minLevel;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Thread _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public RollingFileLoggerProvider(string folder, int maxFiles = 10, LogLevel minLevel = LogLevel.Information)
    {
        _folder = folder;
        _maxFiles = Math.Max(1, maxFiles);
        _minLevel = minLevel;
        Directory.CreateDirectory(folder);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Shotwise log writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(this, n));

    internal bool IsEnabled(LogLevel level) => level >= _minLevel && level != LogLevel.None;

    internal void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        string? currentPath = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var path = CurrentPath();
                    if (path != currentPath || writer is null || writer.BaseStream.Length > MaxFileBytes)
                    {
                        writer?.Dispose();
                        if (writer is not null && path == currentPath) path = RollPath(path);
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                        currentPath = path;
                        Cleanup();
                    }
                    writer.WriteLine(line);
                }
                catch (IOException)
                {
                    // disk issues must never take the app down
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private string CurrentPath() => Path.Combine(_folder, $"shotwise-{DateTime.Now:yyyyMMdd}.log");

    private static string RollPath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{stem}.{i}.log");
            if (!File.Exists(p) || new FileInfo(p).Length < MaxFileBytes) return p;
        }
    }

    private void Cleanup()
    {
        try
        {
            var files = new DirectoryInfo(_folder).GetFiles("shotwise-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(_maxFiles);
            foreach (var f in files) f.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly RollingFileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(RollingFileLoggerProvider owner, string category)
        {
            _owner = owner;
            var dot = category.LastIndexOf('.');
            _category = dot >= 0 ? category[(dot + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder(160);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(' ').Append(Level(logLevel)).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ");
            // R2-P2: the log file is the one sink every component writes to, so secrets (tokens, Authorization headers,
            // query secrets, e-mail addresses) are redacted here with the shared Core helper.
            sb.Append(_category).Append(": ").Append(Shotwise.Core.Platform.LogRedaction.Redact(formatter(state, exception)));
            if (exception is not null) sb.AppendLine().Append(Shotwise.Core.Platform.LogRedaction.Redact(exception.ToString()));
            _owner.Enqueue(sb.ToString());
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
