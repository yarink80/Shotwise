using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Destinations;
using Shotwise.Sharing.Destinations.Graph;
using Shotwise.Sharing.Infrastructure;
using Shotwise.Sharing.Office;

namespace Shotwise.Sharing.Tests;

public class CapabilityFilteringTests
{
    private static ShareFile F(string ext, MediaKind kind = MediaKind.Image, bool step = false) => new("x." + ext, kind, ext, "t", 1, 1, null, step);

    private static ShareService Svc(TempDir dir, IEnumerable<IShareDestination> d, IPolicyService? policy = null, ISettingsService? settings = null)
        => new(d, new AppPaths(dir.Path), settings, policy);

    [Fact]
    public void FiltersByFormatMultiplicityAndStepCapture()
    {
        using var dir = new TempDir();
        var png = new FakeDestination("png", "png", "jpg");
        var video = new FakeDestination("video", "mp4");
        var single = new FakeDestination("single", "png") { SupportsMultiple = false };
        var steps = new FakeDestination("steps", "png") { SupportsStepCapture = true };
        var svc = Svc(dir, new IShareDestination[] { png, video, single, steps });

        Assert.Equal(new[] { "png", "single", "steps" }, svc.For(new[] { F("png") }).Select(d => d.Id).ToArray());
        Assert.Equal(new[] { "png" }, svc.For(new[] { F("png"), F("jpg") }).Select(d => d.Id).ToArray());
        Assert.DoesNotContain(svc.For(new[] { F("png"), F("png") }), d => d.Id == "single");
        Assert.Equal(new[] { "video" }, svc.For(new[] { F("mp4", MediaKind.Video) }).Select(d => d.Id).ToArray());
        Assert.Equal(new[] { "steps" }, svc.For(new[] { F("png", step: true) }).Select(d => d.Id).ToArray());
        Assert.Empty(svc.For(Array.Empty<ShareFile>()));
    }

    [Fact]
    public void AnimatedGifIsADifferentCapabilityThanStaticGif()
    {
        using var dir = new TempDir();
        var staticOnly = new FakeDestination("static", "gif");
        var animated = new FakeDestination("anim", "agif");
        var svc = Svc(dir, new IShareDestination[] { staticOnly, animated });
        Assert.Equal(new[] { "static" }, svc.For(new[] { F("gif") }).Select(d => d.Id).ToArray());
        Assert.Equal(new[] { "anim" }, svc.For(new[] { F("gif", MediaKind.Gif) }).Select(d => d.Id).ToArray());
        Assert.Equal("jpg", FormatKeys.Normalize("JPEG"));
        Assert.Equal("tiff", FormatKeys.Normalize(".tif"));
    }

    [Fact]
    public void EnablementIsStoredInSettingsAndHidesDestinations()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(Path.Combine(dir.Path, "s.json"));
        var svc = Svc(dir, new IShareDestination[] { new FakeDestination("a", "png"), new FakeDestination("b", "png") }, settings: settings);
        Assert.True(svc.IsEnabled("a"));
        svc.SetEnabled("a", false);
        Assert.False(svc.IsEnabled("a"));
        Assert.Contains("a", settings.Current.Share.DisabledDestinations);
        Assert.Equal(new[] { "b" }, svc.For(new[] { F("png") }).Select(d => d.Id).ToArray());
        Assert.Equal(2, svc.Destinations.Count); // still listed for the preferences page
        svc.SetEnabled("a", true);
        Assert.Empty(settings.Current.Share.DisabledDestinations);
    }

    [Fact]
    public void EnablementWorksWithoutSettingsService()
    {
        using var dir = new TempDir();
        var svc = Svc(dir, new IShareDestination[] { new FakeDestination("a", "png") });
        svc.SetEnabled("A", false);
        Assert.False(svc.IsEnabled("a"));
        Assert.Empty(svc.For(new[] { F("png") }));
    }

    [Fact]
    public void PolicyHidesGatedDestinations()
    {
        using var dir = new TempDir();
        var policy = new FakePolicy();
        var ftp = new FakeDestination("ftp", "png") { RequiredPolicies = new[] { PolicyFeature.Ftp } };
        var cloud = new FakeDestination("cloud", "png") { RequiredPolicies = new[] { PolicyFeature.CloudSharing }, Category = DestinationCategory.Storage };
        var local = new FakeDestination("local", "png");
        var svc = Svc(dir, new IShareDestination[] { ftp, cloud, local }, policy);
        Assert.Equal(3, svc.For(new[] { F("png") }).Count());

        policy.Denied.Add(PolicyFeature.Ftp);
        Assert.Equal(new[] { "cloud", "local" }, svc.For(new[] { F("png") }).Select(d => d.Id).ToArray());
        policy.Denied.Add(PolicyFeature.CloudSharing);
        Assert.Equal(new[] { "local" }, svc.For(new[] { F("png") }).Select(d => d.Id).ToArray());
        policy.Denied.Add(PolicyFeature.Outputs);
        Assert.Empty(svc.For(new[] { F("png") }));
    }

    [Fact]
    public void BuiltInDestinationsDeclareExpectedPolicyAndCapabilities()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        Assert.Contains(PolicyFeature.Ftp, new FtpDestination(ctx).RequiredPolicies);
        Assert.Empty(new FileDestination(ctx).RequiredPolicies);
        Assert.Contains(PolicyFeature.CloudSharing, new S3Destination(ctx).RequiredPolicies);
        Assert.Empty(new WordDestination(ctx).RequiredPolicies);
        Assert.True(new PowerPointDestination(ctx).SupportsStepCapture);
        Assert.Contains("mp4", new PowerPointDestination(ctx).SupportedFormats);
        Assert.DoesNotContain("mp4", new WordDestination(ctx).SupportedFormats);
        Assert.DoesNotContain("gif", new SharePointDestination(ctx).SupportedFormats);
        Assert.Equal(new[] { "mp4" }, new YouTubeDestination(ctx).SupportedFormats.ToArray());
        Assert.False(new YouTubeDestination(ctx).SupportsMultiple);
    }

    [Fact]
    public void EvernoteIsListedButNeverOffered()
    {
        using var dir = new TempDir();
        var ev = new EvernoteDestination(Ctx.Make(dir));
        var svc = Svc(dir, new IShareDestination[] { ev });
        Assert.False(svc.IsEnabled("evernote"));
        Assert.Empty(svc.For(new[] { F("png") }));
        Assert.Contains("Email", ev.UnavailableReason);
    }
}

