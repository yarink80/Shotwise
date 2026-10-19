using Microsoft.Extensions.Logging;
using Shotwise.Core.Geometry;

namespace Shotwise.Recording;

internal enum DiskSpaceStatus { Healthy, Warning, Stop }

/// <summary>Free-space thresholds (bytes) for the recording disk guard (SPEC REC-10; Capture preferences).</summary>
internal readonly record struct RecordingDiskThresholds(long WarningBytes, long StopBytes)
{
    public const int DefaultWarningMb = 2048, DefaultStopMb = 500;
    public static RecordingDiskThresholds Default { get; } = FromMegabytes(DefaultWarningMb, DefaultStopMb);
    /// <summary>
    /// Maps the <c>LowDiskWarningMb</c>/<c>LowDiskStopMb</c> preferences (MiB). Non-positive values fall back to
    /// the defaults; a warning threshold below the stop threshold is raised to it (the warning band is then empty).
    /// </summary>
    public static RecordingDiskThresholds FromMegabytes(int warningMb, int stopMb)
    {
        long stop = (stopMb > 0 ? stopMb : DefaultStopMb) * 1024L * 1024;
        long warning = (warningMb > 0 ? warningMb : DefaultWarningMb) * 1024L * 1024;
        return new(Math.Max(warning, stop), stop);
    }
}

internal static class RecordingDiskGuard
{
    public static DiskSpaceStatus Evaluate(long bytes) => Evaluate(bytes, RecordingDiskThresholds.Default);
    public static DiskSpaceStatus Evaluate(long bytes, RecordingDiskThresholds thresholds)
        => bytes < thresholds.StopBytes ? DiskSpaceStatus.Stop : bytes < thresholds.WarningBytes ? DiskSpaceStatus.Warning : DiskSpaceStatus.Healthy;
}

/// <summary>
/// Publishes a finalized recording. A finalized MP4 is the user's only copy, so publication never deletes it:
/// sharing violations are retried with backoff, an occupied destination gets a unique sibling name, and when no
/// move succeeds the finalized intermediate file is kept and its path returned.
/// </summary>
internal static class RecordingOutput
{
    public static string Publish(string finalized, string destination, ILogger logger, Action<string, string>? move = null, int attempts = 5, int initialDelayMs = 50)
    {
        move ??= static (from, to) => File.Move(from, to, false);
        Exception? last = null;
        var delay = initialDelayMs;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (!File.Exists(finalized)) throw new FileNotFoundException("The finalized recording is no longer available.", finalized, last);
            var target = File.Exists(destination) ? UniqueSibling(destination) : destination;
            try { move(finalized, target); return target; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                last = e;
                logger.LogDebug(e,"Publishing recording to {Path} failed (attempt {Attempt})", target, attempt + 1);
            }
            if (attempt + 1 < attempts) { Thread.Sleep(delay); delay = Math.Min(delay * 2, 1000); }
        }
        if (!File.Exists(finalized)) throw new FileNotFoundException("The finalized recording is no longer available.", finalized, last);
        logger.LogWarning(last,"Finalized recording could not be moved to {Destination}; it was kept at {Path}", destination, finalized);
        return finalized;
    }
    internal static string UniqueSibling(string destination)
    {
        var folder = Path.GetDirectoryName(destination)!; var name = Path.GetFileNameWithoutExtension(destination); var extension = Path.GetExtension(destination);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{name} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

internal static class RecordingDimensions
{
    public static SizeI Fit(int width, int height, bool scale)
    {
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
        var factor = scale ? Math.Min(1, Math.Min(1920d / width, 1080d / height)) : 1;
        return new SizeI(Math.Max(2, (int)(width * factor) & ~1), Math.Max(2, (int)(height * factor) & ~1));
    }
}

/// <summary>QPC governor that drops missed deadlines instead of bursting stale frames.</summary>
internal sealed class FrameGovernor
{
    private readonly long _frequency;
    private readonly int _fps;
    private long _origin, _next;
    private bool _started;
    public FrameGovernor(int fps, long frequency)
    {
        if (!Core.Media.RecordingOptions.SupportedFps.Contains(fps) || frequency <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _fps = fps; _frequency = frequency;
    }
    public bool ShouldCapture(long now)
    {
        if (!_started) { _origin = now; _started = true; }
        if (now < _next) return false;
        var slot = (long)((now - _origin) * (double)_fps / _frequency);
        _next = _origin + (long)Math.Ceiling((slot + 1d) * _frequency / _fps);
        return true;
    }
}

internal sealed class RecordingTimeline
{
    private readonly long _frequency;
    private long _start, _pause, _excluded;
    private bool _paused;
    public RecordingTimeline(long frequency) => _frequency = frequency > 0 ? frequency : throw new ArgumentOutOfRangeException(nameof(frequency));
    public void Start(long now) { _start = now; _pause = _excluded = 0; _paused = false; }
    public void Pause(long now) { if (!_paused) { _pause = now; _paused = true; } }
    public void Resume(long now) { if (_paused) { _excluded += now - _pause; _paused = false; } }
    public TimeSpan At(long now) => TimeSpan.FromTicks((long)(Math.Max(0, (_paused ? _pause : now) - _start - _excluded) * (double)TimeSpan.TicksPerSecond / _frequency));
}

/// <summary>Bounded per-source timeline. Missing intervals are silence; late audio cannot shift later audio.</summary>
internal sealed class AudioTimelineQueue
{
    private readonly SortedDictionary<long, float[]> _blocks = new();
    private readonly object _gate = new();
    public void Enqueue(long firstFrame, float[] stereo)
    {
        if (firstFrame < 0 || stereo.Length % 2 != 0) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        lock (_gate)
        {
            _blocks[firstFrame] = stereo;
            while (_blocks.Count > 200) _blocks.Remove(_blocks.First().Key);
        }
    }
    public void Clear() { lock (_gate) _blocks.Clear(); }
    public void Mix(long startFrame, Span<float> stereo, bool muted)
    {
        lock (_gate)
        {
            var end = startFrame + stereo.Length / 2;
            foreach (var (start, samples) in _blocks.ToArray())
            {
                var stop = start + samples.Length / 2;
                if (!muted)
                    for (var frame = Math.Max(startFrame, start); frame < Math.Min(end, stop); frame++)
                        for (var channel = 0; channel < 2; channel++)
                            stereo[(int)((frame - startFrame) * 2 + channel)] += samples[(int)((frame - start) * 2 + channel)];
                if (stop <= end) _blocks.Remove(start);
            }
        }
    }
}
