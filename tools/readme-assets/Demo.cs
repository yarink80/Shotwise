using System.Runtime.CompilerServices;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Rendering;
using SkiaSharp;
using static ReadmeAssets.Gfx;

namespace ReadmeAssets;

/// <summary>
/// docs/images/demo.gif: an annotation sequence on the sample capture. Every frame is a Shotwise document rendered by
/// Shotwise.Rendering (shape, step badges, callout with typed text, arrow, pixelate blur, stamp, cursor), and the frames
/// are encoded by Shotwise.Media's GIF encoder (changed-rectangle deltas, adaptive palettes).
/// </summary>
internal static class Demo
{
    private const double Fps = 25;
    private const double Duration = 10.6;

    private static readonly Argb Rose = Argb.Parse("#E11D48");
    private static readonly Argb Ink = Argb.Parse("#111827");

    public static async Task RunAsync(IGifService gif, SkiaDocumentRenderer renderer, IImageCodec codec)
    {
        var scene = SampleScenes.Dashboard();
        var doc = ShotDocument.FromImage(ToBgra(scene), null, codec);
        var page = doc.Pages[0];
        var stampSvg = Path.Combine(Repo, "src", "Shotwise.App", "Content", "stamps", "stickers", "stamp-approved-green.svg");
        var stampAsset = doc.Assets.Add(File.ReadAllBytes(stampSvg), "image/svg+xml");

        var frames = new List<(BgraImage Frame, TimeSpan Delay)>();
        byte[]? prev = null;
        var n = (int)Math.Round(Duration * Fps);
        for (var i = 0; i < n; i++)
        {
            var t = i / Fps;
            page.Annotations = Build(t, stampAsset);
            var img = renderer.RenderPage(doc, page, new RenderOptions());
            var delay = TimeSpan.FromSeconds(1 / Fps);
            if (prev is not null && img.Pixels.AsSpan().SequenceEqual(prev))
            {
                var last = frames[^1];
                frames[^1] = (last.Frame, last.Delay + delay);
                continue;
            }
            frames.Add((img, delay));
            prev = img.Pixels;
        }

        var path = Path.Combine(OutDir, "demo.gif");
        var o = new GifOptions(GifPreset.Custom, SampleScenes.W, SampleScenes.H, Fps, Loop: true, Dither: false);
        await gif.CreateFromFramesAsync(Enumerate(frames), path, o, CancellationToken.None);
        Record(path, SampleScenes.W, SampleScenes.H);
        Console.WriteLine($"    {frames.Count} distinct frames over {Duration:0.0} s");

        // Poster frame (the finished annotation) for places that do not autoplay GIFs.
        page.Annotations = Build(Duration, stampAsset);
        Save(FromBgra(renderer.RenderPage(doc, page, new RenderOptions())), "demo-poster.png");
    }

    private static async IAsyncEnumerable<(BgraImage, TimeSpan)> Enumerate(List<(BgraImage, TimeSpan)> frames, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var f in frames) { ct.ThrowIfCancellationRequested(); yield return f; }
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ timeline

    private static double Seg(double t, double a, double b) => Math.Clamp((t - a) / (b - a), 0, 1);
    private static double EaseOut(double x) => 1 - Math.Pow(1 - x, 3);
    private static double EaseInOut(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    private static double Back(double x) { const double c1 = 1.9, c3 = c1 + 1; return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2); }
    private static Point2 Lerp(Point2 a, Point2 b, double k) => new(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);

    private static readonly Point2 ShapeA = new(192, 122), ShapeB = new(444, 226);
    private static readonly Point2 CalloutA = new(424, 244), CalloutB = new(690, 292);
    private static readonly Point2 ArrowA = new(676, 240), ArrowB = new(838, 118);
    private static readonly Point2 BlurA = new(352, 466), BlurB = new(600, 584);
    private const string CalloutText = "Export the report here";

