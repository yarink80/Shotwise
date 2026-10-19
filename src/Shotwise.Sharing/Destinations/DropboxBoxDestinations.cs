using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations;

public sealed class DropboxSettings
{
    public string Folder { get; set; } = "/Shotwise";
    public bool PromptEachTime { get; set; }
}

/// <summary>SHR-10 Dropbox: OAuth2 + PKCE (user's app key, no secret), upload sessions for large files, shared link.</summary>
public sealed class DropboxDestination : OAuthDestinationBase
{
    public const int SingleUploadLimit = 8 * 1024 * 1024;
    public const int ChunkSize = 8 * 1024 * 1024;

    public DropboxDestination(ShareContext ctx, HttpClient? http = null) : base(ctx, http) { }

    public override string Id => "dropbox";
    public override string DisplayName => "Dropbox";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Storage;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Cloud;
    public override bool CanShowOptions => true;
    protected override string ProviderId => "dropbox";

    protected override OAuthClientConfig? BuildConfig(string clientId, string? clientSecret) => new(
        Id, new Uri("https://www.dropbox.com/oauth2/authorize"), new Uri("https://api.dropboxapi.com/oauth2/token"), clientId,
        new[] { "files.content.write", "files.metadata.read", "sharing.write" }, clientSecret,
        new Dictionary<string, string> { ["token_access_type"] = "offline" });

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        var s = LoadSettings<DropboxSettings>();
        if (!s.PromptEachTime) return true;
        var picked = await PickAsync(ui, ct).ConfigureAwait(false);
        if (picked is null) return false;
        s.Folder = picked;
        SaveSettings(s);
        return true;
    }

    private Task<string?> PickAsync(IShareUi ui, CancellationToken ct) => ui.PickFolderAsync("Dropbox folder", async parent =>
    {
        var r = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder", t,
            HttpHelpers.Json(new { path = parent ?? "", recursive = false, limit = 500 })), ct).ConfigureAwait(false);
        return (IReadOnlyList<RemoteFolder>)r.Items("entries").Where(e => e.Str(".tag") == "folder")
            .Select(e => new RemoteFolder(e.Str("path_display")!, e.Str("name")!, parent, true)).OrderBy(f => f.Name).ToList();
    });

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<DropboxSettings>();
        var form = new ShareForm();
        form.Add(new ShareFormField { Key = "folder", Label = "Folder (e.g. /Shotwise)", Value = s.Folder });
        form.Add(new ShareFormField { Key = "prompt", Label = "Choose the folder every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptEachTime });
        if (!await ui.ShowFormAsync("Dropbox options", form).ConfigureAwait(false)) return;
        s.Folder = NormalizeFolder(form.Get<string>("folder"));
        s.PromptEachTime = form.Get<bool>("prompt");
        SaveSettings(s);
    }

    internal static string NormalizeFolder(string? f)
    {
        var t = (f ?? "").Trim().Replace('\\', '/').Trim('/');
        return t.Length == 0 ? "" : "/" + t;
    }

    /// <summary>Dropbox-API-Arg must be ASCII: escape every non-ASCII character as \uXXXX.</summary>
    internal static string ApiArg(object o)
    {
        var json = JsonSerializer.Serialize(o);
        var sb = new StringBuilder(json.Length);
        foreach (var c in json) { if (c > 126) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); }
        return sb.ToString();
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<DropboxSettings>();
        var folder = NormalizeFolder(s.Folder);
        var paths = new List<string>();
        for (var i = 0; i < files.Count; i++)
            paths.Add(await UploadAsync(folder + "/" + NameOf(files[i]), files[i].Path, Slice(progress, i, files.Count), ct).ConfigureAwait(false));
        var link = await SharedLinkAsync(paths[0], ct).ConfigureAwait(false);
        return new ShareResult(ShareOutcome.Succeeded, "Uploaded to Dropbox.", link, link, paths, OpenLinkAction(link));
    }

    private async Task<string> UploadAsync(string remotePath, string local, IProgress<double>? progress, CancellationToken ct)
    {
        var length = new FileInfo(local).Length;
        if (length <= SingleUploadLimit)
        {
            var token = await TokenAsync(ct).ConfigureAwait(false);
            var r = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () =>
            {
                var req = HttpHelpers.Bearer(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload", token,
                    new ProgressFileContent(local, "application/octet-stream", v => progress?.Report(v)));
                req.Headers.Add("Dropbox-API-Arg", ApiArg(new { path = remotePath, mode = "add", autorename = true, mute = true }));
                return req;
            }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return r.Str("path_display") ?? remotePath;
        }

        // Upload session: start (empty) → append_v2 chunks → finish.
        var startToken = await TokenAsync(ct).ConfigureAwait(false);
        var start = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () =>
        {
            var req = HttpHelpers.Bearer(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload_session/start", startToken, new ByteArrayContent(Array.Empty<byte>()));
            req.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Headers.Add("Dropbox-API-Arg", ApiArg(new { close = false }));
            return req;
        }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var sessionId = start.Str("session_id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Dropbox returned no upload session.");

        long offset = 0;
        while (length - offset > ChunkSize)
        {
            var o = offset;
            var token = await TokenAsync(ct).ConfigureAwait(false);
            await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () =>
            {
                var req = HttpHelpers.Bearer(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload_session/append_v2", token,
                    new ProgressFileContent(local, "application/octet-stream", null, o, ChunkSize));
                req.Headers.Add("Dropbox-API-Arg", ApiArg(new { cursor = new { session_id = sessionId, offset = o }, close = false }));
                return req;
            }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            offset += ChunkSize;
            progress?.Report(offset / (double)length);
        }
        var lastOffset = offset;
        var finishToken = await TokenAsync(ct).ConfigureAwait(false);
        var fin = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () =>
        {
            var req = HttpHelpers.Bearer(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload_session/finish", finishToken,
                new ProgressFileContent(local, "application/octet-stream", null, lastOffset, length - lastOffset));
            req.Headers.Add("Dropbox-API-Arg", ApiArg(new
            {
                cursor = new { session_id = sessionId, offset = lastOffset },
                commit = new { path = remotePath, mode = "add", autorename = true, mute = true },
            }));
            return req;
        }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        progress?.Report(1);
        return fin.Str("path_display") ?? remotePath;
    }

    private async Task<string?> SharedLinkAsync(string path, CancellationToken ct)
    {
        var token = await TokenAsync(ct).ConfigureAwait(false);
        using var resp = await HttpHelpers.SendAsync(Http, () => HttpHelpers.Bearer(HttpMethod.Post, "https://api.dropboxapi.com/2/sharing/create_shared_link_with_settings", token,
            HttpHelpers.Json(new { path, settings = new { requested_visibility = "public" } })), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return JsonDocument.Parse(body).RootElement.Str("url");
        if (body.Contains("shared_link_already_exists", StringComparison.Ordinal))
        {
            var list = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, "https://api.dropboxapi.com/2/sharing/list_shared_links", t,
                HttpHelpers.Json(new { path, direct_only = true })), ct).ConfigureAwait(false);
            return list.Items("links").Select(l => l.Str("url")).FirstOrDefault();
        }
        throw HttpHelpers.ToException(resp.StatusCode, body);
    }
}

