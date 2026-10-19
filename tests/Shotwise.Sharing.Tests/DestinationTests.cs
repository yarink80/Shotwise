using System.Text.Json;
using MimeKit;
using Shotwise.Core;
using Shotwise.Core.Library;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Destinations;
using Shotwise.Sharing.Destinations.Graph;
using Shotwise.Sharing.Office;

namespace Shotwise.Sharing.Tests;

public class FileDestinationTests
{
    [Fact]
    public async Task CopiesWithAutomaticNamesIntoFolderAndRemembersIt()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(Path.Combine(dir.Path, "s.json"));
        settings.Update(s => { s.Share.Naming.Prefix = "Cap_"; s.Share.Naming.Pattern = "{counter}"; s.Share.Naming.CounterDigits = 2; });
        var ctx = Ctx.Make(dir, settings: settings);
        var target = Path.Combine(dir.Path, "out");
        ctx.Store.Save("file", new FileDestinationSettings { Folder = target, NameMode = FileNameMode.Automatic });
        var a = dir.File("tmp/a.png", "A");
        var b = dir.File("tmp/b.png", "B");
        var dest = new FileDestination(ctx);

        Assert.True(await dest.PrepareAsync(new FakeShareUi(), new[] { Ctx.PngFile(a), Ctx.PngFile(b) }, default));
        var result = await dest.ShareAsync(new[] { Ctx.PngFile(a), Ctx.PngFile(b) }, null, default);

        Assert.Equal(ShareOutcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "Cap_01.png", "Cap_02.png" }, Directory.GetFiles(target).Select(x => Path.GetFileName(x)).OrderBy(x => x).ToArray());
        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "Cap_01.png")));
        Assert.Equal(target, settings.Current.Share.SaveAs.LastFolder);
        Assert.NotNull(result.OpenDestination);
        Assert.Equal(2, result.Locations!.Count);
    }

    [Fact]
    public async Task FixedAndOriginalNamesNeverOverwriteUnlessAsked()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        var target = Path.Combine(dir.Path, "out");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "fixed.png"), "old");
        ctx.Store.Save("file", new FileDestinationSettings { Folder = target, NameMode = FileNameMode.Fixed, FixedName = "fixed" });
        var src = dir.File("tmp/x.png", "new");
        var dest = new FileDestination(ctx);
        await dest.ShareAsync(new[] { Ctx.PngFile(src) }, null, default);
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "fixed.png")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "fixed (2).png")));

        ctx.Store.Save("file", new FileDestinationSettings { Folder = target, NameMode = FileNameMode.Fixed, FixedName = "fixed", OverwriteExisting = true });
        await new FileDestination(ctx).ShareAsync(new[] { Ctx.PngFile(src) }, null, default);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "fixed.png")));
    }

    [Fact]
    public async Task DefaultFolderFollowsTheDataRootNotTheRealDocumentsFolder()
    {
        // Regression: with no folder configured the File destination saved into the real Documents\Shotwise even when
        // SHOTWISE_DATA_ROOT redirected every other path (smoke runs left PNGs in the user's profile).
        using var dir = new TempDir();
        var settings = new SettingsService(Path.Combine(dir.Path, "s.json"));
        var ctx = Ctx.Make(dir, settings: settings);
        var dest = new FileDestination(ctx);
        var folder = dest.ResolveFolder(new FileDestinationSettings());
        Assert.Equal(ctx.Paths.DefaultSaveFolder, folder);
        Assert.StartsWith(Path.GetFullPath(dir.Path), folder);

        var src = dir.File("tmp/a.png", "A");
        var r = await dest.ShareAsync(new[] { Ctx.PngFile(src) }, null, default);
        Assert.Equal(ShareOutcome.Succeeded, r.Outcome);
        Assert.All(r.Locations!, p => Assert.StartsWith(Path.GetFullPath(dir.Path), p));
    }

    [Fact]
    public async Task PromptEachTimeShowsFormAndDeclineCancels()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        ctx.Store.Save("file", new FileDestinationSettings { PromptEachTime = true });
        var dest = new FileDestination(ctx);
        Assert.False(await dest.PrepareAsync(new FakeShareUi { OnForm = _ => false }, new[] { Ctx.PngFile("a.png") }, default));
        var target = Path.Combine(dir.Path, "chosen");
        var ui = new FakeShareUi { OnForm = f => { f.Fields.First(x => x.Key == "folder").Value = target; return true; } };
        Assert.True(await dest.PrepareAsync(ui, new[] { Ctx.PngFile("a.png") }, default));
        var src = dir.File("a.png");
        var r = await dest.ShareAsync(new[] { Ctx.PngFile(src) }, null, default);
        Assert.Equal(ShareOutcome.Succeeded, r.Outcome);
        Assert.True(Directory.Exists(target));
    }
}

