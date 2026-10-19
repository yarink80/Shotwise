using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Settings;

/// <summary>
/// JSON-file settings store (%APPDATA%\Shotwise\settings.json). Loads synchronously on construction (missing or
/// corrupt files fall back to defaults; a corrupt file is preserved as settings.corrupt-{timestamp}.json).
/// Every <see cref="Update"/> is written atomically. Thread-safe; <see cref="Changed"/> is raised on the caller's thread.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly object _gate = new();
    private readonly ILogger<SettingsService> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private AppSettings _current;

    public SettingsService(IAppPaths paths, ILogger<SettingsService>? logger = null)
        : this(paths.SettingsFile(), logger) { }

    public SettingsService(string filePath, ILogger<SettingsService>? logger = null)
    {
        FilePath = filePath;
        _log = logger ?? NullLogger<SettingsService>.Instance;
        _current = Load();
    }

    public string FilePath { get; }

    public AppSettings Current { get { lock (_gate) return _current; } }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        HashSet<string> changed;
        AppSettings snapshot;
        // Lock order everywhere: _writeLock, then _gate.
        _writeLock.Wait();
        try
        {
            string json;
            lock (_gate)
            {
                var before = SectionJson(_current);
                mutate(_current);
                _current.Normalize();
                var after = SectionJson(_current);
                changed = AppSettings.SectionNames.Where(n => before[n] != after[n]).ToHashSet();
                json = JsonSerializer.Serialize(_current, ShotwiseJson.Indented);
                snapshot = _current;
            }
            if (changed.Count == 0) return;
            try { AtomicFile.WriteAllText(FilePath, json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogError(ex, "Failed to write settings to {Path}", FilePath);
            }
        }
        finally { _writeLock.Release(); }
        Changed?.Invoke(this, new SettingsChangedEventArgs(snapshot, changed));
    }

    public async Task SaveAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_current, ShotwiseJson.Indented);
            await AtomicFile.WriteAllTextAsync(FilePath, json).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task ResetAsync()
    {
        AppSettings fresh;
        lock (_gate)
        {
            fresh = new AppSettings();
            fresh.Normalize();
            _current = fresh;
        }
        await SaveAsync().ConfigureAwait(false);
        Changed?.Invoke(this, new SettingsChangedEventArgs(fresh, AppSettings.SectionNames.ToHashSet()));
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Defaults();
            var json = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize<AppSettings>(json, ShotwiseJson.Indented) ?? Defaults();
            s.Normalize();
            return s;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Settings file {Path} is corrupt; using defaults", FilePath);
            try
            {
                var backup = Path.Combine(Path.GetDirectoryName(FilePath)!, $"settings.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(copyEx, "Could not back up corrupt settings file");
            }
            return Defaults();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Cannot read settings file {Path}; using defaults", FilePath);
            return Defaults();
        }
    }

    private static AppSettings Defaults()
    {
        var s = new AppSettings();
        s.Normalize();
        return s;
    }

    private static Dictionary<string, string> SectionJson(AppSettings s)
        => AppSettings.SectionNames.ToDictionary(n => n, n => JsonSerializer.Serialize(s.GetSection(n), s.GetSection(n).GetType(), ShotwiseJson.Compact));
}