public class ShareFlowTests
{
    private static (ShareService Svc, FakeLibrary Lib, FakeClipboard Clip, FakeNotifications Toast, SettingsService Settings) Make(TempDir dir, params IShareDestination[] d)
    {
        var lib = new FakeLibrary();
        var clip = new FakeClipboard();
        var toast = new FakeNotifications();
        var settings = new SettingsService(Path.Combine(dir.Path, "settings.json"));
        return (new ShareService(d, new AppPaths(dir.Path), settings, null, lib, toast, clip), lib, clip, toast, settings);
    }

    [Fact]
    public async Task SuccessWritesHistoryCopiesLinkAndShowsToast()
    {
        using var dir = new TempDir();
        var file = dir.File("a.png");
        var dest = new FakeDestination("up", "png") { OnShare = (_, _) => Task.FromResult(ShareResult.Success("https://example.com/a")) };
        var (svc, lib, clip, toast, _) = Make(dir, dest);
        var item = FakeLibrary.Item(file);
        lib.ByPath[file] = item;

        var result = await svc.ShareAsync("up", new[] { Ctx.PngFile(file, "Cap") }, new FakeShareUi(), null, CancellationToken.None);

        Assert.Equal(ShareOutcome.Succeeded, result.Outcome);
        var (id, entry) = Assert.Single(lib.History);
        Assert.Equal(item.Id, id);
        Assert.Equal("up", entry.DestinationId);
        Assert.Equal("UP", entry.DestinationName);
        Assert.Equal("https://example.com/a", entry.Link);
        Assert.Equal("Cap", entry.Title);
        Assert.Equal(new[] { "https://example.com/a" }, clip.Texts);
        var shown = Assert.Single(toast.Shown);
        Assert.Equal(NotificationKind.Success, shown.Kind);
        Assert.Contains(shown.Actions!, a => a.Label == "Open link");
    }

    [Fact]
    public async Task LinkCopyCanBeTurnedOffAndFilesNotInLibraryAreSkipped()
    {
        using var dir = new TempDir();
        var file = dir.File("a.png");
        var dest = new FakeDestination("up", "png") { OnShare = (_, _) => Task.FromResult(ShareResult.Success("https://example.com/a")) };
        var (svc, lib, clip, _, settings) = Make(dir, dest);
        settings.Update(s => s.Share.CopyLinkToClipboard = false);
        await svc.ShareAsync("up", new[] { Ctx.PngFile(file) }, new FakeShareUi(), null, CancellationToken.None);
        Assert.Empty(clip.Texts);
        Assert.Empty(lib.History);
    }

    [Fact]
    public async Task ClipboardTextOverridesLinkAndToastsCanBeDisabled()
    {
        using var dir = new TempDir();
        var file = dir.File("a.png");
        var dest = new FakeDestination("up", "png") { OnShare = (_, _) => Task.FromResult(new ShareResult(ShareOutcome.Succeeded, "done", "https://l", "EMBED")) };
        var (svc, _, clip, toast, settings) = Make(dir, dest);
        settings.Update(s => s.General.ShowToasts = false);
        await svc.ShareAsync("up", new[] { Ctx.PngFile(file) }, new FakeShareUi(), null, CancellationToken.None);
        Assert.Equal(new[] { "EMBED" }, clip.Texts);
        Assert.Empty(toast.Shown);
    }

