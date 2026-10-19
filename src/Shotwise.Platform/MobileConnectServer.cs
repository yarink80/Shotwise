using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using QRCoder;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Serialization;
namespace Shotwise.Platform;
public sealed class MobileConnectServer(ISettingsService settings, IPolicyService policy, IAppPaths paths, ILogger<MobileConnectServer> log) : IMobileConnectServer, IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly SemaphoreSlim _uploads = new(2);
    private readonly PairingTokenStore _tokens = new();
    private readonly object _authGate = new();
    private readonly Dictionary<string, (int Failures, DateTimeOffset Until)> _failures = new();
    private WebApplication? _app;
    private X509Certificate2? _certificate;
    private volatile bool _running;
    private int _disposeStarted;
    private IPAddress? _boundAddress;
    private Timer? _policyTimer;
    public string CertificateFingerprint => _certificate?.GetCertHashString(HashAlgorithmName.SHA256) ?? "";
    /// <summary>Select before starting. Null chooses the first active private IPv4 LAN address.</summary>
    public IPAddress? ListenAddress { get; set; }
    internal static bool IsPrivateLan(IPAddress a) { var b = a.GetAddressBytes(); return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168); }
    public bool IsRunning => _running;
    public int Port { get; private set; }
    public event EventHandler<ReceivedFile>? FileReceived;
    private static IPAddress[] LanAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(n => n.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)).Distinct().ToArray();
    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            if (_running) return;
            if (!settings.Current.Mobile.AllowSharing || !policy.IsAllowed(PolicyFeature.MobileSharing)) throw new ShotwiseException(ErrorCode.PolicyDisabled, "Mobile sharing is disabled by preferences or policy.");
            var addresses = LanAddresses().Where(IsPrivateLan).ToArray();
            _boundAddress = ListenAddress ?? addresses.FirstOrDefault() ?? throw new InvalidOperationException("No private IPv4 LAN interface available.");
            if (!addresses.Contains(_boundAddress)) throw new InvalidOperationException("Select an active private IPv4 LAN interface.");
            Port = settings.Current.Mobile.Port;
            _certificate = await Task.Run(() => LoadCertificate(LanAddresses()), ct).ConfigureAwait(false);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(MobileConnectServer).Assembly.FullName });
            // Do not log QR tokens, bearer tokens, or upload names through framework logging.
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false; k.Limits.MaxRequestBodySize = MobileFileRules.MaximumBytes + 1024 * 1024;
                k.Limits.MaxConcurrentConnections = 32; k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                k.Listen(_boundAddress, Port, o => o.UseHttps(_certificate));
            });
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                if (!CheckMobileAllowed()) { context.Response.StatusCode = 403; return; }
                // Same-origin requests only. No CORS; reject foreign browser origins explicitly.
                var origin = context.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && origin != $"https://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
                try { await next(context).ConfigureAwait(false); }
                catch (Exception e) when (e is InvalidDataException or BadHttpRequestException or JsonException)
                { if (!context.Response.HasStarted) { context.Response.StatusCode = e is BadHttpRequestException bad ? bad.StatusCode : 400; } }
            });
            app.MapGet("/", () => Results.Content(Resource("index.html"), "text/html"));
            app.MapGet("/app.js", () => Results.Content(Resource("app.js"), "text/javascript"));
            app.MapGet("/app.css", () => Results.Content(Resource("app.css"), "text/css"));
            app.MapGet("/manifest.webmanifest", () => Results.Content(Resource("manifest.webmanifest"), "application/manifest+json"));
            app.MapGet("/api/v1/info", Info);
            app.MapPost("/api/v1/handshake", (Delegate)Handshake);
            app.MapPost("/api/v1/files", (Delegate)Upload);
            try { await app.StartAsync(ct).ConfigureAwait(false); _app = app; _running = true; }
            catch { await app.DisposeAsync().ConfigureAwait(false); _certificate.Dispose(); _certificate = null; throw; }
            log.LogInformation("Mobile HTTPS listener started on IPv4 port {Port}", Port);
            _policyTimer = new Timer(_ =>
            {
                if (!CheckMobileAllowed())
                {
                    _tokens.Clear();
                    _ = StopAfterPolicyChangeAsync();
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is not OperationCanceledException and not ShotwiseException)
        {
            _certificate?.Dispose(); _certificate = null;
            LogRedaction.Wrap(log).LogError(e, "Mobile listener startup failed");
            throw new ShotwiseException(ErrorCode.MobileServerFailed, "Mobile listener startup failed.", e);
        }
        finally { _lifecycle.Release(); }
    }
    internal bool CheckMobileAllowed()
    {
        if (settings.Current.Mobile.AllowSharing && policy.IsAllowed(PolicyFeature.MobileSharing)) return true;
        _tokens.Clear(); return false;
    }
    internal IResult Info(HttpContext context)
    {
        if (!CheckMobileAllowed()) return Results.StatusCode(403);
        var auth = context.Request.Headers.Authorization.ToString();
        var authorized = auth.StartsWith("Bearer ", StringComparison.Ordinal) && _tokens.Authorize(auth[7..], out _)
            || auth.StartsWith("Pairing ", StringComparison.Ordinal) && _tokens.CanPair(auth[8..]);
        if (!authorized) return Results.Json(new { protocolVersion = 1 }, ShotwiseJson.Compact);
        return Results.Json(new { name = ConnectionName, protocolVersion = 1, maxUploadBytes = MaxUpload, fingerprint = CertificateFingerprint }, ShotwiseJson.Compact);
    }
    private async Task StopAfterPolicyChangeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception e) { log.LogWarning("Mobile policy shutdown failed: {Error}", LogRedaction.Redact(e.ToString())); }
    }
    private string ConnectionName => string.IsNullOrWhiteSpace(settings.Current.Mobile.ConnectionName) ? Environment.MachineName : settings.Current.Mobile.ConnectionName;
    private long MaxUpload => Math.Clamp(settings.Current.Mobile.MaxUploadBytes, 1, MobileFileRules.MaximumBytes);
    private static string Resource(string name)
    {
        using var stream = typeof(MobileConnectServer).Assembly.GetManifestResourceStream("Shotwise.Platform.Pwa." + name) ?? throw new InvalidOperationException("PWA resource missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private bool Blocked(string address)
    {
        lock (_authGate)
        {
            foreach (var pair in _failures.Where(p => p.Value.Until <= DateTimeOffset.UtcNow).ToArray()) _failures.Remove(pair.Key);
            return _failures.TryGetValue(address, out var value) && value.Failures >= 8;
        }
    }
    private void Failed(string address)
    {
        lock (_authGate)
        {
            if (_failures.Count >= 1024 && !_failures.ContainsKey(address)) return;
            _failures.TryGetValue(address, out var value);
            _failures[address] = (value.Failures + 1, DateTimeOffset.UtcNow.AddMinutes(1));
        }
    }
    private sealed record HandshakeRequest(string Token, string? DeviceName);
    private async Task<IResult> Handshake(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (Blocked(ip)) return Results.StatusCode(429);
        if (context.Request.ContentLength > 4096) return Results.StatusCode(413);
        using var buffer = new MemoryStream(); var chunk = new byte[1024]; int n;
        while ((n = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        { if (buffer.Length + n > 4096) return Results.StatusCode(413); buffer.Write(chunk, 0, n); }
        var request = JsonSerializer.Deserialize<HandshakeRequest>(buffer.ToArray(), ShotwiseJson.Compact);
        var token = _tokens.Exchange(request?.Token, request?.DeviceName);
        if (token is null) { Failed(ip); return Results.Unauthorized(); }
        return Results.Json(new { token, expiresInSeconds = 43200, name = ConnectionName }, ShotwiseJson.Compact);
    }
    private async Task<IResult> Upload(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (Blocked(ip)) return Results.StatusCode(429);
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || !_tokens.Authorize(auth[7..], out var device)) { Failed(ip); return Results.Unauthorized(); }
        if (!await _uploads.WaitAsync(0, context.RequestAborted).ConfigureAwait(false)) return Results.StatusCode(429);
        var completed = new List<ReceivedFile>(); var created = new List<string>(); bool delivered = false;
        try
        {
            if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
            var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
            if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) return Results.BadRequest();
            var reader = new MultipartReader(boundary, context.Request.Body) { BodyLengthLimit = MaxUpload, HeadersCountLimit = 16, HeadersLengthLimit = 8192 };
            var folder = Path.Combine(paths.Temp, "Mobile"); Directory.CreateDirectory(folder);
            long total = 0; int sections = 0; MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(context.RequestAborted).ConfigureAwait(false)) is not null)
            {
                if (++sections > 100) return Results.BadRequest();
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
                var original = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
                if (string.IsNullOrWhiteSpace(original)) return Results.BadRequest();
                original = Path.GetFileName(original.Replace('\\', '/'));
                var mediaType = MobileFileRules.MediaType(original); if (mediaType is null) return Results.StatusCode(415);
                var final = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(original).ToLowerInvariant());
                var partial = final + ".partial"; created.Add(partial); long size = 0;
                await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536]; int read;
                    while ((read = await section.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
                    {
                        size += read; total += read; if (total > MaxUpload) return Results.StatusCode(413);
                        await file.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
                    }
                }
                if (size == 0) return Results.BadRequest();
                File.Move(partial, final); created.Add(final);
                completed.Add(new(final, original, mediaType, size, device, DateTimeOffset.UtcNow));
            }
            if (completed.Count == 0) return Results.BadRequest();
            if (!CheckMobileAllowed()) return Results.StatusCode(403);
            context.RequestAborted.ThrowIfCancellationRequested();
            delivered = true;
            foreach (var file in completed)
            {
                try { FileReceived?.Invoke(this, file); } catch (Exception e) { LogRedaction.Wrap(log).LogError(e, "Mobile file consumer failed"); }
            }
            return Results.Json(new { received = completed.Count }, ShotwiseJson.Compact);
        }
        finally
        {
            if (!delivered) foreach (var path in created) { try { File.Delete(path); } catch (IOException e) { LogRedaction.Wrap(log).LogWarning(e, "Partial upload cleanup failed"); } }
            _uploads.Release();
        }
    }
    public PairingInfo CreatePairing(TimeSpan validity)
    {
        if (!_running || !policy.IsAllowed(PolicyFeature.MobileSharing)) throw new ShotwiseException(ErrorCode.MobileServerFailed, "Mobile listener is not running.");
        var address = _boundAddress ?? throw new InvalidOperationException("No IPv4 LAN interface selected.");
        var (token, expires) = _tokens.Create(validity);
        // Fragment keeps credentials out of the HTTP request target and server access logs.
        var url = new Uri($"https://{address}:{Port}/#t={token}&fp={CertificateFingerprint}");
        using var data = QRCodeGenerator.GenerateQrCode(url.AbsoluteUri, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data); return new(url, token, expires, png.GetGraphic(6));
    }
    private static X509Certificate2 LoadCertificate(IPAddress[] addresses)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates)
        {
            if (certificate.FriendlyName == "Shotwise Mobile Connect" && certificate.HasPrivateKey && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7)) return certificate;
            certificate.Dispose();
        }
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=Shotwise Mobile Connect", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(Environment.MachineName); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        foreach (var address in addresses) san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build()); request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var eku = new OidCollection { new("1.3.6.1.5.5.7.3.1") }; request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        var persisted = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet) { FriendlyName = "Shotwise Mobile Connect" };
        store.Add(persisted); return persisted;
    }
    public Task StopAsync() => StopAsync(CancellationToken.None);
    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _running = false;
            _policyTimer?.Dispose(); _policyTimer = null;
            try
            {
                if (_app is not null) { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15)); try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); } finally { await _app.DisposeAsync().ConfigureAwait(false); _app = null; } }
            }
            finally { _tokens.Clear(); lock (_authGate) _failures.Clear(); _certificate?.Dispose(); _certificate = null; }
        }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _uploads.Dispose();
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
