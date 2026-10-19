using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;

namespace Shotwise.Media;

/// <summary>GIF encoding/decoding (SPEC MED-04/05). Dithering comes from <see cref="GifOptions.DitherMode"/> (Core).</summary>
public sealed class GifService(IVideoService video, ILogger<GifService> logger) : IGifService
{
    public GifOptions DefaultsFor(GifPreset preset, int srcWidth, int srcHeight) => DefaultsFor(preset, srcWidth, srcHeight, 30);

    public GifOptions DefaultsFor(GifPreset preset, int srcWidth, int srcHeight, double srcFps)
    {
        if (srcWidth < 1 || srcHeight < 1) throw new ArgumentOutOfRangeException(nameof(srcWidth));
        // Custom keeps the source size and frame rate, capped at 30 fps (higher rates bloat GIFs; most viewers clamp them).
        double customFps = double.IsFinite(srcFps) && srcFps > 0 ? Math.Min(30, srcFps) : 15;
        return preset switch
        {
            GifPreset.ScreenVideo => new(preset, 1280, 720, 5),
            GifPreset.HighMotion => new(preset, 640, 360, 15),
            GifPreset.ReducedFileSize => new(preset, 400, Math.Max(1, (int)Math.Round(400.0 * srcHeight / srcWidth)), 3),
            GifPreset.Custom => new(preset, srcWidth, srcHeight, customFps),
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };
    }
    public async Task CreateFromVideoAsync(string videoPath, string gifPath, GifOptions o, IProgress<double>? p, CancellationToken ct)
    {
        Validate(o);
        var info = await video.ProbeAsync(videoPath, ct).ConfigureAwait(false);
        var range = (o.Range ?? new(TimeSpan.Zero, info.Duration)).Clamp(new(TimeSpan.Zero, info.Duration));
        if (range.IsEmpty) throw new ArgumentException("GIF range is empty.");
        if (range.Duration.TotalSeconds > 30) logger.LogWarning("GIF duration exceeds 30 seconds: {Duration}", range.Duration);
        async IAsyncEnumerable<(BgraImage, TimeSpan)> Frames([EnumeratorCancellation] CancellationToken token)
        {
            long n = (long)Math.Ceiling(range.Duration.TotalSeconds * o.Fps);
            for (long i = 0; i < n; i++)
            {
                token.ThrowIfCancellationRequested();
                var offset = TimeSpan.FromSeconds(i / o.Fps);
                var delay = TimeSpan.FromSeconds(Math.Min(1 / o.Fps, (range.Duration - offset).TotalSeconds));
                yield return (await video.GetFrameAsync(videoPath, range.Start + offset, token).ConfigureAwait(false), delay);
                p?.Report((i + 1.0) / n);
            }
        }
        await CreateFromFramesAsync(Frames(ct), gifPath, o, ct).ConfigureAwait(false);
    }
    public Task CreateFromFramesAsync(IAsyncEnumerable<(BgraImage Frame, TimeSpan Delay)> frames, string gifPath, GifOptions o, CancellationToken ct)
    {
        Validate(o);
        var ditherMode = o.DitherMode;
        bool dither = ditherMode != GifDitherMode.None;
        return Task.Run(async () =>
        {
            string temp = OutputFile.Temporary(gifPath, ".gif");
            try
            {
                BgraImage? last = null;
                using (var stream = File.Create(temp))
                using (var encoder = new GifEncoder(stream, o.Width, o.Height, o.Loop))
                {
                    BgraImage? pending = null, first = null; TimeSpan pendingDelay = default;
                    bool NeedsClear(BgraImage from, BgraImage to)
                    {
                        for (int i = 3; i < from.Pixels.Length; i += 4) if (from.Pixels[i] >= 128 && to.Pixels[i] < 128) return true;
                        return false;
                    }
                    void Push(BgraImage image, TimeSpan delay)
                    {
                        if (pending is not null) encoder.Write(pending, pendingDelay, dither, ct, ditherMode, NeedsClear(pending, image));
                        first ??= image; pending = image; pendingDelay = delay;
                    }
                    await foreach (var (frame, delay) in frames.WithCancellation(ct).ConfigureAwait(false))
                    {
                        ct.ThrowIfCancellationRequested();
                        last = PixelScaler.Resize(frame, o.Width, o.Height);
                        Push(last, delay);
                    }
                    if (last is null) throw new ArgumentException("At least one frame is required.", nameof(frames));
                    if (o.FadeToBlack)
                    {
                        int n = Math.Max(1, (int)Math.Round(o.Fps * .5));
                        for (int i = 1; i <= n; i++)
                        {
                            var fade = last.Clone();
                            for (int j = 0; j < fade.Pixels.Length; j += 4) for (int c = 0; c < 3; c++) fade.Pixels[j + c] = (byte)(fade.Pixels[j + c] * (n - i) / n);
                            Push(fade, TimeSpan.FromSeconds(.5 / n));
                        }
                    }
                    encoder.Write(pending!, pendingDelay, dither, ct, ditherMode, o.Loop && NeedsClear(pending!, first!));
                }
                ct.ThrowIfCancellationRequested(); File.Move(temp, gifPath, true);
                logger.LogInformation("Created GIF {Path}", gifPath);
            }
            finally { OutputFile.Delete(temp); }
        }, ct);
    }
    private static void Validate(GifOptions o)
    {
        if (o.Width is < 1 or > 16384 || o.Height is < 1 or > 16384 || !double.IsFinite(o.Fps) || o.Fps is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(o));
    }
    public Task<AnimatedImage> DecodeAsync(string gifPath, CancellationToken ct) => Task.Run(() =>
    {
        using var stream = File.OpenRead(gifPath);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int Read(BitmapMetadata? md, string query, int fallback = 0) => md?.GetQuery(query) is { } value ? Convert.ToInt32(value) : fallback;
        var metadata = decoder.Metadata as BitmapMetadata;
        int w = Read(metadata, "/logscrdesc/Width", decoder.Frames[0].PixelWidth), h = Read(metadata, "/logscrdesc/Height", decoder.Frames[0].PixelHeight);
        if ((long)w * h * decoder.Frames.Count > 256_000_000) throw new InvalidDataException("GIF exceeds decoded memory limit.");
        var canvas = new BgraImage(w, h); var frames = new List<BgraImage>(); var delays = new List<TimeSpan>();
        int backgroundIndex = Read(metadata, "/logscrdesc/BackgroundColorIndex");
        Color background = decoder.Palette?.Colors.ElementAtOrDefault(backgroundIndex) ?? Colors.Transparent;
        void Background(BgraImage target, int left, int top, int width, int height, BitmapMetadata? md)
        {
            bool transparent = Read(md, "/grctlext/TransparencyFlag") != 0;
            for (int y = top; y < top + height; y++) for (int x = left; x < left + width; x++)
            {
                int i = (y * w + x) * 4;
                target.Pixels[i] = transparent ? (byte)0 : background.B; target.Pixels[i + 1] = transparent ? (byte)0 : background.G;
                target.Pixels[i + 2] = transparent ? (byte)0 : background.R; target.Pixels[i + 3] = transparent ? (byte)0 : background.A;
            }
        }
        Background(canvas, 0, 0, w, h, decoder.Frames[0].Metadata as BitmapMetadata);
        foreach (var frame in decoder.Frames)
        {
            ct.ThrowIfCancellationRequested();
            var md = frame.Metadata as BitmapMetadata;
            int left = Read(md, "/imgdesc/Left"), top = Read(md, "/imgdesc/Top"), disposal = Read(md, "/grctlext/Disposal");
            var backup = disposal == 3 ? canvas.Clone() : null;
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            var image = new BgraImage(converted.PixelWidth, converted.PixelHeight);
            if (left < 0 || top < 0 || (long)left + image.Width > w || (long)top + image.Height > h) throw new InvalidDataException("GIF frame exceeds its logical canvas.");
            converted.CopyPixels(image.Pixels, image.Width * 4, 0);
            for (int y = 0; y < image.Height && y + top < h; y++)
            for (int x = 0; x < image.Width && x + left < w; x++)
            {
                int src = (y * image.Width + x) * 4, dst = ((y + top) * w + x + left) * 4, a = image.Pixels[src + 3];
                for (int c = 0; c < 4; c++) canvas.Pixels[dst + c] = (byte)Math.Min(255, image.Pixels[src + c] + canvas.Pixels[dst + c] * (255 - a) / 255);
            }
            frames.Add(canvas.Clone()); delays.Add(TimeSpan.FromMilliseconds(Math.Max(1, Read(md, "/grctlext/Delay", 10)) * 10));
            if (disposal == 2) Background(canvas, left, top, image.Width, image.Height, md);
            if (backup is not null) canvas = backup;
        }
        int loop = 1;
        if (metadata?.GetQuery("/appext/application") is byte[] app && System.Text.Encoding.ASCII.GetString(app) == "NETSCAPE2.0" && metadata.GetQuery("/appext/data") is byte[] data && data.Length >= 4) loop = data[2] | data[3] << 8;
        return new AnimatedImage(w, h, frames, delays, loop);
    }, ct);
}
