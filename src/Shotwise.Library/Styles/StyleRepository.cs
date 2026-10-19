using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Styles;

namespace Shotwise.Library.Styles;

/// <summary>
/// Themes (<c>.shottheme</c>) and palettes (<c>.shotpalette</c>). Built-ins come from <c>Content\themes|palettes</c>
/// (falling back to <see cref="BuiltInContent"/>); user items live in <c>%APPDATA%\Shotwise\themes|palettes</c>.
/// A user file with the id of a built-in theme is an <i>override</i> (edited built-in); deleting it or
/// <see cref="RestoreDefaultsAsync"/> reverts to the shipped definition.
/// </summary>
public sealed class StyleRepository : IStyleRepository
{
    public const string ThemeExtension = ".shottheme";
    public const string PaletteExtension = ".shotpalette";
    private const long MaxFileBytes = 16L * 1024 * 1024;

    private readonly string _contentRoot;
    private readonly string _themesDir;
    private readonly string _palettesDir;
    private readonly string _activeFile;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private List<Theme>? _themes;
    private List<Palette>? _palettes;
    private string? _activeId;

    public StyleRepository(IAppPaths paths, ILogger<StyleRepository>? logger = null) : this(paths.Content, paths.AppData, logger) { }

    internal StyleRepository(string contentRoot, string appData, ILogger? logger = null)
    {
        _contentRoot = contentRoot;
        _themesDir = Path.Combine(appData, "themes");
        _palettesDir = Path.Combine(appData, "palettes");
        _activeFile = Path.Combine(appData, "active-theme.json");
        _log = Shotwise.Core.Platform.LogRedaction.Wrap(logger ?? NullLogger.Instance);
    }

