using System.Net;
using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Destinations;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Tests;

public class SecurityRegressionTests
{
    [Fact]
    public void BareProgramNamesNeverResolveFromCurrentDirectory()
    {
        var name = Guid.NewGuid().ToString("N") + ".exe";
        var path = Path.Combine(Environment.CurrentDirectory, name);
        try
        {
            File.WriteAllText(path, "not executable");
            Assert.Throws<ShotwiseException>(() => ProgramDestination.ResolveExecutable(name));
            Assert.Equal(path, ProgramDestination.ResolveExecutable(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DestinationOptionsExposeNonemptyFormsAndCancelWithoutSaving()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        IShareDestination[] destinations =
        {
            new FtpDestination(ctx), new S3Destination(ctx), new CustomHttpDestination(ctx),
            new ProgramDestination(ctx), new EmailDestination(ctx),
            new Shotwise.Sharing.Office.WordDestination(ctx), new Shotwise.Sharing.Office.ExcelDestination(ctx),
            new Shotwise.Sharing.Office.PowerPointDestination(ctx), new Shotwise.Sharing.Office.OneNoteDestination(ctx),
            new GoogleDriveDestination(ctx), new YouTubeDestination(ctx), new DropboxDestination(ctx), new BoxDestination(ctx),
            new Shotwise.Sharing.Destinations.Graph.TeamsDestination(ctx),
        };
        foreach (var dest in destinations)
        {
            var calls = 0;
            var ui = new FakeShareUi { OnForm = f => { calls++; Assert.NotEmpty(f.Fields); return false; } };
            Assert.True(dest.CanShowOptions);
            await dest.ShowOptionsAsync(ui);
            Assert.Equal(1, calls);
            Assert.False(File.Exists(ctx.Store.PathFor(dest.Id)));
        }
    }

    [Fact]
    public async Task CustomUploaderNeverFollowsCrossOriginRedirectWithSecretHeaders()
    {
        using var dir = new TempDir();
        using var target = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        using var source = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        target.Start(); source.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await source.AcceptTcpClientAsync();
            using var stream = connection.GetStream();
            var buffer = new byte[16384];
            await stream.ReadAsync(buffer);
            var response = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{targetPort}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
        });
        var dest = new CustomHttpDestination(Ctx.Make(dir)) { UploadTimeout = TimeSpan.FromSeconds(3) };
        dest.Import($$$"""{"name":"redirect","requestUrl":"http://127.0.0.1:{{{sourcePort}}}/upload","headers":{"X-Api-Key":"{secret:token}"}}""");
        dest.SetSecret("redirect", "token", "private");
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => dest.ShareAsync(new[] { Ctx.PngFile(dir.File("a.png")) }, null, default));
        Assert.Contains("307", ex.Message);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(target.Pending());
    }

