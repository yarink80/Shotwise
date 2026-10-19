using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Shotwise.Core;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Settings;
using Shotwise.Media;
using Xunit;
using Xunit.Abstractions;

namespace Shotwise.Recording.Tests;

/// <summary>
/// FX-1 regressions: finalized recordings are never deleted (R1-#1), source/device faults keep a playable partial
/// recording (R1-#2), window bounds follow the DWM frame (R1-#4), audio teardown cannot deadlock (R1-#7),
/// low-disk thresholds come from preferences (REC-10) and recorder output accepts MP4 metadata (MED-06).
/// </summary>
public sealed class RecordingFaultTests(ITestOutputHelper output)
{
    private static readonly SizeI FrameSize = new(256, 144); // above every hardware H.264 minimum (NVENC 145x49)

    // ---------------------------------------------------------------- helpers

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Shotwise-fx1-{Guid.NewGuid():N}");
        public TempFolder() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    /// <summary>Collects warnings and detects the recorder returning to Idle on its own after recording.</summary>
    private sealed class Watch
    {
        private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _recorded;
        public ConcurrentQueue<string> Warnings { get; } = new();
        public Watch(IVideoRecorder recorder)
        {
            recorder.Warning += (_, key) => Warnings.Enqueue(key);
            recorder.StateChanged += (_, state) =>
            {
                if (state == RecorderState.Recording) _recorded = true;
                else if (state == RecorderState.Idle && _recorded) _idle.TrySetResult();
                else if (state == RecorderState.Faulted) _faulted.TrySetResult();
            };
        }
        public async Task WaitForAutoStopAsync()
        {
            var done = await Task.WhenAny(_idle.Task, _faulted.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(done == _idle.Task, done == _faulted.Task ? "Recorder faulted instead of keeping the partial recording." : "Recorder did not stop after the source fault.");
        }
    }

    private static BgraImage Solid(SizeI size, byte b, byte g, byte r)
    {
        var image = new BgraImage(size.Width, size.Height);
        for (var i = 0; i < image.Pixels.Length; i += 4) { image.Pixels[i] = b; image.Pixels[i + 1] = g; image.Pixels[i + 2] = r; image.Pixels[i + 3] = 255; }
        return image;
    }

    /// <summary>An editor frame source that is torn down mid-recording (throws once <paramref name="failAfter"/> is reached).</summary>
    private sealed class FailingFrames(SizeI size, TimeSpan failAfter) : IFrameSource
    {
        public SizeI FrameSize => size;
        public BgraImage GetFrame(TimeSpan timestamp)
        {
            if (timestamp >= failAfter) throw new ObjectDisposedException("frame source", "The frame source was closed while recording.");
            return Solid(size, 200, 80, 20);
        }
    }

    private sealed class SteadyFrames(SizeI size) : IFrameSource
    {
        public SizeI FrameSize => size;
        public BgraImage GetFrame(TimeSpan timestamp) => Solid(size, 20, 160, (byte)((int)timestamp.TotalMilliseconds % 256));
    }