    private static List<Annotation> Build(double t, AssetId stamp)
    {
        var list = new List<Annotation>();
        Point2? cursor = null;

        // 1. rounded rectangle around the revenue card (dragged)
        var cursorPath = new (double T0, double T1, Point2 From, Point2 To)[]
        {
            (0.5, 1.2, new(640, 560), ShapeA),
            (2.2, 2.8, ShapeB, CalloutA),
            (4.3, 4.8, CalloutB, ArrowA),
            (5.8, 6.5, ArrowB, BlurA),
            (7.6, 8.1, BlurB, new(900, 590)),
        };
        if (t >= 0.5 && t < 8.1)
        {
            foreach (var (t0, t1, from, to) in cursorPath)
                if (t >= t0 && t <= t1) cursor = Lerp(from, to, EaseInOut(Seg(t, t0, t1)));
        }

        if (t >= 1.2)
        {
            var k = EaseOut(Seg(t, 1.2, 1.8));
            var end = Lerp(ShapeA, ShapeB, k);
            list.Add(new ShapeAnnotation
            {
                Shape = ShapeKind.RoundedRectangle, CornerRadius = 14, Start = ShapeA, End = end,
                Stroke = new StrokeStyle(Rose, 4), Fill = FillStyle.None,
                Shadow = new ShadowStyle(Argb.Black, 90, 2, 6, 0.25),
            });
            if (t < 1.8) cursor = end;
        }
        if (t >= 1.8 && t < 2.2) cursor = ShapeB;

        // step 1
        if (t >= 1.9) list.Add(Step(1, new Point2(ShapeB.X, ShapeA.Y), Back(Seg(t, 1.9, 2.25)), 36));

        // 2. callout dragged, then typed
        if (t >= 2.8)
        {
            var k = EaseOut(Seg(t, 2.8, 3.3));
            var end = Lerp(CalloutA, CalloutB, k);
            var chars = (int)Math.Floor(Seg(t, 3.35, 4.25) * CalloutText.Length);
            list.Add(new CalloutAnnotation
            {
                ShapePresetId = CalloutPresets.RoundedRectangle, Start = CalloutA, End = end,
                Fill = FillStyle.Solid(Argb.Parse("#FEF08A")), Stroke = new StrokeStyle(Argb.Parse("#CA8A04"), 2),
                Shadow = new ShadowStyle(Argb.Black, 90, 3, 10, 0.22),
                Text = RichText.FromPlain(CalloutText[..chars] + (t > 3.3 && t < 4.4 && ((int)(t * 4) % 2 == 0) ? "|" : ""), "Segoe UI", 19, Ink, bold: true),
                TextStyle = TextLayoutStyle.Default with { HAlign = HorizontalAlign.Center, VAlign = VerticalAlign.Middle, Padding = new Thickness2(10) },
            });
            if (t < 3.3) cursor = end;
            else if (t < 4.3) cursor = CalloutB;
        }

        // 3. arrow to the Export button, then step 2
        if (t >= 4.8)
        {
            var k = EaseOut(Seg(t, 4.8, 5.4));
            var end = Lerp(ArrowA, ArrowB, k);
            list.Add(new ArrowAnnotation
            {
                Start = ArrowA, End = end, EndCap = ArrowCap.Arrow, EndCapScale = 1.0,
                Stroke = new StrokeStyle(Rose, 5), Shadow = new ShadowStyle(Argb.Black, 90, 2, 6, 0.25),
            });
            if (t < 5.4) cursor = end;
            else if (t < 5.8) cursor = ArrowB;
        }
        if (t >= 5.5) list.Add(Step(2, new Point2(SampleScenes.ExportButton.Left - 4, SampleScenes.ExportButton.Top - 2), Back(Seg(t, 5.5, 5.85)), 34));

        // 4. pixelate the e-mail column (dragged), then step 3
        if (t >= 6.5)
        {
            var k = EaseOut(Seg(t, 6.5, 7.3));
            var end = Lerp(BlurA, BlurB, k);
            list.Add(new BlurAnnotation { Start = BlurA, End = end, Mode = BlurKind.Pixelate, Intensity = 70 });
            if (t < 7.3) cursor = end;
            else if (t < 7.6) cursor = BlurB;
        }
        if (t >= 7.4) list.Add(Step(3, new Point2(BlurA.X - 14, (BlurA.Y + BlurB.Y) / 2), Back(Seg(t, 7.4, 7.75)), 34));

        // 5. stamp slapped on
        if (t >= 8.2)
        {
            var k = Seg(t, 8.2, 8.5);
            var s = 1.0 + 0.45 * (1 - EaseOut(k));
            var c = new Point2(838, 270);
            double w = 176 * s, h = 64 * s;
            list.Add(new StampAnnotation
            {
                Asset = stamp, Start = new(c.X - w / 2, c.Y - h / 2), End = new(c.X + w / 2, c.Y + h / 2),
                RotationDegrees = -7, Opacity = Math.Min(1, 0.25 + k * 1.5),
                Shadow = new ShadowStyle(Argb.Black, 90, 2, 6, 0.18),
            });
        }

        if (cursor is { } cp) list.Add(new CursorAnnotation { Cursor = CursorKind.Arrow, Start = cp, End = cp, Scale = 1.15 });
        return list;
    }

    private static StepAnnotation Step(int value, Point2 center, double k, double size)
    {
        var s = Math.Max(0.5, size * k);
        return new StepAnnotation
        {
            Value = value, Style = StepStyle.Circle, Fill = FillStyle.Solid(Rose), TextColor = Argb.White,
            Start = new(center.X - s / 2, center.Y - s / 2), End = new(center.X + s / 2, center.Y + s / 2),
            Shadow = new ShadowStyle(Argb.Black, 90, 2, 5, 0.3),
        };
    }
}
