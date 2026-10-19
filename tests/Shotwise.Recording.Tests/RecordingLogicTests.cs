using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Recording;
using Xunit;

namespace Shotwise.Recording.Tests;

public sealed class RecordingLogicTests
{
    [Theory]
    [InlineData(499, 2)] [InlineData(500, 1)] [InlineData(2047, 1)] [InlineData(2048, 0)]
    public void DiskGuardHonorsBothThresholds(long mebibytes, int expected)
        => Assert.Equal(expected, (int)RecordingDiskGuard.Evaluate(mebibytes * 1024 * 1024));

    [Theory]
    [InlineData(3840, 2160, true, 1920, 1080)]
    [InlineData(1080, 1920, true, 606, 1080)]
    [InlineData(1919, 1079, true, 1918, 1078)]
    [InlineData(3841, 2161, false, 3840, 2160)]
    [InlineData(2, 2, true, 2, 2)]
    public void DimensionsKeepAspectWithinBoundsAndAreEven(int w, int h, bool scale, int expectedW, int expectedH)
        => Assert.Equal(new SizeI(expectedW, expectedH), RecordingDimensions.Fit(w, h, scale));

    [Theory]
    [InlineData(5)] [InlineData(10)] [InlineData(15)] [InlineData(30)] [InlineData(60)]
    public void GovernorDoesNotDriftOverOneMinute(int fps)
    {
        var governor = new FrameGovernor(fps, 1000);
        var count = 0;
        for (var now = 0; now < 60000; now++) if (governor.ShouldCapture(now)) count++;
        Assert.Equal(fps * 60, count);
    }
    [Fact]
    public void GovernorDropsMissedSlotsWithoutBursting()
    {
        var governor = new FrameGovernor(30, 3000);
        Assert.True(governor.ShouldCapture(10000));
        Assert.False(governor.ShouldCapture(10000));
        Assert.True(governor.ShouldCapture(10950));
        Assert.False(governor.ShouldCapture(10951));
        Assert.True(governor.ShouldCapture(11000));
    }
    [Fact]
    public void MultiplePausesAreRemovedAndPausedElapsedIsFrozen()
    {
        var clock = new RecordingTimeline(1000); clock.Start(10000);
        clock.Pause(12000); clock.Pause(13000);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.At(15000));
        clock.Resume(17000); clock.Resume(18000);
        Assert.Equal(TimeSpan.FromSeconds(3), clock.At(18000));
        clock.Pause(19000); clock.Resume(21000);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.At(22000));
    }
    [Fact]
    public void AudioGapsAreSilenceAndSourcesAlignByFrame()
    {
        var mic = new AudioTimelineQueue(); var system = new AudioTimelineQueue();
        mic.Enqueue(2, new float[] { .1f, .2f, .3f, .4f });
        system.Enqueue(3, new float[] { .5f, .6f });
        var output = new float[10]; mic.Mix(0, output, false); system.Mix(0, output, false);
        Assert.Equal(new float[] { 0, 0, 0, 0, .1f, .2f, .8f, 1, 0, 0 }, output);
        var late = new float[4]; mic.Mix(5, late, false); Assert.All(late, sample => Assert.Equal(0, sample));
    }
    [Fact]
    public void PartialBlocksSurviveAndMutedBlocksDoNotReplay()
    {
        var queue = new AudioTimelineQueue(); queue.Enqueue(0, new float[] { 1, 2, 3, 4, 5, 6 });
        var first = new float[2]; queue.Mix(0, first, false); Assert.Equal(new float[] { 1, 2 }, first);
        var second = new float[2]; queue.Mix(1, second, true); Assert.Equal(new float[] { 0, 0 }, second);
        var third = new float[2]; queue.Mix(2, third, false); Assert.Equal(new float[] { 5, 6 }, third);
    }
    [Fact]
    public void Nv12HasLimitedRangeBlackAndNeutralChroma()
    {
        var bytes = Mp4Writer.ToNv12(new BgraImage(2, 2));
        Assert.Equal(new byte[] { 16, 16, 16, 16, 128, 128 }, bytes);
    }
    [Fact]
    public async Task SoftwareEncoderWritesSyntheticVideoAndAudioToMp4()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Shotwise-encoder-test-{Guid.NewGuid():N}.mp4");
        try
        {
            // No desktop/audio devices are accessed. MF remains on a single owned thread.
            await Task.Factory.StartNew(() =>
            {
                using var writer = new Mp4Writer(path, new SizeI(64, 48), new RecordingOptions(Fps: 10, HardwareEncoding: false, RecordMicrophone: false));
                Assert.Equal("SoftwareNv12", writer.ActivePath);
                for (var i = 0; i < 10; i++)
                {
                    var image = new BgraImage(64, 48);
                    for (var p = 0; p < image.Pixels.Length; p += 4) { image.Pixels[p + 2] = (byte)(i * 25); image.Pixels[p + 3] = 255; }
                    writer.Video(image, TimeSpan.FromMilliseconds(i * 100), TimeSpan.FromMilliseconds(100));
                    writer.Audio(new float[9600], i * 4800);
                }
                writer.Finish();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var data = await File.ReadAllBytesAsync(path);
            Assert.True(data.Length > 1024);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(data, 4, 4));
            Assert.Contains("moov", System.Text.Encoding.ASCII.GetString(data));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task Direct2DComposesSyntheticFrameAndReadbackPreservesColor()
    {
        await Task.Factory.StartNew(() =>
        {
            using var canvas = new Direct2DOverlayCanvas(new SizeI(64, 48));
            canvas.Begin(new BgraImage(64, 48));
            canvas.DrawRectangle(new Rect2(0, 0, 32, 48), null, 0, Argb.Red);
            canvas.DrawLine(new Point2(40, 5), new Point2(60, 40), Argb.White, 2);
            canvas.DrawText("A", new Point2(35, 1), "Segoe UI", 12, Argb.White);
            var result = canvas.End(); var p = (20 * 64 + 10) * 4;
            Assert.Equal(Argb.Red.R, result.Pixels[p + 2]); Assert.Equal(Argb.Red.B, result.Pixels[p]);
            canvas.Begin(result);
            using var texture = canvas.EndTexture();
            canvas.Begin(texture);
            var gpuResult = canvas.End();
            Assert.Equal(result.Pixels[p + 2], gpuResult.Pixels[p + 2]);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    /// <summary>Reads back a finalized MP4 through the MF source reader (compressed samples; no decoder).</summary>
    internal static class Mp4Probe
    {
        public sealed record Result(int Width, int Height, int VideoFrames, TimeSpan VideoEnd, bool HasAudio);
        public static Result Read(string path) => Task.Factory.StartNew(() =>
        {
            Vortice.MediaFoundation.MediaFactory.MFStartup();
            try
            {
                using var reader = Vortice.MediaFoundation.MediaFactory.MFCreateSourceReaderFromURL(path, null);
                ulong size;
                using (var native = reader.GetNativeMediaType(Vortice.MediaFoundation.SourceReaderIndex.FirstVideoStream, 0))
                    size = native.GetUInt64(Vortice.MediaFoundation.MediaTypeAttributeKeys.FrameSize);
                var audio = true;
                try { reader.GetNativeMediaType(Vortice.MediaFoundation.SourceReaderIndex.FirstAudioStream, 0).Dispose(); }
                catch (SharpGen.Runtime.SharpGenException) { audio = false; }
                var frames = 0; var end = 0L;
                while (true)
                {
                    using var sample = reader.ReadSample(Vortice.MediaFoundation.SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out var time);
                    if ((flags & Vortice.MediaFoundation.SourceReaderFlag.EndOfStream) != 0) break;
                    if (sample != null) { frames++; end = Math.Max(end, time + sample.SampleDuration); }
                }
                return new Result((int)(size >> 32), (int)(uint)size, frames, TimeSpan.FromTicks(end), audio);
            }
            finally { Vortice.MediaFoundation.MediaFactory.MFShutdown(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
    }
    private static void WriteRedFrames(Direct2DOverlayCanvas canvas, Mp4Writer writer, int count, Action<int>? after = null)
    {
        for (var i = 0; i < count; i++)
        {
            canvas.Begin(new BgraImage(canvas.FrameSize.Width, canvas.FrameSize.Height));
            canvas.DrawRectangle(new Rect2(0, 0, canvas.FrameSize.Width / 2, canvas.FrameSize.Height), null, 0, Argb.Red);
            using var frame = canvas.EndTexture();
            writer.Video(frame, TimeSpan.FromMilliseconds(i * 100), TimeSpan.FromMilliseconds(100));
            after?.Invoke(i);
        }
    }
    [GpuFact]
    public async Task DxgiSurfaceEncoderAcceptsSyntheticFrames()
    {
        // 256x144 is above the minimum frame size of current hardware H.264 MFTs (NVENC: 145x49).
        var path = Path.Combine(Path.GetTempPath(), $"Shotwise-gpu-test-{Guid.NewGuid():N}.mp4");
        try
        {
            await Task.Factory.StartNew(() =>
            {
                using var canvas = new Direct2DOverlayCanvas(new SizeI(256, 144));
                var log = new WarningLog();
                using var writer = new Mp4Writer(path, canvas.FrameSize, new RecordingOptions(Fps: 10, RecordMicrophone: false, RecordSystemAudio: false), canvas.Device, log);
                Assert.True(writer.GpuInput);
                Assert.Equal("HardwareDxgi", writer.ActivePath);
                WriteRedFrames(canvas, writer, 10);
                Assert.Equal("HardwareDxgi", writer.ActivePath);
                writer.Finish();
                Assert.Equal("HardwareDxgi", writer.ActivePath);
                Assert.Equal(0, log.Warnings);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(new FileInfo(path).Length > 1024);
            var probe = Mp4Probe.Read(path);
            Assert.Equal((256, 144, 10), (probe.Width, probe.Height, probe.VideoFrames));
            Assert.Equal(TimeSpan.FromSeconds(1), probe.VideoEnd);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task UndersizedDxgiFramesSurviveAsynchronousHardwareEncoderFailure()
    {
        // Below the hardware minimum MF may select an encoder that accepts the media types and then
        // fails a later WriteSample with MF_E_UNEXPECTED. The writer must re-encode everything in software.
        var path = Path.Combine(Path.GetTempPath(), $"Shotwise-small-gpu-test-{Guid.NewGuid():N}.mp4");
        try
        {
            await Task.Factory.StartNew(() =>
            {
                using var canvas = new Direct2DOverlayCanvas(new SizeI(64, 48));
                var log = new WarningLog();
                using var writer = new Mp4Writer(path, canvas.FrameSize, new RecordingOptions(Fps: 10, RecordMicrophone: false, RecordSystemAudio: false), canvas.Device, log);
                WriteRedFrames(canvas, writer, 10);
                writer.Finish();
                Assert.InRange(log.Warnings, 0, 1);
                Assert.Equal(log.Warnings == 1 ? "SoftwareNv12" : "HardwareDxgi", writer.ActivePath);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var probe = Mp4Probe.Read(path);
            Assert.Equal((64, 48, 10), (probe.Width, probe.Height, probe.VideoFrames));
            Assert.Equal(TimeSpan.FromSeconds(1), probe.VideoEnd);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [GpuTheory]
    [InlineData("Sample", 6)]
    [InlineData("Finalize", 1)]
    public async Task HardwareFailureAfterAcceptedFramesReencodesEverythingInSoftware(string failureStage, int failOnCall)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Shotwise-late-fallback-{Guid.NewGuid():N}.mp4");
        try
        {
            await Task.Factory.StartNew(() =>
            {
                using var canvas = new Direct2DOverlayCanvas(new SizeI(256, 144));
                var log = new WarningLog(); var calls = 0; var failures = 0;
                using var writer = new Mp4Writer(path, canvas.FrameSize,
                    new RecordingOptions(Fps: 10, RecordMicrophone: true, RecordSystemAudio: false), canvas.Device, log,
                    stage =>
                    {
                        if (stage != failureStage || ++calls != failOnCall) return;
                        failures++;
                        throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(unchecked((int)0xC00D36BB)));
                    });
                Assert.Equal("HardwareDxgi", writer.ActivePath);
                WriteRedFrames(canvas, writer, 10, i => writer.Audio(new float[9600], i * 4800));
                Assert.Equal(failureStage == "Sample" ? "SoftwareNv12" : "HardwareDxgi", writer.ActivePath);
                writer.Finish();
                Assert.Equal("SoftwareNv12", writer.ActivePath);
                Assert.Equal(1, failures);
                Assert.Equal(1, log.Warnings);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var probe = Mp4Probe.Read(path);
            Assert.Equal((256, 144, 10), (probe.Width, probe.Height, probe.VideoFrames));
            Assert.Equal(TimeSpan.FromSeconds(1), probe.VideoEnd);
            Assert.True(probe.HasAudio);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings++;
        }
    }
    [GpuTheory]
    [InlineData("BeginWriting", true)]
    [InlineData("FirstSample", true)]
    [InlineData("BeginWriting", false)]
    [InlineData("FirstSample", false)]
    public async Task HardwareFailureRetriesSoftwareAndPreservesPendingAudio(string failureStage, bool keepSendingTextures)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Shotwise-fallback-{Guid.NewGuid():N}.mp4");
        try
        {
            await Task.Factory.StartNew(() =>
            {
                using var canvas = new Direct2DOverlayCanvas(new SizeI(64, 48));
                var log = new WarningLog(); var failures = 0;
                using var writer = new Mp4Writer(path, canvas.FrameSize,
                    new RecordingOptions(Fps: 10, RecordMicrophone: true, RecordSystemAudio: false), canvas.Device, log,
                    stage =>
                    {
                        if (stage != failureStage) return;
                        failures++;
                        throw new System.Runtime.InteropServices.COMException("Injected hardware failure", unchecked((int)0xC00D36BB));
                    });
                writer.Audio(new float[9600], 0);
                for (var i = 0; i < 10; i++)
                {
                    canvas.Begin(new BgraImage(64, 48));
                    canvas.DrawRectangle(new Rect2(0, 0, 32, 48), null, 0, Argb.Red);
                    using var frame = canvas.EndTexture();
                    // The first pending frame is a texture; the recorder switches to CPU frames afterward.
                    if (i == 0 || keepSendingTextures)
                        writer.Video(frame, TimeSpan.FromMilliseconds(i * 100), TimeSpan.FromMilliseconds(100));
                    else
                        writer.Video(new BgraImage(64, 48), TimeSpan.FromMilliseconds(i * 100), TimeSpan.FromMilliseconds(100));
                    if (i > 0) writer.Audio(new float[9600], i * 4800);
                    Assert.Equal("SoftwareNv12", writer.ActivePath);
                    Assert.False(writer.GpuInput);
                }
                writer.Finish();
                Assert.Equal(1, failures);
                Assert.Equal(1, log.Warnings);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var data = await File.ReadAllBytesAsync(path);
            Assert.True(data.Length > 1024);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(data, 4, 4));
            var container = System.Text.Encoding.Latin1.GetString(data);
            Assert.Contains("moov", container);
            Assert.Contains("vide", container);
            Assert.Contains("soun", container);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task LifecycleRejectsInvalidTransitionsAndDisposesIdempotently()
    {
        var recorder = new VideoRecorder(NullLogger<VideoRecorder>.Instance, new WebcamPreviewFactory(NullLoggerFactory.Instance));
        Assert.Equal(RecorderState.Idle, recorder.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.PauseAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync());
        await recorder.CancelAsync();
        await recorder.DisposeAsync(); await recorder.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => recorder.StartAsync());
    }
    [Fact]
    public async Task InvalidOptionsAreRejectedBeforeNativeInitialization()
    {
        await using var recorder = new VideoRecorder(NullLogger<VideoRecorder>.Instance, new WebcamPreviewFactory(NullLoggerFactory.Instance));
        var error = await Assert.ThrowsAsync<ShotwiseException>(() => recorder.PrepareAsync(new RecordingTarget(new RectI(0, 0, 100, 100), null, null), new RecordingOptions(Fps: 25), Path.Combine(Path.GetTempPath(), "invalid.mp4"), CancellationToken.None));
        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
    }
    private sealed class SyntheticFrames : IFrameSource
    {
        public SizeI FrameSize => new(64, 48);
        public BgraImage GetFrame(TimeSpan timestamp)
        {
            var image = new BgraImage(64, 48);
            for (var i = 0; i < image.Pixels.Length; i += 4) { image.Pixels[i + 1] = (byte)((int)timestamp.TotalMilliseconds % 256); image.Pixels[i + 3] = 255; }
            return image;
        }
    }
    [Fact]
    public async Task FrameSourceRecordingPausesRestartsAndFinalizesWithoutCapturingDesktop()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Shotwise-source-test-{Guid.NewGuid():N}.mp4");
        await using var recorder = new VideoRecorder(NullLogger<VideoRecorder>.Instance, new WebcamPreviewFactory(NullLoggerFactory.Instance));
        try
        {
            await recorder.PrepareFramesAsync(new SyntheticFrames(), new RecordingOptions(Fps: 30, RecordMicrophone: false, RecordSystemAudio: false), path, CancellationToken.None);
            await recorder.StartAsync(); await Task.Delay(200); await recorder.PauseAsync();
            var paused = recorder.Elapsed; await Task.Delay(80); Assert.Equal(paused, recorder.Elapsed);
            await recorder.ResumeAsync(); await Task.Delay(100); await recorder.RestartAsync();
            Assert.Equal(RecorderState.Ready, recorder.State); Assert.Equal(TimeSpan.Zero, recorder.Elapsed);
            await recorder.StartAsync(); await Task.Delay(250);
            var result = await recorder.StopAsync();
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.InRange(result.Duration.TotalSeconds, .15, 3);
            Assert.True(new FileInfo(path).Length > 1024);
            var probe = Mp4Probe.Read(path);
            Assert.Equal((64, 48), (probe.Width, probe.Height));
            Assert.True(probe.VideoFrames > 0);
            Assert.Equal(result, await recorder.StopAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task CancelRemovesOnlyItsTemporaryFileAndPreservesExistingDestination()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"Shotwise-cancel-test-{Guid.NewGuid():N}"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "result.mp4");
        await using var recorder = new VideoRecorder(NullLogger<VideoRecorder>.Instance, new WebcamPreviewFactory(NullLoggerFactory.Instance));
        try
        {
            await recorder.PrepareFramesAsync(new SyntheticFrames(), new RecordingOptions(HardwareEncoding: false, RecordMicrophone: false, RecordSystemAudio: false), path, CancellationToken.None);
            await recorder.StartAsync(); await Task.Delay(150);
            await File.WriteAllTextAsync(path, "unrelated output");
            await recorder.CancelAsync();
            Assert.Equal("unrelated output", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }
    /// <summary>
    /// Hardware-encoder tests need a real GPU (an H.264 MFT on a non-software DXGI adapter). Hosted CI runners only have
    /// Microsoft's Basic Render/WARP adapter, so these tests are skipped there with an explicit reason.
    /// </summary>
    internal static class GpuEncoderRequirement
    {
        public static readonly string? SkipReason = Probe();

        private static string? Probe()
        {
            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                {
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        var software = (desc.Flags & Vortice.DXGI.AdapterFlags.Software) != 0 || desc.VendorId == 0x1414; // 0x1414 = Microsoft Basic Render / WARP
                        if (!software) return null;
                    }
                }
                return "Requires a hardware GPU with an H.264 encoder; only software display adapters were found (e.g. a CI virtual machine).";
            }
            catch (Exception ex)
            {
                return "Requires a hardware GPU; the DXGI adapter probe failed: " + ex.Message;
            }
        }
    }
    internal sealed class GpuFactAttribute : FactAttribute
    {
        public GpuFactAttribute() { if (GpuEncoderRequirement.SkipReason is { } reason) Skip = reason; }
    }
    internal sealed class GpuTheoryAttribute : TheoryAttribute
    {
        public GpuTheoryAttribute() { if (GpuEncoderRequirement.SkipReason is { } reason) Skip = reason; }
    }

    /// <summary>Skipped unless SHOTWISE_MANUAL_RECORDING=1; the recording is kept when SHOTWISE_MANUAL_RECORDING_KEEP=1.</summary>
    internal sealed class ManualRecordingFactAttribute : FactAttribute
    {
        public ManualRecordingFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SHOTWISE_MANUAL_RECORDING") != "1")
                Skip = "Opt-in desktop/device test: set SHOTWISE_MANUAL_RECORDING=1 to capture 5 seconds of the primary display and system audio.";
        }
    }
    [ManualRecordingFact]
    [Trait("Category", "Manual")]
    public async Task RecordPrimaryDisplayWithLoopbackForFiveSeconds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Shotwise-manual-{Guid.NewGuid():N}.mp4");
        await using var recorder = new VideoRecorder(NullLogger<VideoRecorder>.Instance, new WebcamPreviewFactory(NullLoggerFactory.Instance));
        var primary = DesktopNative.Monitors().FindIndex(m => m.Handle == DesktopNative.MonitorFromPoint(default, 1));
        await recorder.PrepareAsync(new RecordingTarget(null, null, primary), new RecordingOptions(RecordMicrophone: false, Countdown: false), path, CancellationToken.None);
        await recorder.StartAsync(); await Task.Delay(TimeSpan.FromSeconds(5));
        var result = await recorder.StopAsync();
        try
        {
            Assert.True(File.Exists(result.Mp4Path)); Assert.True(new FileInfo(result.Mp4Path).Length > 1024);
            Assert.InRange(result.Duration.TotalSeconds, 4.5, 10);
            var probe = Mp4Probe.Read(result.Mp4Path);
            Assert.Equal((result.FrameSize.Width, result.FrameSize.Height), ((double)probe.Width, (double)probe.Height));
            Assert.InRange(probe.VideoEnd.TotalSeconds, 4.5, 10);
            Assert.True(probe.VideoFrames > 30);
        }
        finally { if (Environment.GetEnvironmentVariable("SHOTWISE_MANUAL_RECORDING_KEEP") != "1" && File.Exists(path)) File.Delete(path); }
    }
}


