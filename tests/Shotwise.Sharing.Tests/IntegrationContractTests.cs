using System.Text.Json;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Tests;

/// <summary>P3 integration contracts: A15 (secret key scheme + loopback ports), A18 (ShareFile.LibraryItemId), A24 (per-binding options).</summary>
public class IntegrationContractTests
{
    [Fact]
    public void ClientSecretKeySchemeIsSharedWithTheAccountsUi()
    {
        using var dir = new TempDir();
        var creds = new InMemoryCredentialStore();
        var ctx = Ctx.Make(dir, creds);

        Assert.Equal("Shotwise:google:client-secret", ShareCredentialKeys.ClientSecret("google"));

        // The Accounts page (UI.Dialogs) writes through ICredentialStore with the Core key; Sharing must read it.
        creds.Write(ShareCredentialKeys.ClientSecret("google"), "from-ui");
        Assert.Equal("from-ui", ctx.GetClientSecret("google"));

        // Sharing writes the same key (and removes the pre-integration "client_secret" spelling).
        creds.Write(CredentialKeys.Make("box", "client_secret"), "legacy");
        Assert.Equal("legacy", ctx.GetClientSecret("box")); // legacy fallback
        ctx.SetClientSecret("box", "new");
        Assert.Equal("new", creds.Read("Shotwise:box:client-secret"));
        Assert.Null(creds.Read(CredentialKeys.Make("box", "client_secret")));
        ctx.SetClientSecret("box", null);
        Assert.Null(ctx.GetClientSecret("box"));
    }

    [Fact]
    public void LoopbackPortsComeFromCoreSettings()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(Path.Combine(dir.Path, "s.json"));
        var ctx = Ctx.Make(dir, settings: settings);

        Assert.Null(ctx.GetLoopbackPort("dropbox"));
        settings.Update(s => s.Share.LoopbackPorts["dropbox"] = ShareCredentialKeys.DefaultLoopbackPort); // what the Accounts page does
        Assert.Equal(53682, ctx.GetLoopbackPort("dropbox"));

        ctx.SetLoopbackPort("box", 50123);
        Assert.Equal(50123, settings.Current.Share.LoopbackPorts["box"]);
        ctx.SetLoopbackPort("box", null);
        Assert.False(settings.Current.Share.LoopbackPorts.ContainsKey("box"));
        Assert.Contains("dropbox", ShareCredentialKeys.FixedPortProviders);
        Assert.DoesNotContain("google", ShareCredentialKeys.FixedPortProviders);
    }

    private sealed class ProbeSettings
    {
        public string Folder { get; set; } = "default";
        public int Quality { get; set; } = 1;
    }

    [Fact]
    public void OverlayAppliesPresetOptionsOnlyInsideTheScope()
    {
        using var dir = new TempDir();
        var store = new DestinationSettingsStore(Path.Combine(dir.Path, "share"));
        store.Save("file", new ProbeSettings { Folder = "saved", Quality = 5 });

        using (DestinationSettingsStore.Overlay("file", JsonDocument.Parse("{\"Folder\":\"preset\",\"Quality\":9}").RootElement))
        {
            var s = store.Load<ProbeSettings>("file");
            Assert.Equal("saved", s.Folder);   // routing cannot be changed by presets
            Assert.Equal(9, s.Quality);         // presentation is allowlisted
            Assert.Equal("default", store.Load<ProbeSettings>("other").Folder); // other destinations unaffected
        }
        Assert.Equal("saved", store.Load<ProbeSettings>("file").Folder);
    }

    [Fact]
    public async Task ShareServicePassesBindingOptionsAndPrefersLibraryItemId()
    {
        using var dir = new TempDir();
        var store = new DestinationSettingsStore(Path.Combine(dir.Path, "share"));
        var library = new FakeLibrary();
        string? seenFolder = null;
        var dest = new FakeDestination("probe", "png")
        {
            OnShare = (_, _) => { seenFolder = store.Load<ProbeSettings>("probe").Folder; return Task.FromResult(ShareResult.Success("https://x/1")); },
        };
        var svc = new ShareService(new IShareDestination[] { dest }, new AppPaths(dir.Path), library: library);
        var file = dir.File("a.png");
        var itemId = Guid.NewGuid();
        var share = Ctx.PngFile(file) with { LibraryItemId = itemId };

        var r = await svc.ShareAsync("probe", new[] { share }, new FakeShareUi(), JsonDocument.Parse("{\"folder\":\"from-preset\"}").RootElement, null, CancellationToken.None);

        Assert.Equal(ShareOutcome.Succeeded, r.Outcome);
        Assert.Equal("default", seenFolder);
        Assert.Equal(itemId, Assert.Single(library.History).Id); // no FindByPath needed for temp-materialized files
        Assert.Equal("default", store.Load<ProbeSettings>("probe").Folder);
    }
}
