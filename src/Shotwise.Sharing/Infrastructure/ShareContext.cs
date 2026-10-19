using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;

namespace Shotwise.Sharing.Infrastructure;

/// <summary>Per-destination JSON settings in %APPDATA%\Shotwise\share\{id}.json. Never holds secrets.</summary>
public sealed class DestinationSettingsStore
{
    private readonly string _folder;
    private readonly object _gate = new();

    public DestinationSettingsStore(string folder) => _folder = folder;

    public string FolderPath => _folder;

    public string PathFor(string id)
    {
        var safe = string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        return Path.Combine(_folder, safe + ".json");
    }

    // Per-call option overlays (capture preset OutputBinding.Options, CONTRACT A24). AsyncLocal so concurrent shares never see each other's overlay.
    private static readonly AsyncLocal<(string Id, System.Text.Json.JsonElement Options)?> s_overlay = new();

    /// <summary>
    /// While the returned scope is alive (on this async flow), <see cref="Load{T}"/> for <paramref name="id"/> returns the saved
    /// settings with allowlisted presentation properties of <paramref name="options"/> overlaid. Saving is unaffected.
    /// </summary>
    public static IDisposable Overlay(string id, System.Text.Json.JsonElement options)
    {
        var previous = s_overlay.Value;
        s_overlay.Value = options.ValueKind == System.Text.Json.JsonValueKind.Object ? (id, options.Clone()) : null;
        return new OverlayScope(previous);
    }

    private sealed class OverlayScope((string Id, System.Text.Json.JsonElement Options)? previous) : IDisposable
    {
        private bool _done;
        public void Dispose() { if (_done) return; _done = true; s_overlay.Value = previous; }
    }

    public T Load<T>(string id) where T : class, new()
    {
        T value;
        lock (_gate)
        {
            try
            {
                var p = PathFor(id);
                value = File.Exists(p) ? ShotwiseJson.Deserialize<T>(File.ReadAllText(p)) ?? new T() : new T();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                value = new T();
            }
        }
        return s_overlay.Value is { } o && string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase) ? ApplyOverlay(value, o.Options, id) : value;
    }

    /// <summary>Merge only scalar presentation properties explicitly permitted for the destination.</summary>
    internal static T ApplyOverlay<T>(T value, System.Text.Json.JsonElement overlay, string id = "") where T : class, new()
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(ShotwiseJson.Serialize(value)) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            foreach (var prop in overlay.EnumerateObject())
            {
                // Presets may adjust presentation only, never routing, execution or trust.
                var allowed = id.ToLowerInvariant() switch
                {
                    "file" => prop.Name.ToLowerInvariant() is "quality" or "format" or "namemode" or "fixedname",
                    "ftp" => prop.Name.ToLowerInvariant() is "namemode" or "fixedname",
                    _ => false,
                };
                if (!allowed) continue;
                if (prop.Value.ValueKind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array) continue;
                var existing = node.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) node.Remove(existing);
                node[existing ?? prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
            }
            return ShotwiseJson.Deserialize<T>(node.ToJsonString()) ?? value;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            return value; // malformed preset options never break a share; saved settings apply
        }
    }

    public void Save<T>(string id, T value) where T : class
    {
        lock (_gate)
            AtomicFile.WriteAllText(PathFor(id), ShotwiseJson.Serialize(value, indented: true));
    }
}

