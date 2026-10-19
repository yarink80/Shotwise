using System.Net.Http.Headers;
using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations;

public sealed class ImgurSettings
{
    /// <summary>"anonymous" (Client-ID only) or "account" (OAuth2 bearer; needs the app's client secret too).</summary>
    public string Mode { get; set; } = "anonymous";
    public bool PromptForTitle { get; set; }
}

/// <summary>SHR-12 Imgur: anonymous uploads with the user's own client id, or account uploads via OAuth2.</summary>
public sealed class ImgurDestination : OAuthDestinationBase
{
    public const long MaxImageBytes = 20L * 1024 * 1024;
    public const long MaxVideoBytes = 200L * 1024 * 1024;
    private string? _title;

    public ImgurDestination(ShareContext ctx, HttpClient? http = null, string? apiBase = null) : base(ctx, http) => ApiBase = apiBase ?? "https://api.imgur.com/3/";

    internal string ApiBase { get; }

    public override string Id => "imgur";
    public override string DisplayName => "Imgur";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Uploader;
    protected override string ProviderId => "imgur";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "jpg", "gif", "agif", "bmp", "tiff", "webp", "mp4");
    public override bool CanShowOptions => true;
    public override bool RequiresAccount => LoadSettings<ImgurSettings>().Mode == "account";
    public override bool IsConfigured => Ctx.GetClientId("imgur") is not null && (LoadSettings<ImgurSettings>().Mode != "account" || Session.HasTokens);

    protected override OAuthClientConfig? BuildConfig(string clientId, string? clientSecret) => new(
        Id, new Uri("https://api.imgur.com/oauth2/authorize"), new Uri("https://api.imgur.com/oauth2/token"), clientId, Array.Empty<string>(), clientSecret);

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        var s = LoadSettings<ImgurSettings>();
        if (Ctx.GetClientId("imgur") is null)
        {
            await ui.ShowMessageAsync("Imgur", "Register an application at imgur.com/account/settings/apps and enter its client id in Preferences → Share → Accounts.", true).ConfigureAwait(false);
            return false;
        }
        if (s.Mode == "account") await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        _title = null;
        if (s.PromptForTitle)
        {
            var form = new ShareForm { OkText = "Upload" };
            form.Add(new ShareFormField { Key = "title", Label = "Title", Value = files[0].Title });
            if (!await ui.ShowFormAsync("Upload to Imgur", form).ConfigureAwait(false)) return false;
            _title = form.Get<string>("title");
        }
        return true;
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<ImgurSettings>();
        var form = new ShareForm();
        form.Add(new ShareFormField
        {
            Key = "mode", Label = "Upload as", Kind = ShareFieldKind.Choice, Value = s.Mode,
            Choices = new[] { ("anonymous", "Anonymous"), ("account", "My Imgur account") },
        });
        form.Add(new ShareFormField { Key = "title", Label = "Ask for a title every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptForTitle });
        if (!await ui.ShowFormAsync("Imgur options", form).ConfigureAwait(false)) return;
        s.Mode = form.Get<string>("mode") ?? "anonymous";
        s.PromptForTitle = form.Get<bool>("title");
        SaveSettings(s);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<ImgurSettings>();
        var clientId = Ctx.GetClientId("imgur") ?? throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Imgur client id missing.");
        var links = new List<string>();
        var extra = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var isVideo = FormatKeys.Of(f) == "mp4";
            var length = new FileInfo(f.Path).Length;
            if (length > (isVideo ? MaxVideoBytes : MaxImageBytes))
                throw new ShotwiseException(ErrorCode.FileTooLarge, $"Imgur accepts {(isVideo ? "videos up to 200" : "images up to 20")} MB.");
            var auth = s.Mode == "account" ? "Bearer " + await TokenAsync(ct).ConfigureAwait(false) : "Client-ID " + clientId;
            var slice = Slice(progress, i, files.Count);
            var title = _title ?? f.Title;
            var r = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () =>
            {
                var form = new MultipartFormDataContent();
                form.Add(new ProgressFileContent(f.Path, FormatKeys.ContentType(f.Path), v => slice?.Report(v)), isVideo ? "video" : "image", NameOf(f));
                form.Add(new StringContent(isVideo ? "file" : "file"), "type");
                if (!string.IsNullOrWhiteSpace(title)) form.Add(new StringContent(title), "title");
                var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + "image") { Content = form };
                req.Headers.TryAddWithoutValidation("Authorization", auth);
                return req;
            }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            var data = r.Obj("data") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Imgur returned no data.");
            links.Add(data.Str("link") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Imgur returned no link."));
            if (data.Str("deletehash") is { } dh) extra.Add($"delete:{dh}");
        }
        return new ShareResult(ShareOutcome.Succeeded, "Uploaded to Imgur.", links[0], links[0], links.Concat(extra).ToList(), OpenLinkAction(links[0]));
    }
}

