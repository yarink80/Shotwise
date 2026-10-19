using System.Net;
using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations.Graph;

public sealed class OneDriveSettings
{
    public string FolderId { get; set; } = "";
    /// <summary>"organization" (default), "anonymous" (falls back to organization when tenant policy forbids) or "none".</summary>
    public string LinkScope { get; set; } = "organization";
    public bool PromptEachTime { get; set; }
}

/// <summary>SHR-08 OneDrive: Graph upload (simple + resumable), folder picker, sharing link.</summary>
public sealed class OneDriveDestination : GraphDestinationBase
{
    public OneDriveDestination(ShareContext ctx, IMicrosoftTokenProvider? tokens = null, HttpClient? http = null) : base(ctx, tokens, http) { }

    public override string Id => "onedrive";
    public override string DisplayName => "OneDrive";
    public override string IconGlyph => "";
    protected override string[] Scopes => new[] { "Files.ReadWrite.All", "User.Read" };

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        var s = LoadSettings<OneDriveSettings>();
        if (!s.PromptEachTime) return true;
        var id = await ui.PickFolderAsync("OneDrive folder", parent => Api(ui).ListFoldersAsync("/me/drive", parent, ct)).ConfigureAwait(false);
        if (id is null) return false;
        s.FolderId = id;
        SaveSettings(s);
        return true;
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        await EnsureSignedInAsync(ui, CancellationToken.None).ConfigureAwait(false);
        var s = LoadSettings<OneDriveSettings>();
        var form = new ShareForm();
        form.Add(new ShareFormField
        {
            Key = "scope", Label = "Sharing link", Kind = ShareFieldKind.Choice, Value = s.LinkScope,
            Choices = new[] { ("organization", "People in my organization"), ("anonymous", "Anyone with the link"), ("none", "Do not create a link") },
        });
        form.Add(new ShareFormField { Key = "prompt", Label = "Choose the folder every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptEachTime });
        if (!await ui.ShowFormAsync("OneDrive options", form).ConfigureAwait(false)) return;
        s.LinkScope = form.Get<string>("scope") ?? "organization";
        s.PromptEachTime = form.Get<bool>("prompt");
        var folder = await ui.PickFolderAsync("OneDrive folder (Cancel = root)", parent => Api(ui).ListFoldersAsync("/me/drive", parent, CancellationToken.None)).ConfigureAwait(false);
        s.FolderId = folder ?? "";
        SaveSettings(s);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<OneDriveSettings>();
        var api = Api();
        var uploaded = new List<DriveItemRef>();
        for (var i = 0; i < files.Count; i++)
            uploaded.Add(await api.UploadAsync("/me/drive", string.IsNullOrEmpty(s.FolderId) ? null : s.FolderId, NameOf(files[i]), files[i].Path, Slice(progress, i, files.Count), ct).ConfigureAwait(false));
        var link = s.LinkScope == "none" ? null : await api.CreateLinkAsync("/me/drive", uploaded[0].Id, s.LinkScope, ct).ConfigureAwait(false);
        link ??= uploaded[0].WebUrl;
        return new ShareResult(ShareOutcome.Succeeded, "Uploaded to OneDrive.", link, link, uploaded.Select(u => u.WebUrl ?? u.Name).ToList(), OpenLinkAction(link));
    }
}

public sealed class SharePointSettings
{
    /// <summary>Site address such as "contoso.sharepoint.com/sites/team"; empty = the tenant root site.</summary>
    public string SiteAddress { get; set; } = "";
    public string DriveId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string LinkScope { get; set; } = "organization";
}

/// <summary>SHR-08 SharePoint: site → document library → folder.</summary>
public sealed class SharePointDestination : GraphDestinationBase
{
    public SharePointDestination(ShareContext ctx, IMicrosoftTokenProvider? tokens = null, HttpClient? http = null) : base(ctx, tokens, http) { }

