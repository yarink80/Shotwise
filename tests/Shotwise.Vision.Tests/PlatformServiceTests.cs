using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Vision;

namespace Shotwise.Vision.Tests;

public class ModelManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shotwise-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls;
        public byte[] Payload = Array.Empty<byte>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        }
    }

    private static ModelInfo Info(byte[] payload, string? sha = null)
        => new("test", "Test", "unit", new Uri("https://example.invalid/test.onnx"), sha ?? Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), payload.Length, "MIT");

    private ModelManager Make(FakeHandler h, ModelInfo info)
        => new(_dir, new[] { info }, new HttpClient(h), NullLogger<ModelManager>.Instance);

    [Fact]
    public void EmbeddedManifest_IsWellFormed()
    {
        var models = ModelManager.LoadEmbeddedManifest();
        Assert.Contains(models, m => m.Id == ModelIds.YuNet);
        Assert.Contains(models, m => m.Id == ModelIds.Lama);
        Assert.All(models, m =>
        {
            Assert.Equal(64, m.Sha256.Length);
            Assert.Equal("https", m.Url.Scheme);
            Assert.True(m.SizeBytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(m.License));
        });
    }

    [Fact]
    public async Task Ensure_DownloadsVerifiesAndReportsProgress()
    {
        var payload = RandomNumberGenerator.GetBytes(300_000);
        var h = new FakeHandler { Payload = payload };
        var mm = Make(h, Info(payload));
        Assert.False(mm.IsAvailable("test"));
        Assert.Equal(0, h.Calls);                                   // IsAvailable never downloads

        double last = 0;
        var path = await mm.EnsureAsync("test", new Progress<double>(v => last = v), default);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.True(mm.IsAvailable("test"));
        Assert.Equal(1, h.Calls);

        await mm.EnsureAsync("test", null, default);                // already present: no second download
        Assert.Equal(1, h.Calls);
        await Task.Delay(50);
        Assert.True(last > 0);
    }

    [Fact]
    public async Task Ensure_RejectsCorruptDownload_AndLeavesNothingBehind()
    {
        var payload = RandomNumberGenerator.GetBytes(10_000);
        var h = new FakeHandler { Payload = payload };
        var mm = Make(h, Info(payload, sha: new string('0', 64)));
        await Assert.ThrowsAsync<Shotwise.Core.ShotwiseException>(() => mm.EnsureAsync("test", null, default));
        Assert.False(mm.IsAvailable("test"));
        Assert.False(File.Exists(mm.GetPath("test")));
        Assert.Empty(Directory.GetFiles(_dir));                     // no .partial left
    }

    [Fact]
    public async Task TamperedFile_IsNotAvailableAndIsReplaced()
    {
        var payload = RandomNumberGenerator.GetBytes(50_000);
        var h = new FakeHandler { Payload = payload };
        var mm = Make(h, Info(payload));
        var path = await mm.EnsureAsync("test", null, default);
        var tampered = (byte[])payload.Clone();
        tampered[10] ^= 0xFF;
        await File.WriteAllBytesAsync(path, tampered);              // same size, new mtime -> verification marker invalid
        Assert.False(mm.IsAvailable("test"));
        await mm.EnsureAsync("test", null, default);
        Assert.Equal(2, h.Calls);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SideLoadedUnknownModel_IsHonoured()
    {
        var mm = Make(new FakeHandler(), Info(new byte[] { 1 }));
        Assert.False(mm.IsAvailable(ModelIds.NoiseSuppressor));
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(mm.GetPath(ModelIds.NoiseSuppressor), new byte[] { 1, 2, 3 });
        Assert.True(mm.IsAvailable(ModelIds.NoiseSuppressor));
        Assert.Equal(mm.GetPath(ModelIds.NoiseSuppressor), await mm.EnsureAsync(ModelIds.NoiseSuppressor, null, default));
    }

    [Fact]
    public async Task UnknownModelWithoutFile_Throws_AndBadIdsAreRejected()
    {
        var mm = Make(new FakeHandler(), Info(new byte[] { 1 }));
        await Assert.ThrowsAsync<Shotwise.Core.ShotwiseException>(() => mm.EnsureAsync("nope", null, default));
        Assert.False(mm.IsAvailable("..\\evil"));
        await Assert.ThrowsAsync<ArgumentException>(() => mm.EnsureAsync("..\\evil", null, default));
    }

    [Fact]
    public async Task Cancellation_StopsDownload()
    {
        var payload = new byte[1000];
        var mm = Make(new FakeHandler { Payload = payload }, Info(payload));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mm.EnsureAsync("test", null, cts.Token));
    }
}

public class OptionalModelServiceTests
{
    private sealed class NoModels : IModelManager
    {
        public IReadOnlyList<ModelInfo> Models => Array.Empty<ModelInfo>();
        public bool IsAvailable(string modelId) => false;
        public Task<string> EnsureAsync(string modelId, IProgress<double>? p, CancellationToken ct) => throw new InvalidOperationException("no implicit download allowed");
    }

