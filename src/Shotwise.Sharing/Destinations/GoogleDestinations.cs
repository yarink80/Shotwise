using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations;

/// <summary>Google OAuth + resumable-upload plumbing shared by Drive and YouTube (REST; scopes are the narrowest available).</summary>
public abstract class GoogleDestinationBase : OAuthDestinationBase
{
    /// <summary>Resumable chunk size: a multiple of 256 KiB as required by Google.</summary>
    public const int ChunkSize = 8 * 1024 * 1024;

    protected GoogleDestinationBase(ShareContext ctx, HttpClient? http = null) : base(ctx, http) { }

    protected override string ProviderId => "google";
    protected abstract string Scope { get; }
    public override DestinationCategory Category => DestinationCategory.Storage;
    public override bool CanShowOptions => true;

    protected override OAuthClientConfig? BuildConfig(string clientId, string? clientSecret) => new(
        Id, new Uri("https://accounts.google.com/o/oauth2/v2/auth"), new Uri("https://oauth2.googleapis.com/token"),
        clientId, new[] { Scope }, clientSecret,
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" });

    /// <summary>Starts a resumable session and uploads the file in chunks; returns the created resource JSON.</summary>
    protected async Task<JsonElement> ResumableUploadAsync(string initUrl, object metadata, string localPath, string contentType, IProgress<double>? progress, CancellationToken ct)
    {
        var token = await TokenAsync(ct).ConfigureAwait(false);
        var length = new FileInfo(localPath).Length;
        string sessionUrl;
        using (var init = await HttpHelpers.SendAsync(Http, () =>
        {
            var r = HttpHelpers.Bearer(HttpMethod.Post, initUrl, token, HttpHelpers.Json(metadata));
            r.Headers.Add("X-Upload-Content-Type", contentType);
            r.Headers.Add("X-Upload-Content-Length", length.ToString());
            return r;
        }, ct).ConfigureAwait(false))
        {
            if (!init.IsSuccessStatusCode) throw HttpHelpers.ToException(init.StatusCode, await init.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            sessionUrl = init.Headers.Location?.AbsoluteUri ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Google did not return an upload session.");
        }

        long offset = 0;
        if (length == 0) throw new ShotwiseException(ErrorCode.UploadRejected, "The file is empty.");
        while (true)
        {
            var size = (int)Math.Min(ChunkSize, length - offset);
            var start = offset;
            using var resp = await HttpHelpers.SendAsync(Http, () =>
            {
                var r = new HttpRequestMessage(HttpMethod.Put, sessionUrl) { Content = new ProgressFileContent(localPath, contentType, null, start, size) };
                r.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + size - 1, length);
                return r;
            }, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 308)
            {
                // Server tells us how much it has: "Range: bytes=0-N".
                var range = resp.Headers.TryGetValues("Range", out var vals) ? vals.FirstOrDefault() : null;
                offset = range is not null && range.Contains('-') && long.TryParse(range[(range.LastIndexOf('-') + 1)..], out var last) ? last + 1 : offset + size;
                progress?.Report(offset / (double)length);
                continue;
            }
            if (!resp.IsSuccessStatusCode) throw HttpHelpers.ToException(resp.StatusCode, body);
            progress?.Report(1);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
    }
}

public sealed class GoogleDriveSettings
{
    /// <summary>Folder (created on first use, visible to this app only because of the drive.file scope).</summary>
    public string FolderName { get; set; } = "Shotwise";
    public string FolderId { get; set; } = "";
    /// <summary>"anyone" = anyone with the link can view; "private" = only the owner (link opens for signed-in owner).</summary>
    public string Access { get; set; } = "anyone";
}

/// <summary>SHR-09 Google Drive: drive.file scope, resumable upload, shareable link.</summary>
public sealed class GoogleDriveDestination : GoogleDestinationBase
{
    public GoogleDriveDestination(ShareContext ctx, HttpClient? http = null) : base(ctx, http) { }

    public override string Id => "googledrive";
    public override string DisplayName => "Google Drive";
    public override string IconGlyph => "";
    protected override string Scope => "https://www.googleapis.com/auth/drive.file";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Cloud;

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        return true;
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<GoogleDriveSettings>();
        var form = new ShareForm { Description = "Shotwise can only see folders and files it created (Google 'drive.file' permission)." };
        form.Add(new ShareFormField { Key = "folder", Label = "Folder name", Value = s.FolderName });
        form.Add(new ShareFormField
        {
            Key = "access", Label = "Link access", Kind = ShareFieldKind.Choice, Value = s.Access,
            Choices = new[] { ("anyone", "Anyone with the link"), ("private", "Only me") },
        });
        if (!await ui.ShowFormAsync("Google Drive options", form).ConfigureAwait(false)) return;
        var name = (form.Get<string>("folder") ?? "").Trim();
        if (name != s.FolderName) s.FolderId = "";
        s.FolderName = name;
        s.Access = form.Get<string>("access") ?? "anyone";
        SaveSettings(s);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<GoogleDriveSettings>();
        var folderId = await EnsureFolderAsync(s, ct).ConfigureAwait(false);
        var links = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var meta = new Dictionary<string, object> { ["name"] = NameOf(f) };
            if (folderId is not null) meta["parents"] = new[] { folderId };
            var created = await ResumableUploadAsync("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id,webViewLink",
                meta, f.Path, FormatKeys.ContentType(f.Path), Slice(progress, i, files.Count), ct).ConfigureAwait(false);
            var id = created.Str("id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Google Drive returned no file id.");
            if (s.Access == "anyone")
            {
                await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}/permissions", t,
                    HttpHelpers.Json(new { role = "reader", type = "anyone" })), ct).ConfigureAwait(false);
            }
            links.Add(created.Str("webViewLink") ?? $"https://drive.google.com/file/d/{id}/view");
        }
        return new ShareResult(ShareOutcome.Succeeded, "Uploaded to Google Drive.", links[0], links[0], links, OpenLinkAction(links[0]));
    }

    private async Task<string?> EnsureFolderAsync(GoogleDriveSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.FolderName)) return null;
        if (!string.IsNullOrEmpty(s.FolderId)) return s.FolderId;
        var q = Uri.EscapeDataString($"mimeType='application/vnd.google-apps.folder' and name='{s.FolderName.Replace("\\", "\\\\").Replace("'", "\\'")}' and trashed=false");
        var found = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files?q={q}&fields=files(id)&pageSize=1", t), ct).ConfigureAwait(false);
        var id = found.Items("files").Select(f => f.Str("id")).FirstOrDefault();
        if (id is null)
        {
            var created = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id", t,
                HttpHelpers.Json(new { name = s.FolderName, mimeType = "application/vnd.google-apps.folder" })), ct).ConfigureAwait(false);
            id = created.Str("id");
        }
        s.FolderId = id ?? "";
        SaveSettings(s);
        return id;
    }
}

