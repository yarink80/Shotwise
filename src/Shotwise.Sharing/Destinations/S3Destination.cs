using System.Text;
using System.Xml.Linq;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Infrastructure;
using Shotwise.Sharing.Naming;

namespace Shotwise.Sharing.Destinations;

public sealed class S3Settings
{
    /// <summary>Host (s3.amazonaws.com, minio.local:9000, ...) or a full URL including scheme.</summary>
    public string Endpoint { get; set; } = "s3.amazonaws.com";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    /// <summary>true = https://endpoint/bucket/key (MinIO, Ceph...), false = https://bucket.endpoint/key.</summary>
    public bool PathStyle { get; set; }
    public string AccessKeyId { get; set; } = "";
    /// <summary>Optional template for the public link: {key} {name} {bucket} {endpoint}. Blank = pre-signed URL (see PresignHours).</summary>
    public string PublicUrlTemplate { get; set; } = "";
    /// <summary>Validity of the pre-signed link when no template is set (0 = plain object URL).</summary>
    public int PresignHours { get; set; } = 24;
    /// <summary>Canned ACL sent with uploads (e.g. "public-read"); blank = none.</summary>
    public string Acl { get; set; } = "";
    public bool AutoName { get; set; } = true;
}

/// <summary>SHR-12 S3-compatible storage with in-house SigV4 signing; PUT for ≤ 16 MiB, multipart above.</summary>
public sealed class S3Destination : ShareDestinationBase
{
    public const int MultipartThreshold = 16 * 1024 * 1024;
    public const int PartSize = 16 * 1024 * 1024;

    private readonly NamingCounter _counter;
    private readonly HttpClient _http;

    public S3Destination(ShareContext ctx, HttpClient? http = null) : base(ctx)
    {
        _http = http ?? ctx.Http;
        _counter = new NamingCounter(ctx.Settings);
    }

    public override string Id => "s3";
    public override string DisplayName => "S3-compatible storage";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Uploader;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Everything;
    public override bool CanShowOptions => true;
    public override bool RequiresAccount => true;
    public override bool IsConfigured
    {
        get { var s = LoadSettings<S3Settings>(); return s.Bucket.Length > 0 && s.AccessKeyId.Length > 0 && SecretKey is not null; }
    }

