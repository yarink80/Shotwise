using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Media;
using Xunit;

namespace Shotwise.Media.Tests;

public sealed class MediaTests
{
    private static VideoService Video() => new(NullLogger<VideoService>.Instance);
    private static GifService Gif() => new(Video(), NullLogger<GifService>.Instance);
    private static string Temp(string ext) => Path.Combine(Path.GetTempPath(), $"shotwise-media-{Guid.NewGuid():N}.{ext}");
    private static BgraImage Solid(int w, int h, byte r, byte g = 0, byte b = 0)
    {
        var image = new BgraImage(w, h);
        for (int i = 0; i < image.Pixels.Length; i += 4) { image.Pixels[i] = b; image.Pixels[i + 1] = g; image.Pixels[i + 2] = r; image.Pixels[i + 3] = 255; }
        return image;
    }
    private static async IAsyncEnumerable<(BgraImage, TimeSpan)> Frames(IEnumerable<BgraImage> frames, double fps, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var frame in frames) { ct.ThrowIfCancellationRequested(); yield return (frame, TimeSpan.FromSeconds(1 / fps)); await Task.Yield(); }
    }
    [Fact]
    public async Task GifRoundTripPreservesDeltaAndDelayRounding()
    {
        string path = Temp("gif");
        try
        {
            var frames = Enumerable.Range(0, 15).Select(_ => Solid(32, 20, 255)).ToArray();
            for (int n = 0; n < frames.Length; n++) for (int y = 3; y < 7; y++) for (int x = n; x < n + 4; x++) { int i = (y * 32 + x) * 4; frames[n].Pixels[i] = 255; frames[n].Pixels[i + 2] = 0; }
            await Gif().CreateFromFramesAsync(Frames(frames, 15), path, new(GifPreset.Custom, 32, 20, 15, Dither: false), default);
            var decoded = await Gif().DecodeAsync(path, default);
            Assert.Equal(0, decoded.LoopCount);
            Assert.Equal(15, decoded.Frames.Count); Assert.InRange(decoded.Duration.TotalSeconds, .99, 1.01);
            for (int i = 0; i < frames.Length; i++) Assert.Equal(frames[i].Pixels, decoded.Frames[i].Pixels);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task LzwCrossesCodeWidthsAndResetsDictionary()
    {
        string path = Temp("gif");
        try
        {
            var image = Solid(256, 256, 0); var random = new Random(41);
            for (int i = 0; i < image.Pixels.Length; i += 4) { byte v = (byte)random.Next(128); image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = v; }
            await Gif().CreateFromFramesAsync(Frames(new[] { image }, 5), path, new(GifPreset.Custom, 256, 256, 5, Dither: false), default);
            var decoded = await Gif().DecodeAsync(path, default); Assert.Equal(image.Pixels, decoded.Frames[0].Pixels);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void QuantizerHasBoundedGradientError()
    {
        var image = Solid(256, 128, 0);
        for (int y = 0; y < 128; y++) for (int x = 0; x < 256; x++) { int i = (y * 256 + x) * 4; image.Pixels[i] = (byte)x; image.Pixels[i + 1] = (byte)(y * 2); image.Pixels[i + 2] = (byte)((x + y) / 2); }
        var palette = OctreePalette.Create(image, default); var indices = palette.Map(image, false, default);
        double error = 0;
        for (int i = 0; i < indices.Length; i++) for (int c = 0; c < 3; c++) error += Math.Pow(image.Pixels[i * 4 + 2 - c] - palette.Rgb[indices[i] * 3 + c], 2);
        Assert.InRange(Math.Sqrt(error / (indices.Length * 3)), 0, 30);
        Assert.DoesNotContain((byte)0, indices);
    }
    [Fact]
    public void KeptRangesMergeOverlappingAndClampedCuts()
    {
        var edl = new VideoEditList("source", TimeSpan.FromSeconds(10)) { Trim = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(9)) };
        edl.Cuts.Add(new(TimeSpan.Zero, TimeSpan.FromSeconds(2))); edl.Cuts.Add(new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6))); edl.Cuts.Add(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8)));
        Assert.Equal(new[] { new TimeRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)), new TimeRange(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9)) }, edl.KeptRanges());
        Assert.Equal(TimeSpan.FromSeconds(3), edl.KeptDuration); Assert.False(edl.CanAddCut(new(TimeSpan.Zero, TimeSpan.FromSeconds(10))));
    }
    private static Task Generate(string path, bool audio = false, int width = 64, int height = 48, int fps = 10) => Task.Run(() =>
    {
        using var runtime = new MediaRuntime();
        using var writer = new Mp4Writer(path, width, height, fps, new(HardwareEncoding: false), audio);
        for (int i = 0; i < fps * 2; i++) { writer.Video(Solid(width, height, (byte)(i * 10), 60), i * 10000000L / fps, 10000000 / fps); if (audio) writer.Audio(new byte[(48000 / fps) * 4], i * (48000L / fps)); }
        writer.Complete();
    });
    [Fact]
    public async Task MetadataRewritePreservesPlaybackAndRepeatedUpdates()
    {
        string path = Temp("mp4");
        try
        {
            await Generate(path); var service = Video(); var before = await service.GetFrameAsync(path, TimeSpan.FromSeconds(.7));
            for (int i = 0; i < 2; i++) await service.WriteMetadataAsync(path, new($"Title {i}", "Comment", new[] { "tag" }, DateTimeOffset.UtcNow, "Shotwise"));
            var info = await service.ProbeAsync(path); var after = await service.GetFrameAsync(path, TimeSpan.FromSeconds(.7));
            Assert.Equal(before.Pixels, after.Pixels); Assert.InRange(info.Duration.TotalSeconds, 1.99, 2.01);
            Assert.Contains("Title 1", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task RenderCutsAndCombineHaveExpectedDurationAndAudio()
    {
        string source = Temp("mp4"), cut = Temp("mp4"), combined = Temp("mp4");
        try
        {
            await Generate(source, true);
            var edl = new VideoEditList(source, TimeSpan.FromSeconds(2)); edl.Cuts.Add(new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(1.2)));
            var service = Video();
            await service.RenderAsync(edl, cut, new(HardwareEncoding: false), null, default);
            var info = await service.ProbeAsync(cut); Assert.InRange(info.Duration.TotalSeconds, 1.2, 1.4); Assert.True(info.HasAudio);
            var beforeCut = await service.GetFrameAsync(cut, TimeSpan.FromSeconds(.4));
            var afterCut = await service.GetFrameAsync(cut, TimeSpan.FromSeconds(.5));
            Assert.InRange(beforeCut.Pixels[2], (byte)25, (byte)55); Assert.InRange(afterCut.Pixels[2], (byte)105, (byte)135);
            await service.CombineAsync(new[] { source, cut }, combined, new(HardwareEncoding: false), null, default);
            var joined = await service.ProbeAsync(combined); Assert.InRange(joined.Duration.TotalSeconds, 3.2, 3.4); Assert.True(joined.HasAudio);
        }
        finally { File.Delete(source); File.Delete(cut); File.Delete(combined); }
    }
    private sealed class FrameSource : IFrameSource
    {
        public Shotwise.Core.Geometry.SizeI FrameSize => new(64, 48);
        public BgraImage GetFrame(TimeSpan timestamp) => Solid(64, 48, (byte)(timestamp.TotalMilliseconds % 256), 30);
    }
    [Fact]
    public async Task ImageSessionPausesResumesAndWritesPlayableOutput()
    {
        string path = Temp("mp4");
        try
        {
            await using var session = new VideoFromImagesSession(NullLogger<VideoFromImagesSession>.Instance);
            await session.StartAsync(new FrameSource(), new(Fps: 10, RecordMicrophone: false), path, default);
            await Task.Delay(220); await session.PauseAsync(); var paused = session.Elapsed;
            await Task.Delay(100); Assert.Equal(paused, session.Elapsed);
            await session.ResumeAsync(); await Task.Delay(150); var result = await session.StopAsync();
            Assert.Equal(RecorderState.Idle, session.State); Assert.True(result.Duration >= TimeSpan.FromSeconds(.3));
            var info = await Video().ProbeAsync(path); Assert.InRange(Math.Abs((info.Duration - result.Duration).TotalSeconds), 0, .1);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task CancellationPreservesExistingOutputAndRemovesTemporary()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"shotwise-media-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "keep.gif"); await File.WriteAllTextAsync(path, "existing");
        try
        {
            using var cts = new CancellationTokenSource();
            async IAsyncEnumerable<(BgraImage, TimeSpan)> CancelFrames()
            {
                yield return (Solid(32, 20, 255), TimeSpan.FromMilliseconds(100)); await Task.Yield(); cts.Cancel(); cts.Token.ThrowIfCancellationRequested();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gif().CreateFromFramesAsync(CancelFrames(), path, new(GifPreset.Custom, 32, 20, 10), cts.Token));
            Assert.Equal("existing", await File.ReadAllTextAsync(path)); Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task PresetsFadeAndNonLoopingAreEncoded()
    {
        string path = Temp("gif");
        try
        {
            var gif = Gif(); Assert.Equal(new GifOptions(GifPreset.ScreenVideo, 1280, 720, 5), gif.DefaultsFor(GifPreset.ScreenVideo, 1920, 1080));
            Assert.Equal(225, gif.DefaultsFor(GifPreset.ReducedFileSize, 1920, 1080).Height);
            await gif.CreateFromFramesAsync(Frames(new[] { Solid(32, 20, 255) }, 10), path, new(GifPreset.Custom, 32, 20, 10, Loop: false, FadeToBlack: true), default);
            var decoded = await gif.DecodeAsync(path, default); Assert.Equal(6, decoded.Frames.Count); Assert.Equal(1, decoded.LoopCount);
            Assert.InRange(decoded.Duration.TotalSeconds, .59, .61); Assert.Equal((byte)0, decoded.Frames[^1].Pixels[2]);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task NarrationRequiresAnAudioSampleContract()
    {
        await using var session = new VideoFromImagesSession(NullLogger<VideoFromImagesSession>.Instance);
        var ex = await Assert.ThrowsAsync<Shotwise.Core.ShotwiseException>(() => session.StartAsync(new FrameSource(), new(), Temp("mp4"), default));
        Assert.Equal(Shotwise.Core.ErrorCode.AudioInitFailed, ex.Code); Assert.Equal(RecorderState.Idle, session.State);
    }
    [Fact]
    public async Task TransparentPixelsCanReplacePreviouslyOpaquePixels()
    {
        string path = Temp("gif");
        try
        {
            var first = Solid(32, 20, 255); var second = first.Clone(); Array.Clear(second.Pixels, 0, 32 * 4);
            await Gif().CreateFromFramesAsync(Frames(new[] { first, second, first }, 5), path, new(GifPreset.Custom, 32, 20, 5, Dither: false), default);
            var decoded = await Gif().DecodeAsync(path, default);
            Assert.Equal(first.Pixels, decoded.Frames[0].Pixels); Assert.Equal(second.Pixels, decoded.Frames[1].Pixels); Assert.Equal(first.Pixels, decoded.Frames[2].Pixels);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task CombineLetterboxesDifferentSizesAndSuppliesMissingAudio()
    {
        string first = Temp("mp4"), second = Temp("mp4"), output = Temp("mp4");
        try
        {
            await Generate(first); await Generate(second, true, 48, 64, 15);
            await Video().CombineAsync(new[] { first, second }, output, new(HardwareEncoding: false), null, default);
            var info = await Video().ProbeAsync(output); Assert.Equal(64, info.Width); Assert.Equal(48, info.Height); Assert.Equal(10, info.Fps); Assert.True(info.HasAudio);
            Assert.InRange(info.Duration.TotalSeconds, 3.9, 4.1);
            var frame = await Video().GetFrameAsync(output, TimeSpan.FromSeconds(2.5));
            Assert.InRange(frame.Pixels[2], (byte)0, (byte)15); Assert.InRange(frame.Pixels[(24 * 64 + 32) * 4 + 1], (byte)40, (byte)80);
            var thumbnails = await Video().GetThumbnailsAsync(output, 4, 24); Assert.Equal(4, thumbnails.Count); Assert.All(thumbnails, t => Assert.Equal(24, t.Height));
        }
        finally { File.Delete(first); File.Delete(second); File.Delete(output); }
    }
    [Theory]
    [InlineData(GifDitherMode.FloydSteinberg)]
    [InlineData(GifDitherMode.Ordered)]
    [InlineData(GifDitherMode.None)]
    public async Task DitherModesCreateDecodableGif(GifDitherMode mode)
    {
        string path = Temp("gif");
        try
        {
            var service = new GifService(Video(), NullLogger<GifService>.Instance);
            var options = new GifOptions(GifPreset.Custom, 64, 48, 5).WithDitherMode(mode);
            Assert.Equal(mode, options.DitherMode);
            await service.CreateFromFramesAsync(Frames(new[] { Solid(64, 48, 125, 60, 205) }, 5), path, options, default);
            var animation = await service.DecodeAsync(path, default); Assert.Single(animation.Frames); Assert.Equal((byte)125, animation.Frames[0].Pixels[2]);
        }
        finally { File.Delete(path); }
    }
    private sealed class FakeRecorder : IVideoRecorder
    {
        public IFrameSource? Source; public RecordingOptions? Options; public string? Path; public int Pauses;
        public RecorderState State { get; private set; }
        public TimeSpan Elapsed => TimeSpan.FromSeconds(1);
        public event EventHandler<RecorderState>? StateChanged;
        public event EventHandler<AudioLevels>? Levels;
        public event EventHandler<string>? Warning { add { } remove { } }
        private void Set(RecorderState s) { State = s; StateChanged?.Invoke(this, s); }
        public Task PrepareAsync(RecordingTarget target, RecordingOptions options, string outputMp4Path, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareFramesAsync(IFrameSource source, RecordingOptions options, string outputMp4Path, CancellationToken ct) { Source = source; Options = options; Path = outputMp4Path; Set(RecorderState.Ready); return Task.CompletedTask; }
        public Task StartAsync() { Set(RecorderState.Recording); Levels?.Invoke(this, new AudioLevels(.5, 0)); return Task.CompletedTask; }
        public Task PauseAsync() { Pauses++; Set(RecorderState.Paused); return Task.CompletedTask; }
        public Task ResumeAsync() { Set(RecorderState.Recording); return Task.CompletedTask; }
        /// <summary>When set, the recorder reports this path (as it does when its own destination was occupied).</summary>
        public string? PublishAs;
        public Task<Shotwise.Core.Capture.VideoCaptureResult> StopAsync() { var p = PublishAs ?? Path!; File.WriteAllText(p, "mp4"); Set(RecorderState.Idle); return Task.FromResult(new Shotwise.Core.Capture.VideoCaptureResult(p, Elapsed, new(64, 48), Shotwise.Core.Documents.DocumentMetadata.Create(), false)); }
        public Task CancelAsync() { Set(RecorderState.Idle); return Task.CompletedTask; }
        public Task RestartAsync() => Task.CompletedTask;
        public void SetMicrophoneMuted(bool muted) { }
        public void SetSystemAudioMuted(bool muted) { }
        public void SetWebcamFullscreen(bool fullscreen) { }
        public void AddOverlay(IFrameOverlay overlay) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeRecorderFactory(FakeRecorder recorder) : IVideoRecorderFactory { public IVideoRecorder Create() => recorder; }

    [Fact]
    public async Task NarrationIsDelegatedToTheCoreRecorder()
    {
        // CONTRACT A1: with an IVideoRecorderFactory registered, frames + audio go through IVideoRecorder.PrepareFramesAsync.
        string path = Temp("mp4");
        var recorder = new FakeRecorder();
        try
        {
            await using var session = new VideoFromImagesSession(NullLogger<VideoFromImagesSession>.Instance, null, new FakeRecorderFactory(recorder));
            double level = 0; session.Levels += (_, l) => level = l.Microphone;
            await session.StartAsync(new FrameSource(), new(Fps: 12, RecordMicrophone: true, RecordSystemAudio: true, Background: Shotwise.Core.Imaging.Argb.White), path, default);
            Assert.Equal(RecorderState.Recording, session.State);
            Assert.True(recorder.Options!.RecordMicrophone); Assert.True(recorder.Options.RecordSystemAudio);
            Assert.Equal(10, recorder.Options.Fps); // nearest supported recorder rate
            Assert.Equal(.5, level);
            Assert.Equal(255, recorder.Source!.GetFrame(TimeSpan.Zero).Pixels[3]); // flattened over the background
            Assert.NotEqual(path, recorder.Path); // records to a temporary file, published on Stop
            await session.PauseAsync(); Assert.Equal(RecorderState.Paused, session.State); await session.ResumeAsync();
            var result = await session.StopAsync();
            Assert.Equal(Path.GetFullPath(path), result.Mp4Path); Assert.True(File.Exists(path)); Assert.False(File.Exists(recorder.Path));
            Assert.Equal(RecorderState.Idle, session.State);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FramesOfNonMacroblockAlignedVideoDecode()
    {
        // 64x40 is coded as 64x48 by H.264; the decoder reports a media-type change that must not fail frame reads (P3 fix).
        string input = Temp("mp4"), output = Temp("gif");
        try
        {
            await Generate(input, false, 64, 40, 10);
            var info = await Video().ProbeAsync(input); Assert.Equal(64, info.Width); Assert.Equal(40, info.Height);
            var frame = await Video().GetFrameAsync(input, TimeSpan.FromSeconds(1)); Assert.Equal(40, frame.Height);
            var thumbs = await Video().GetThumbnailsAsync(input, 3, 20); Assert.Equal(3, thumbs.Count);
            await Gif().CreateFromVideoAsync(input, output, Gif().DefaultsFor(GifPreset.Custom, 64, 40, info.Fps) with { Fps = 5 }, null, default);
            Assert.True((await Gif().DecodeAsync(output, default)).Frames.Count >= 5);
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    [Fact]
    public async Task VideoToGifUsesSelectedRangeAndTargetFps()
    {
        string input = Temp("mp4"), output = Temp("gif");
        try
        {
            await Generate(input);
            await Gif().CreateFromVideoAsync(input, output, new(GifPreset.Custom, 32, 24, 5, Range: new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(1.5))), null, default);
            var animation = await Gif().DecodeAsync(output, default); Assert.Equal(5, animation.Frames.Count); Assert.Equal(TimeSpan.FromSeconds(1), animation.Duration);
            Assert.InRange(animation.Frames[0].Pixels[2], (byte)35, (byte)65);
        }
        finally { File.Delete(input); File.Delete(output); }
    }
    [Fact]
    public async Task RenderCanReplaceItsSourceAtomically()
    {
        string path = Temp("mp4");
        try
        {
            await Generate(path);
            var edit = new VideoEditList(path, TimeSpan.FromSeconds(2)) { Trim = new(TimeSpan.FromSeconds(.2), TimeSpan.FromSeconds(1.2)) };
            await Video().RenderAsync(edit, path, new(HardwareEncoding: false, IncludeAudio: false), null, default);
            var info = await Video().ProbeAsync(path); Assert.InRange(info.Duration.TotalSeconds, .9, 1.1); Assert.False(info.HasAudio);
        }
        finally { File.Delete(path); }
    }

    // ---- FX-1: a finalized image video is never deleted because its publication failed (R1-#1 pattern)

    private static string TempFolder() { var d = Path.Combine(Path.GetTempPath(), $"shotwise-media-{Guid.NewGuid():N}"); Directory.CreateDirectory(d); return d; }
    private static void LockedMove(string from, string to) => throw new IOException("The process cannot access the file because it is being used by another process.");

    [Fact]
    public async Task ImageSessionKeepsFinalizedVideoWhenPublishingFails()
    {
        string directory = TempFolder(), path = Path.Combine(directory, "slides.mp4");
        try
        {
            await using var session = new VideoFromImagesSession(NullLogger<VideoFromImagesSession>.Instance);
            int attempts = 0; session.MoveFile = (from, to) => { attempts++; LockedMove(from, to); };
            await session.StartAsync(new FrameSource(), new(Fps: 10, RecordMicrophone: false, RecordSystemAudio: false), path, default);
            await Task.Delay(400); var result = await session.StopAsync();
            Assert.True(attempts > 1);
            Assert.Equal(RecorderState.Idle, session.State);
            Assert.NotEqual(path, result.Mp4Path); Assert.True(File.Exists(result.Mp4Path)); Assert.False(File.Exists(path));
            var info = await Video().ProbeAsync(result.Mp4Path); Assert.True(info.Duration > TimeSpan.Zero);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DelegatedImageSessionKeepsRecordingWhenPublishingFails()
    {
        string directory = TempFolder(), path = Path.Combine(directory, "narrated.mp4");
        var recorder = new FakeRecorder();
        try
        {
            await using var session = new VideoFromImagesSession(NullLogger<VideoFromImagesSession>.Instance, null, new FakeRecorderFactory(recorder));
            session.MoveFile = LockedMove;
            await session.StartAsync(new FrameSource(), new(Fps: 10, RecordMicrophone: true, RecordSystemAudio: false), path, default);
            var result = await session.StopAsync();
            Assert.Equal(recorder.Path, result.Mp4Path); Assert.True(File.Exists(result.Mp4Path));
            Assert.Equal(RecorderState.Idle, session.State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DelegatedImageSessionPublishesThePathTheRecorderReported()
    {
        string directory = TempFolder(), path = Path.Combine(directory, "narrated.mp4");
        var recorder = new FakeRecorder { PublishAs = Path.Combine(directory, "recorder-sibling (2).mp4") };
        try
        {
            await using var session = new VideoFromImagesSession(NullLogger<VideoFromImagesSession>.Instance, null, new FakeRecorderFactory(recorder));
            await session.StartAsync(new FrameSource(), new(Fps: 10, RecordMicrophone: true, RecordSystemAudio: false), path, default);
            var result = await session.StopAsync();
            Assert.Equal(path, result.Mp4Path); Assert.True(File.Exists(path)); Assert.False(File.Exists(recorder.PublishAs));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReplaceRetriesTransientSharingViolationsWithoutDeletingTheSource()
    {
        string directory = TempFolder(), source = Path.Combine(directory, "a.tmp"), destination = Path.Combine(directory, "b.mp4");
        try
        {
            File.WriteAllText(source, "new"); File.WriteAllText(destination, "old"); int calls = 0;
            OutputFile.Replace(source, destination, (f, t) => { if (++calls < 3) throw new IOException("sharing violation"); File.Move(f, t, true); }, initialDelayMs: 1);
            Assert.Equal(3, calls); Assert.Equal("new", File.ReadAllText(destination)); Assert.False(File.Exists(source));
            File.WriteAllText(source, "kept");
            Assert.Throws<IOException>(() => OutputFile.Replace(source, destination, LockedMove, initialDelayMs: 1));
            Assert.Equal("kept", File.ReadAllText(source));
        }
        finally { Directory.Delete(directory, true); }
    }
}
