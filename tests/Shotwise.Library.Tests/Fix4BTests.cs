using Shotwise.Core;
using Shotwise.Core.Library;

namespace Shotwise.Library.Tests;

public class Fix4BTests
{
    [Fact]
    public async Task MovePreservesEqualLengthEqualTimestampDifferentContent()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var source = Path.Combine(h.Root, "same.png");
        var original = Shotwise.Core.Imaging.PngCodec.Encode(Samples.Image(8, 8));
        await File.WriteAllBytesAsync(source, original);
        var targetRoot = Path.Combine(h.Base, "target");
        Directory.CreateDirectory(targetRoot);
        var target = Path.Combine(targetRoot, "same.png");
        var other = original.ToArray(); other[^1] ^= 1;
        await File.WriteAllBytesAsync(target, other);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        await h.Service.MoveLibraryAsync(targetRoot, null, CancellationToken.None);
        Assert.Equal(other, File.ReadAllBytes(target));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(targetRoot, "same (moved 2).png")));
    }

    [Fact]
    public async Task RestorePreservesEqualLengthDifferentContentAndHonorsBudget()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var item = await h.Service.AddCaptureAsync(Samples.Doc(), Samples.Image(8, 8));
        var archive = Path.Combine(h.Base, "backup.shotarchive");
        await h.Service.BackupAsync(archive, null, CancellationToken.None);
        var original = File.ReadAllBytes(item.Path);
        var other = original.ToArray(); other[^1] ^= 1;
        File.WriteAllBytes(item.Path, other);
        h.Service.MaxRestoreBytes = 1;
        await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(archive, null, CancellationToken.None));
        h.Service.MaxRestoreBytes = 50L * 1024 * 1024 * 1024;
        await h.Service.RestoreAsync(archive, null, CancellationToken.None);
        Assert.Equal(other, File.ReadAllBytes(item.Path));
        var restored = Directory.GetFiles(h.Root, "*restored*", SearchOption.AllDirectories).Single();
        Assert.Equal(original, File.ReadAllBytes(restored));
    }
}
