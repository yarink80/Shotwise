using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Capture;

/// <summary>On-disk format of presets.json and exported <c>.shotpresets</c> files.</summary>
public sealed class PresetFile
{
    public const string FileFormat = "shotpresets";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FileFormat;
    public int Version { get; set; } = CurrentVersion;
    public string? ActivePresetId { get; set; }
    /// <summary>Built-in preset hotkey defaults already applied to this file (<see cref="PresetDefaults.HotkeyDefaultsVersion"/>; 0 = before POLISH-1).</summary>
    public int HotkeyDefaultsVersion { get; set; }
    public List<CapturePreset> Presets { get; set; } = new();
    public List<PresetGroup> Groups { get; set; } = new();
}

/// <summary>
/// JSON-file preset repository (%APPDATA%\Shotwise\presets.json). Seeds the built-in presets on first run.
/// Thread-safe. All mutations are persisted atomically and raise <see cref="Changed"/>.
/// </summary>
public sealed class PresetRepository : IPresetRepository
{
    private readonly object _gate = new();
    private readonly ILogger<PresetRepository> _log;
    private readonly string _path;
    private List<CapturePreset> _presets = new();
    private List<PresetGroup> _groups = new();
    private string? _activeId;
    private CapturePreset? _lastUsed;

    public PresetRepository(IAppPaths paths, ILogger<PresetRepository>? logger = null) : this(paths.PresetsFile(), logger) { }

    public PresetRepository(string filePath, ILogger<PresetRepository>? logger = null)
    {
        _path = filePath;
        _log = logger ?? NullLogger<PresetRepository>.Instance;
        Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CapturePreset> All { get { lock (_gate) return _presets.OrderBy(p => p.Order).ToList(); } }
    public IReadOnlyList<PresetGroup> Groups { get { lock (_gate) return _groups.OrderBy(g => g.Order).ToList(); } }

    public CapturePreset Active
    {
        get
        {
            lock (_gate)
                return _presets.FirstOrDefault(p => p.Id == _activeId) ?? _presets.OrderBy(p => p.Order).FirstOrDefault() ?? PresetDefaults.CreateBuiltIns()[0];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                if (_presets.All(p => p.Id != value.Id)) throw new ShotwiseException(ErrorCode.InvalidArgument, $"Preset '{value.Id}' is not in the repository.");
                _activeId = value.Id;
                PersistLocked();
            }
            RaiseChanged();
        }
    }

    public CapturePreset? LastUsed { get { lock (_gate) return _lastUsed; } }

    public void MarkUsed(CapturePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        lock (_gate) _lastUsed = preset.Clone();
    }

    public CapturePreset? Find(string id) { lock (_gate) return _presets.FirstOrDefault(p => p.Id == id); }

