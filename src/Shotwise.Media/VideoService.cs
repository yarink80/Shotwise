using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using VideoInfo = Shotwise.Core.Media.VideoInfo;

namespace Shotwise.Media;

public sealed class VideoService(ILogger<VideoService> logger) : IVideoService
{
    private Task<T> Run<T>(Func<T> action, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try { using var runtime = new MediaRuntime(); return action(); }
        catch (OperationCanceledException) { throw; }
        catch (ShotwiseException) { throw; }
        catch (Exception ex) { logger.LogError(ex, "Media operation failed"); throw new ShotwiseException(ErrorCode.MediaRenderFailed, "Media operation failed.", ex); }
    }, ct);
    public Task<VideoInfo> ProbeAsync(string path, CancellationToken ct = default) => Run(() => { using var reader = new VideoReader(path); return reader.Info; }, ct);
    public Task<BgraImage> GetFrameAsync(string path, TimeSpan at, CancellationToken ct = default) => Run(() =>
    {
        using var reader = new VideoReader(path);
        long target = Math.Clamp(at.Ticks, 0, Math.Max(0, reader.Info.Duration.Ticks - 1));
        return reader.At(target, ct);
    }, ct);
    public Task<IReadOnlyList<BgraImage>> GetThumbnailsAsync(string path, int count, int height, CancellationToken ct = default) => Run<IReadOnlyList<BgraImage>>(() =>
    {
        if (count is < 1 or > 1000 || height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(count));
        using var reader = new VideoReader(path); var result = new List<BgraImage>(count);
        if ((double)reader.Info.Width / reader.Info.Height * height * height * count > 64_000_000) throw new ArgumentOutOfRangeException(nameof(count), "Thumbnail strip exceeds the memory limit.");
        for (int i = 0; i < count; i++) { ct.ThrowIfCancellationRequested(); var frame = reader.At(reader.Info.Duration.Ticks * i / count, ct); result.Add(PixelScaler.Resize(frame, Math.Max(1, (int)Math.Round((double)frame.Width / frame.Height * height)), height)); }
        return result;
    }, ct);
    public Task RenderAsync(VideoEditList edl, string outputPath, VideoExportOptions o, IProgress<double>? p, CancellationToken ct)
    {
        var snapshot = edl.Clone();
        return Run(() => { Export(new[] { (snapshot.SourcePath, snapshot.KeptRanges().ToArray()) }, outputPath, o, p, ct); return true; }, ct);
    }
    public Task CombineAsync(IReadOnlyList<string> inputs, string outputPath, VideoExportOptions o, IProgress<double>? p, CancellationToken ct)
    {
        var paths = inputs.ToArray();
        if (paths.Length < 2) throw new ArgumentException("At least two videos are required.", nameof(inputs));
        return Run(() => { Export(paths.Select(path => { using var r = new VideoReader(path); return (path, new[] { new TimeRange(TimeSpan.Zero, r.Info.Duration) }); }).ToArray(), outputPath, o, p, ct); return true; }, ct);
    }
    private static void Export((string Path, TimeRange[] Ranges)[] inputs, string output, VideoExportOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        VideoInfo firstInfo;
        using (var first = new VideoReader(inputs[0].Path)) firstInfo = first.Info;
        int w = options.Width ?? firstInfo.Width, h = options.Height ?? firstInfo.Height;
        w = (w + 1) & ~1; h = (h + 1) & ~1;
        double fps = options.Fps ?? firstInfo.Fps;
        long total = inputs.Sum(i => i.Ranges.Sum(r => r.Duration.Ticks));
        if (total <= 0) throw new ShotwiseException(ErrorCode.CannotCutEntireMedia, "No media remains after cuts.");
        string temp = OutputFile.Temporary(output, ".mp4");
        try
        {
            using (var writer = new Mp4Writer(temp, w, h, fps, options, options.IncludeAudio))
            {
                long timeline = 0, videoIndex = 0;
                foreach (var input in inputs)
                {
                    using var reader = new VideoReader(input.Path);
                    foreach (var range in input.Ranges)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (range.Start < TimeSpan.Zero || range.End > reader.Info.Duration + TimeSpan.FromSeconds(1 / fps)) throw new ArgumentException("Edit range exceeds source duration.");
                        reader.Seek(range.Start.Ticks);
                        var current = reader.Read(ct);
                        var next = reader.Read(ct);
                        long end = timeline + range.Duration.Ticks;
                        using var audio = options.IncludeAudio ? new AudioRangeReader(input.Path, reader.Info.HasAudio, range, timeline) : null;
                        while ((long)Math.Round(videoIndex * 1e7 / fps) < end)
                        {
                            ct.ThrowIfCancellationRequested(); long t = (long)Math.Round(videoIndex * 1e7 / fps);
                            if (t < timeline) { videoIndex++; continue; }
                            long sourceTime = range.Start.Ticks + t - timeline;
                            while (next is { } n && n.Time <= sourceTime) { current = next; next = reader.Read(ct); }
                            if (current is null) throw new InvalidDataException("Source ended before the kept range.");
                            long duration = Math.Min(total, (long)Math.Round((videoIndex + 1) * 1e7 / fps)) - t;
                            writer.Video(current.Value.Image, t, duration); videoIndex++;
                            audio?.Advance(writer, Math.Min(range.Duration.Ticks, t + duration - timeline), ct);
                            progress?.Report(Math.Min(1, (double)(t + duration) / total));
                        }
                        audio?.Advance(writer, range.Duration.Ticks, ct);
                        timeline = end;
                    }
                }
                writer.Complete();
            }
            // The sources are untouched, so a destination that stays locked fails the export (temp removed below).
            ct.ThrowIfCancellationRequested(); OutputFile.Replace(temp, output); progress?.Report(1);
        }
        finally { OutputFile.Delete(temp); }
    }
    public Task WriteMetadataAsync(string mp4Path, VideoMetadata md, CancellationToken ct = default) => Task.Run(() => Mp4Metadata.Write(mp4Path, md, ct), ct);
}