public sealed class XSettings
{
    public string Text { get; set; } = "";
    public bool PromptEachTime { get; set; } = true;
}

/// <summary>
/// SHR-13 X (Twitter): OAuth 2.0 PKCE with the user's own X developer app, v2 media upload + POST /2/tweets.
/// Current reality: posting through the X API requires a paid developer tier. This destination is wired to the documented v2 endpoints but could
/// not be verified against the live service during development (no paid credentials); it fails with a clear message when the API refuses.
/// </summary>
public sealed class XDestination : OAuthDestinationBase
{
    public const int MaxTweetLength = 280;
    public const int ChunkSize = 4 * 1024 * 1024;
    private string _text = "";

    public XDestination(ShareContext ctx, HttpClient? http = null) : base(ctx, http) { }

    public override string Id => "x";
    public override string DisplayName => "X (Twitter)";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Social;
    protected override string ProviderId => "x";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "jpg", "gif", "agif", "mp4", "webp");
    public override bool SupportsMultiple => true;
    public override bool CanShowOptions => true;

    protected override OAuthClientConfig? BuildConfig(string clientId, string? clientSecret) => new(
        Id, new Uri("https://x.com/i/oauth2/authorize"), new Uri("https://api.x.com/2/oauth2/token"), clientId,
        new[] { "tweet.read", "tweet.write", "users.read", "media.write", "offline.access" }, clientSecret);

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        var s = LoadSettings<XSettings>();
        _text = s.Text;
        if (!s.PromptEachTime) return true;
        var form = new ShareForm { OkText = "Post", Description = "Posting needs an X developer app with write access (paid API tier)." };
        form.Add(new ShareFormField { Key = "text", Label = $"Post text (max {MaxTweetLength})", Kind = ShareFieldKind.MultilineText, Value = s.Text });
        if (!await ui.ShowFormAsync("Post to X", form).ConfigureAwait(false)) return false;
        _text = (form.Get<string>("text") ?? "").Trim();
        if (_text.Length > MaxTweetLength) { await ui.ShowMessageAsync("X", $"The text is {_text.Length} characters; the limit is {MaxTweetLength}.", true).ConfigureAwait(false); return false; }
        return true;
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<XSettings>();
        var form = new ShareForm();
        form.Add(new ShareFormField { Key = "text", Label = "Default text", Value = s.Text });
        form.Add(new ShareFormField { Key = "prompt", Label = "Ask for the text every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptEachTime });
        if (!await ui.ShowFormAsync("X options", form).ConfigureAwait(false)) return;
        s.Text = form.Get<string>("text") ?? "";
        s.PromptEachTime = form.Get<bool>("prompt");
        SaveSettings(s);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        if (files.Count > 4) throw new ShotwiseException(ErrorCode.ShareMultipleFiles, "X accepts at most four pictures per post.");
        var ids = new List<string>();
        for (var i = 0; i < files.Count; i++)
            ids.Add(await UploadMediaAsync(files[i], Slice(progress, i, files.Count), ct).ConfigureAwait(false));
        var body = new Dictionary<string, object> { ["media"] = new Dictionary<string, object> { ["media_ids"] = ids } };
        if (!string.IsNullOrWhiteSpace(_text)) body["text"] = _text;
        var r = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, "https://api.x.com/2/tweets", t, HttpHelpers.Json(body)), ct).ConfigureAwait(false);
        var id = r.Obj("data")?.Str("id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "X returned no post id.");
        var link = "https://x.com/i/web/status/" + id;
        return new ShareResult(ShareOutcome.Succeeded, "Posted to X.", link, link, new[] { link }, OpenLinkAction(link));
    }

    private async Task<string> UploadMediaAsync(ShareFile f, IProgress<double>? progress, CancellationToken ct)
    {
        var key = FormatKeys.Of(f);
        var category = key switch { "mp4" => "tweet_video", "agif" or "gif" => "tweet_gif", _ => "tweet_image" };
        var length = new FileInfo(f.Path).Length;
        var mime = FormatKeys.ContentType(f.Path);
        if (category == "tweet_image" && length <= 5 * 1024 * 1024)
        {
            var r = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, "https://api.x.com/2/media/upload", t, SimpleForm(f, category, mime, progress)), ct).ConfigureAwait(false);
            return r.Obj("data")?.Str("id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "X returned no media id.");
        }

        var init = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, "https://api.x.com/2/media/upload/initialize", t,
            HttpHelpers.Json(new { total_bytes = length, media_type = mime, media_category = category })), ct).ConfigureAwait(false);
        var mediaId = init.Obj("data")?.Str("id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "X returned no media id.");
        long offset = 0;
        var segment = 0;
        while (offset < length)
        {
            var size = (int)Math.Min(ChunkSize, length - offset);
            var o = offset;
            var idx = segment;
            await JsonAsync(t =>
            {
                var form = new MultipartFormDataContent();
                form.Add(new ProgressFileContent(f.Path, "application/octet-stream", null, o, size), "media", "chunk");
                form.Add(new StringContent(idx.ToString()), "segment_index");
                return HttpHelpers.Bearer(HttpMethod.Post, $"https://api.x.com/2/media/upload/{Uri.EscapeDataString(mediaId)}/append", t, form);
            }, ct).ConfigureAwait(false);
            offset += size;
            segment++;
            progress?.Report(offset / (double)length);
        }
        var fin = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Post, $"https://api.x.com/2/media/upload/{Uri.EscapeDataString(mediaId)}/finalize", t, new ByteArrayContent(Array.Empty<byte>())), ct).ConfigureAwait(false);
        // Video/GIF are processed asynchronously: poll until succeeded.
        var info = fin.Obj("data")?.Obj("processing_info");
        for (var attempt = 0; info is { } pi && pi.Str("state") is "pending" or "in_progress" && attempt < 60; attempt++)
        {
            var wait = pi.TryGetProperty("check_after_secs", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 2;
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 15)), ct).ConfigureAwait(false);
            var status = await JsonAsync(t => HttpHelpers.Bearer(HttpMethod.Get, $"https://api.x.com/2/media/upload?command=STATUS&media_id={Uri.EscapeDataString(mediaId)}", t), ct).ConfigureAwait(false);
            info = status.Obj("data")?.Obj("processing_info");
        }
        if (info is { } last && last.Str("state") == "failed") throw new ShotwiseException(ErrorCode.UploadRejected, "X could not process the media.");
        return mediaId;
    }

    private static MultipartFormDataContent SimpleForm(ShareFile f, string category, string mime, IProgress<double>? progress)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ProgressFileContent(f.Path, mime, v => progress?.Report(v)), "media", NameOf(f));
        form.Add(new StringContent(category), "media_category");
        form.Add(new StringContent(mime), "media_type");
        return form;
    }
}

/// <summary>
/// SHR-13 Evernote: shipped disabled (documented decision). Evernote's developer API keys are issued only on request and are restricted,
/// so there is no key this app could use on behalf of users, and the old desktop integration points no longer exist.
/// </summary>
public sealed class EvernoteDestination : ShareDestinationBase, IUnavailableDestination
{
    public EvernoteDestination(ShareContext ctx) : base(ctx) { }

    public override string Id => "evernote";
    public override string DisplayName => "Evernote";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Social;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "agif");
    public override bool IsConfigured => false;
    public override bool RequiresAccount => true;
    public override IReadOnlyList<PolicyFeature> RequiredPolicies => Array.Empty<PolicyFeature>();

    public string UnavailableReason =>
        "Evernote is not available: Evernote only issues developer API keys on request and does not offer user-supplied OAuth for desktop apps. " +
        "Workaround: use the Email destination with your personal Evernote email-in address.";

    public override Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
        => Task.FromResult(ShareResult.Failure(UnavailableReason));
}
