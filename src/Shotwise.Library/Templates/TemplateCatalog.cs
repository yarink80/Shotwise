using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Documents;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Styles;

namespace Shotwise.Library.Templates;

/// <summary>
/// Built-in (<c>Content\templates\*.shottemplate</c>) and user (<c>%APPDATA%\Shotwise\templates</c>) templates.
/// A user file with the id of a built-in template shadows it; deleting that file reverts to the shipped version.
/// </summary>
public sealed class TemplateCatalog : ITemplateCatalog
{
    public const string Extension = ".shottemplate";
    private const long MaxFileBytes = 64L * 1024 * 1024;

    private readonly string _builtInDir;
    private readonly string _userDir;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private List<TemplateDefinition>? _cache;

    public TemplateCatalog(IAppPaths paths, ILogger<TemplateCatalog>? logger = null) : this(Path.Combine(paths.Content, "templates"), Path.Combine(paths.AppData, "templates"), logger) { }

    internal TemplateCatalog(string builtInDir, string userDir, ILogger? logger = null)
    {
        _builtInDir = builtInDir;
        _userDir = userDir;
        _log = Shotwise.Core.Platform.LogRedaction.Wrap(logger ?? NullLogger.Instance);
    }

    public event EventHandler? Changed;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log.LogWarning(ex, "A template Changed handler threw."); }
    }

    private static string SafeFile(string id) => Regex.Replace(id, @"[^A-Za-z0-9._-]", "_");

    private TemplateDefinition? TryRead(string path, bool builtIn)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) return null;
            var t = JsonSerializer.Deserialize<TemplateDefinition>(File.ReadAllBytes(path), ShotwiseJson.Compact);
            if (t is null || !(string.IsNullOrEmpty(t.Format) || t.Format == TemplateDefinition.FileFormat) || t.Version > 1) return null;
            Normalize(t, builtIn);
            return t;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Skipping unreadable template '{Path}'.", path);
            return null;
        }
    }

    private static void Normalize(TemplateDefinition t, bool builtIn)
    {
        t.Format = TemplateDefinition.FileFormat;
        t.LocalizedNames ??= new();
        t.Keywords ??= new();
        t.Assets ??= new();
        t.Prototype ??= new Page();
        if (string.IsNullOrWhiteSpace(t.Id)) t.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(t.Name)) t.Name = "Untitled template";
        t.IsBuiltIn = builtIn;
    }

    private List<TemplateDefinition> Load()
    {
        var list = new List<TemplateDefinition>();
        if (Directory.Exists(_builtInDir))
            foreach (var f in Directory.EnumerateFiles(_builtInDir, "*" + Extension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (TryRead(f, true) is { } t && list.All(x => x.Id != t.Id)) list.Add(t);
        if (Directory.Exists(_userDir))
            foreach (var f in Directory.EnumerateFiles(_userDir, "*" + Extension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (TryRead(f, false) is { } t)
                {
                    var i = list.FindIndex(x => x.Id == t.Id);
                    if (i >= 0) list[i] = t; else list.Add(t);
                }
        return list;
    }

    public Task<IReadOnlyList<TemplateDefinition>> GetTemplatesAsync()
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<TemplateDefinition>>((_cache ??= Load()).ToList());
    }

    public async Task ImportAsync(string path)
    {
        TemplateDefinition? t;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new ShotwiseException(ErrorCode.FileNotFound, "Template file not found.") { Detail = path };
            if (fi.Length > MaxFileBytes) throw new ShotwiseException(ErrorCode.FileTooLarge, "Template file is too large.") { Detail = path };
            t = JsonSerializer.Deserialize<TemplateDefinition>(await File.ReadAllBytesAsync(path).ConfigureAwait(false), ShotwiseJson.Compact);
        }
        catch (JsonException ex) { throw new ShotwiseException(ErrorCode.TemplateImportFailed, "The template file is not valid.", ex) { Detail = path }; }
        catch (IOException ex) { throw new ShotwiseException(ErrorCode.TemplateImportFailed, "The template file could not be read.", ex) { Detail = path }; }
        if (t is null || !(string.IsNullOrEmpty(t.Format) || t.Format == TemplateDefinition.FileFormat))
            throw new ShotwiseException(ErrorCode.TemplateImportFailed, "Not a Shotwise template file.") { Detail = path };
        if (t.Version > 1) throw new ShotwiseException(ErrorCode.TemplateIncompatible, "The template was created by a newer version.") { Detail = path };
        foreach (var a in t.Assets ?? new())
            if (a.Data is null || a.Data.Length > 32 * 1024 * 1024)
                throw new ShotwiseException(ErrorCode.TemplateImportFailed, "The template contains an invalid asset.") { Detail = path };
        Normalize(t, false);
        // Importing never replaces an existing template: duplicates get a fresh id.
        var existing = await GetTemplatesAsync().ConfigureAwait(false);
        if (existing.Any(x => x.Id == t.Id)) t.Id = Guid.NewGuid().ToString("N");
        await WriteUserAsync(t).ConfigureAwait(false);
    }

    private async Task WriteUserAsync(TemplateDefinition t)
    {
        await AtomicFile.WriteAllTextAsync(Path.Combine(_userDir, SafeFile(t.Id) + Extension), JsonSerializer.Serialize(t, ShotwiseJson.Indented)).ConfigureAwait(false);
        lock (_lock) _cache = null;
        RaiseChanged();
    }

    /// <summary>Saves (creates or overwrites) a user template, e.g. after "Save as template" in the editor.</summary>
    public Task SaveAsync(TemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var copy = JsonSerializer.Deserialize<TemplateDefinition>(JsonSerializer.Serialize(template, ShotwiseJson.Compact), ShotwiseJson.Compact)!;
        Normalize(copy, false);
        return WriteUserAsync(copy);
    }

    public async Task ExportAsync(TemplateDefinition template, string path)
    {
        ArgumentNullException.ThrowIfNull(template);
        var copy = JsonSerializer.Deserialize<TemplateDefinition>(JsonSerializer.Serialize(template, ShotwiseJson.Compact), ShotwiseJson.Compact)!;
        copy.IsBuiltIn = false;
        copy.Format = TemplateDefinition.FileFormat;
        await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(copy, ShotwiseJson.Indented)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string templateId)
    {
        var all = await GetTemplatesAsync().ConfigureAwait(false);
        var t = all.FirstOrDefault(x => x.Id == templateId);
        if (t is null) return;
        var file = Path.Combine(_userDir, SafeFile(templateId) + Extension);
        if (t.IsBuiltIn) throw new ShotwiseException(ErrorCode.InvalidArgument, "Built-in templates cannot be deleted.") { Detail = templateId };
        await Task.Run(() => { if (File.Exists(file)) File.Delete(file); }).ConfigureAwait(false);
        lock (_lock) _cache = null;
        RaiseChanged();
    }
}
