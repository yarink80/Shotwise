using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Shotwise.Core.Vision;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;

namespace Shotwise.Vision.Tests;

/// <summary>
/// Opt-in tests that really download models (set SHOTWISE_NET_TESTS=1; LaMa is ~208 MB so it additionally needs SHOTWISE_NET_TESTS_LAMA=1).
/// They pass vacuously otherwise.
/// </summary>
public class NetworkModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shotwise-net-models");

    public void Dispose() { /* keep the download cache between runs; it lives in %TEMP% */ }

    private ModelManager Manager() => new(_dir, ModelManager.LoadEmbeddedManifest(), new HttpClient { Timeout = TimeSpan.FromMinutes(20) }, NullLogger<ModelManager>.Instance);

    private static bool On(string name) => Environment.GetEnvironmentVariable(name) == "1";

    [Fact]
    public async Task YuNet_DownloadsAndRuns()
    {
        if (!On("SHOTWISE_NET_TESTS")) return;
        var mm = Manager();
        var path = await mm.EnsureAsync(ModelIds.YuNet, null, default);
        Assert.True(File.Exists(path));
        Assert.True(mm.IsAvailable(ModelIds.YuNet));
        var det = new FaceDetector(mm, NullLogger<FaceDetector>.Instance);
        using var m = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(180));
        var faces = await det.DetectAsync(TestImages.ToImage(m), default);   // no face in a flat image, but the network must run
        Assert.Empty(faces);
        using var m2 = new Mat(300, 900, MatType.CV_8UC3, Scalar.All(120));   // different size re-creates the detector
        Assert.Empty(await det.DetectAsync(TestImages.ToImage(m2), default));
    }

    [Fact]
    public async Task Lama_DownloadsAndInpaints()
    {
        if (!On("SHOTWISE_NET_TESTS_LAMA")) return;
        var mm = Manager();
        await mm.EnsureAsync(ModelIds.Lama, null, default);
        using var svc = new Shotwise.Vision.Inpainting.InpaintingService(mm, NullLogger<Shotwise.Vision.Inpainting.InpaintingService>.Instance);
        Assert.Contains(InpaintQuality.HighQuality, svc.Available);

        using var clean = new Mat(300, 400, MatType.CV_8UC3);
        for (var y = 0; y < 300; y++) for (var x = 0; x < 400; x++) clean.Set(y, x, new Vec3b((byte)(100 + x / 4), (byte)(120 + y / 3), 200));
        var dirty = clean.Clone();
        Cv2.Rectangle(dirty, new Rect(150, 110, 90, 60), TestImages.Bgr(0, 0, 0), -1);
        var mask = Mask.FromRect(400, 300, new Shotwise.Core.Geometry.RectI(150, 110, 90, 60));
        var result = await svc.InpaintAsync(TestImages.ToImage(dirty), mask, InpaintQuality.HighQuality, default);
        var err = TestImages.MeanAbsDiff(TestImages.ToImage(clean), result);
        var errDirty = TestImages.MeanAbsDiff(TestImages.ToImage(clean), TestImages.ToImage(dirty));
        Console.WriteLine($"LaMa error {err:F2} vs dirty {errDirty:F2}");
        Assert.True(err < errDirty / 4, $"LaMa result error {err:F2} vs {errDirty:F2}");
    }
}