public sealed class OAuthPortSettings
{
    public Dictionary<string, int> Ports { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Shared services handed to every destination (keeps destination constructors uniform and testable).</summary>
public sealed class ShareContext
{
    private readonly Dictionary<string, string> _clientIdOverrides = new(StringComparer.OrdinalIgnoreCase);

    public ShareContext(IAppPaths paths, ICredentialStore credentials, IOAuthHelper oauth, ISettingsService? settings = null,
        IClipboardWriter? clipboard = null, HttpClient? http = null, ILoggerFactory? loggerFactory = null,
        Core.Imaging.IImageCodec? codec = null)
    {
        Codec = codec;
        Paths = paths;
        Credentials = credentials;
        OAuth = oauth;
        Settings = settings;
        Clipboard = clipboard;
        Http = http ?? SharedHttp.Client;
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        Store = new DestinationSettingsStore(Path.Combine(paths.AppData, "share"));
    }

    public IAppPaths Paths { get; }
    public ICredentialStore Credentials { get; }
    public IOAuthHelper OAuth { get; }
    public ISettingsService? Settings { get; }
    public IClipboardWriter? Clipboard { get; }
    public Core.Imaging.IImageCodec? Codec { get; }
    public HttpClient Http { get; }
    public ILoggerFactory LoggerFactory { get; }
    public DestinationSettingsStore Store { get; }

    public ILogger Log(string category) => LoggerFactory.CreateLogger(category);

    /// <summary>User-supplied OAuth client id for a provider ("microsoft","google","dropbox","box","slack","imgur","x").</summary>
    public string? GetClientId(string provider)
    {
        if (_clientIdOverrides.TryGetValue(provider, out var o) && !string.IsNullOrWhiteSpace(o)) return o;
        var v = Settings?.Current.Share.ClientIds.GetValueOrDefault(provider);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>Optional OAuth client secret (some providers require one even for desktop apps); lives only in the credential store.</summary>
    /// <remarks>Key = <see cref="ShareCredentialKeys.ClientSecret"/> ("Shotwise:{provider}:client-secret"), shared with the Accounts UI.
    /// The pre-integration key "…:client_secret" is still read as a fallback.</remarks>
    public string? GetClientSecret(string provider)
        => Credentials.Read(ShareCredentialKeys.ClientSecret(provider)) ?? Credentials.Read(Auth.CredentialKeys.Make(provider, "client_secret"));

    public void SetClientSecret(string provider, string? secret)
    {
        var key = ShareCredentialKeys.ClientSecret(provider);
        Credentials.Delete(Auth.CredentialKeys.Make(provider, "client_secret"));
        if (string.IsNullOrEmpty(secret)) Credentials.Delete(key);
        else Credentials.Write(key, secret);
    }

    /// <summary>
    /// Fixed loopback redirect port for providers that only accept pre-registered redirect URIs (Dropbox, Box, Imgur, X). Null = random port.
    /// Read from Core <c>Share.LoopbackPorts</c> (written by the Accounts UI); the legacy <c>share\oauth.json</c> is a fallback.
    /// </summary>
    public int? GetLoopbackPort(string provider)
    {
        if (Settings?.Current.Share.LoopbackPorts is { } ports && ports.TryGetValue(provider, out var sp) && sp is > 0 and <= 65535) return sp;
        return Store.Load<OAuthPortSettings>("oauth").Ports.TryGetValue(provider, out var p) && p is > 0 and <= 65535 ? p : null;
    }

    public void SetLoopbackPort(string provider, int? port)
    {
        if (Settings is not null)
            Settings.Update(s => { if (port is > 0 and <= 65535) s.Share.LoopbackPorts[provider] = port.Value; else s.Share.LoopbackPorts.Remove(provider); });
        var legacy = Store.Load<OAuthPortSettings>("oauth");
        if (Settings is null && port is > 0 and <= 65535) legacy.Ports[provider] = port.Value; else legacy.Ports.Remove(provider);
        Store.Save("oauth", legacy);
    }

    /// <summary>Runtime override (tests / options dialogs before settings are saved).</summary>
    public void SetClientId(string provider, string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) _clientIdOverrides.Remove(provider);
        else _clientIdOverrides[provider] = clientId.Trim();
    }
}

internal static class SharedHttp
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var h = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Shotwise/0.1");
        return h;
    }
}

/// <summary>Format-key helpers: ShareFile.Format is an extension; destinations declare keys png/jpg/gif/agif/mp4/pdf/tiff/bmp/webp/shotx.</summary>
public static class FormatKeys
{
    public static readonly IReadOnlySet<string> Raster5 = Set("png", "jpg", "gif", "bmp", "tiff");
    public static readonly IReadOnlySet<string> Images = Set("png", "jpg", "gif", "bmp", "tiff", "webp");
    public static readonly IReadOnlySet<string> ImagesAndPdf = Set("png", "jpg", "gif", "bmp", "tiff", "webp", "pdf");
    public static readonly IReadOnlySet<string> Cloud = Set("png", "jpg", "gif", "agif", "mp4", "bmp", "tiff", "webp", "pdf");
    public static readonly IReadOnlySet<string> Everything = Set("png", "jpg", "gif", "agif", "mp4", "bmp", "tiff", "webp", "pdf", "shotx");

    public static IReadOnlySet<string> Set(params string[] keys) => new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string ext)
    {
        var e = ext.TrimStart('.').ToLowerInvariant();
        return e switch { "jpeg" or "jpe" or "jfif" => "jpg", "tif" => "tiff", "shotwise" => "shotx", _ => e };
    }

    /// <summary>The capability key of a file (animated GIFs are "agif", static ones "gif").</summary>
    public static string Of(ShareFile f)
    {
        var k = Normalize(f.Format);
        if (k == "gif" && f.Kind == Core.Library.MediaKind.Gif) return "agif";
        return k;
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".mp4" => "video/mp4",
        ".shotx" => "application/zip",
        _ => "application/octet-stream",
    };
}
