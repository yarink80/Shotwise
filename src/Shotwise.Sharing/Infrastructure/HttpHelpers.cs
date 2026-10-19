using System.Net;
using System.Text.Json;
using Shotwise.Core;

namespace Shotwise.Sharing.Infrastructure;

/// <summary>Streams a file as request content and reports upload progress (0..1).</summary>
internal sealed class ProgressFileContent : HttpContent
{
    private readonly string _path;
    private readonly long _offset;
    private readonly long _length;
    private readonly Action<double>? _progress;

    public ProgressFileContent(string path, string contentType, Action<double>? progress, long offset = 0, long? length = null)
    {
        _path = path;
        _offset = offset;
        _length = length ?? new FileInfo(path).Length - offset;
        _progress = progress;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await SerializeAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        await SerializeAsync(stream, ct).ConfigureAwait(false);
    }

    private async Task SerializeAsync(Stream stream, CancellationToken ct)
    {
        await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        fs.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[81920];
        long remaining = _length, sent = 0;
        while (remaining > 0)
        {
            var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct).ConfigureAwait(false);
            if (n == 0) break;
            await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            remaining -= n;
            sent += n;
            _progress?.Invoke(_length == 0 ? 1 : sent / (double)_length);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}

internal static class HttpHelpers
{
    internal static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        const int limit = 4 * 1024 * 1024;
        if (content.Headers.ContentLength > limit) throw new ShotwiseException(ErrorCode.UploadRejected, "Response exceeds 4 MiB.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > limit) throw new ShotwiseException(ErrorCode.UploadRejected, "Response exceeds 4 MiB.");
            bytes.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
    /// <summary>Sends a request built by <paramref name="factory"/>; retries 429/502/503/504 (honouring Retry-After).</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Func<HttpRequestMessage> factory, CancellationToken ct, int maxRetries = 3)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(factory(), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= maxRetries) throw new ShotwiseException(ErrorCode.NetworkError, "Network error: " + ex.Message, ex);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
                continue;
            }
            if (attempt < maxRetries && resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                resp.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }
            return resp;
        }
    }

    /// <summary>Throws a user-presentable exception for non-success responses; otherwise returns the parsed JSON (or an empty object for empty bodies).</summary>
    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using (resp)
        {
            var body = await ReadBoundedAsync(resp.Content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw ToException(resp.StatusCode, body);
            if (string.IsNullOrWhiteSpace(body)) return default;
            try { return JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException ex) { throw new ShotwiseException(ErrorCode.UploadRejected, "The server returned an unexpected response.", ex); }
        }
    }

    public static ShotwiseException ToException(HttpStatusCode status, string body)
    {
        var code = status switch
        {
            HttpStatusCode.Unauthorized => ErrorCode.AuthorizationRequired,
            HttpStatusCode.Forbidden => ErrorCode.AuthenticationFailed,
            HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.InsufficientStorage => ErrorCode.QuotaExceeded,
            HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => ErrorCode.NetworkError,
            _ => ErrorCode.UploadRejected,
        };
        return new ShotwiseException(code, $"The service answered HTTP {(int)status}.");
    }

    internal static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(no details)";
        try
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.String) return e.GetString() + (r.TryGetProperty("error_description", out var ed) ? " - " + ed.GetString() : "");
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString()!;
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("error_summary", out var es)) return es.GetString() ?? "";
                }
                if (r.TryGetProperty("message", out var mm) && mm.ValueKind == JsonValueKind.String) return mm.GetString()!;
                if (r.TryGetProperty("error_summary", out var s2) && s2.ValueKind == JsonValueKind.String) return s2.GetString()!;
            }
        }
        catch (JsonException) { }
        return body.Length <= 200 ? body : body[..200] + "…";
    }

    public static string? Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonElement? Obj(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static HttpRequestMessage Bearer(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var r = new HttpRequestMessage(method, url) { Content = content };
        r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    public static StringContent Json(object o) => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");
}