    public override string Id => "sharepoint";
    public override string DisplayName => "SharePoint";
    public override string IconGlyph => "";
    protected override string[] Scopes => new[] { "Sites.ReadWrite.All", "Files.ReadWrite.All", "User.Read" };
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "jpg", "agif", "mp4", "bmp", "tiff", "webp", "pdf");
    public override bool IsConfigured => base.IsConfigured && !string.IsNullOrEmpty(LoadSettings<SharePointSettings>().DriveId);

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(LoadSettings<SharePointSettings>().DriveId))
        {
            await ShowOptionsAsync(ui).ConfigureAwait(false);
            if (string.IsNullOrEmpty(LoadSettings<SharePointSettings>().DriveId)) return false;
        }
        return true;
    }

    internal static string SitePath(string address)
    {
        var a = address.Trim();
        if (a.Length == 0) return "/sites/root";
        a = a.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase).Trim('/');
        var slash = a.IndexOf('/');
        return slash < 0 ? $"/sites/{a}" : $"/sites/{a[..slash]}:/{a[(slash + 1)..]}";
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        await EnsureSignedInAsync(ui, CancellationToken.None).ConfigureAwait(false);
        var s = LoadSettings<SharePointSettings>();
        var f1 = new ShareForm { Description = "Example: contoso.sharepoint.com/sites/team (blank = root site)" };
        f1.Add(new ShareFormField { Key = "site", Label = "Site", Value = s.SiteAddress });
        if (!await ui.ShowFormAsync("SharePoint site", f1).ConfigureAwait(false)) return;
        s.SiteAddress = (f1.Get<string>("site") ?? "").Trim();
        var api = Api(ui);
        var site = await api.GetAsync(SitePath(s.SiteAddress), CancellationToken.None).ConfigureAwait(false);
        var siteId = site.Str("id") ?? throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Site not found.");
        var drives = await api.ListAsync($"/sites/{Uri.EscapeDataString(siteId)}/drives", CancellationToken.None).ConfigureAwait(false);
        if (drives.Count == 0) throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "The site has no document libraries.");
        var f2 = new ShareForm();
        f2.Add(new ShareFormField
        {
            Key = "drive", Label = "Document library", Kind = ShareFieldKind.Choice, Value = drives[0].Str("id"),
            Choices = drives.Select(d => (d.Str("id")!, d.Str("name") ?? d.Str("id")!)).ToList(),
        });
        f2.Add(new ShareFormField
        {
            Key = "scope", Label = "Sharing link", Kind = ShareFieldKind.Choice, Value = s.LinkScope,
            Choices = new[] { ("organization", "People in my organization"), ("anonymous", "Anyone with the link"), ("none", "Do not create a link") },
        });
        if (!await ui.ShowFormAsync("SharePoint library", f2).ConfigureAwait(false)) return;
        s.DriveId = f2.Get<string>("drive") ?? drives[0].Str("id")!;
        s.LinkScope = f2.Get<string>("scope") ?? "organization";
        var driveBase = $"/drives/{Uri.EscapeDataString(s.DriveId)}";
        s.FolderId = await ui.PickFolderAsync("SharePoint folder (Cancel = library root)", p => Api(ui).ListFoldersAsync(driveBase, p, CancellationToken.None)).ConfigureAwait(false) ?? "";
        SaveSettings(s);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<SharePointSettings>();
        if (string.IsNullOrEmpty(s.DriveId)) throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Choose a SharePoint library in the options first.");
        var api = Api();
        var driveBase = $"/drives/{Uri.EscapeDataString(s.DriveId)}";
        var uploaded = new List<DriveItemRef>();
        for (var i = 0; i < files.Count; i++)
            uploaded.Add(await api.UploadAsync(driveBase, string.IsNullOrEmpty(s.FolderId) ? null : s.FolderId, NameOf(files[i]), files[i].Path, Slice(progress, i, files.Count), ct).ConfigureAwait(false));
        var link = s.LinkScope == "none" ? null : await api.CreateLinkAsync(driveBase, uploaded[0].Id, s.LinkScope, ct).ConfigureAwait(false);
        link ??= uploaded[0].WebUrl;
        return new ShareResult(ShareOutcome.Succeeded, "Uploaded to SharePoint.", link, link, uploaded.Select(u => u.WebUrl ?? u.Name).ToList(), OpenLinkAction(link));
    }
}