public sealed class YouTubeSettings
{
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "";
    public string CategoryId { get; set; } = "28";
    /// <summary>"public", "unlisted" or "private". Default is private so nothing is published by accident.</summary>
    public string Privacy { get; set; } = "private";
    /// <summary>"url" or "embed".</summary>
    public string ClipboardContents { get; set; } = "url";
    public bool PromptEachTime { get; set; } = true;
}

/// <summary>SHR-09 YouTube: MP4 upload with title/description/tags/category/privacy; URL or embed code to the clipboard.</summary>
public sealed class YouTubeDestination : GoogleDestinationBase
{
    private YouTubeSettings? _s;
    private string _title = "";

    public YouTubeDestination(ShareContext ctx, HttpClient? http = null) : base(ctx, http) { }

    public override string Id => "youtube";
    public override string DisplayName => "YouTube";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Social;
    protected override string Scope => "https://www.googleapis.com/auth/youtube.upload";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("mp4");
    public override bool SupportsMultiple => false;

    internal static readonly (string, string)[] Categories =
    {
        ("28", "Science & Technology"), ("27", "Education"), ("22", "People & Blogs"), ("24", "Entertainment"),
        ("20", "Gaming"), ("26", "Howto & Style"), ("29", "Nonprofits & Activism"),
    };

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        var s = LoadSettings<YouTubeSettings>();
        _title = files[0].Title;
        _s = s;
        if (!s.PromptEachTime) return true;
        var form = new ShareForm { OkText = "Upload" };
        form.Add(new ShareFormField { Key = "title", Label = "Title", Value = _title, Required = true });
        form.Add(new ShareFormField { Key = "desc", Label = "Description", Kind = ShareFieldKind.MultilineText, Value = s.Description });
        form.Add(new ShareFormField { Key = "tags", Label = "Tags (comma separated)", Value = s.Tags });
        form.Add(new ShareFormField { Key = "cat", Label = "Category", Kind = ShareFieldKind.Choice, Value = s.CategoryId, Choices = Categories });
        form.Add(new ShareFormField
        {
            Key = "privacy", Label = "Privacy", Kind = ShareFieldKind.Choice, Value = s.Privacy,
            Choices = new[] { ("private", "Private"), ("unlisted", "Unlisted"), ("public", "Public") },
        });
        form.Add(new ShareFormField
        {
            Key = "clip", Label = "Copy to clipboard", Kind = ShareFieldKind.Choice, Value = s.ClipboardContents,
            Choices = new[] { ("url", "Video URL"), ("embed", "Embed code") },
        });
        if (!await ui.ShowFormAsync("Upload to YouTube", form).ConfigureAwait(false)) return false;
        _title = (form.Get<string>("title") ?? _title).Trim();
        s.Description = form.Get<string>("desc") ?? "";
        s.Tags = form.Get<string>("tags") ?? "";
        s.CategoryId = form.Get<string>("cat") ?? "28";
        s.Privacy = form.Get<string>("privacy") ?? "private";
        s.ClipboardContents = form.Get<string>("clip") ?? "url";
        SaveSettings(s);
        return true;
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<YouTubeSettings>();
        var form = new ShareForm();
        form.Add(new ShareFormField { Key = "prompt", Label = "Ask for title and privacy every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptEachTime });
        form.Add(new ShareFormField
        {
            Key = "privacy", Label = "Default privacy", Kind = ShareFieldKind.Choice, Value = s.Privacy,
            Choices = new[] { ("private", "Private"), ("unlisted", "Unlisted"), ("public", "Public") },
        });
        form.Add(new ShareFormField
        {
            Key = "clip", Label = "Copy to clipboard", Kind = ShareFieldKind.Choice, Value = s.ClipboardContents,
            Choices = new[] { ("url", "Video URL"), ("embed", "Embed code") },
        });
        if (!await ui.ShowFormAsync("YouTube options", form).ConfigureAwait(false)) return;
        s.PromptEachTime = form.Get<bool>("prompt");
        s.Privacy = form.Get<string>("privacy") ?? "private";
        s.ClipboardContents = form.Get<string>("clip") ?? "url";
        SaveSettings(s);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = _s ?? LoadSettings<YouTubeSettings>();
        var f = files[0];
        var title = string.IsNullOrWhiteSpace(_title) ? f.Title : _title;
        var metadata = new
        {
            snippet = new
            {
                title = title.Length > 100 ? title[..100] : title,
                description = s.Description,
                tags = s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                categoryId = s.CategoryId,
            },
            status = new { privacyStatus = s.Privacy, selfDeclaredMadeForKids = false },
        };
        var video = await ResumableUploadAsync("https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status",
            metadata, f.Path, "video/mp4", progress, ct).ConfigureAwait(false);
        var id = video.Str("id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "YouTube returned no video id.");
        var url = $"https://youtu.be/{id}";
        var clip = s.ClipboardContents == "embed"
            ? $"<iframe width=\"560\" height=\"315\" src=\"https://www.youtube.com/embed/{id}\" title=\"YouTube video player\" frameborder=\"0\" allowfullscreen></iframe>"
            : url;
        return new ShareResult(ShareOutcome.Succeeded, $"Uploaded to YouTube ({s.Privacy}). Processing may take a few minutes.", url, clip, new[] { url }, OpenLinkAction(url));
    }
}
