using Microsoft.Extensions.Logging;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Library.Catalog;

namespace Shotwise.App.Hosting;

/// <summary>
/// Library video/GIF thumbnails (CONTRACT A19): adapts Media's <see cref="IVideoService"/> (MP4 frame at ~10 %) and
/// <see cref="IGifService"/> (first decoded frame) to the Library's optional <see cref="IMediaThumbnailSource"/> hook.
/// </summary>
public sealed class MediaThumbnailSource : IMediaThumbnailSource
{
    private const int ThumbnailHeight = 320;
    private readonly IVideoService _video;
    private readonly IGifService _gif;
    private readonly ILogger<MediaThumbnailSource> _log;

    public MediaThumbnailSource(IVideoService video, IGifService gif, ILogger<MediaThumbnailSource> log)
    {
        _video = video;
        _gif = gif;
        _log = log;
    }

    public async Task<BgraImage?> GetThumbnailAsync(string path, MediaKind kind, CancellationToken ct = default)
    {
        try
        {
            if (kind == MediaKind.Gif || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                var animation = await _gif.DecodeAsync(path, ct).ConfigureAwait(false);
                return animation.Frames.Count > 0 ? animation.Frames[0] : null;
            }
            if (kind != MediaKind.Video) return null;
            var info = await _video.ProbeAsync(path, ct).ConfigureAwait(false);
            // A frame a little into the clip is more representative than the (often black) first frame.
            var at = info.Duration > TimeSpan.FromSeconds(2) ? TimeSpan.FromTicks(info.Duration.Ticks / 10) : TimeSpan.Zero;
            var frame = await _video.GetFrameAsync(path, at, ct).ConfigureAwait(false);
            if (frame.Height <= ThumbnailHeight) return frame;
            int w = Math.Max(1, (int)Math.Round(frame.Width * (ThumbnailHeight / (double)frame.Height)));
            return ImageOps.ResizeArea(frame, w, ThumbnailHeight);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "No media thumbnail for {Path}", path);
            return null;
        }
    }
}
