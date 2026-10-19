using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Platform;
using Xunit;
namespace Shotwise.Platform.Tests;

/// <summary>Installer (since 0.9.0): install-layout detection and the uninstaller's "only remove our own shortcut" rule (temp folders only).</summary>
public sealed class InstallIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shotwise-tests", "install-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string Layout(bool portable)
    {
        var root = Path.Combine(_dir, portable ? "portable" : "installed");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(root, "Update.exe"), "");
        File.WriteAllText(Path.Combine(current, "sq.version"), "");
        if (portable) File.WriteAllText(Path.Combine(root, ".portable"), "");
        var exe = Path.Combine(current, "Shotwise.exe");
        File.WriteAllText(exe, "");
        return exe;
    }

    [Fact]
    public void Detect_DistinguishesInstalledPortableAndDevelopment()
    {
        var installed = Layout(portable: false);
        var portable = Layout(portable: true);
        Assert.Equal(InstallKind.Installed, InstallLocation.Detect(installed));
        Assert.Equal(InstallKind.Portable, InstallLocation.Detect(portable));
        Assert.Equal(InstallKind.Development, InstallLocation.Detect(Environment.ProcessPath));
        Assert.Equal(Path.Combine(_dir, "installed"), InstallLocation.RootOf(installed));
        Assert.Null(InstallLocation.RootOf(Environment.ProcessPath!));
    }

    [Fact]
    public void IsUnder_RequiresAPathBoundary()
    {
        Assert.True(InstallLocation.IsUnder(@"C:\Users\a\AppData\Local\Shotwise.App\current\Shotwise.exe", @"C:\Users\a\AppData\Local\Shotwise.App"));
        Assert.False(InstallLocation.IsUnder(@"C:\Users\a\AppData\Local\Shotwise.AppX\Shotwise.exe", @"C:\Users\a\AppData\Local\Shotwise.App"));
        Assert.False(InstallLocation.IsUnder(@"C:\Users\a\AppData\Local\Shotwise\library.db", @"C:\Users\a\AppData\Local\Shotwise.App"));
    }

    [Fact]
    public void RemoveOwnedLink_DeletesOnlyLinksIntoTheInstallRootOrDangling()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        var log = NullLogger<StartMenuShortcut>.Instance;

        var foreign = Path.Combine(_dir, "foreign.lnk");          // points at another existing exe (a dev build): kept
        new StartMenuShortcut(foreign, log).Ensure(Environment.ProcessPath!);
        Assert.StartsWith("kept", InstallIntegration.RemoveOwnedLink(foreign, root));
        Assert.True(File.Exists(foreign));

        var ownedExe = Path.Combine(root, "current", "Shotwise.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ownedExe)!);
        File.WriteAllText(ownedExe, "");
        var owned = Path.Combine(_dir, "owned.lnk");
        new StartMenuShortcut(owned, log).Ensure(ownedExe);
        Assert.StartsWith("removed", InstallIntegration.RemoveOwnedLink(owned, root));
        Assert.False(File.Exists(owned));

        var dangling = Path.Combine(_dir, "dangling.lnk");
        new StartMenuShortcut(dangling, log).Ensure(Path.Combine(_dir, "gone", "Shotwise.exe"));
        Assert.StartsWith("removed", InstallIntegration.RemoveOwnedLink(dangling, root));
        Assert.Equal("not present", InstallIntegration.RemoveOwnedLink(dangling, root));
    }

    [Fact]
    public void DesktopShortcut_CarriesTheAumid()
    {
        var lnk = Path.Combine(_dir, "Desktop", "Shotwise.lnk");
        var sut = new DesktopShortcut(lnk, NullLogger<StartMenuShortcut>.Instance);
        Assert.False(sut.Exists);
        Assert.True(sut.Ensure(Environment.ProcessPath!));
        Assert.True(sut.Exists);
        Assert.Equal(AppIdentity.AppUserModelId, StartMenuShortcut.Read(lnk)!.AppUserModelId);
        sut.Remove();
        Assert.False(sut.Exists);
    }
}