public sealed class BoxSettings
{
    public string FolderId { get; set; } = "0";
    public bool PromptEachTime { get; set; }
    /// <summary>Optional sub-folder created (or reused) under the chosen folder.</summary>
    public string SubFolderName { get; set; } = "Shotwise";
}

/// <summary>SHR-10 Box: OAuth2 (+PKCE), multipart upload (≤ 50 MB) and chunked upload sessions above, shared link.</summary>
public sealed class BoxDestination : OAuthDestinationBase
{
    public const long ChunkedThreshold = 50L * 1024 * 1024;

    public BoxDestination(ShareContext ctx, HttpClient? http = null) : base(ctx, http) { }

    public override string Id => "box";
    public override string DisplayName => "Box";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Storage;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Cloud;
    public override bool CanShowOptions => true;
    protected override string ProviderId => "box";

    protected override OAuthClientConfig? BuildConfig(string clientId, string? clientSecret) => new(
        Id, new Uri("https://account.box.com/api/oauth2/authorize"), new Uri("https://api.box.com/oauth2/token"), clientId,
        Array.Empty<string>(), clientSecret); // Box scopes are configured on the app registration

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        var s = LoadSettings<BoxSettings>();
        if (!s.PromptEachTime) return true;
        var id = await PickAsync(ui, ct).ConfigureAwait(false);
        if (id is null) return false;
        s.FolderId = id;
        SaveSettings(s);
        return true;
    }

    private Task<string?> PickAsync(IShareUi ui, CancellationToken ct) => ui.PickFolderAsync("Box folder", async parent =>
    {
        var r = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Get, $"https://api.box.com/2.0/folders/{Uri.EscapeDataString(parent ?? "0")}/items?fields=id,name,type&limit=1000", t), ct).ConfigureAwait(false);
        return (IReadOnlyList<RemoteFolder>)r.Items("entries").Where(e => e.Str("type") == "folder")
            .Select(e => new RemoteFolder(e.Str("id")!, e.Str("name")!, parent, true)).OrderBy(f => f.Name).ToList();
    });

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<BoxSettings>();
        var form = new ShareForm();
        form.Add(new ShareFormField { Key = "sub", Label = "Create/use sub-folder (blank = none)", Value = s.SubFolderName });
        form.Add(new ShareFormField { Key = "prompt", Label = "Choose the folder every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptEachTime });
        if (!await ui.ShowFormAsync("Box options", form).ConfigureAwait(false)) return;
        s.SubFolderName = (form.Get<string>("sub") ?? "").Trim();
        s.PromptEachTime = form.Get<bool>("prompt");
        if (Session.HasTokens)
        {
            var id = await PickAsync(ui, CancellationToken.None).ConfigureAwait(false);
            if (id is not null) s.FolderId = id;
        }
        SaveSettings(s);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<BoxSettings>();
        var folderId = await EnsureSubFolderAsync(s, ct).ConfigureAwait(false);
        var ids = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var p = Slice(progress, i, files.Count);
            ids.Add(new FileInfo(f.Path).Length <= ChunkedThreshold
                ? await UploadSmallAsync(folderId, NameOf(f), f.Path, p, ct).ConfigureAwait(false)
                : await UploadChunkedAsync(folderId, NameOf(f), f.Path, p, ct).ConfigureAwait(false));
        }
        var link = await SharedLinkAsync(ids[0], ct).ConfigureAwait(false);
        return new ShareResult(ShareOutcome.Succeeded, "Uploaded to Box.", link, link, ids.Select(i => "box:" + i).ToList(), OpenLinkAction(link));
    }

    private async Task<string> EnsureSubFolderAsync(BoxSettings s, CancellationToken ct)
    {
        var parent = string.IsNullOrEmpty(s.FolderId) ? "0" : s.FolderId;
        if (string.IsNullOrWhiteSpace(s.SubFolderName)) return parent;
        var token = await TokenAsync(ct).ConfigureAwait(false);
        using var resp = await HttpHelpers.SendAsync(Http, () => HttpHelpers.Bearer(HttpMethod.Post, "https://api.box.com/2.0/folders", token,
            HttpHelpers.Json(new { name = s.SubFolderName.Trim(), parent = new { id = parent } })), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return JsonDocument.Parse(body).RootElement.Str("id") ?? parent;
        if ((int)resp.StatusCode == 409) // already exists: Box returns the conflicting folder
        {
            using var d = JsonDocument.Parse(body);
            var id = d.RootElement.Obj("context_info")?.Items("conflicts").Select(c => c.Str("id")).FirstOrDefault();
            if (id is not null) return id;
        }
        throw HttpHelpers.ToException(resp.StatusCode, body);
    }

    private async Task<string> UploadSmallAsync(string folderId, string name, string path, IProgress<double>? progress, CancellationToken ct)
    {
        var token = await TokenAsync(ct).ConfigureAwait(false);
        var r = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () =>
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(new { name, parent = new { id = folderId } }), Encoding.UTF8), "attributes");
            form.Add(new ProgressFileContent(path, FormatKeys.ContentType(path), v => progress?.Report(v)), "file", name);
            return HttpHelpers.Bearer(HttpMethod.Post, "https://upload.box.com/api/2.0/files/content", token, form);
        }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return r.Items("entries").Select(e => e.Str("id")).FirstOrDefault() ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Box returned no file id.");
    }

    private async Task<string> UploadChunkedAsync(string folderId, string name, string path, IProgress<double>? progress, CancellationToken ct)
    {
        var length = new FileInfo(path).Length;
        var session = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, "https://upload.box.com/api/2.0/files/upload_sessions", t,
            HttpHelpers.Json(new { folder_id = folderId, file_size = length, file_name = name })), ct).ConfigureAwait(false);
        var sessionId = session.Str("id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Box returned no upload session.");
        var partSize = session.TryGetProperty("part_size", out var ps) ? ps.GetInt64() : 8L * 1024 * 1024;
        var endpoints = session.Obj("session_endpoints");
        var partUrl = endpoints?.Str("upload_part") ?? $"https://upload.box.com/api/2.0/files/upload_sessions/{sessionId}";
        var commitUrl = endpoints?.Str("commit") ?? partUrl + "/commit";

        var parts = new List<object>();
        using var wholeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long offset = 0;
        while (offset < length)
        {
            var size = (int)Math.Min(partSize, length - offset);
            var buffer = new byte[size];
            await fs.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            wholeHash.AppendData(buffer);
            var digest = Convert.ToBase64String(SHA1.HashData(buffer));
            var start = offset;
            var token = await TokenAsync(ct).ConfigureAwait(false);
            var r = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () =>
            {
                var req = HttpHelpers.Bearer(HttpMethod.Put, partUrl, token, new ByteArrayContent(buffer));
                req.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                req.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + size - 1, length);
                req.Headers.Add("Digest", "sha=" + digest);
                return req;
            }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (r.Obj("part") is { } part) parts.Add(JsonSerializer.Deserialize<object>(part.GetRawText())!);
            offset += size;
            progress?.Report(offset / (double)length);
        }
        var fileDigest = Convert.ToBase64String(wholeHash.GetHashAndReset());
        var token2 = await TokenAsync(ct).ConfigureAwait(false);
        var commit = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () =>
        {
            var req = HttpHelpers.Bearer(HttpMethod.Post, commitUrl, token2, HttpHelpers.Json(new { parts }));
            req.Headers.Add("Digest", "sha=" + fileDigest);
            return req;
        }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return commit.Items("entries").Select(e => e.Str("id")).FirstOrDefault() ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Box did not confirm the upload.");
    }

    private async Task<string?> SharedLinkAsync(string fileId, CancellationToken ct)
    {
        var r = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Put, $"https://api.box.com/2.0/files/{Uri.EscapeDataString(fileId)}?fields=shared_link", t,
            HttpHelpers.Json(new { shared_link = new { access = "open" } })), ct).ConfigureAwait(false);
        return r.Obj("shared_link")?.Str("url");
    }
}