    private string? SecretKey => Ctx.Credentials.Read(CredentialKeys.Make("s3", "secret"));

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<S3Settings>();
        var form = new ShareForm { Description = "The secret key is stored in Windows Credential Manager." };
        form.Add(new ShareFormField { Key = "endpoint", Label = "Endpoint", Value = s.Endpoint });
        form.Add(new ShareFormField { Key = "region", Label = "Region", Value = s.Region });
        form.Add(new ShareFormField { Key = "bucket", Label = "Bucket", Value = s.Bucket, Required = true });
        form.Add(new ShareFormField { Key = "prefix", Label = "Key prefix (folder)", Value = s.KeyPrefix, Placeholder = "screenshots/" });
        form.Add(new ShareFormField { Key = "path", Label = "Path-style addressing", Kind = ShareFieldKind.Checkbox, Value = s.PathStyle });
        form.Add(new ShareFormField { Key = "ak", Label = "Access key id", Value = s.AccessKeyId, Required = true });
        form.Add(new ShareFormField { Key = "sk", Label = "Secret access key (blank = keep)", Kind = ShareFieldKind.Password });
        form.Add(new ShareFormField { Key = "url", Label = "Public URL template", Value = s.PublicUrlTemplate, Placeholder = "https://cdn.example.com/{key}" });
        form.Add(new ShareFormField { Key = "presign", Label = "Pre-signed link validity (hours, 0 = plain URL)", Kind = ShareFieldKind.Number, Value = (double)s.PresignHours, Minimum = 0, Maximum = 168 });
        form.Add(new ShareFormField { Key = "acl", Label = "Canned ACL", Value = s.Acl, Placeholder = "public-read" });
        form.Add(new ShareFormField { Key = "auto", Label = "Automatic file names", Kind = ShareFieldKind.Checkbox, Value = s.AutoName });
        if (!await ui.ShowFormAsync("S3-compatible storage", form).ConfigureAwait(false)) return;
        s.Endpoint = (form.Get<string>("endpoint") ?? "").Trim();
        s.Region = (form.Get<string>("region") ?? "us-east-1").Trim();
        s.Bucket = (form.Get<string>("bucket") ?? "").Trim();
        s.KeyPrefix = (form.Get<string>("prefix") ?? "").Trim().TrimStart('/');
        s.PathStyle = form.Get<bool>("path");
        s.AccessKeyId = (form.Get<string>("ak") ?? "").Trim();
        s.PublicUrlTemplate = (form.Get<string>("url") ?? "").Trim();
        s.PresignHours = (int)(form.Get<double?>("presign") ?? 24);
        s.Acl = (form.Get<string>("acl") ?? "").Trim();
        s.AutoName = form.Get<bool>("auto");
        var sk = form.Get<string>("sk");
        if (!string.IsNullOrEmpty(sk)) Ctx.Credentials.Write(CredentialKeys.Make("s3", "secret"), sk);
        SaveSettings(s);
    }

    internal static Uri ObjectUri(S3Settings s, string key)
    {
        var ep = s.Endpoint.Contains("://", StringComparison.Ordinal) ? new Uri(s.Endpoint) : new Uri("https://" + s.Endpoint);
        var encodedKey = string.Join('/', key.Split('/').Select(SigV4.Encode));
        var authority = ep.IsDefaultPort ? ep.Host : $"{ep.Host}:{ep.Port}";
        return s.PathStyle
            ? new Uri($"{ep.Scheme}://{authority}/{SigV4.Encode(s.Bucket)}/{encodedKey}")
            : new Uri($"{ep.Scheme}://{s.Bucket}.{authority}/{encodedKey}");
    }

    internal static string ExpandTemplate(string template, S3Settings s, string key)
    {
        var encodedKey = string.Join('/', key.Split('/').Select(SigV4.Encode));
        return template.Replace("{key}", encodedKey, StringComparison.Ordinal)
            .Replace("{name}", SigV4.Encode(key.Split('/')[^1]), StringComparison.Ordinal)
            .Replace("{bucket}", s.Bucket, StringComparison.Ordinal)
            .Replace("{endpoint}", s.Endpoint, StringComparison.Ordinal);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<S3Settings>();
        var secret = SecretKey;
        if (string.IsNullOrEmpty(s.Bucket) || string.IsNullOrEmpty(s.AccessKeyId) || secret is null)
            throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Configure bucket and keys in the S3 options first.");
        var links = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var name = s.AutoName ? AutoName(f) : NameOf(f);
            var key = (s.KeyPrefix.Length > 0 && !s.KeyPrefix.EndsWith('/') ? s.KeyPrefix + "/" : s.KeyPrefix) + name;
            var uri = ObjectUri(s, key);
            var slice = Slice(progress, i, files.Count);
            if (new FileInfo(f.Path).Length <= MultipartThreshold) await PutAsync(s, secret, uri, f, slice, ct).ConfigureAwait(false);
            else await MultipartAsync(s, secret, uri, f, slice, ct).ConfigureAwait(false);
            links.Add(LinkFor(s, secret, uri, key));
        }
        return new ShareResult(ShareOutcome.Succeeded, "Uploaded to S3.", links[0], links[0], links, OpenLinkAction(links[0]));
    }

    private string AutoName(ShareFile f)
    {
        var share = Ctx.Settings?.Current.Share ?? new Core.Settings.ShareSettings();
        var omit = share.DoNotIncludeCaptureSourceInNames || (Ctx.Settings?.Current.Privacy.StripMetadataOnShare ?? false);
        return NamingEngine.BuildName(new NamingContext { Title = f.Title, Counter = _counter.Next(), Width = f.Width, Height = f.Height }, share.Naming, omit) + Path.GetExtension(f.Path);
    }

    private static string LinkFor(S3Settings s, string secret, Uri uri, string key)
    {
        if (!string.IsNullOrWhiteSpace(s.PublicUrlTemplate)) return ExpandTemplate(s.PublicUrlTemplate, s, key);
        if (s.PresignHours <= 0) return uri.AbsoluteUri;
        return SigV4.Presign("GET", uri, s.AccessKeyId, secret, s.Region, "s3", DateTimeOffset.UtcNow, TimeSpan.FromHours(s.PresignHours)).AbsoluteUri;
    }

    private async Task PutAsync(S3Settings s, string secret, Uri uri, ShareFile f, IProgress<double>? progress, CancellationToken ct)
    {
        var data = await File.ReadAllBytesAsync(f.Path, ct).ConfigureAwait(false);
        var ctype = FormatKeys.ContentType(f.Path);
        var extra = new Dictionary<string, string> { ["content-type"] = ctype };
        if (!string.IsNullOrEmpty(s.Acl)) extra["x-amz-acl"] = s.Acl;
        await SendAsync(s, secret, HttpMethod.Put, uri, extra, data, ctype, ct).ConfigureAwait(false);
        progress?.Report(1);
    }

    private async Task MultipartAsync(S3Settings s, string secret, Uri uri, ShareFile f, IProgress<double>? progress, CancellationToken ct)
    {
        var ctype = FormatKeys.ContentType(f.Path);
        var init = new Dictionary<string, string> { ["content-type"] = ctype };
        if (!string.IsNullOrEmpty(s.Acl)) init["x-amz-acl"] = s.Acl;
        var initXml = await SendAsync(s, secret, HttpMethod.Post, WithQuery(uri, "uploads"), init, Array.Empty<byte>(), ctype, ct).ConfigureAwait(false);
        var uploadId = XDocument.Parse(initXml.Body).Descendants().FirstOrDefault(e => e.Name.LocalName == "UploadId")?.Value
                       ?? throw new ShotwiseException(ErrorCode.UploadRejected, "The store returned no multipart upload id.");
        try
        {
            var length = new FileInfo(f.Path).Length;
            var etags = new List<(int, string)>();
            await using var fs = File.OpenRead(f.Path);
            var part = 1;
            long done = 0;
            while (done < length)
            {
                var size = (int)Math.Min(PartSize, length - done);
                var buf = new byte[size];
                await fs.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
                var r = await SendAsync(s, secret, HttpMethod.Put, WithQuery(uri, $"partNumber={part}&uploadId={SigV4.Encode(uploadId)}"), new Dictionary<string, string>(), buf, null, ct).ConfigureAwait(false);
                etags.Add((part, r.ETag ?? throw new ShotwiseException(ErrorCode.UploadRejected, "A part upload returned no ETag.")));
                done += size;
                part++;
                progress?.Report(done / (double)length);
            }
            var xml = new StringBuilder("<CompleteMultipartUpload>");
            foreach (var (n, tag) in etags) xml.Append("<Part><PartNumber>").Append(n).Append("</PartNumber><ETag>").Append(System.Security.SecurityElement.Escape(tag)).Append("</ETag></Part>");
            xml.Append("</CompleteMultipartUpload>");
            var complete = await SendAsync(s, secret, HttpMethod.Post, WithQuery(uri, $"uploadId={SigV4.Encode(uploadId)}"),
                new Dictionary<string, string> { ["content-type"] = "application/xml" }, Encoding.UTF8.GetBytes(xml.ToString()), "application/xml", ct).ConfigureAwait(false);
            if (complete.Body.Contains("<Error>", StringComparison.Ordinal)) throw new ShotwiseException(ErrorCode.UploadRejected, "The store failed to assemble the object: " + complete.Body);
        }
        catch
        {
            try { await SendAsync(s, secret, HttpMethod.Delete, WithQuery(uri, $"uploadId={SigV4.Encode(uploadId)}"), new Dictionary<string, string>(), Array.Empty<byte>(), null, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* best-effort abort */ }
            throw;
        }
    }

    private static Uri WithQuery(Uri uri, string query) => new UriBuilder(uri) { Query = query }.Uri;

    private sealed record S3Response(string Body, string? ETag);

    private async Task<S3Response> SendAsync(S3Settings s, string secret, HttpMethod method, Uri uri, Dictionary<string, string> headers, byte[] body, string? contentType, CancellationToken ct)
    {
        var payloadHash = SigV4.HashPayload(body);
        var signed = SigV4.Sign(method.Method, uri, headers, payloadHash, s.AccessKeyId, secret, s.Region, "s3", DateTimeOffset.UtcNow, s3: true, includeContentSha256Header: true);
        using var resp = await HttpHelpers.SendAsync(_http, () =>
        {
            var req = new HttpRequestMessage(method, uri);
            if (body.Length > 0 || method == HttpMethod.Put || method == HttpMethod.Post)
            {
                req.Content = new ByteArrayContent(body);
                if (contentType is not null) req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            foreach (var h in headers) if (!h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            req.Headers.TryAddWithoutValidation("x-amz-date", signed.AmzDate);
            req.Headers.TryAddWithoutValidation("x-amz-content-sha256", signed.PayloadHash);
            req.Headers.TryAddWithoutValidation("Authorization", signed.Authorization);
            return req;
        }, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw HttpHelpers.ToException(resp.StatusCode, ExtractS3Error(text));
        return new S3Response(text, resp.Headers.ETag?.Tag);
    }

    private static string ExtractS3Error(string xml)
    {
        try
        {
            var d = XDocument.Parse(xml);
            var code = d.Descendants().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value;
            var msg = d.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
            return $"{code}: {msg}";
        }
        catch (System.Xml.XmlException) { return xml.Length > 200 ? xml[..200] : xml; }
    }
}
