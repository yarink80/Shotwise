using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Platform;
using Xunit;
namespace Shotwise.Platform.Tests;

/// <summary>POLISH-1: the Start menu shortcut is written through IShellLinkW and carries the Shotwise AUMID (tested in a temp folder).</summary>
public sealed class StartMenuShortcutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shotwise-tests", "lnk-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void Ensure_WritesTargetIconAndAumid_ThenIsCurrent()
    {
        var lnk = Path.Combine(_dir, "Shotwise.lnk");
        var exe = Environment.ProcessPath!;
        var sut = new StartMenuShortcut(lnk, NullLogger<StartMenuShortcut>.Instance);
        Assert.False(sut.IsCurrent(exe));
        Assert.True(sut.Ensure(exe));
        var info = StartMenuShortcut.Read(lnk);
        Assert.NotNull(info);
        Assert.Equal(exe, info!.Target, ignoreCase: true);
        Assert.Equal(exe, info.IconPath, ignoreCase: true);
        Assert.Equal(Path.GetDirectoryName(exe), info.WorkingDirectory, ignoreCase: true);
        Assert.Equal(AppIdentity.AppUserModelId, info.AppUserModelId);
        Assert.Equal(AppIdentity.Description, info.Description);
        Assert.True(sut.IsCurrent(exe));
        Assert.False(sut.Ensure(exe));          // already current: not rewritten
        Assert.True(sut.Ensure(exe, force: true));
    }

    [Fact]
    public void Ensure_RewritesAStaleTarget_AndRemoveDeletes()
    {
        var lnk = Path.Combine(_dir, "Shotwise.lnk");
        var sut = new StartMenuShortcut(lnk, NullLogger<StartMenuShortcut>.Instance);
        var other = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        sut.Ensure(other);
        Assert.False(sut.IsCurrent(Environment.ProcessPath!));
        Assert.True(sut.Ensure(Environment.ProcessPath!));
        Assert.True(sut.IsCurrent(Environment.ProcessPath!));
        sut.Remove();
        Assert.False(File.Exists(lnk));
        Assert.Null(StartMenuShortcut.Read(lnk));
        sut.Remove();                            // no-op when missing
    }

    [Fact]
    public void DefaultPath_IsTheUsersStartMenuProgramsFolder()
        => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "Shotwise.lnk"),
            StartMenuShortcut.DefaultPath, ignoreCase: true);
}