    [Fact]
    public async Task CustomUploaderDeadlineCancelsStalledResponse()
    {
        using var dir = new TempDir();
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var dest = new CustomHttpDestination(Ctx.Make(dir)) { UploadTimeout = TimeSpan.FromMilliseconds(200) };
        dest.Import($$"""{"name":"stall","requestUrl":"http://127.0.0.1:{{port}}/upload"}""");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dest.ShareAsync(new[] { Ctx.PngFile(dir.File("a.png")) }, null, default));
    }

    [Fact]
    public async Task ServiceRunsPerShareFolderPromptAndHonorsCancellation()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        ctx.Store.Save("file", new FileDestinationSettings { PromptEachTime = true });
        var svc = new ShareService(new[] { new FileDestination(ctx) }, ctx.Paths);
        var prompts = 0;
        var ui = new FakeShareUi { OnForm = _ => { prompts++; return false; } };
        var files = new[] { Ctx.PngFile(dir.File("a.png")) };
        Assert.Equal(ShareOutcome.Cancelled, (await svc.ShareAsync("file", files, ui, null, default)).Outcome);
        Assert.Equal(ShareOutcome.Cancelled, (await svc.ShareAsync("file", files, ui, null, default)).Outcome);
        Assert.Equal(2, prompts);
    }

    [Fact]
    public void PresetsCannotReplaceProgramsHostsOrTrustSettings()
    {
        using var dir = new TempDir();
        var store = Ctx.Make(dir).Store;
        store.Save("ftp", new FtpDestinationSettings { Host = "trusted" });
        using var json = JsonDocument.Parse("""{"host":"evil","directory":"//evil","hostFingerprints":{"evil:22":"x"},"fixedName":"picture"}""");
        using (DestinationSettingsStore.Overlay("ftp", json.RootElement))
        {
            var s = store.Load<FtpDestinationSettings>("ftp");
            Assert.Equal("trusted", s.Host);
            Assert.Equal("/", s.Directory);
            Assert.Empty(s.HostFingerprints);
            Assert.Equal("picture", s.FixedName);
        }
        using (DestinationSettingsStore.Overlay("program", JsonDocument.Parse("""{"programs":[{"executable":"evil.exe"}]}""").RootElement))
            Assert.Empty(store.Load<ProgramDestinationSettings>("program").Programs);
    }

    [Fact]
    public void DefinitionImportsRefuseCollisionAndDiscardSuppliedIdentity()
    {
        using var dir = new TempDir();
        var dest = new CustomHttpDestination(Ctx.Make(dir));
        var supplied = Guid.NewGuid();
        var json = $$"""{"id":"{{supplied}}","name":"same","requestUrl":"https://example.com"}""";
        dest.Import(json);
        var first = Assert.Single(dest.Definitions()).Id;
        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(supplied, first);
        dest.SetSecret("same", "token", "private");
        Assert.Throws<IOException>(() => dest.Import(json));
        Assert.Equal(first, Assert.Single(dest.Definitions()).Id);
        dest.Remove("same");
        dest.Import(json);
        Assert.NotEqual(first, Assert.Single(dest.Definitions()).Id);
    }

    [Fact]
    public async Task SftpRequiresExplicitVerificationAndPinsPerHost()
    {
        using var dir = new TempDir();
        var dest = new FtpDestination(Ctx.Make(dir));
        var s = new FtpDestinationSettings { Host = "one", Protocol = FtpProtocol.Sftp };
        var ui = new FakeShareUi { OnForm = _ => false };
        await dest.PrepareAsync(ui, Array.Empty<ShareFile>(), default);
        Assert.False(await dest.TrustHostAsync(s, "fingerprint"));
        ui.OnForm = f => { Assert.Contains("SHA256:fingerprint", f.Description); f.Fields.Single().Value = true; return true; };
        Assert.True(await dest.TrustHostAsync(s, "fingerprint"));
        ui.OnForm = _ => throw new Exception("Known hosts must not prompt");
        Assert.True(await dest.TrustHostAsync(s, "fingerprint"));
        Assert.False(await dest.TrustHostAsync(s, "changed"));
        s.Host = "two";
        ui.OnForm = _ => false;
        Assert.False(await dest.TrustHostAsync(s, "fingerprint"));
    }

    [Fact]
    public async Task ResponseBodiesAreBoundedAndErrorsContainNoServerSecrets()
    {
        using var content = new StringContent(new string('x', 4 * 1024 * 1024 + 1));
        await Assert.ThrowsAsync<ShotwiseException>(() => HttpHelpers.ReadBoundedAsync(content, default));
        Assert.DoesNotContain("secret", HttpHelpers.ToException(HttpStatusCode.BadRequest, "secret").Message);
    }

    [Theory]
    [InlineData("./evil.exe")]
    [InlineData("subdir/evil.exe")]
    [InlineData("C:evil.exe")]
    public void AmbiguousExecutablePathsAreRejected(string path)
        => Assert.Throws<ShotwiseException>(() => ProgramDestination.ResolveExecutable(path));
}