    public event EventHandler? Changed;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log.LogWarning(ex, "A style Changed handler threw."); }
    }

    private static string SafeFile(string id) => Regex.Replace(id, @"[^A-Za-z0-9._-]", "_");

    // ------------------------------------------------------------------ loading

    private T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), ShotwiseJson.Compact);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Skipping unreadable style file '{Path}'.", path);
            return null;
        }
    }

    private static void Normalize(Theme t, bool builtIn)
    {
        t.QuickStyles ??= new();
        t.ThemeColors ??= new();
        if (t.ThemeColors.Count > 8) t.ThemeColors = t.ThemeColors.Take(8).ToList();
        if (string.IsNullOrWhiteSpace(t.Id)) t.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(t.FontFamily)) t.FontFamily = "Segoe UI";
        t.Format = Theme.FileFormat;
        t.IsBuiltIn = builtIn;
        foreach (var q in t.QuickStyles)
        {
            if (string.IsNullOrWhiteSpace(q.Id)) q.Id = Guid.NewGuid().ToString("N");
            q.IsBuiltIn = builtIn;
        }
    }

    private List<Theme> LoadBuiltInThemes()
    {
        var list = new List<Theme>();
        var dir = Path.Combine(_contentRoot, "themes");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*" + ThemeExtension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (ReadJson<Theme>(f) is { } t && (t.Format is null or "" or Theme.FileFormat))
                {
                    Normalize(t, true);
                    if (list.All(x => x.Id != t.Id)) list.Add(t);
                }
        foreach (var t in BuiltInContent.CreateThemes())
            if (list.All(x => x.Id != t.Id)) list.Add(t);
        var order = BuiltInContent.CreateThemes().Select(t => t.Id).ToList();
        return list.OrderBy(t => order.IndexOf(t.Id) is var i and >= 0 ? i : int.MaxValue).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_themes is not null) return;
            LoadAll();
        }
    }

    private void LoadAll()
    {
        var builtIns = LoadBuiltInThemes();
        var result = builtIns.ToList();
        if (Directory.Exists(_themesDir))
            foreach (var f in Directory.EnumerateFiles(_themesDir, "*" + ThemeExtension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (ReadJson<Theme>(f) is not { } t) continue;
                var idx = result.FindIndex(x => x.Id == t.Id && x.IsBuiltIn);
                if (idx >= 0) { Normalize(t, true); result[idx] = t; }
                else if (result.All(x => x.Id != t.Id)) { Normalize(t, false); result.Add(t); }
            }
        _themes = result;

        var pal = new List<Palette>();
        var pdir = Path.Combine(_contentRoot, "palettes");
        if (Directory.Exists(pdir))
            foreach (var f in Directory.EnumerateFiles(pdir, "*" + PaletteExtension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (ReadJson<Palette>(f) is { } p)
                {
                    p.IsBuiltIn = true; p.Colors ??= new();
                    if (pal.All(x => x.Id != p.Id)) pal.Add(p);
                }
        foreach (var p in BuiltInContent.CreatePalettes())
            if (pal.All(x => x.Id != p.Id)) pal.Add(p);
        if (Directory.Exists(_palettesDir))
            foreach (var f in Directory.EnumerateFiles(_palettesDir, "*" + PaletteExtension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (ReadJson<Palette>(f) is { } p && pal.All(x => x.Id != p.Id))
                {
                    p.IsBuiltIn = false; p.Colors ??= new();
                    pal.Add(p);
                }
        _palettes = pal;

        _activeId = ReadActiveId();
        if (_activeId is null || _themes.All(t => t.Id != _activeId)) _activeId = BuiltInContent.BasicId;
    }

    private string? ReadActiveId()
    {
        try
        {
            if (!File.Exists(_activeFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_activeFile));
            return doc.RootElement.TryGetProperty("themeId", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    // ------------------------------------------------------------------ IStyleRepository

    public Task<IReadOnlyList<Theme>> GetThemesAsync()
    {
        EnsureLoaded();
        lock (_lock) return Task.FromResult<IReadOnlyList<Theme>>(_themes!.ToList());
    }

    public Theme ActiveTheme
    {
        get
        {
            EnsureLoaded();
            lock (_lock) return _themes!.FirstOrDefault(t => t.Id == _activeId) ?? _themes!.First();
        }
    }

    public async Task SetActiveThemeAsync(string themeId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_themes!.All(t => t.Id != themeId)) throw new ShotwiseException(ErrorCode.InvalidArgument, "Unknown theme.") { Detail = themeId };
            _activeId = themeId;
        }
        await AtomicFile.WriteAllTextAsync(_activeFile, JsonSerializer.Serialize(new { themeId }, ShotwiseJson.Indented)).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task SaveThemeAsync(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        EnsureLoaded();
        var copy = JsonSerializer.Deserialize<Theme>(JsonSerializer.Serialize(theme, ShotwiseJson.Compact), ShotwiseJson.Compact)!;
        bool builtInId;
        lock (_lock) builtInId = _themes!.Any(t => t.Id == copy.Id && t.IsBuiltIn);
        if (string.IsNullOrWhiteSpace(copy.Name)) copy.Name = "Untitled theme";
        Normalize(copy, builtInId);
        await AtomicFile.WriteAllTextAsync(Path.Combine(_themesDir, SafeFile(copy.Id) + ThemeExtension), JsonSerializer.Serialize(copy, ShotwiseJson.Indented)).ConfigureAwait(false);
        lock (_lock)
        {
            var i = _themes!.FindIndex(t => t.Id == copy.Id);
            if (i >= 0) _themes[i] = copy; else _themes.Add(copy);
        }
        RaiseChanged();
    }

    public async Task DeleteThemeAsync(string id)
    {
        EnsureLoaded();
        Theme? existing;
        lock (_lock) existing = _themes!.FirstOrDefault(t => t.Id == id);
        if (existing is null) return;
        var file = Path.Combine(_themesDir, SafeFile(id) + ThemeExtension);
        if (existing.IsBuiltIn && !File.Exists(file))
            throw new ShotwiseException(ErrorCode.InvalidArgument, "Built-in themes cannot be deleted.") { Detail = id };
        await Task.Run(() => { if (File.Exists(file)) File.Delete(file); }).ConfigureAwait(false);
        lock (_lock) LoadAll();
        RaiseChanged();
    }

    public async Task<Theme> ImportAsync(string path)
    {
        EnsureLoaded();
        Theme? t;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new ShotwiseException(ErrorCode.FileNotFound, "Theme file not found.") { Detail = path };
            if (fi.Length > MaxFileBytes) throw new ShotwiseException(ErrorCode.FileTooLarge, "Theme file is too large.") { Detail = path };
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            t = JsonSerializer.Deserialize<Theme>(bytes, ShotwiseJson.Compact);
        }
        catch (JsonException ex) { throw new ShotwiseException(ErrorCode.ThemeImportFailed, "The theme file is not valid.", ex) { Detail = path }; }
        catch (IOException ex) { throw new ShotwiseException(ErrorCode.ThemeImportFailed, "The theme file could not be read.", ex) { Detail = path }; }
        if (t is null || !(string.IsNullOrEmpty(t.Format) || t.Format == Theme.FileFormat))
            throw new ShotwiseException(ErrorCode.ThemeImportFailed, "Not a Shotwise theme file.") { Detail = path };
        if (t.Version > 1) throw new ShotwiseException(ErrorCode.NewerFormatVersion, "The theme was created by a newer version.") { Detail = path };
        if (string.IsNullOrWhiteSpace(t.Name)) t.Name = Path.GetFileNameWithoutExtension(path);
        lock (_lock)
        {
            // Never overwrite an existing theme (built-in or user) by import.
            if (string.IsNullOrWhiteSpace(t.Id) || _themes!.Any(x => x.Id == t.Id)) t.Id = Guid.NewGuid().ToString("N");
        }
        t.IsDefault = false;
        Normalize(t, false);
        await SaveThemeAsync(t).ConfigureAwait(false);
        return t;
    }

    public async Task ExportAsync(Theme theme, string path)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var copy = JsonSerializer.Deserialize<Theme>(JsonSerializer.Serialize(theme, ShotwiseJson.Compact), ShotwiseJson.Compact)!;
        copy.IsBuiltIn = false;
        copy.IsDefault = false;
        foreach (var q in copy.QuickStyles) q.IsBuiltIn = false;
        copy.Format = Theme.FileFormat;
        await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(copy, ShotwiseJson.Indented)).ConfigureAwait(false);
    }

    public async Task RestoreDefaultsAsync()
    {
        EnsureLoaded();
        await Task.Run(() =>
        {
            if (!Directory.Exists(_themesDir)) return;
            List<string> builtInIds;
            lock (_lock) builtInIds = _themes!.Where(t => t.IsBuiltIn).Select(t => t.Id).ToList();
            foreach (var id in builtInIds)
            {
                var f = Path.Combine(_themesDir, SafeFile(id) + ThemeExtension);
                if (File.Exists(f)) File.Delete(f);
            }
        }).ConfigureAwait(false);
        lock (_lock) LoadAll();
        RaiseChanged();
    }

    public IReadOnlyList<Palette> Palettes
    {
        get
        {
            EnsureLoaded();
            lock (_lock) return _palettes!.ToList();
        }
    }

    public async Task SavePaletteAsync(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        EnsureLoaded();
        var copy = JsonSerializer.Deserialize<Palette>(JsonSerializer.Serialize(palette, ShotwiseJson.Compact), ShotwiseJson.Compact)!;
        copy.Colors ??= new();
        lock (_lock)
        {
            // Built-in palettes are immutable: saving "over" one creates a user copy.
            if (string.IsNullOrWhiteSpace(copy.Id) || _palettes!.Any(p => p.Id == copy.Id && p.IsBuiltIn)) copy.Id = Guid.NewGuid().ToString("N");
        }
        copy.IsBuiltIn = false;
        if (string.IsNullOrWhiteSpace(copy.Name)) copy.Name = "Untitled palette";
        await AtomicFile.WriteAllTextAsync(Path.Combine(_palettesDir, SafeFile(copy.Id) + PaletteExtension), JsonSerializer.Serialize(copy, ShotwiseJson.Indented)).ConfigureAwait(false);
        lock (_lock)
        {
            var i = _palettes!.FindIndex(p => p.Id == copy.Id);
            if (i >= 0) _palettes[i] = copy; else _palettes.Add(copy);
        }
        RaiseChanged();
    }

    public async Task DeletePaletteAsync(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var p = _palettes!.FirstOrDefault(x => x.Id == id);
            if (p is null) return;
            if (p.IsBuiltIn) throw new ShotwiseException(ErrorCode.InvalidArgument, "Built-in palettes cannot be deleted.") { Detail = id };
        }
        var file = Path.Combine(_palettesDir, SafeFile(id) + PaletteExtension);
        await Task.Run(() => { if (File.Exists(file)) File.Delete(file); }).ConfigureAwait(false);
        lock (_lock) _palettes!.RemoveAll(p => p.Id == id);
        RaiseChanged();
    }
}
