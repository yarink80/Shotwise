using System.Net.Http.Headers;
using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations;

public sealed class SlackSettings
{
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string Message { get; set; } = "";
    public string Workspace { get; set; } = "";
    public bool PromptEachTime { get; set; } = true;
}

/// <summary>
/// SHR-11 Slack with the user's own Slack app: paste a bot (xoxb-) or user (xoxp-) token. The token is verified with auth.test and kept in the
/// credential store. Upload uses files.getUploadURLExternal → raw POST → files.completeUploadExternal.
/// Needed scopes: files:write, chat:write, channels:read, groups:read, im:read, mpim:read, users:read.
/// </summary>
public sealed class SlackDestination : ShareDestinationBase
{
    private const string Api = "https://slack.com/api/";
    private readonly HttpClient _http;
    private SlackSettings? _pending;

    public SlackDestination(ShareContext ctx, HttpClient? http = null, string? apiBase = null) : base(ctx)
    {
        _http = http ?? ctx.Http;
        ApiBase = apiBase ?? Api;
    }

    internal string ApiBase { get; }

    public override string Id => "slack";
    public override string DisplayName => "Slack";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Social;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Cloud;
    public override bool RequiresAccount => true;
    public override bool CanShowOptions => true;
    public override bool IsConfigured => Token is not null;

    private string? Token => Ctx.Credentials.Read(CredentialKeys.Make("slack", "token"));

    public override async Task SignInAsync(IShareUi ui, CancellationToken ct)
    {
        var form = new ShareForm { Description = "Create a Slack app for your workspace, add the scopes files:write, chat:write, channels:read, groups:read, im:read, mpim:read, users:read, install it, and paste its Bot (xoxb-) or User (xoxp-) token." };
        form.Add(new ShareFormField { Key = "token", Label = "Slack token", Kind = ShareFieldKind.Password, Required = true });
        if (!await ui.ShowFormAsync("Connect Slack", form).ConfigureAwait(false)) throw new ShotwiseException(ErrorCode.ShareCancelled, "Cancelled.");
        var token = (form.Get<string>("token") ?? "").Trim();
        if (!token.StartsWith("xox", StringComparison.Ordinal)) throw new ShotwiseException(ErrorCode.AuthenticationFailed, "That does not look like a Slack token (expected xoxb-… or xoxp-…).");
        var who = await CallAsync("auth.test", token, null, ct).ConfigureAwait(false);
        Ctx.Credentials.Write(CredentialKeys.Make("slack", "token"), token);
        var s = LoadSettings<SlackSettings>();
        s.Workspace = who.Str("team") ?? "";
        SaveSettings(s);
    }

    public override Task SignOutAsync()
    {
        Ctx.Credentials.Delete(CredentialKeys.Make("slack", "token"));
        return Task.CompletedTask;
    }