public class ClipboardDestinationTests
{
    [Fact]
    public async Task SingleImageGoesAsImageWithPreferencesAndMultipleAsFileDropList()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(Path.Combine(dir.Path, "s.json"));
        settings.Update(s => { s.Editor.PreventClipboardTransparency = true; s.Editor.AddHtmlToClipboard = true; });
        var clip = new FakeClipboard();
        var dest = new ClipboardDestination(Ctx.Make(dir, clipboard: clip, settings: settings, codec: new FakeCodec()));
        var f = dir.File("a.png");

        var r = await dest.ShareAsync(new[] { Ctx.PngFile(f) }, null, default);
        Assert.Equal(ShareOutcome.Succeeded, r.Outcome);
        var content = Assert.Single(clip.Contents);
        Assert.True(content.PreventTransparency);
        Assert.NotNull(content.Image);
        Assert.Contains("<img", content.Html);

        await dest.ShareAsync(new[] { Ctx.PngFile(f), Ctx.PngFile(f) }, null, default);
        Assert.Single(clip.DropLists);
        Assert.Equal(2, clip.DropLists[0].Count);
    }

    [Fact]
    public async Task MissingClipboardWriterIsReported()
    {
        using var dir = new TempDir();
        var dest = new ClipboardDestination(Ctx.Make(dir));
        Assert.False(dest.IsConfigured);
        await Assert.ThrowsAsync<ShotwiseException>(() => dest.ShareAsync(new[] { Ctx.PngFile("a.png") }, null, default));
    }
}

public class EmailDestinationTests
{
    [Fact]
    public void InlineImagesUseContentIdsAndAttachmentsOtherwise()
    {
        using var dir = new TempDir();
        var png = dir.FileBytes("a.png", 20);
        var pdf = dir.FileBytes("b.pdf", 20);
        var s = new EmailDestinationSettings { To = "a@example.com; b@example.com", Cc = "c@example.com", Subject = "Look", Body = "See <this>" };
        var msg = EmailDestination.BuildMessage(s, new[] { Ctx.PngFile(png, "Shot"), new ShareFile(pdf, MediaKind.Image, "pdf", "Doc", 1, 1, null, false) }, inline: true);

        Assert.Equal("1", msg.Headers["X-Unsent"]);
        Assert.Equal(2, msg.To.Count);
        Assert.Single(msg.Cc);
        Assert.Equal("Look", msg.Subject);
        var body = Assert.IsType<Multipart>(msg.Body);
        var html = msg.HtmlBody!;
        Assert.Contains("cid:", html);
        Assert.Contains("See &lt;this&gt;", html);
        Assert.Single(msg.BodyParts.OfType<MimePart>().Where(p => p.ContentId is not null));
        Assert.Single(msg.Attachments); // the PDF is attached, not inlined
    }

    [Fact]
    public void AttachModeAttachesEverythingAndDefaultsSubject()
    {
        using var dir = new TempDir();
        var png = dir.FileBytes("a.png", 20);
        var msg = EmailDestination.BuildMessage(new EmailDestinationSettings(), new[] { Ctx.PngFile(png, "Shot title") }, inline: false);
        Assert.Equal("Shot title", msg.Subject);
        Assert.Single(msg.Attachments);
        Assert.Empty(msg.To);
        Assert.Null(msg.HtmlBody);
    }
}

public class FtpDestinationTests
{
    [Fact]
    public void RemoteNamesFollowModeAndPublicLinkIsEscaped()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        var dest = new FtpDestination(ctx);
        var f = new ShareFile(Path.Combine(dir.Path, "My Shot.PNG"), MediaKind.Image, "png", "t", 1, 1, null, false);

        Assert.Equal("My Shot.PNG", dest.RemoteName(new FtpDestinationSettings { NameMode = FtpNameMode.Original }, f, 0, 1));
        Assert.Equal("report.PNG", dest.RemoteName(new FtpDestinationSettings { NameMode = FtpNameMode.Fixed, FixedName = "report" }, f, 0, 1));
        Assert.Equal("report_2.PNG", dest.RemoteName(new FtpDestinationSettings { NameMode = FtpNameMode.Fixed, FixedName = "report" }, f, 1, 3));
        Assert.EndsWith(".PNG", dest.RemoteName(new FtpDestinationSettings(), f, 0, 1)); // automatic