    [Fact]
    public async Task FaceDetector_WithoutModel_ReturnsEmpty()
    {
        var det = new FaceDetector(new NoModels(), NullLogger<FaceDetector>.Instance);
        using var m = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(200));
        Assert.Empty(await det.DetectAsync(TestImages.ToImage(m), default));
    }

    [Fact]
    public void NoiseSuppressor_WithoutModel_IsUnavailableAndTransparent()
    {
        var ns = new NoiseSuppressor(new NoModels(), NullLogger<NoiseSuppressor>.Instance);
        Assert.False(ns.IsAvailable);
        using var s = ns.CreateStream(48000, 2);
        var buf = Enumerable.Range(0, 960).Select(i => (float)Math.Sin(i * 0.1)).ToArray();
        var copy = (float[])buf.Clone();
        s.Process(buf);
        Assert.Equal(copy, buf);
    }

    [Fact]
    public void Resampler_ProducesExpectedLengthAndContinuity()
    {
        var up = new NoiseSuppressor.Resampler(16000, 48000);
        var outp = new List<float>();
        var input = Enumerable.Range(0, 160).Select(i => (float)i).ToArray();
        up.Process(input.AsSpan(0, 80), outp);
        up.Process(input.AsSpan(80), outp);
        Assert.InRange(outp.Count, 470, 480);
        for (var i = 1; i < outp.Count; i++) Assert.True(outp[i] >= outp[i - 1] - 1e-4f, "ramp must stay monotonic across block boundaries");
    }

    [Fact]
    public async Task SmartRedact_FacesOnly_UsesDetectorAndSkipsOcr()
    {
        var ocr = new ThrowingOcr();
        var faces = new FixedFaces(new Rect2(10, 10, 30, 30));
        var svc = new SmartRedactService(ocr, faces, NullLogger<SmartRedactService>.Instance);
        using var m = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(200));
        var hits = await svc.FindAsync(TestImages.ToImage(m), new HashSet<RedactCategory> { RedactCategory.Face }, default);
        var h = Assert.Single(hits);
        Assert.Equal(RedactCategory.Face, h.Category);
    }

    [Fact]
    public async Task SmartRedact_TextCategories_UseOcrOutput()
    {
        var word = new OcrWord("a@b.org", new Rect2(5, 5, 70, 12), 1);
        var ocr = new FixedOcr(new OcrResult("en", "a@b.org", new[] { new OcrLine("a@b.org", word.Bounds, new[] { word }) }, 0));
        var svc = new SmartRedactService(ocr, new FixedFaces(), NullLogger<SmartRedactService>.Instance);
        using var m = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(200));
        var hits = await svc.FindAsync(TestImages.ToImage(m), new HashSet<RedactCategory> { RedactCategory.Email, RedactCategory.Phone }, default);
        var h = Assert.Single(hits);
        Assert.Equal(RedactCategory.Email, h.Category);
        Assert.Equal("a@b.org", h.Text);
    }

    private sealed class ThrowingOcr : IOcrService
    {
        public IReadOnlyList<string> AvailableLanguages => Array.Empty<string>();
        public string EngineName => "throwing";
        public Task<OcrResult> RecognizeAsync(BgraImage img, Rect2? region = null, string? language = null, CancellationToken ct = default)
            => throw new InvalidOperationException("OCR must not run for face-only redaction");
    }

    private sealed class FixedOcr : IOcrService
    {
        private readonly OcrResult _r;
        public FixedOcr(OcrResult r) => _r = r;
        public IReadOnlyList<string> AvailableLanguages => new[] { "en" };
        public string EngineName => "fixed";
        public Task<OcrResult> RecognizeAsync(BgraImage img, Rect2? region = null, string? language = null, CancellationToken ct = default) => Task.FromResult(_r);
    }

    private sealed class FixedFaces : IFaceDetector
    {
        private readonly Rect2[] _f;
        public FixedFaces(params Rect2[] f) => _f = f;
        public Task<IReadOnlyList<Rect2>> DetectAsync(BgraImage img, CancellationToken ct) => Task.FromResult<IReadOnlyList<Rect2>>(_f);
    }
}

public class DependencyInjectionTests
{
    [Fact]
    public void AddShotwiseVision_RegistersEveryContract()
    {
        var services = new ServiceCollection();
        services.AddShotwiseVision();
        foreach (var t in new[]
        {
            typeof(IModelManager), typeof(IOcrService), typeof(ITableExtractor), typeof(ITextEditService), typeof(IInpaintingService),
            typeof(ISmartMoveService), typeof(ISimplifyService), typeof(ISmartRedactService), typeof(IFaceDetector), typeof(IMagicWandService),
            typeof(Shotwise.Core.Capture.IStitcher), typeof(INoiseSuppressor)
        })
            Assert.Contains(services, d => d.ServiceType == t);
    }
}