public sealed class TeamsSettings
{
    /// <summary>"channel" or "chat".</summary>
    public string Kind { get; set; } = "channel";
    public string TeamId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>SHR-08 Teams: channel (SharePoint files folder) or chat (OneDrive "Microsoft Teams Chat Files") with a message referencing the uploaded file.</summary>
public sealed class TeamsDestination : GraphDestinationBase
{
    private TeamsSettings? _target;

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<TeamsSettings>();
        var form = new ShareForm { Description = "Choose the team, channel or chat when sharing." };
        form.Add(new ShareFormField { Key = "message", Label = "Default message", Kind = ShareFieldKind.MultilineText, Value = s.Message });
        if (!await ui.ShowFormAsync("Teams options", form).ConfigureAwait(false)) return;
        s.Message = form.Get<string>("message") ?? "";
        SaveSettings(s);
    }

    public TeamsDestination(ShareContext ctx, IMicrosoftTokenProvider? tokens = null, HttpClient? http = null) : base(ctx, tokens, http) { }

    public override string Id => "teams";
    public override string DisplayName => "Microsoft Teams";
    public override string IconGlyph => "";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "jpg", "agif", "mp4");
    protected override string[] Scopes => new[] { "Team.ReadBasic.All", "Channel.ReadBasic.All", "Chat.ReadBasic", "Chat.ReadWrite", "ChannelMessage.Send", "Files.ReadWrite.All", "User.Read" };

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        await EnsureSignedInAsync(ui, ct).ConfigureAwait(false);
        var s = LoadSettings<TeamsSettings>();
        var api = Api(ui);
        var teams = await api.ListAsync("/me/joinedTeams?$select=id,displayName", ct).ConfigureAwait(false);
        var chats = await api.ListAsync("/me/chats?$select=id,topic,chatType&$top=50", ct).ConfigureAwait(false);
        var choices = new List<(string, string)>();
        choices.AddRange(teams.Select(t => ("team:" + t.Str("id"), "Team: " + t.Str("displayName"))));
        choices.AddRange(chats.Select(c => ("chat:" + c.Str("id"), "Chat: " + (c.Str("topic") is { Length: > 0 } topic ? topic : (c.Str("chatType") ?? "chat") + " " + (c.Str("id")?[..Math.Min(8, c.Str("id")!.Length)])))));
        if (choices.Count == 0) { await ui.ShowMessageAsync("Teams", "No teams or chats were found for this account.", true).ConfigureAwait(false); return false; }

        var current = s.Kind == "chat" ? "chat:" + s.ChatId : "team:" + s.TeamId;
        var form = new ShareForm { OkText = "Next" };
        form.Add(new ShareFormField { Key = "target", Label = "Send to", Kind = ShareFieldKind.Choice, Value = choices.Any(c => c.Item1 == current) ? current : choices[0].Item1, Choices = choices });
        form.Add(new ShareFormField { Key = "msg", Label = "Message", Kind = ShareFieldKind.MultilineText, Value = s.Message });
        if (!await ui.ShowFormAsync("Share to Teams", form).ConfigureAwait(false)) return false;
        var target = form.Get<string>("target") ?? choices[0].Item1;
        s.Message = form.Get<string>("msg") ?? "";
        if (target.StartsWith("chat:", StringComparison.Ordinal)) { s.Kind = "chat"; s.ChatId = target[5..]; }
        else
        {
            s.Kind = "channel";
            s.TeamId = target[5..];
            var channels = await api.ListAsync($"/teams/{Uri.EscapeDataString(s.TeamId)}/channels?$select=id,displayName", ct).ConfigureAwait(false);
            if (channels.Count == 0) { await ui.ShowMessageAsync("Teams", "This team has no channels you can see.", true).ConfigureAwait(false); return false; }
            var f2 = new ShareForm { OkText = "Share" };
            f2.Add(new ShareFormField
            {
                Key = "channel", Label = "Channel", Kind = ShareFieldKind.Choice,
                Value = channels.Any(c => c.Str("id") == s.ChannelId) ? s.ChannelId : channels[0].Str("id"),
                Choices = channels.Select(c => (c.Str("id")!, c.Str("displayName") ?? "channel")).ToList(),
            });
            if (!await ui.ShowFormAsync("Teams channel", f2).ConfigureAwait(false)) return false;
            s.ChannelId = f2.Get<string>("channel") ?? channels[0].Str("id")!;
        }
        SaveSettings(s);
        _target = s;
        return true;
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = _target ?? LoadSettings<TeamsSettings>();
        var api = Api();
        var uploaded = new List<(DriveItemRef Item, string Url)>();
        string messagePath;
        if (s.Kind == "chat")
        {
            if (string.IsNullOrEmpty(s.ChatId)) throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Choose a Teams chat first.");
            messagePath = $"/chats/{Uri.EscapeDataString(s.ChatId)}/messages";
            for (var i = 0; i < files.Count; i++)
            {
                var item = await api.UploadAsync("/me/drive", null, NameOf(files[i]), files[i].Path, Slice(progress, i, files.Count), ct, "Microsoft Teams Chat Files").ConfigureAwait(false);
                var url = await api.CreateLinkAsync("/me/drive", item.Id, "organization", ct).ConfigureAwait(false) ?? item.WebUrl
                          ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Teams did not return a link for the uploaded file.");
                uploaded.Add((item, url));
            }
        }
        else
        {
            if (string.IsNullOrEmpty(s.TeamId) || string.IsNullOrEmpty(s.ChannelId)) throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Choose a Teams channel first.");
            messagePath = $"/teams/{Uri.EscapeDataString(s.TeamId)}/channels/{Uri.EscapeDataString(s.ChannelId)}/messages";
            var folder = await api.GetAsync($"/teams/{Uri.EscapeDataString(s.TeamId)}/channels/{Uri.EscapeDataString(s.ChannelId)}/filesFolder", ct).ConfigureAwait(false);
            var folderId = folder.Str("id") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "The channel has no files folder.");
            var driveId = folder.Obj("parentReference")?.Str("driveId") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "The channel files drive is unknown.");
            for (var i = 0; i < files.Count; i++)
            {
                var item = await api.UploadAsync($"/drives/{Uri.EscapeDataString(driveId)}", folderId, NameOf(files[i]), files[i].Path, Slice(progress, i, files.Count), ct).ConfigureAwait(false);
                uploaded.Add((item, item.WebUrl ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Teams did not return a link for the uploaded file.")));
            }
        }

        var attachments = new List<object>();
        var html = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(s.Message)) html.Append("<p>").Append(WebUtility.HtmlEncode(s.Message).Replace("\n", "<br>")).Append("</p>");
        foreach (var (item, url) in uploaded)
        {
            var id = item.ETagGuid ?? Guid.NewGuid().ToString();
            attachments.Add(new Dictionary<string, object> { ["id"] = id, ["contentType"] = "reference", ["contentUrl"] = url, ["name"] = item.Name });
            html.Append("<attachment id=\"").Append(id).Append("\"></attachment>");
        }
        var posted = await api.PostAsync(messagePath, new Dictionary<string, object>
        {
            ["body"] = new Dictionary<string, object> { ["contentType"] = "html", ["content"] = html.ToString() },
            ["attachments"] = attachments,
        }, ct).ConfigureAwait(false);
        var link = posted.Str("webUrl") ?? uploaded[0].Url;
        return new ShareResult(ShareOutcome.Succeeded, "Posted to Teams.", link, uploaded[0].Url, uploaded.Select(u => u.Url).ToList(), OpenLinkAction(link));
    }
}