    public Task SaveAsync(CapturePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Id)) preset.Id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            var copy = preset.Clone();
            var i = _presets.FindIndex(p => p.Id == copy.Id);
            // Remember an explicit "no hotkey" so default-hotkey migrations never re-add it; assigning a key resets the flag.
            if (copy.Hotkey is { IsEmpty: false }) copy.HotkeyCleared = false;
            else if (i >= 0 && _presets[i].Hotkey is { IsEmpty: false }) copy.HotkeyCleared = true;
            if (i >= 0) _presets[i] = copy;
            else
            {
                copy.Order = _presets.Count == 0 ? 0 : _presets.Max(p => p.Order) + 1;
                _presets.Add(copy);
            }
            PersistLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        lock (_gate)
        {
            if (_presets.RemoveAll(p => p.Id == id) == 0) return Task.CompletedTask;
            if (_activeId == id) _activeId = _presets.OrderBy(p => p.Order).FirstOrDefault()?.Id;
            PersistLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task ReorderAsync(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            var order = 0;
            var byId = _presets.ToDictionary(p => p.Id);
            foreach (var id in ids)
                if (byId.Remove(id, out var p)) p.Order = order++;
            foreach (var p in byId.Values.OrderBy(p => p.Order)) p.Order = order++;
            PersistLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task SaveGroupAsync(PresetGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        lock (_gate)
        {
            var i = _groups.FindIndex(g => g.Id == group.Id);
            if (i >= 0) _groups[i] = group; else _groups.Add(group);
            PersistLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task DeleteGroupAsync(string groupId, bool deletePresets = false)
    {
        lock (_gate)
        {
            _groups.RemoveAll(g => g.Id == groupId);
            if (deletePresets) _presets.RemoveAll(p => p.GroupId == groupId);
            else foreach (var p in _presets.Where(p => p.GroupId == groupId)) p.GroupId = null;
            if (_presets.All(p => p.Id != _activeId)) _activeId = _presets.OrderBy(p => p.Order).FirstOrDefault()?.Id;
            PersistLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public async Task ImportAsync(string path)
    {
        PresetFile file;
        try
        {
            await using var fs = File.OpenRead(path);
            if (fs.Length > 16 * 1024 * 1024) throw new ShotwiseException(ErrorCode.FileTooLarge, "Preset file is too large.");
            file = await JsonSerializer.DeserializeAsync<PresetFile>(fs, ShotwiseJson.Indented).ConfigureAwait(false)
                   ?? throw new ShotwiseException(ErrorCode.PresetImportFailed, "Preset file is empty.");
        }
        catch (JsonException ex)
        {
            throw new ShotwiseException(ErrorCode.PresetImportFailed, "Preset file is not valid JSON.", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new ShotwiseException(ErrorCode.FileNotFound, $"File not found: {path}", ex);
        }
        if (!string.Equals(file.Format, PresetFile.FileFormat, StringComparison.OrdinalIgnoreCase))
            throw new ShotwiseException(ErrorCode.PresetImportFailed, "Not a preset file.");
        if (file.Version > PresetFile.CurrentVersion)
            throw new ShotwiseException(ErrorCode.NewerFormatVersion, $"Preset file version {file.Version} is newer than supported.");

        lock (_gate)
        {
            var groupMap = new Dictionary<string, string>();
            foreach (var g in file.Groups ?? new())
            {
                var existing = _groups.FirstOrDefault(x => string.Equals(x.Name, g.Name, StringComparison.CurrentCultureIgnoreCase));
                if (existing is not null) { groupMap[g.Id] = existing.Id; continue; }
                var ng = g with { Id = _groups.Any(x => x.Id == g.Id) ? Guid.NewGuid().ToString("N") : g.Id, Order = _groups.Count };
                groupMap[g.Id] = ng.Id;
                _groups.Add(ng);
            }
            var nextOrder = _presets.Count == 0 ? 0 : _presets.Max(p => p.Order) + 1;
            var usedHotkeys = _presets.Where(p => p.Hotkey is { IsEmpty: false }).Select(p => p.Hotkey!.Value).ToHashSet();
            foreach (var p in file.Presets ?? new())
            {
                if (p is null) continue;
                if (string.IsNullOrWhiteSpace(p.Id) || _presets.Any(x => x.Id == p.Id)) p.Id = Guid.NewGuid().ToString("N");
                p.IsBuiltIn = false;
                p.RequiresReview = true;
                p.OpenInProgramId = null;
                p.Outputs = (p.Outputs ?? new()).Select(o => o with { Options = null }).ToList();
                p.Order = nextOrder++;
                p.GroupId = p.GroupId is not null && groupMap.TryGetValue(p.GroupId, out var gid) ? gid : null;
                if (p.Hotkey is { } hk && !usedHotkeys.Add(hk)) p.Hotkey = null;
                p.Effects ??= new();
                p.Outputs ??= new();
                p.SelectionOptions ??= SelectionOptions.Default;
                p.Timing ??= TimingOptions.None;
                p.Recording ??= PresetDefaults.Recording;
                _presets.Add(p);
            }
            PersistLocked();
        }
        RaiseChanged();
    }

    public async Task ExportAsync(IEnumerable<string> ids, string path)
    {
        PresetFile file;
        lock (_gate)
        {
            var set = ids.ToHashSet();
            var presets = _presets.Where(p => set.Contains(p.Id)).OrderBy(p => p.Order).Select(p => p.Clone()).ToList();
            var groupIds = presets.Select(p => p.GroupId).Where(g => g is not null).ToHashSet();
            file = new PresetFile { Presets = presets, Groups = _groups.Where(g => groupIds.Contains(g.Id)).ToList() };
        }
        await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(file, ShotwiseJson.Indented)).ConfigureAwait(false);
    }

    public Task ResetDefaultsAsync()
    {
        lock (_gate)
        {
            foreach (var b in PresetDefaults.CreateBuiltIns())
            {
                // A user preset that took a default key keeps it; the built-in then ships without one.
                if (b.Hotkey is { } dk && _presets.Any(p => p.Id != b.Id && p.Hotkey == dk)) b.Hotkey = null;
                var i = _presets.FindIndex(p => p.Id == b.Id);
                if (i >= 0)
                {
                    b.Order = _presets[i].Order;
                    _presets[i] = b;
                }
                else
                {
                    b.Order = _presets.Count == 0 ? 0 : _presets.Max(p => p.Order) + 1;
                    _presets.Add(b);
                }
            }
            if (_presets.All(p => p.Id != _activeId)) _activeId = PresetDefaults.AllInOneId;
            PersistLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(_path), ShotwiseJson.Indented);
                    if (file is not null && file.Version <= PresetFile.CurrentVersion)
                    {
                        _presets = (file.Presets ?? new()).Where(p => p is not null).ToList();
                        _groups = file.Groups ?? new();
                        _activeId = file.ActivePresetId;
                        foreach (var p in _presets)
                        {
                            p.Effects ??= new();
                            p.Outputs ??= new();
                            p.SelectionOptions ??= SelectionOptions.Default;
                            p.Timing ??= TimingOptions.None;
                            p.Recording ??= PresetDefaults.Recording;
                        }
                        if (file.HotkeyDefaultsVersion < PresetDefaults.HotkeyDefaultsVersion)
                        {
                            var applied = ApplyDefaultHotkeys(_presets);
                            _log.LogInformation("Preset hotkey defaults v{Version} applied to {Count} built-in preset(s)", PresetDefaults.HotkeyDefaultsVersion, applied);
                            PersistLocked();
                        }
                        return;
                    }
                    _log.LogWarning("Preset file {Path} has an unsupported version; using defaults (file kept)", _path);
                    File.Copy(_path, _path + ".unsupported", overwrite: true);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log.LogWarning(ex, "Preset file {Path} could not be read; restoring built-in presets", _path);
                try { if (File.Exists(_path)) File.Copy(_path, _path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException) { _log.LogDebug(copyEx, "Backup failed"); }
            }
            _presets = PresetDefaults.CreateBuiltIns().ToList();
            _groups = new();
            _activeId = PresetDefaults.AllInOneId;
            PersistLocked();
        }
    }

    /// <summary>
    /// Gives built-in presets their default hotkey (<see cref="PresetDefaults.DefaultHotkey"/>) when they have none, the user did not
    /// clear it on purpose (<see cref="CapturePreset.HotkeyCleared"/>) and no other preset uses the key. Returns the number changed.
    /// </summary>
    public static int ApplyDefaultHotkeys(IList<CapturePreset> presets)
    {
        int applied = 0;
        foreach (var p in presets)
        {
            if (!p.IsBuiltIn || p.Hotkey is { IsEmpty: false } || p.HotkeyCleared) continue;
            if (PresetDefaults.DefaultHotkey(p.Id) is not { } key) continue;
            if (presets.Any(o => !ReferenceEquals(o, p) && o.Hotkey == key)) continue;
            p.Hotkey = key;
            applied++;
        }
        return applied;
    }

    private void PersistLocked()
    {
        var file = new PresetFile { ActivePresetId = _activeId, HotkeyDefaultsVersion = PresetDefaults.HotkeyDefaultsVersion, Presets = _presets.OrderBy(p => p.Order).ToList(), Groups = _groups };
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, ShotwiseJson.Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Failed to write presets to {Path}", _path);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
