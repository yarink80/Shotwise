using System.Security.Cryptography;
using System.Text;

namespace Shotwise.Sharing.Destinations;

/// <summary>AWS Signature Version 4 (header-based) — self-contained so no AWS SDK is needed for S3-compatible stores.</summary>
public static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public sealed record SignedRequest(string Authorization, string AmzDate, string PayloadHash, string CanonicalRequest, string StringToSign, string SignedHeaders);

    /// <param name="headers">All headers to sign except host / x-amz-date / x-amz-content-sha256 which are added here (names are case-insensitive).</param>
    /// <param name="includeContentSha256Header">S3 requires x-amz-content-sha256 to be a signed header; the generic AWS test suite does not.</param>
    public static SignedRequest Sign(string method, Uri uri, IReadOnlyDictionary<string, string> headers, string payloadHash,
        string accessKey, string secretKey, string region, string service, DateTimeOffset now, string? sessionToken = null,
        bool s3 = false, bool includeContentSha256Header = false)
    {
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var dateStamp = amzDate[..8];

        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in headers) all[kv.Key.ToLowerInvariant()] = CollapseSpaces(kv.Value);
        all["host"] = HostHeader(uri);
        all["x-amz-date"] = amzDate;
        if (includeContentSha256Header) all["x-amz-content-sha256"] = payloadHash;
        if (!string.IsNullOrEmpty(sessionToken)) all["x-amz-security-token"] = sessionToken;

        var signedHeaders = string.Join(';', all.Keys);
        var canonicalHeaders = string.Concat(all.Select(kv => kv.Key + ":" + kv.Value + "\n"));
        var canonicalRequest = string.Join('\n', method.ToUpperInvariant(), CanonicalUri(uri, s3), CanonicalQuery(uri), canonicalHeaders, signedHeaders, payloadHash);

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        var kSigning = Hmac(kService, "aws4_request");
        var signature = Hex(Hmac(kSigning, stringToSign));

        var auth = $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return new SignedRequest(auth, amzDate, payloadHash, canonicalRequest, stringToSign, signedHeaders);
    }

    public static string HashPayload(ReadOnlySpan<byte> data) => Hex(SHA256.HashData(data));

    /// <summary>Query-string presigned URL (signs only the host header; payload UNSIGNED-PAYLOAD).</summary>
    public static Uri Presign(string method, Uri uri, string accessKey, string secretKey, string region, string service,
        DateTimeOffset now, TimeSpan expires, string? sessionToken = null, bool s3 = true)
    {
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var dateStamp = amzDate[..8];
        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var extra = new List<(string, string)>
        {
            ("X-Amz-Algorithm", Algorithm),
            ("X-Amz-Credential", $"{accessKey}/{scope}"),
            ("X-Amz-Date", amzDate),
            ("X-Amz-Expires", ((int)Math.Clamp(expires.TotalSeconds, 1, 604800)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("X-Amz-SignedHeaders", "host"),
        };
        if (!string.IsNullOrEmpty(sessionToken)) extra.Add(("X-Amz-Security-Token", sessionToken));
        var baseQuery = uri.Query.TrimStart('?');
        var q = (baseQuery.Length > 0 ? baseQuery + "&" : "") + string.Join('&', extra.Select(p => Encode(p.Item1) + "=" + Encode(p.Item2)));
        var withQuery = new UriBuilder(uri) { Query = q }.Uri;
        var canonicalRequest = string.Join('\n', method.ToUpperInvariant(), CanonicalUri(withQuery, s3), CanonicalQuery(withQuery),
            "host:" + HostHeader(withQuery) + "\n", "host", "UNSIGNED-PAYLOAD");
        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var kSigning = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp), region), service), "aws4_request");
        var signature = Hex(Hmac(kSigning, stringToSign));
        return new UriBuilder(withQuery) { Query = q + "&X-Amz-Signature=" + signature }.Uri;
    }

    private static string HostHeader(Uri uri) => uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

    internal static string CanonicalUri(Uri uri, bool s3)
    {
        var raw = uri.AbsolutePath;
        if (raw.Length == 0) return "/";
        var segments = raw.Split('/');
        // AbsolutePath is already percent-encoded once. S3 signs it as-is; other services encode every segment a second time.
        for (var i = 0; i < segments.Length; i++)
        {
            var decoded = Uri.UnescapeDataString(segments[i]);
            segments[i] = s3 ? Encode(decoded) : Encode(Encode(decoded));
        }
        var result = string.Join('/', segments);
        return result.Length == 0 ? "/" : result;
    }

    internal static string CanonicalQuery(Uri uri)
    {
        var q = uri.Query;
        if (q.Length <= 1) return "";
        var pairs = q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var eq = p.IndexOf('=');
            var k = eq < 0 ? p : p[..eq];
            var v = eq < 0 ? "" : p[(eq + 1)..];
            return (K: Encode(Uri.UnescapeDataString(k.Replace('+', ' '))), V: Encode(Uri.UnescapeDataString(v.Replace('+', ' '))));
        });
        return string.Join('&', pairs.OrderBy(p => p.K, StringComparer.Ordinal).ThenBy(p => p.V, StringComparer.Ordinal).Select(p => p.K + "=" + p.V));
    }

    /// <summary>RFC 3986 percent-encoding (unreserved characters A-Za-z0-9-_.~ stay).</summary>
    internal static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static string CollapseSpaces(string v)
    {
        var sb = new StringBuilder();
        var space = false;
        foreach (var c in v.Trim())
        {
            if (c is ' ' or '\t') { if (!space) sb.Append(' '); space = true; }
            else { sb.Append(c); space = false; }
        }
        return sb.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