    /// <summary>A WASAPI-like capture device: delivers 48 kHz stereo float on its own thread until unplugged.</summary>
    private sealed class FakeAudioDevice : IWaveIn
    {
        private Thread? _thread;
        private volatile bool _running;
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording()
        {
            _running = true;
            _thread = new Thread(() =>
            {
                var clock = Stopwatch.StartNew(); long sent = 0;
                while (_running)
                {
                    var due = (long)(clock.Elapsed.TotalSeconds * 48000);
                    if (due > sent)
                    {
                        var frames = (int)(due - sent); var samples = new float[frames * 2];
                        for (var i = 0; i < frames; i++) samples[i * 2] = samples[i * 2 + 1] = 0.3f * (float)Math.Sin(2 * Math.PI * 440 * (sent + i) / 48000);
                        var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
                        DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length)); sent = due;
                    }
                    Thread.Sleep(10);
                }
            }) { IsBackground = true, Name = "Fake audio capture" };
            _thread.Start();
        }
        /// <summary>What WASAPI reports when the endpoint disappears (AUDCLNT_E_DEVICE_INVALIDATED).</summary>
        public void Unplug()
        {
            _running = false; _thread?.Join();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(new COMException("The audio endpoint device has been unplugged.", unchecked((int)0x88890004))));
        }
        public void StopRecording() => _running = false;
        public void Dispose() { _running = false; _thread?.Join(); }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public Task SaveAsync() => Task.CompletedTask;
        public void Update(Action<AppSettings> mutate) => mutate(Current);
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task ResetAsync() => Task.CompletedTask;
    }

    private static VideoRecorder NewRecorder(ISettingsService? settings = null)
        => new(NullLogger<VideoRecorder>.Instance, new WebcamPreviewFactory(NullLoggerFactory.Instance), settings: settings);

    private static RecordingOptions Options(bool hardware, bool audio = false)
        => new(Fps: 30, HardwareEncoding: hardware, RecordMicrophone: audio, RecordSystemAudio: false);

    /// <summary>Playable = the MF source reader reaches end of stream with samples, the decoder returns a frame and the duration is positive.</summary>
    private async Task<VideoInfo> AssertPlayableAsync(string path, SizeI size, double minimumSeconds, bool audio = false)
    {
        Assert.True(File.Exists(path), $"{path} was not kept.");
        var probe = RecordingLogicTests.Mp4Probe.Read(path);
        Assert.Equal((size.Width, size.Height), (probe.Width, probe.Height));
        Assert.True(probe.VideoFrames > 0);
        Assert.True(probe.VideoEnd > TimeSpan.Zero);
        var media = new VideoService(NullLogger<VideoService>.Instance);
        var info = await media.ProbeAsync(path);
        output.WriteLine($"{Path.GetFileName(path)}: {info.Width}x{info.Height}, {info.Duration.TotalSeconds:0.000} s, {probe.VideoFrames} frames, audio={info.HasAudio}");
        Assert.True(info.Duration.TotalSeconds >= minimumSeconds, $"Duration {info.Duration} is shorter than {minimumSeconds} s.");
        Assert.Equal(audio, info.HasAudio);
        var frame = await media.GetFrameAsync(path, info.Duration / 2);
        Assert.Equal((size.Width, size.Height), (frame.Width, frame.Height));
        return info;
    }

    // ---------------------------------------------------------------- R1-#2 source / device faults

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task FrameSourceFaultFinalizesAndKeepsPartialRecording(bool hardware)
    {
        using var folder = new TempFolder(); var path = folder.File("partial.mp4");
        await using var recorder = NewRecorder(); var watch = new Watch(recorder);
        await recorder.PrepareFramesAsync(new FailingFrames(FrameSize, TimeSpan.FromMilliseconds(700)), Options(hardware), path, CancellationToken.None);
        await recorder.StartAsync();
        await watch.WaitForAutoStopAsync();
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Contains("Recording_SourceLost", watch.Warnings);
        var result = await recorder.StopAsync();
        Assert.Equal(path, result.Mp4Path);
        Assert.InRange(result.Duration.TotalSeconds, 0.6, 5);
        await AssertPlayableAsync(path, FrameSize, 0.4);
        Assert.Single(Directory.GetFiles(folder.Path)); // no intermediate file left behind
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task DeviceRemovalDuringCompositionFinalizesPartialRecording(bool hardware)
    {
        using var folder = new TempFolder(); var path = folder.File("device-removed.mp4");
        await using var recorder = NewRecorder(); var watch = new Watch(recorder);
        var composed = 0;
        recorder.StageProbe = stage =>
        {
            // DXGI_ERROR_DEVICE_REMOVED from Direct2D/D3D11 on the recorder thread after ~0.5 s of frames.
            if (stage == "Compose" && ++composed > 15) throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(unchecked((int)0x887A0005)));
        };
        await recorder.PrepareFramesAsync(new SteadyFrames(FrameSize), Options(hardware), path, CancellationToken.None);
        await recorder.StartAsync();
        await watch.WaitForAutoStopAsync();
        Assert.Contains("Recording_SourceLost", watch.Warnings);
        Assert.DoesNotContain(watch.Warnings, w => w.StartsWith("Error_", StringComparison.Ordinal));
        var result = await recorder.StopAsync();
        Assert.Equal(path, result.Mp4Path);
        await AssertPlayableAsync(path, FrameSize, 0.3);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task AudioDeviceLossFinalizesPartialRecordingWithAudio(bool hardware)
    {
        using var folder = new TempFolder(); var path = folder.File("mic-unplugged.mp4");
        var device = new FakeAudioDevice();
        await using var recorder = NewRecorder(); var watch = new Watch(recorder);
        recorder.AudioFactory = _ => { var audio = new RecordingAudio(() => recorder.Elapsed, NullLogger.Instance); audio.Attach(device, mic: true); return audio; };
        await recorder.PrepareFramesAsync(new SteadyFrames(FrameSize), Options(hardware, audio: true), path, CancellationToken.None);
        await recorder.StartAsync(); await Task.Delay(900);
        device.Unplug();
        await watch.WaitForAutoStopAsync();
        Assert.Contains("Recording_SourceLost", watch.Warnings);
        var result = await recorder.StopAsync();
        Assert.Equal(path, result.Mp4Path);
        await AssertPlayableAsync(path, FrameSize, 0.6, audio: true);
    }

    [Fact]
    public async Task SourceFaultBeforeFirstFrameFaultsWithoutLeavingFiles()
    {
        using var folder = new TempFolder(); var path = folder.File("never.mp4");
        await using var recorder = NewRecorder(); var watch = new Watch(recorder);
        // No frame ever reaches the writer, so there is nothing to keep: this must fault, not publish an empty MP4.
        recorder.StageProbe = stage => { if (stage == "Compose") throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(unchecked((int)0x887A0005))); };
        await recorder.PrepareFramesAsync(new SteadyFrames(FrameSize), Options(false), path, CancellationToken.None);
        await recorder.StartAsync();
        for (var i = 0; i < 100 && recorder.State != RecorderState.Faulted; i++) await Task.Delay(50);
        Assert.Equal(RecorderState.Faulted, recorder.State);
        await Assert.ThrowsAsync<ShotwiseException>(() => recorder.StopAsync());
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    // ---------------------------------------------------------------- R1-#1 never delete a finalized recording

    [Fact]
    public async Task OccupiedDestinationPublishesUniqueSiblingAndKeepsBothFiles()
    {
        using var folder = new TempFolder(); var path = folder.File("result.mp4");
        await using var recorder = NewRecorder(); var watch = new Watch(recorder);
        await recorder.PrepareFramesAsync(new SteadyFrames(FrameSize), Options(false), path, CancellationToken.None);
        await recorder.StartAsync(); await Task.Delay(500);
        await File.WriteAllTextAsync(path, "unrelated output");
        var result = await recorder.StopAsync();
        Assert.Equal(folder.File("result (2).mp4"), result.Mp4Path);
        Assert.Equal("unrelated output", await File.ReadAllTextAsync(path));
        Assert.Contains("Recording_SavedToAlternatePath", watch.Warnings);
        Assert.Equal(RecorderState.Idle, recorder.State);
        await AssertPlayableAsync(result.Mp4Path, FrameSize, 0.3);
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task FailedMoveKeepsFinalizedRecordingAndReturnsItsPath()
    {
        using var folder = new TempFolder(); var path = folder.File("locked.mp4");
        await using var recorder = NewRecorder(); var watch = new Watch(recorder);
        var attempts = 0;
        recorder.MoveFile = (_, _) => { attempts++; throw new IOException("The process cannot access the file because it is being used by another process."); };
        await recorder.PrepareFramesAsync(new SteadyFrames(FrameSize), Options(true), path, CancellationToken.None);
        await recorder.StartAsync(); await Task.Delay(500);
        var result = await recorder.StopAsync();
        Assert.True(attempts > 1, "The move was not retried.");
        Assert.NotEqual(path, result.Mp4Path);
        Assert.StartsWith(".shotwise-", Path.GetFileName(result.Mp4Path));
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Contains("Recording_SavedToAlternatePath", watch.Warnings);
        Assert.DoesNotContain(watch.Warnings, w => w.StartsWith("Error_", StringComparison.Ordinal));
        await AssertPlayableAsync(result.Mp4Path, FrameSize, 0.3);
        Assert.Equal(result, await recorder.StopAsync());
        await recorder.CancelAsync(); // cancelling afterwards must not delete the published recording either
        Assert.True(File.Exists(result.Mp4Path));
    }

    [Fact]
    public void PublishRetriesTransientSharingViolations()
    {
        using var folder = new TempFolder();
        var finalized = folder.File(".shotwise-x.mp4"); File.WriteAllText(finalized, "mp4");
        var calls = 0;
        var path = RecordingOutput.Publish(finalized, folder.File("out.mp4"), NullLogger.Instance, (from, to) =>
        {
            if (++calls < 3) throw new IOException("sharing violation");
            File.Move(from, to);
        }, initialDelayMs: 1);
        Assert.Equal(folder.File("out.mp4"), path);
        Assert.Equal(3, calls);
        Assert.False(File.Exists(finalized));
    }

    [Fact]
    public void PublishNeverDeletesTheFinalizedFile()
    {
        using var folder = new TempFolder();
        var finalized = folder.File(".shotwise-y.mp4"); File.WriteAllText(finalized, "mp4");
        var path = RecordingOutput.Publish(finalized, folder.File("out.mp4"), NullLogger.Instance, (_, _) => throw new UnauthorizedAccessException(), initialDelayMs: 1);
        Assert.Equal(finalized, path);
        Assert.Equal("mp4", File.ReadAllText(finalized));
    }

    // ---------------------------------------------------------------- R1-#7 audio teardown

    /// <summary>
    /// Mimics WasapiCapture: a capture callback that already snapshotted the DataAvailable delegate runs while
    /// Dispose joins the capture thread. Joining under the source lock deadlocked (the callback needs that lock).
    /// </summary>
    private sealed class InFlightCallbackDevice : IWaveIn
    {
        private EventHandler<WaveInEventArgs>? _snapshot;
        public bool Joined { get; private set; }
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }
        public void StartRecording() => _snapshot = DataAvailable;
        public void StopRecording() { }
        public void Dispose()
        {
            var capture = new Thread(() => _snapshot?.Invoke(this, new WaveInEventArgs(new byte[3840], 3840))) { IsBackground = true };
            capture.Start();
            Joined = capture.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DisposingAudioSourceDoesNotDeadlockWithInFlightCallback()
    {
        var device = new InFlightCallbackDevice();
        var audio = new RecordingAudio(() => TimeSpan.Zero, NullLogger.Instance);
        audio.Attach(device, mic: true);
        var disposal = Task.Run(audio.Dispose);
        Assert.Same(disposal, await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(20))));
        Assert.True(device.Joined, "The capture thread could not finish while the source was being disposed (lock held during join).");
    }

    // ---------------------------------------------------------------- REC-10 thresholds from preferences

    [Theory]
    [InlineData(4096, 1000, 4096, 1000)]
    [InlineData(100, 500, 500, 500)]   // warning below stop: raised to stop
    [InlineData(0, -5, 2048, 500)]     // invalid: defaults
    public void DiskThresholdsFollowPreferences(int warningMb, int stopMb, long expectedWarningMb, long expectedStopMb)
    {
        var thresholds = RecordingDiskThresholds.FromMegabytes(warningMb, stopMb);
        Assert.Equal(expectedWarningMb * 1024 * 1024, thresholds.WarningBytes);
        Assert.Equal(expectedStopMb * 1024 * 1024, thresholds.StopBytes);
        Assert.Equal(DiskSpaceStatus.Stop, RecordingDiskGuard.Evaluate(thresholds.StopBytes - 1, thresholds));
        Assert.Equal(DiskSpaceStatus.Healthy, RecordingDiskGuard.Evaluate(thresholds.WarningBytes, thresholds));
    }

    [Fact]
    public async Task LowDiskStopPreferenceIsEnforcedAtPrepare()
    {
        using var folder = new TempFolder();
        var settings = new FakeSettings(); settings.Current.Capture.LowDiskStopMb = int.MaxValue; // ~2 PiB: never available
        await using var recorder = NewRecorder(settings);
        var error = await Assert.ThrowsAsync<ShotwiseException>(() => recorder.PrepareFramesAsync(new SteadyFrames(FrameSize), Options(false), folder.File("x.mp4"), CancellationToken.None));
        Assert.Equal(ErrorCode.LowDiskSpace, error.Code);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task LowDiskWarningPreferenceRaisesWarning()
    {
        using var folder = new TempFolder();
        var settings = new FakeSettings(); settings.Current.Capture.LowDiskWarningMb = int.MaxValue; settings.Current.Capture.LowDiskStopMb = 1;
        await using var recorder = NewRecorder(settings); var watch = new Watch(recorder);
        await recorder.PrepareFramesAsync(new SteadyFrames(FrameSize), Options(false), folder.File("x.mp4"), CancellationToken.None);
        Assert.Contains("Recording_LowDiskSpace", watch.Warnings);
        await recorder.CancelAsync();
    }

    // ---------------------------------------------------------------- MED-06 metadata on recorder output

    [Fact]
    public async Task MetadataCanBeWrittenToRecorderOutput()
    {
        using var folder = new TempFolder(); var path = folder.File("tagged.mp4");
        var device = new FakeAudioDevice();
        await using var recorder = NewRecorder();
        recorder.AudioFactory = _ => { var audio = new RecordingAudio(() => recorder.Elapsed, NullLogger.Instance); audio.Attach(device, mic: true); return audio; };
        await recorder.PrepareFramesAsync(new SteadyFrames(FrameSize), Options(true, audio: true), path, CancellationToken.None);
        await recorder.StartAsync(); await Task.Delay(800);
        var result = await recorder.StopAsync();
        var before = await AssertPlayableAsync(result.Mp4Path, FrameSize, 0.5, audio: true);
        var media = new VideoService(NullLogger<VideoService>.Instance);
        await media.WriteMetadataAsync(path, new VideoMetadata("First title", "c", new[] { "a" }, DateTimeOffset.Now, "Shotwise"));
        var metadata = new VideoMetadata("Recorded demo – überprüft", "Recorded by FX-1 tests", new[] { "demo", "fx1" }, new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero), "Shotwise.Recording.Tests");
        await media.WriteMetadataAsync(path, metadata);
        var after = await AssertPlayableAsync(path, FrameSize, 0.5, audio: true);
        Assert.Equal(before.Duration, after.Duration);
        // Inspect the live movie box only (earlier ones are kept as 'free' space so media offsets stay valid).
        var file = await File.ReadAllBytesAsync(path);
        byte[]? bytes = null; var moovCount = 0;
        for (long offset = 0; offset + 8 <= file.Length;)
        {
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)offset)); var header = 8;
            if (size == 1) { size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan((int)offset + 8)); header = 16; }
            if (size == 0) size = file.Length - offset;
            if (System.Text.Encoding.Latin1.GetString(file, (int)offset + 4, 4) == "moov") { moovCount++; bytes = file.AsSpan((int)offset + header, (int)size - header).ToArray(); }
            offset += size;
        }
        Assert.Equal(1, moovCount);
        bool Contains(byte[] needle) => bytes!.AsSpan().IndexOf(needle) >= 0;
        Assert.True(Contains(System.Text.Encoding.UTF8.GetBytes(metadata.Title!)));
        Assert.True(Contains(System.Text.Encoding.UTF8.GetBytes("demo;fx1")));
        Assert.True(Contains(System.Text.Encoding.UTF8.GetBytes(metadata.SourceApp!)));
        Assert.False(Contains(System.Text.Encoding.UTF8.GetBytes("First title")));
        Assert.True(Contains(new byte[] { 0xA9, (byte)'n', (byte)'a', (byte)'m' }));
    }

    // ---------------------------------------------------------------- manual: real window (R1-#4 + R1-#2)

    [DllImport("user32.dll")] private static extern nint SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    /// <summary>A plain red top-level window with its own message loop thread.</summary>
    private sealed class RedWindow : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW { public int cbSize; public uint style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, nuint w, nint l);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
        [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private uint _threadId;
        public nint Handle { get; private set; }
        public string Title { get; } = $"Shotwise FX-1 window {Guid.NewGuid():N}";
        public RedWindow(int x, int y, int width, int height)
        {
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                var cls = new WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                    hInstance = GetModuleHandleW(null), hbrBackground = CreateSolidBrush(0x000000FF), lpszClassName = "ShotwiseFx1Red" + Guid.NewGuid().ToString("N"),
                };
                RegisterClassExW(ref cls);
                const uint overlapped = 0x00CF0000, visible = 0x10000000, topmost = 0x00000008;
                Handle = CreateWindowExW(topmost, cls.lpszClassName, Title, overlapped | visible, x, y, width, height, 0, 0, cls.hInstance, 0);
                SetForegroundWindow(Handle);
                _ready.Set();
                while (GetMessageW(out var msg, 0, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
                if (IsWindow(Handle)) DestroyWindow(Handle);
            }) { IsBackground = true, Name = "FX-1 test window" };
            _thread.SetApartmentState(ApartmentState.STA); _thread.Start(); _ready.Wait();
            Assert.NotEqual(0, Handle);
        }
        /// <summary>Destroys the window (the user closing the recorded application).</summary>
        public void Close() { if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012 /* WM_QUIT */, 0, 0); _thread.Join(); }
        public void Dispose() { if (_thread.IsAlive) Close(); _ready.Dispose(); }
    }

    [RecordingLogicTests.ManualRecordingFact]
    [Trait("Category", "Manual")]
    public async Task ClosingRecordedWindowKeepsPartialRecordingAlignedToDwmFrame()
    {
        SetProcessDpiAwarenessContext(-4); // PerMonitorV2, as the app manifest declares; fails harmlessly if already set
        using var folder = new TempFolder(); var path = folder.File("window.mp4");
        using var window = new RedWindow(240, 180, 720, 480);
        await Task.Delay(700); // let DWM paint the window
        Assert.True(DesktopNative.TryGetWindowBounds(window.Handle, out var frame));
        GetWindowRect(window.Handle, out var rect);
        output.WriteLine($"DWM frame {frame}; GetWindowRect {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
        Assert.True(frame.Width <= rect.Right - rect.Left && frame.Height <= rect.Bottom - rect.Top);
        GetCursorPos(out var savedCursor);
        var center = new PointI(frame.X + frame.Width / 2, frame.Y + frame.Height * 2 / 3);
        SetCursorPos(center.X, center.Y);
        try
        {
            await using var recorder = NewRecorder(); var watch = new Watch(recorder);
            var options = new RecordingOptions(Fps: 30, ScaleTo1080p: false, RecordMicrophone: false, RecordSystemAudio: false, IncludeCursor: false,
                CursorHighlight: true, CursorHighlightColor: Argb.FromArgb(255, 0, 255, 0), Countdown: false);
            await recorder.PrepareAsync(new RecordingTarget(null, window.Handle, null), options, path, CancellationToken.None);
            await recorder.StartAsync(); await Task.Delay(1500);
            window.Close();
            await watch.WaitForAutoStopAsync();
            Assert.Contains("Recording_SourceLost", watch.Warnings);
            var result = await recorder.StopAsync();
            Assert.Equal(path, result.Mp4Path);
            Assert.Equal(window.Title, result.Metadata.WindowTitle);
            Assert.Equal(Process.GetCurrentProcess().ProcessName, result.Metadata.AppName);
            var size = new SizeI(frame.Width & ~1, frame.Height & ~1);
            Assert.Equal(new Size2(size.Width, size.Height), result.FrameSize);
            await AssertPlayableAsync(path, size, 1.0);

            var image = await new VideoService(NullLogger<VideoService>.Instance).GetFrameAsync(path, TimeSpan.FromSeconds(1));
            (byte R, byte G, byte B) Pixel(int x, int y) { var p = (y * image.Width + x) * 4; return (image.Pixels[p + 2], image.Pixels[p + 1], image.Pixels[p]); }
            bool Red((byte R, byte G, byte B) c) => c.R > 150 && c.G < 90 && c.B < 90;
            bool Green((byte R, byte G, byte B) c) => c.G > 150 && c.R < 120 && c.B < 120;
            // Client area reaches the right and bottom edges: no black bars from the invisible resize borders.
            var right = Pixel(image.Width - 4, image.Height / 2); var bottom = Pixel(image.Width / 2, image.Height - 4);
            output.WriteLine($"right edge {right}, bottom edge {bottom}");
            Assert.True(Red(right), $"Right edge is {right}, expected the red client area.");
            Assert.True(Red(bottom), $"Bottom edge is {bottom}, expected the red client area.");
            // The 40 px cursor highlight is centred on the cursor: ±14 px on both sides must be inside it (an offset ≥ 6 px fails).
            int cx = center.X - frame.X, cy = center.Y - frame.Y;
            var samples = new[] { Pixel(cx - 14, cy), Pixel(cx + 14, cy), Pixel(cx, cy - 14), Pixel(cx, cy + 14) };
            output.WriteLine($"highlight samples {string.Join(", ", samples)}");
            Assert.All(samples, s => Assert.True(Green(s), $"Cursor highlight is offset: sample {s}."));
        }
        finally { SetCursorPos(savedCursor.X, savedCursor.Y); }
    }
}