        Assert.Equal("https://x.test/s/a%20b.png", FtpDestination.PublicLink(new FtpDestinationSettings { PublicUrlBase = "https://x.test/s/" }, "a b.png"));
        Assert.Null(FtpDestination.PublicLink(new FtpDestinationSettings(), "a.png"));
    }

    [Fact]
    public void DefaultPortsPerProtocolAndPasswordsLiveInCredentialManager()
    {
        Assert.Equal(21, new FtpDestinationSettings { Protocol = FtpProtocol.Ftp }.EffectivePort);
        Assert.Equal(990, new FtpDestinationSettings { Protocol = FtpProtocol.FtpsImplicit }.EffectivePort);
        Assert.Equal(22, new FtpDestinationSettings { Protocol = FtpProtocol.Sftp }.EffectivePort);
        Assert.Equal(2121, new FtpDestinationSettings { Port = 2121 }.EffectivePort);
        Assert.Equal("Shotwise:ftp:bob@host", FtpDestination.PasswordKey(new FtpDestinationSettings { UserName = "bob", Host = "host" }));
    }

    [Fact]
    public async Task OptionsFormStoresPasswordOnlyInCredentialStore()
    {
        using var dir = new TempDir();
        var creds = new InMemoryCredentialStore();
        var ctx = Ctx.Make(dir, creds);
        var dest = new FtpDestination(ctx);
        var ui = new FakeShareUi
        {
            OnForm = f =>
            {
                f.Fields.First(x => x.Key == "host").Value = "ftp.example.com";
                f.Fields.First(x => x.Key == "user").Value = "bob";
                f.Fields.First(x => x.Key == "password").Value = "hunter2";
                f.Fields.First(x => x.Key == "protocol").Value = "Sftp";
                return true;
            },
        };
        await dest.ShowOptionsAsync(ui);
        Assert.Equal("hunter2", creds.Read("Shotwise:ftp:bob@ftp.example.com"));
        var json = File.ReadAllText(ctx.Store.PathFor("ftp"));
        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("ftp.example.com", json);
        Assert.True(dest.IsConfigured);
    }
}

public class GraphTests
{
    private const string ItemJson = """{"id":"I1","name":"a.png","webUrl":"https://tenant/a.png","parentReference":{"driveId":"D1"},"eTag":"\"{6D2E5B6C-1111-2222-3333-444455556666},3\""}""";

    private static GraphApi Api(FakeHandler h) => new(h.Client(), _ => Task.FromResult("TOKEN"));

    [Fact]
    public async Task SmallFilesUseSimplePutWithBearerToken()
    {
        using var dir = new TempDir();
        var h = new FakeHandler(_ => FakeHandler.Json(ItemJson, 201));
        var item = await Api(h).UploadAsync("/me/drive", null, "a b.png", dir.File("a.png", "DATA"), null, default);

        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/root:/a%20b.png:/content?@microsoft.graph.conflictBehavior=rename", req.Url.OriginalString);
        Assert.Equal("Bearer TOKEN", req.Auth);
        Assert.Equal("DATA", req.BodyText);
        Assert.Equal("I1", item.Id);
        Assert.Equal("D1", item.DriveId);
        Assert.Equal("6d2e5b6c-1111-2222-3333-444455556666", item.ETagGuid);
    }

    [Fact]
    public async Task LargeFilesUseResumableSessionWithoutLeakingTheToken()
    {
        using var dir = new TempDir();
        var size = 11 * 1024 * 1024;
        var file = dir.FileBytes("big.mp4", size);
        var h = new FakeHandler(r =>
        {
            if (r.Url.AbsolutePath.EndsWith("createUploadSession")) return FakeHandler.Json("""{"uploadUrl":"https://upload.example/session/1"}""");
            if (r.ContentHeaders?.ContentRange?.From == 0) return FakeHandler.Json("""{"nextExpectedRanges":["10485760-"]}""", 202);
            return FakeHandler.Json(ItemJson, 201);
        });
        var reported = new List<double>();
        var item = await Api(h).UploadAsync("/drives/D1", "FOLDER", "big.mp4", file, new Progress<double>(reported.Add), default);

        Assert.Equal(3, h.Requests.Count);
        Assert.EndsWith("/drives/D1/items/FOLDER:/big.mp4:/createUploadSession", h.Requests[0].Url.AbsolutePath);
        Assert.Equal("bytes 0-10485759/11534336", h.Requests[1].ContentHeaders!.ContentRange!.ToString());
        Assert.Equal("bytes 10485760-11534335/11534336", h.Requests[2].ContentHeaders!.ContentRange!.ToString());
        Assert.Null(h.Requests[1].Auth);
        Assert.Null(h.Requests[2].Auth);
        Assert.Equal(GraphApi.ChunkSize, h.Requests[1].Body.Length);
        Assert.Equal("I1", item.Id);
    }