    [Fact]
    public async Task OnlyOneShareRunsAtATime()
    {
        using var dir = new TempDir();
        var file = dir.File("a.png");
        var gate = new TaskCompletionSource<ShareResult>();
        var slow = new FakeDestination("slow", "png") { OnShare = (_, _) => gate.Task };
        var fast = new FakeDestination("fast", "png");
        var (svc, _, _, toast, _) = Make(dir, slow, fast);
        var ui = new FakeShareUi();

        var first = svc.ShareAsync("slow", new[] { Ctx.PngFile(file) }, ui, null, CancellationToken.None);
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(svc.IsShareRunning);

        var second = await svc.ShareAsync("fast", new[] { Ctx.PngFile(file) }, ui, null, CancellationToken.None);
        Assert.Equal(ShareOutcome.Failed, second.Outcome);
        Assert.Contains("already", second.Message);
        Assert.Equal(0, fast.ShareCalls);

        gate.SetResult(ShareResult.Success());
        Assert.Equal(ShareOutcome.Succeeded, (await first).Outcome);
        Assert.False(svc.IsShareRunning);
        Assert.Equal(ShareOutcome.Succeeded, (await svc.ShareAsync("fast", new[] { Ctx.PngFile(file) }, ui, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ErrorCases()
    {
        using var dir = new TempDir();
        var file = dir.File("a.png");
        var ok = new FakeDestination("ok", "png");
        var multiOff = new FakeDestination("single", "png") { SupportsMultiple = false };
        var (svc, _, _, toast, settings) = Make(dir, ok, multiOff);
        var ui = new FakeShareUi();
        var f = Ctx.PngFile(file);

        Assert.Equal(ShareOutcome.Failed, (await svc.ShareAsync("ok", Array.Empty<ShareFile>(), ui, null, default)).Outcome);
        Assert.Contains("Unknown", (await svc.ShareAsync("nope", new[] { f }, ui, null, default)).Message);
        Assert.Contains("one file", (await svc.ShareAsync("single", new[] { f, f }, ui, null, default)).Message);
        Assert.Contains("not found", (await svc.ShareAsync("ok", new[] { Ctx.PngFile(Path.Combine(dir.Path, "missing.png")) }, ui, null, default)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not accept", (await svc.ShareAsync("ok", new[] { f with { Format = "mp4" } }, ui, null, default)).Message);
        settings.Update(s => s.Share.DisabledDestinations.Add("ok"));
        Assert.Contains("turned off", (await svc.ShareAsync("ok", new[] { f }, ui, null, default)).Message);
    }

    [Fact]
    public async Task PolicyBlocksSharingAtRunTime()
    {
        using var dir = new TempDir();
        var file = dir.File("a.png");
        var policy = new FakePolicy();
        policy.Denied.Add(PolicyFeature.Ftp);
        var dest = new FakeDestination("ftp", "png") { RequiredPolicies = new[] { PolicyFeature.Ftp } };
        var svc = new ShareService(new IShareDestination[] { dest }, new AppPaths(dir.Path), null, policy);
        var r = await svc.ShareAsync("ftp", new[] { Ctx.PngFile(file) }, new FakeShareUi(), null, default);
        Assert.Equal(ShareOutcome.Failed, r.Outcome);
        Assert.Contains("policy", r.Message);
        Assert.Equal(0, dest.ShareCalls);
    }

    [Fact]
    public async Task PrepareDeclineCancelsExceptionsBecomeFailuresAndCancellationIsCancelled()
    {
        using var dir = new TempDir();
        var file = dir.File("a.png");
        var declined = new FakeDestination("declined", "png") { PrepareResult = false };
        var boom = new FakeDestination("boom", "png") { OnShare = (_, _) => throw new InvalidOperationException("kaput") };
        var cancels = new FakeDestination("cancels", "png") { OnShare = (_, _) => throw new OperationCanceledException() };
        var (svc, lib, clip, toast, _) = Make(dir, declined, boom, cancels);
        var ui = new FakeShareUi();
        var f = Ctx.PngFile(file);

        Assert.Equal(ShareOutcome.Cancelled, (await svc.ShareAsync("declined", new[] { f }, ui, null, default)).Outcome);
        Assert.Equal(0, declined.ShareCalls);

        var failed = await svc.ShareAsync("boom", new[] { f }, ui, null, default);
        Assert.Equal(ShareOutcome.Failed, failed.Outcome);
        Assert.Equal("kaput", failed.Message);
        Assert.Equal(NotificationKind.Error, Assert.Single(toast.Shown).Kind);
        Assert.Empty(lib.History);

        Assert.Equal(ShareOutcome.Cancelled, (await svc.ShareAsync("cancels", new[] { f }, ui, null, default)).Outcome);
        Assert.False(svc.IsShareRunning);
    }

    [Fact]
    public async Task WorksWithoutOptionalServices()
    {
        using var dir = new TempDir();
        var file = dir.File("a.png");
        var dest = new FakeDestination("up", "png") { OnShare = (_, _) => Task.FromResult(ShareResult.Success("https://x")) };
        var svc = new ShareService(new IShareDestination[] { dest }, new AppPaths(dir.Path));
        Assert.Equal(ShareOutcome.Succeeded, (await svc.ShareAsync("up", new[] { Ctx.PngFile(file) }, new FakeShareUi(), null, default)).Outcome);
    }
}