    private async Task<JsonElement> CallAsync(string method, string token, Dictionary<string, string>? form, CancellationToken ct, bool get = false)
    {
        var url = ApiBase + method;
        if (get && form is { Count: > 0 }) url += "?" + string.Join('&', form.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        var r = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(_http, () =>
        {
            var req = HttpHelpers.Bearer(get ? HttpMethod.Get : HttpMethod.Post, url, token, get ? null : new FormUrlEncodedContent(form ?? new Dictionary<string, string>()));
            return req;
        }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return CheckOk(r, method);
    }

    private static JsonElement CheckOk(JsonElement r, string method)
    {
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return r;
        var err = r.Str("error") ?? "unknown_error";
        var code = err is "invalid_auth" or "not_authed" or "token_revoked" or "account_inactive" or "missing_scope" ? ErrorCode.AuthenticationFailed : ErrorCode.UploadRejected;
        throw new ShotwiseException(code, $"Slack ({method}) refused the request: {err}" + (r.Str("needed") is { } needed ? $" (needs scope {needed})" : ""));
    }

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        var token = Token;
        if (token is null)
        {
            await SignInAsync(ui, ct).ConfigureAwait(false);
            token = Token!;
        }
        var s = LoadSettings<SlackSettings>();
        _pending = s;
        if (!s.PromptEachTime && !string.IsNullOrEmpty(s.ChannelId)) return true;

        var choices = await ListConversationsAsync(token, ct).ConfigureAwait(false);
        if (choices.Count == 0) { await ui.ShowMessageAsync("Slack", "No conversations are visible to this token. Invite the app to a channel first.", true).ConfigureAwait(false); return false; }
        var form = new ShareForm { OkText = "Send" };
        form.Add(new ShareFormField { Key = "channel", Label = "Channel or conversation", Kind = ShareFieldKind.Choice, Value = choices.Any(c => c.Key == s.ChannelId) ? s.ChannelId : choices[0].Key, Choices = choices });
        form.Add(new ShareFormField { Key = "msg", Label = "Message", Kind = ShareFieldKind.MultilineText, Value = s.Message });
        if (!await ui.ShowFormAsync("Share to Slack", form).ConfigureAwait(false)) return false;
        s.ChannelId = form.Get<string>("channel") ?? choices[0].Key;
        s.ChannelName = choices.First(c => c.Key == s.ChannelId).Label;
        s.Message = form.Get<string>("msg") ?? "";
        SaveSettings(s);
        return true;
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<SlackSettings>();
        var form = new ShareForm { Description = Token is null ? "Not connected. Use Sign in to paste a token." : "Connected to " + (string.IsNullOrEmpty(s.Workspace) ? "Slack" : s.Workspace) };
        form.Add(new ShareFormField { Key = "prompt", Label = "Choose the channel and message every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptEachTime });
        if (!await ui.ShowFormAsync("Slack options", form).ConfigureAwait(false)) return;
        s.PromptEachTime = form.Get<bool>("prompt");
        SaveSettings(s);
    }

    internal async Task<List<(string Key, string Label)>> ListConversationsAsync(string token, CancellationToken ct)
    {
        var users = new Dictionary<string, string>();
        try
        {
            string? cursor = null;
            for (var page = 0; page < 5; page++)
            {
                var form = new Dictionary<string, string> { ["limit"] = "200" };
                if (cursor is not null) form["cursor"] = cursor;
                var r = await CallAsync("users.list", token, form, ct, get: true).ConfigureAwait(false);
                foreach (var u in r.Items("members")) if (u.Str("id") is { } id) users[id] = u.Str("real_name") is { Length: > 0 } rn ? rn : u.Str("name") ?? id;
                cursor = r.Obj("response_metadata")?.Str("next_cursor");
                if (string.IsNullOrEmpty(cursor)) break;
            }
        }
        catch (ShotwiseException) { /* users:read missing: show ids */ }

        var result = new List<(string, string)>();
        string? cur = null;
        for (var page = 0; page < 10; page++)
        {
            var form = new Dictionary<string, string> { ["types"] = "public_channel,private_channel,mpim,im", ["exclude_archived"] = "true", ["limit"] = "200" };
            if (cur is not null) form["cursor"] = cur;
            var r = await CallAsync("conversations.list", token, form, ct, get: true).ConfigureAwait(false);
            foreach (var c in r.Items("channels"))
            {
                var id = c.Str("id");
                if (id is null) continue;
                if (c.TryGetProperty("is_im", out var im) && im.ValueKind == JsonValueKind.True)
                    result.Add((id, "DM: " + (c.Str("user") is { } uid ? users.GetValueOrDefault(uid, uid) : id)));
                else if (c.TryGetProperty("is_mpim", out var mp) && mp.ValueKind == JsonValueKind.True) result.Add((id, "Group DM: " + c.Str("name")));
                else result.Add((id, (c.TryGetProperty("is_private", out var pv) && pv.ValueKind == JsonValueKind.True ? "Private: " : "#") + c.Str("name")));
            }
            cur = r.Obj("response_metadata")?.Str("next_cursor");
            if (string.IsNullOrEmpty(cur)) break;
        }
        return result.OrderBy(c => c.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var token = Token ?? throw new ShotwiseException(ErrorCode.AuthorizationRequired, "Connect Slack first.");
        var s = _pending ?? LoadSettings<SlackSettings>();
        if (string.IsNullOrEmpty(s.ChannelId)) throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Choose a Slack channel first.");

        var completed = new List<object>();
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var name = NameOf(f);
            var length = new FileInfo(f.Path).Length;
            var up = await CallAsync("files.getUploadURLExternal", token, new Dictionary<string, string> { ["filename"] = name, ["length"] = length.ToString() }, ct).ConfigureAwait(false);
            var url = up.Str("upload_url") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Slack returned no upload URL.");
            var fileId = up.Str("file_id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Slack returned no file id.");
            var slice = Slice(progress, i, files.Count);
            using (var resp = await HttpHelpers.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ProgressFileContent(f.Path, "application/octet-stream", v => slice?.Report(v)),
            }, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) throw HttpHelpers.ToException(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            completed.Add(new Dictionary<string, string> { ["id"] = fileId, ["title"] = string.IsNullOrWhiteSpace(f.Title) ? name : f.Title });
        }

        var body = new Dictionary<string, object> { ["files"] = completed, ["channel_id"] = s.ChannelId };
        if (!string.IsNullOrWhiteSpace(s.Message)) body["initial_comment"] = s.Message;
        var done = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(_http, () =>
            HttpHelpers.Bearer(HttpMethod.Post, ApiBase + "files.completeUploadExternal", token,
                new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        CheckOk(done, "files.completeUploadExternal");
        var links = done.Items("files").Select(f => f.Str("permalink")).Where(l => l is not null).Select(l => l!).ToList();
        var link = links.FirstOrDefault();
        return new ShareResult(ShareOutcome.Succeeded, $"Sent to {(string.IsNullOrEmpty(s.ChannelName) ? "Slack" : s.ChannelName)}.", link, link, links, OpenLinkAction(link));
    }
}