    [Fact]
    public async Task AnonymousLinksFallBackToOrganizationWhenPolicyForbids()
    {
        var h = new FakeHandler(r => r.BodyText.Contains("anonymous")
            ? FakeHandler.Json("""{"error":{"message":"blocked"}}""", 403)
            : FakeHandler.Json("""{"link":{"webUrl":"https://share/org"}}"""));
        var link = await Api(h).CreateLinkAsync("/me/drive", "I1", "anonymous", default);
        Assert.Equal("https://share/org", link);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task FolderListingReturnsOnlyFolders()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"value":[{"id":"1","name":"B","folder":{"childCount":2}},{"id":"2","name":"file.txt"},{"id":"3","name":"A","folder":{"childCount":0}}]}"""));
        var folders = await Api(h).ListFoldersAsync("/me/drive", null, default);
        Assert.Equal(new[] { "A", "B" }, folders.Select(f => f.Name).ToArray());
        Assert.False(folders[0].HasChildren);
        Assert.True(folders[1].HasChildren);
    }

    [Fact]
    public async Task ErrorsBecomeShotwiseExceptions()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"error":{"message":"quota"}}""", 507));
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => Api(h).UploadAsync("/me/drive", null, "a.png", dir.File("a.png"), null, default));
        Assert.Equal(ErrorCode.QuotaExceeded, ex.Code);
        Assert.DoesNotContain("quota", ex.Message);
    }

    private sealed class FakeTokens : IMicrosoftTokenProvider
    {
        public bool IsSignedIn => true;
        public string? AccountName => "me";
        public Task<string> GetTokenAsync(IReadOnlyCollection<string> scopes, IShareUi? ui, CancellationToken ct) => Task.FromResult("TOKEN");
        public Task SignInAsync(IReadOnlyCollection<string> scopes, IShareUi ui, CancellationToken ct) => Task.CompletedTask;
        public Task SignOutAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task OneDriveEndToEndUploadsAndCreatesLink()
    {
        using var dir = new TempDir();
        var h = new FakeHandler(r => r.Url.AbsolutePath.EndsWith("createLink") ? FakeHandler.Json("""{"link":{"webUrl":"https://1drv/share"}}""") : FakeHandler.Json(ItemJson, 201));
        var ctx = Ctx.Make(dir);
        var dest = new OneDriveDestination(ctx, new FakeTokens(), h.Client());
        ctx.Store.Save("onedrive", new OneDriveSettings { FolderId = "F9" });

        var result = await dest.ShareAsync(new[] { Ctx.PngFile(dir.File("a.png")) }, null, default);

        Assert.Equal("https://1drv/share", result.Link);
        Assert.Contains("/me/drive/items/F9:/a.png:/content", h.Requests[0].Url.AbsolutePath);
        Assert.Contains("\"scope\":\"organization\"", h.Requests[1].BodyText);
        Assert.True(dest.RequiresAccount);
    }

    [Fact]
    public async Task TeamsChannelUploadsToChannelFilesFolderThenPostsMessageWithAttachment()
    {
        using var dir = new TempDir();
        var h = new FakeHandler(r =>
        {
            var p = r.Url.AbsolutePath;
            if (p.EndsWith("/filesFolder")) return FakeHandler.Json("""{"id":"FF","parentReference":{"driveId":"DRV"}}""");
            if (p.EndsWith("/messages")) return FakeHandler.Json("""{"id":"M1","webUrl":"https://teams/msg"}""", 201);
            return FakeHandler.Json(ItemJson, 201);
        });
        var ctx = Ctx.Make(dir);
        ctx.Store.Save("teams", new TeamsSettings { Kind = "channel", TeamId = "T1", ChannelId = "C1", Message = "hi <all>" });
        var dest = new TeamsDestination(ctx, new FakeTokens(), h.Client());

        var result = await dest.ShareAsync(new[] { Ctx.PngFile(dir.File("a.png")) }, null, default);

        Assert.Equal("https://teams/msg", result.Link);
        Assert.Contains("/drives/DRV/items/FF:/a.png:/content", h.Requests[1].Url.AbsolutePath);
        var msg = JsonDocument.Parse(h.Requests[2].BodyText).RootElement;
        var content = msg.GetProperty("body").GetProperty("content").GetString()!;
        Assert.Contains("hi &lt;all&gt;", content);
        Assert.Contains("<attachment id=\"6d2e5b6c-1111-2222-3333-444455556666\"></attachment>", content);
        Assert.Equal("reference", msg.GetProperty("attachments")[0].GetProperty("contentType").GetString());
    }

    [Fact]
    public void SharePointSiteAddressesMapToGraphPaths()
    {
        Assert.Equal("/sites/root", SharePointDestination.SitePath(""));
        Assert.Equal("/sites/contoso.sharepoint.com", SharePointDestination.SitePath("https://contoso.sharepoint.com/"));
        Assert.Equal("/sites/contoso.sharepoint.com:/sites/team", SharePointDestination.SitePath("contoso.sharepoint.com/sites/team"));
    }
}

