using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Imaging;
using Shotwise.Core.Settings;

namespace Shotwise.Sharing.Naming;

public enum BatchOverwriteMode { Rename, Overwrite, Skip }

/// <summary>Batch convert/export request (SHR-01): files → effects → format → automatic naming.</summary>
public sealed record BatchExportOptions(
    IReadOnlyList<string> Files,
    string OutputFolder,
    ImageFormat Format,
    EncodeOptions? Encode = null,
    IReadOnlyList<EffectSpec>? Effects = null,
    /// <summary>Naming pattern; null/empty = keep the original file name. Supports {original} plus all naming tokens.</summary>
    string? NamePattern = null,
    BatchOverwriteMode Overwrite = BatchOverwriteMode.Rename);

public sealed record BatchItemResult(string Source, string? Output, bool Succeeded, bool Skipped, string? Error);

public sealed record BatchExportResult(IReadOnlyList<BatchItemResult> Items)
{
    public int Succeeded => Items.Count(i => i.Succeeded);
    public int Failed => Items.Count(i => !i.Succeeded && !i.Skipped);
    public int Skipped => Items.Count(i => i.Skipped);
}

public interface IBatchExporter
{
    Task<BatchExportResult> ExportAsync(BatchExportOptions options, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Applies the effect chain to every input image and writes it in the requested format with automatic naming.</summary>
public sealed class BatchExporter : IBatchExporter
{
    private readonly IImageCodec _codec;
    private readonly IEffectProcessor? _effects;
    private readonly ISettingsService? _settings;
    private readonly NamingCounter _counter;
    private readonly ILogger _log;

    public BatchExporter(IImageCodec codec, IEffectProcessor? effects = null, ISettingsService? settings = null, ILogger<BatchExporter>? log = null)
    {
        _codec = codec;
        _effects = effects;
        _settings = settings;
        _counter = new NamingCounter(settings);
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public Task<BatchExportResult> ExportAsync(BatchExportOptions o, IProgress<double>? progress, CancellationToken ct)
        => Task.Run(() => Run(o, progress, ct), ct);

    private BatchExportResult Run(BatchExportOptions o, IProgress<double>? progress, CancellationToken ct)
    {
        if (o.Files.Count == 0) return new BatchExportResult(Array.Empty<BatchItemResult>());
        Directory.CreateDirectory(o.OutputFolder);
        var share = _settings?.Current.Share ?? new ShareSettings();
        var omit = share.DoNotIncludeCaptureSourceInNames || (_settings?.Current.Privacy.StripMetadataOnShare ?? false);
        var effects = (o.Effects ?? Array.Empty<EffectSpec>()).Where(e => e.Enabled).ToList();
        var ext = o.Format.DefaultExtension();
        var items = new List<BatchItemResult>();

        for (var i = 0; i < o.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = o.Files[i];
            try
            {
                var original = Path.GetFileNameWithoutExtension(src);
                string baseName;
                if (string.IsNullOrWhiteSpace(o.NamePattern))
                    baseName = NamingEngine.Sanitize(original);
                else
                {
                    var info = new FileInfo(src);
                    var ctx = new NamingContext { Timestamp = info.Exists ? info.LastWriteTime : DateTimeOffset.Now, OriginalName = original, Counter = _counter.Next() };
                    var expanded = NamingEngine.Expand(o.NamePattern, ctx, share.Naming, omit);
                    baseName = NamingEngine.Sanitize(expanded);
                }
                if (baseName.Length == 0) baseName = "Shotwise";

                var dest = NamingEngine.UniquePath(o.OutputFolder, baseName, ext, o.Overwrite == BatchOverwriteMode.Overwrite);
                if (o.Overwrite == BatchOverwriteMode.Skip && File.Exists(Path.Combine(o.OutputFolder, baseName + ext)))
                {
                    items.Add(new BatchItemResult(src, null, false, true, null));
                    continue;
                }
                if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
                    dest = NamingEngine.UniquePath(o.OutputFolder, baseName, ext, overwrite: false); // never clobber the source in place

                var pages = _codec.LoadAllPages(src).ToList();
                if (_effects is not null && effects.Count > 0)
                    pages = pages.Select(p => _effects.ApplyAll(p, effects, null).Image).ToList();

                if (pages.Count > 1 && o.Format is ImageFormat.Pdf or ImageFormat.Tiff)
                    _codec.SaveMultipage(pages, dest, o.Format, o.Encode);
                else
                    _codec.Save(pages[0], dest, o.Format, o.Encode);
                items.Add(new BatchItemResult(src, dest, true, false, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Batch export failed for {File}", src);
                items.Add(new BatchItemResult(src, null, false, false, ex is ShotwiseException ? ex.Message : ex.Message));
            }
            progress?.Report((i + 1) / (double)o.Files.Count);
        }
        return new BatchExportResult(items);
    }
}
