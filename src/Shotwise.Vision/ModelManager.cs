using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>Well-known model ids of the embedded manifest.</summary>
public static class ModelIds
{
    public const string YuNet = "yunet";
    public const string Lama = "lama";
    /// <summary>Optional, never downloaded by default: a user-supplied streaming noise-suppression ONNX (see <see cref="NoiseSuppressor"/>).</summary>
    public const string NoiseSuppressor = "noise-suppressor";
}

/// <summary>
/// Manifest-driven model store (VIS-09). Models are only ever downloaded by an explicit <see cref="EnsureAsync"/> call;
/// every download is size- and SHA-256-verified before it becomes visible. Files placed manually under the models folder
/// as <c>&lt;id&gt;.onnx</c> are honoured for ids that are not in the manifest (side-loading).
/// </summary>
public sealed class ModelManager : IModelManager
{
    private const string ResourceName = "Shotwise.Vision.models.manifest.json";
    private static readonly HttpClient SharedHttp = CreateHttp();

    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly ILogger<ModelManager> _log;
    private readonly Dictionary<string, ModelInfo> _byId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModelManager(IAppPaths paths, ILogger<ModelManager> log)
        : this(paths.Models, LoadEmbeddedManifest(), SharedHttp, log) { }

    internal ModelManager(string modelsDir, IReadOnlyList<ModelInfo> models, HttpClient http, ILogger<ModelManager> log)
    {
        _dir = modelsDir;
        _http = http;
        _log = log;
        Models = models;
        _byId = models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ModelInfo> Models { get; }

    /// <summary>Local path of the model file (it may not exist).</summary>
    public string GetPath(string modelId) => Path.Combine(_dir, SanitizeId(modelId) + ".onnx");

    public bool IsAvailable(string modelId)
    {
        try
        {
            var path = GetPath(modelId);
            if (!File.Exists(path)) return false;
            if (!_byId.TryGetValue(modelId, out var info)) return new FileInfo(path).Length > 0; // side-loaded
            return new FileInfo(path).Length == info.SizeBytes && IsMarkedVerified(path, info);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IsAvailable({Id}) failed", modelId);
            return false;
        }
    }

    public async Task<string> EnsureAsync(string modelId, IProgress<double>? p, CancellationToken ct)
    {
        var path = GetPath(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_byId.TryGetValue(modelId, out var info))
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0) { p?.Report(1); return path; }
                throw new ShotwiseException(ErrorCode.ModelUnavailable,$"Unknown model '{modelId}' and no side-loaded file at {path}.");
            }

            Directory.CreateDirectory(_dir);
            if (File.Exists(path) && new FileInfo(path).Length == info.SizeBytes)
            {
                if (IsMarkedVerified(path, info)) { p?.Report(1); return path; }
                var existing = await HashFileAsync(path, ct).ConfigureAwait(false);
                if (string.Equals(existing, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    MarkVerified(path, info);
                    p?.Report(1);
                    return path;
                }
                _log.LogWarning("Model {Id} on disk fails SHA-256 verification; re-downloading", modelId);
            }

            await DownloadAsync(info, path, p, ct).ConfigureAwait(false);
            return path;
        }
        finally { _gate.Release(); }
    }

    private async Task DownloadAsync(ModelInfo info, string finalPath, IProgress<double>? p, CancellationToken ct)
    {
        var tmp = finalPath + ".partial";
        try
        {
            _log.LogInformation("Downloading model {Id} from {Url}", info.Id, info.Url);
            using var resp = await _http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.SizeBytes;
            using var sha = SHA256.Create();
            long read = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) p?.Report(Math.Min(0.99, (double)read / total));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }
            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (read != info.SizeBytes || !string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ShotwiseException(ErrorCode.ModelChecksumMismatch,
                    $"Downloaded model '{info.Id}' failed verification (size {read}/{info.SizeBytes}, sha256 {hash}).");
            File.Move(tmp, finalPath, overwrite: true);
            MarkVerified(finalPath, info);
            p?.Report(1);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var h = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    // A small marker next to the model remembers a successful verification (sha|length|mtime) so IsAvailable stays cheap.
    private static string MarkerPath(string modelPath) => modelPath + ".verified";

    private static string MarkerValue(string modelPath, ModelInfo info)
    {
        var fi = new FileInfo(modelPath);
        return $"{info.Sha256}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
    }

    private static bool IsMarkedVerified(string modelPath, ModelInfo info)
    {
        try { var m = MarkerPath(modelPath); return File.Exists(m) && File.ReadAllText(m) == MarkerValue(modelPath, info); }
        catch { return false; }
    }

    private static void MarkVerified(string modelPath, ModelInfo info)
    {
        try { File.WriteAllText(MarkerPath(modelPath), MarkerValue(modelPath, info)); } catch { /* cache only */ }
    }

    private static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
            throw new ArgumentException("Invalid model id.", nameof(id));
        return id;
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Shotwise-ModelManager/1.0");
        return c;
    }

    internal static IReadOnlyList<ModelInfo> LoadEmbeddedManifest()
    {
        using var s = typeof(ModelManager).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded model manifest missing.");
        return ParseManifest(s);
    }

    internal static IReadOnlyList<ModelInfo> ParseManifest(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ModelInfo>();
        foreach (var e in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            Uri? lic = e.TryGetProperty("licenseUrl", out var lu) && lu.ValueKind == JsonValueKind.String ? new Uri(lu.GetString()!) : null;
            list.Add(new ModelInfo(
                e.GetProperty("id").GetString()!,
                e.GetProperty("name").GetString()!,
                e.GetProperty("purpose").GetString()!,
                new Uri(e.GetProperty("url").GetString()!),
                e.GetProperty("sha256").GetString()!,
                e.GetProperty("sizeBytes").GetInt64(),
                e.GetProperty("license").GetString()!,
                lic));
        }
        return list;
    }
}