public class CloudRestDestinationTests
{
    [Fact]
    public async Task GoogleDriveCreatesFolderUploadsResumableAndSharesPublicly()
    {
        using var dir = new TempDir();
        var h = new FakeHandler(r =>
        {
            var u = r.Url.AbsoluteUri;
            if (r.Method == HttpMethod.Get && u.Contains("/drive/v3/files?q=")) return FakeHandler.Json("""{"files":[]}""");
            if (r.Method == HttpMethod.Post && u.EndsWith("/drive/v3/files?fields=id")) return FakeHandler.Json("""{"id":"FOLDER"}""");
            if (r.Method == HttpMethod.Post && u.Contains("uploadType=resumable"))
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                resp.Headers.Location = new Uri("https://upload.example/session/9");
                return resp;
            }
            if (r.Method == HttpMethod.Put) return FakeHandler.Json("""{"id":"FILE1","webViewLink":"https://drive.google.com/file/d/FILE1/view"}""");
            return FakeHandler.Json("{}"); // permissions
        });
        var ctx = Ctx.Make(dir);
        Ctx.SignedIn(ctx, "googledrive", "google");
        var dest = new GoogleDriveDestination(ctx, h.Client());

        Assert.True(dest.IsConfigured);
        var result = await dest.ShareAsync(new[] { Ctx.PngFile(dir.File("a.png", "PIXELS")) }, null, default);

        Assert.Equal("https://drive.google.com/file/d/FILE1/view", result.Link);
        var init = h.Requests.Single(r => r.Url.AbsoluteUri.Contains("uploadType=resumable"));
        Assert.Equal("Bearer ACCESS", init.Auth);
        Assert.Equal("image/png", init.Header("X-Upload-Content-Type"));
        Assert.Contains("\"parents\":[\"FOLDER\"]", init.BodyText);
        var put = h.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("PIXELS", put.BodyText);
        Assert.Equal("bytes 0-5/6", put.ContentHeaders!.ContentRange!.ToString());
        Assert.Contains(h.Requests, r => r.Url.AbsolutePath.EndsWith("/permissions") && r.BodyText.Contains("anyone"));
    }

    [Fact]
    public async Task YouTubeUploadsWithMetadataAndCopiesEmbedCode()
    {
        using var dir = new TempDir();
        var h = new FakeHandler(r =>
        {
            if (r.Method == HttpMethod.Post)
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                resp.Headers.Location = new Uri("https://upload.example/yt/1");
                return resp;
            }
            return FakeHandler.Json("""{"id":"VID123"}""");
        });
        var ctx = Ctx.Make(dir);
        Ctx.SignedIn(ctx, "youtube", "google");
        ctx.Store.Save("youtube", new YouTubeSettings { PromptEachTime = false, Privacy = "unlisted", ClipboardContents = "embed", Tags = "a, b" });
        var dest = new YouTubeDestination(ctx, h.Client());
        var video = new ShareFile(dir.File("v.mp4", "VIDEO"), MediaKind.Video, "mp4", "My video", 1, 1, TimeSpan.FromSeconds(3), false);

        Assert.True(await dest.PrepareAsync(new FakeShareUi(), new[] { video }, default));
        var result = await dest.ShareAsync(new[] { video }, null, default);

        Assert.Equal("https://youtu.be/VID123", result.Link);
        Assert.Contains("youtube.com/embed/VID123", result.ClipboardText);
        var meta = JsonDocument.Parse(h.Requests[0].BodyText).RootElement;
        Assert.Equal("My video", meta.GetProperty("snippet").GetProperty("title").GetString());
        Assert.Equal("unlisted", meta.GetProperty("status").GetProperty("privacyStatus").GetString());
        Assert.Equal(2, meta.GetProperty("snippet").GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public void DropboxApiArgIsAsciiOnlyAndFoldersNormalised()
    {
        var arg = DropboxDestination.ApiArg(new { path = "/Shots/café 日本.png" });
        Assert.All(arg, c => Assert.True(c < 127));
        Assert.Contains("\\u00e9", arg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/a/b", DropboxDestination.NormalizeFolder(@"\a\b\"));
        Assert.Equal("", DropboxDestination.NormalizeFolder(" / "));
    }

    [Fact]
    public async Task DropboxUploadsAndReusesExistingSharedLink()
    {
        using var dir = new TempDir();
        var h = new FakeHandler(r =>
        {
            var p = r.Url.AbsolutePath;
            if (p.EndsWith("/files/upload")) return FakeHandler.Json("""{"path_display":"/Shotwise/a.png"}""");
            if (p.EndsWith("create_shared_link_with_settings")) return FakeHandler.Json("""{"error_summary":"shared_link_already_exists/.."}""", 409);
            return FakeHandler.Json("""{"links":[{"url":"https://dropbox.com/s/abc/a.png"}]}""");
        });
        var ctx = Ctx.Make(dir);
        Ctx.SignedIn(ctx, "dropbox", "dropbox");
        var result = await new DropboxDestination(ctx, h.Client()).ShareAsync(new[] { Ctx.PngFile(dir.File("a.png")) }, null, default);
        Assert.Equal("https://dropbox.com/s/abc/a.png", result.Link);
        Assert.Contains("\"path\":\"/Shotwise/a.png\"", h.Requests[0].Header("Dropbox-API-Arg"));
    }

    [Fact]
    public async Task BoxMultipartUploadAndSharedLink()
    {
        using var dir = new TempDir();
        var h = new FakeHandler(r =>
        {
            var p = r.Url.AbsolutePath;
            if (p.EndsWith("/2.0/folders")) return FakeHandler.Json("""{"id":"SUB"}""", 201);
            if (p.EndsWith("/files/content")) return FakeHandler.Json("""{"entries":[{"id":"FILE7"}]}""", 201);
            return FakeHandler.Json("""{"shared_link":{"url":"https://app.box.com/s/xyz"}}""");
        });
        var ctx = Ctx.Make(dir);
        Ctx.SignedIn(ctx, "box", "box");
        var result = await new BoxDestination(ctx, h.Client()).ShareAsync(new[] { Ctx.PngFile(dir.File("a.png", "BOXDATA")) }, null, default);
        Assert.Equal("https://app.box.com/s/xyz", result.Link);
        var upload = h.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/files/content"));
        Assert.Contains("\"id\":\"SUB\"", upload.BodyText); // parent folder from the created sub-folder
        Assert.Contains("BOXDATA", upload.BodyText);
    }

    [Fact]
    public void ProvidersWithRegisteredRedirectUrisUseTheConfiguredLoopbackPort()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        ctx.SetClientId("dropbox", "app-key");
        var dest = new DropboxDestination(ctx);
        Assert.Null(dest.ResolveConfig()!.FixedLoopbackPort);
        ctx.SetLoopbackPort("dropbox", 53682);
        var cfg = dest.ResolveConfig()!;
        Assert.Equal(53682, cfg.FixedLoopbackPort);
        Assert.Equal("app-key", cfg.ClientId);
        Assert.Equal(53682, Ctx.Make(dir).GetLoopbackPort("dropbox")); // persisted in share\oauth.json
        ctx.SetLoopbackPort("dropbox", null);
        Assert.Null(ctx.GetLoopbackPort("dropbox"));
        Assert.Null(new BoxDestination(Ctx.Make(dir)).ResolveConfig()); // no client id yet
    }

    [Fact]
    public async Task UnconfiguredOAuthDestinationsExplainWhatToDo()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        var ui = new FakeShareUi();
        var dest = new DropboxDestination(ctx);
        Assert.False(dest.IsConfigured);
        await Assert.ThrowsAsync<ShotwiseException>(() => dest.PrepareAsync(ui, new[] { Ctx.PngFile("a.png") }, default));
        Assert.Contains("client id", Assert.Single(ui.Messages));
    }
}

public class SlackAndImgurTests
{
    [Fact]
    public async Task SlackUploadsViaExternalUploadFlow()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(null!);
        stub.Handler = r => r.Path switch
        {
            "/api/files.getUploadURLExternal" => new StubResponse(200, $$"""{"ok":true,"upload_url":"{{stub.BaseUrl}}/up/1","file_id":"F1"}"""),
            "/up/1" => new StubResponse(200, "OK"),
            "/api/files.completeUploadExternal" => new StubResponse(200, """{"ok":true,"files":[{"id":"F1","permalink":"https://acme.slack.com/files/U/F1/a.png"}]}"""),
            _ => new StubResponse(200, """{"ok":false,"error":"unexpected"}"""),
        };
        var creds = new InMemoryCredentialStore();
        creds.Write("Shotwise:slack:token", "xoxb-test");
        var ctx = Ctx.Make(dir, creds);
        ctx.Store.Save("slack", new SlackSettings { ChannelId = "C1", ChannelName = "#general", Message = "look", PromptEachTime = false });
        var dest = new SlackDestination(ctx, apiBase: stub.BaseUrl + "/api/");

        Assert.True(await dest.PrepareAsync(new FakeShareUi(), new[] { Ctx.PngFile("x.png") }, default));
        var result = await dest.ShareAsync(new[] { Ctx.PngFile(dir.File("a.png", "SLACKDATA"), "Nice") }, null, default);

        Assert.Equal("https://acme.slack.com/files/U/F1/a.png", result.Link);
        var reqs = stub.Snapshot();
        Assert.Equal("Bearer xoxb-test", reqs[0].Headers["Authorization"]);
        Assert.Contains("filename=a.png", reqs[0].BodyText);
        Assert.Contains("length=9", reqs[0].BodyText);
        Assert.Equal("SLACKDATA", reqs[1].BodyText);
        var complete = JsonDocument.Parse(reqs[2].BodyText).RootElement;
        Assert.Equal("C1", complete.GetProperty("channel_id").GetString());
        Assert.Equal("look", complete.GetProperty("initial_comment").GetString());
        Assert.Equal("Nice", complete.GetProperty("files")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task SlackSignInValidatesAndStoresTokenInCredentialStoreOnly()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(_ => new StubResponse(200, """{"ok":true,"team":"Acme"}"""));
        var creds = new InMemoryCredentialStore();
        var ctx = Ctx.Make(dir, creds);
        var dest = new SlackDestination(ctx, apiBase: stub.BaseUrl + "/api/");
        var ui = new FakeShareUi { OnForm = f => { f.Fields[0].Value = "xoxp-secret-token"; return true; } };
        await dest.SignInAsync(ui, default);
        Assert.Equal("xoxp-secret-token", creds.Read("Shotwise:slack:token"));
        Assert.DoesNotContain("xoxp", File.ReadAllText(ctx.Store.PathFor("slack")));
        Assert.True(dest.IsConfigured);
        await dest.SignOutAsync();
        Assert.False(dest.IsConfigured);

        var bad = new FakeShareUi { OnForm = f => { f.Fields[0].Value = "not-a-token"; return true; } };
        await Assert.ThrowsAsync<ShotwiseException>(() => dest.SignInAsync(bad, default));
    }

    [Fact]
    public async Task SlackApiErrorsAreReported()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(r => r.Path.EndsWith("getUploadURLExternal") ? new StubResponse(200, """{"ok":false,"error":"missing_scope","needed":"files:write"}""") : new StubResponse(200, "{}"));
        var creds = new InMemoryCredentialStore();
        creds.Write("Shotwise:slack:token", "xoxb-x");
        var ctx = Ctx.Make(dir, creds);
        ctx.Store.Save("slack", new SlackSettings { ChannelId = "C1", PromptEachTime = false });
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => new SlackDestination(ctx, apiBase: stub.BaseUrl + "/api/").ShareAsync(new[] { Ctx.PngFile(dir.File("a.png")) }, null, default));
        Assert.Contains("files:write", ex.Message);
    }

    [Fact]
    public async Task ImgurAnonymousUploadUsesClientIdHeader()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(_ => new StubResponse(200, """{"success":true,"data":{"link":"https://i.imgur.com/abc.png","deletehash":"DEL"}}"""));
        var ctx = Ctx.Make(dir);
        ctx.SetClientId("imgur", "myclient");
        var dest = new ImgurDestination(ctx, apiBase: stub.BaseUrl + "/3/");
        Assert.True(dest.IsConfigured);
        Assert.False(dest.RequiresAccount);

        var result = await dest.ShareAsync(new[] { Ctx.PngFile(dir.File("a.png", "IMG"), "Pic") }, null, default);

        Assert.Equal("https://i.imgur.com/abc.png", result.Link);
        Assert.Contains("delete:DEL", result.Locations!);
        var req = Assert.Single(stub.Snapshot());
        Assert.Equal("/3/image", req.Path);
        Assert.Equal("Client-ID myclient", req.Headers["Authorization"]);
        Assert.Contains("IMG", req.BodyText);
        Assert.Contains("Pic", req.BodyText);
    }

    [Fact]
    public async Task ImgurRequiresClientIdAndRejectsOversizedImages()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        var ui = new FakeShareUi();
        var dest = new ImgurDestination(ctx);
        Assert.False(await dest.PrepareAsync(ui, new[] { Ctx.PngFile("a.png") }, default));
        Assert.Contains("client id", Assert.Single(ui.Messages));

        ctx.SetClientId("imgur", "id");
        var big = dir.File("big.png");
        using (var fs = new FileStream(big, FileMode.Open)) fs.SetLength(ImgurDestination.MaxImageBytes + 1);
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => dest.ShareAsync(new[] { Ctx.PngFile(big) }, null, default));
        Assert.Equal(ErrorCode.FileTooLarge, ex.Code);
    }
}

public class OfficeAndMiscTests
{
    [Fact]
    public void OneNoteFindsFreeSpaceBelowExistingOutlines()
    {
        const string xml = """
        <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="{x}">
          <one:Outline><one:Position x="36" y="100" z="0"/><one:Size width="300" height="150"/></one:Outline>
          <one:Outline><one:Position x="36" y="400" z="1"/><one:Size width="300" height="50"/></one:Outline>
        </one:Page>
        """;
        Assert.Equal(474, OneNoteDestination.NextFreeY(xml));
        Assert.Equal(36, OneNoteDestination.NextFreeY("<broken"));
    }

    [Fact]
    public async Task OfficeStaThreadRunsOnStaAndPropagatesExceptions()
    {
        var sta = new Shotwise.Sharing.Office.OfficeStaThread();
        try
        {
            var state = await sta.RunAsync(() => Thread.CurrentThread.GetApartmentState());
            Assert.Equal(ApartmentState.STA, state);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sta.RunAsync<int>(() => throw new InvalidOperationException("x")));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sta.RunAsync(() => 1, cts.Token));
        }
        finally { sta.Dispose(); }
    }

    [Fact]
    public void ReportsOfficeAsUnconfiguredWhenProgIdIsMissing()
        => Assert.False(OfficeCom.IsRegistered("Shotwise.DefinitelyNotInstalled.Application"));

    [Fact]
    public void DestinationSettingsRoundTripAndSurviveCorruptFiles()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        ctx.Store.Save("word", new WordSettings { Link = true, Position = OfficeInsertPosition.EndOfDocument });
        var back = ctx.Store.Load<WordSettings>("word");
        Assert.True(back.Link);
        Assert.Equal(OfficeInsertPosition.EndOfDocument, back.Position);
        File.WriteAllText(ctx.Store.PathFor("word"), "{ corrupt");
        Assert.False(ctx.Store.Load<WordSettings>("word").Link);
        Assert.EndsWith(Path.Combine("Roaming", "share", "weird_id_.json"), ctx.Store.PathFor("weird id?"));
    }

    [Fact]
    public void AllDestinationsHaveUniqueIdsAndSensibleMetadata()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        var all = new IShareDestination[]
        {
            new FileDestination(ctx), new ClipboardDestination(ctx), new PrintDestination(ctx), new EmailDestination(ctx), new FtpDestination(ctx),
            new ProgramDestination(ctx), new WordDestination(ctx), new ExcelDestination(ctx), new PowerPointDestination(ctx), new OneNoteDestination(ctx),
            new OneDriveDestination(ctx), new SharePointDestination(ctx), new TeamsDestination(ctx), new GoogleDriveDestination(ctx), new YouTubeDestination(ctx),
            new DropboxDestination(ctx), new BoxDestination(ctx), new SlackDestination(ctx), new ImgurDestination(ctx), new S3Destination(ctx),
            new CustomHttpDestination(ctx), new XDestination(ctx), new EvernoteDestination(ctx),
        };
        Assert.Equal(all.Length, all.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
            Assert.NotEmpty(d.SupportedFormats);
            Assert.All(d.SupportedFormats, f => Assert.Contains(f, Shotwise.Sharing.Infrastructure.FormatKeys.Everything));
        });
    }
}
