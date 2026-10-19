using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;
using Shotwise.Editor;

namespace Shotwise.Editor.Tests;

/// <summary>
/// Deterministic renderer fake: background colour + base image + annotations drawn as filled rotated-bounds boxes
/// (fill colour, else stroke colour). Outlines: polylines for line-like / freehand objects, filled rotated boxes otherwise.
/// </summary>
internal sealed class TestRenderer : IDocumentRenderer
{
    public int RenderCalls;
    public RenderOptions? LastOptions;
    public bool EmptyOutlines;

    public BgraImage RenderPage(ShotDocument doc, Page page, RenderOptions o)
    {
        RenderCalls++;
        LastOptions = o;
        var region = o.Region ?? page.CanvasRect;
        var w = Math.Max(1, (int)Math.Ceiling(region.Width));
        var h = Math.Max(1, (int)Math.Ceiling(region.Height));
        var img = new BgraImage(w, h);
        if (!o.TransparentBackground) img.Fill(page.Background.Color);
        if (page.Background.BaseImage is { } bi && doc.Assets.Contains(bi.Asset))
            img.DrawOver(doc.Assets.GetImage(bi.Asset), (int)Math.Round(bi.Offset.X - region.X), (int)Math.Round(bi.Offset.Y - region.Y));
        if (o.IncludeAnnotations)
        {
            foreach (var a in page.AllAnnotations())
            {
                if (o.Only is not null && !o.Only.Contains(a.Id)) continue;
                if (o.Exclude is not null && o.Exclude.Contains(a.Id)) continue;
                var r = a.RotatedBounds.ToRectIOutward();
                if (r.Width <= 0 || r.Height <= 0) r = new RectI(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
                var c = a.Fill.IsVisible ? a.Fill.Color : a.Stroke.Color;
                if (c.A == 0) continue;
                var box = BgraImage.CreateFilled(r.Width, r.Height, c);
                img.DrawOver(box, (int)Math.Round(r.X - region.X), (int)Math.Round(r.Y - region.Y));
            }
        }
        return img;
    }

    public void RenderAnnotation(object skCanvas, ShotDocument doc, Page page, Annotation a) { }

    public BgraImage RenderThumbnail(ShotDocument doc, int maxSide) => BgraImage.CreateFilled(4, 4, Argb.White);

    public Rect2 MeasureBounds(Annotation a) => a.RotatedBounds.Inflate(a.Stroke.Width / 2);

    public TextLayoutInfo LayoutText(RichText text, TextLayoutStyle style, double maxWidth)
    {
        var plain = text.PlainText;
        if (plain.Length == 0) return TextLayoutInfo.Empty;
        var fs = text.Runs.FirstOrDefault()?.FontSize ?? 16;
        var lines = plain.Split('\n');
        return new TextLayoutInfo(new Size2(lines.Max(l => l.Length) * fs * 0.5, lines.Length * fs * 1.2), Array.Empty<TextLineInfo>());
    }

    public Geometry2 GetOutline(Annotation a)
    {
        if (EmptyOutlines) return Geometry2.Empty;
        switch (a)
        {
            case ArrowAnnotation or LineAnnotation:
                return Geometry2.FromPolyline(new[] { a.Start, a.End }, a.Stroke.Width);
            case PenAnnotation p:
                return Geometry2.FromPolyline(p.Points, a.Stroke.Width);
            case HighlightAnnotation { Points: { Count: > 0 } hp }:
                return Geometry2.FromPolyline(hp, a.Stroke.Width);
        }
        var b = a is StepAnnotation st ? Rect2.FromPoints(st.Start, st.End) : a.Bounds;
        var c = b.Center;
        var pts = new[] { b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft }.Select(p => GeometryMath.Rotate(p, c, a.RotationDegrees)).ToArray();
        return new Geometry2(new[] { new GeometryFigure(pts, true, true, a.Stroke.IsVisible ? a.Stroke.Width : 0) });
    }
}

/// <summary>Adds a 2 px border around the image (content offset 2,2).</summary>
internal sealed class TestEffects : IEffectProcessor
{
    public int Calls;
    public EffectSpec? Last;
    public TimeSpan Delay = TimeSpan.Zero;

    public EffectResult Apply(BgraImage src, EffectSpec effect, DocumentMetadata? md)
    {
        Calls++;
        Last = effect;
        if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
        var dst = BgraImage.CreateFilled(src.Width + 4, src.Height + 4, Argb.Black);
        dst.Blit(src, 2, 2);
        return new EffectResult(dst, new Point2(2, 2));
    }

    public EffectResult ApplyAll(BgraImage src, IEnumerable<EffectSpec> effects, DocumentMetadata? md) => new(src, Point2.Zero);
    public BgraImage Preview(BgraImage src, EffectSpec e, int maxSide) => src;
}

internal sealed class TestCodec : IImageCodec
{
    public BgraImage Decode(ReadOnlySpan<byte> data) => PngCodec.Decode(data);
    public BgraImage Load(string path) => PngCodec.Decode(File.ReadAllBytes(path));
    public IReadOnlyList<BgraImage> LoadAllPages(string path) => new[] { Load(path) };
    public byte[] Encode(BgraImage img, ImageFormat fmt, EncodeOptions? o = null) => PngCodec.Encode(img);
    public void Save(BgraImage img, string path, ImageFormat fmt, EncodeOptions? o = null) => File.WriteAllBytes(path, Encode(img, fmt, o));
    public void SaveMultipage(IReadOnlyList<BgraImage> pages, string path, ImageFormat fmt, EncodeOptions? o = null) => throw new NotSupportedException();
    public IReadOnlyList<ImageFormatInfo> SupportedFormats { get; } = Array.Empty<ImageFormatInfo>();
}

/// <summary>Fills the masked pixels with <see cref="FillColor"/>; records calls.</summary>
internal sealed class FakeInpainting : IInpaintingService
{
    public int Calls;
    public InpaintQuality LastQuality;
    public Mask? LastMask;
    public Argb FillColor = Argb.Green;
    public InpaintQuality[] Available { get; set; } = { InpaintQuality.Fast, InpaintQuality.Quality };

    public Task<BgraImage> InpaintAsync(BgraImage img, Mask mask, InpaintQuality q = InpaintQuality.Fast, CancellationToken ct = default)
    {
        Calls++;
        LastQuality = q;
        LastMask = mask;
        var outp = img.Clone();
        for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
                if (mask.Contains(x, y)) outp.SetPixel(x, y, FillColor);
        return Task.FromResult(outp);
    }
}

internal sealed class FakeSmartMove : ISmartMoveService
{
    public List<SmartComponent> Components = new();
    public double AutoTolerance = 0.42;
    public int DetectCalls;
    public double LastTolerance;

    public Task<double> AutoToleranceAsync(BgraImage img, CancellationToken ct) => Task.FromResult(AutoTolerance);

    public Task<IReadOnlyList<SmartComponent>> DetectAsync(BgraImage img, double tolerance, CancellationToken ct)
    {
        DetectCalls++;
        LastTolerance = tolerance;
        return Task.FromResult<IReadOnlyList<SmartComponent>>(Components.ToList());
    }
}

internal sealed class FakeMagicWand : IMagicWandService
{
    public PointI LastSeed;
    public double LastTolerance;
    public bool LastContiguous;
    public int RefineCalls;
    public RectI Region = new(10, 10, 20, 20);

    public Mask Select(BgraImage img, PointI seed, double tolerance, bool contiguous)
    {
        LastSeed = seed;
        LastTolerance = tolerance;
        LastContiguous = contiguous;
        return Mask.FromRect(img.Width, img.Height, Region);
    }

    public Mask Refine(Mask m, int radius)
    {
        RefineCalls++;
        return m.Morph(radius);
    }
}

internal sealed class FakeSimplify : ISimplifyService
{
    public int GenerateCalls;
    public int LastDetail;
    public Palette? LastPalette;
    public int DominantCalls;
    public int ShapeCount = 3;

    public Task<IReadOnlyList<SimplifyShapeAnnotation>> GenerateAsync(BgraImage img, int detail, Palette palette, CancellationToken ct)
    {
        GenerateCalls++;
        LastDetail = detail;
        LastPalette = palette;
        var list = Enumerable.Range(0, ShapeCount).Select(i => new SimplifyShapeAnnotation
        {
            Start = new Point2(10 + i * 30, 10),
            End = new Point2(30 + i * 30, 20),
            Shape = i % 2 == 0 ? SimplifyShapeKind.Rectangle : SimplifyShapeKind.TextLeft,
            PaletteIndex = i,
            Fill = FillStyle.None,
        }).ToList();
        return Task.FromResult<IReadOnlyList<SimplifyShapeAnnotation>>(list);
    }

    public Task<Palette> DominantColorsAsync(BgraImage img, int k, CancellationToken ct)
    {
        DominantCalls++;
        return Task.FromResult(new Palette { Id = "img", Name = "Image", Colors = new() { Argb.Red, Argb.Green, Argb.Blue } });
    }
}

internal sealed class FakeSmartRedact : ISmartRedactService
{
    public List<RedactionHit> Hits = new();
    public int Calls;

    public Task<IReadOnlyList<RedactionHit>> FindAsync(BgraImage img, IReadOnlySet<RedactCategory> categories, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<RedactionHit>>(Hits.Where(h => categories.Contains(h.Category)).ToList());
    }
}

internal sealed class FakeOcr : IOcrService
{
    public Rect2? LastRegion;
    public string? LastLanguage;
    public OcrResult Result = new("en", "Hello", new[] { new OcrLine("Hello", new Rect2(10, 10, 50, 12), Array.Empty<OcrWord>()) }, 0);
    public IReadOnlyList<string> AvailableLanguages { get; } = new[] { "en" };
    public string EngineName => "fake";

    public Task<OcrResult> RecognizeAsync(BgraImage img, Rect2? region = null, string? language = null, CancellationToken ct = default)
    {
        LastRegion = region;
        LastLanguage = language;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeTextEdit : ITextEditService
{
    public List<(OcrLine, TextStyleSample)> Lines = new()
    {
        (new OcrLine("Total: 42", new Rect2(20, 20, 60, 14), Array.Empty<OcrWord>()), new TextStyleSample(Argb.Black, Argb.White, 14, true, "Arial")),
    };

    public Task<IReadOnlyList<(OcrLine Line, TextStyleSample Style)>> AnalyzeAsync(BgraImage img, Rect2? region, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<(OcrLine, TextStyleSample)>>(Lines.ToList());
}

internal sealed class FakeSettings : ISettingsService
{
    public AppSettings Current { get; } = new();
    public Task SaveAsync() => Task.CompletedTask;
    public void Update(Action<AppSettings> mutate) => mutate(Current);
    public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
    public Task ResetAsync() => Task.CompletedTask;
}

internal sealed class FakeStyleRepository : IStyleRepository
{
    public Theme ActiveTheme { get; set; } = new() { Name = "Test" };
    public IReadOnlyList<Palette> Palettes { get; } = Array.Empty<Palette>();
    public event EventHandler Changed { add { } remove { } }
    public Task<IReadOnlyList<Theme>> GetThemesAsync() => Task.FromResult<IReadOnlyList<Theme>>(new[] { ActiveTheme });
    public Task SetActiveThemeAsync(string themeId) => Task.CompletedTask;
    public Task SaveThemeAsync(Theme theme) => Task.CompletedTask;
    public Task DeleteThemeAsync(string id) => Task.CompletedTask;
    public Task<Theme> ImportAsync(string path) => Task.FromResult(ActiveTheme);
    public Task ExportAsync(Theme theme, string path) => Task.CompletedTask;
    public Task RestoreDefaultsAsync() => Task.CompletedTask;
    public Task SavePaletteAsync(Palette palette) => Task.CompletedTask;
    public Task DeletePaletteAsync(string id) => Task.CompletedTask;
}

/// <summary>A session over a solid-colour capture with all fakes wired; helpers for pointer gestures.</summary>
internal sealed class TestKit : IDisposable
{
    public TestRenderer Renderer { get; } = new();
    public TestEffects Effects { get; } = new();
    public FakeInpainting Inpainting { get; } = new();
    public FakeSmartMove SmartMove { get; } = new();
    public FakeMagicWand Wand { get; } = new();
    public FakeSimplify Simplify { get; } = new();
    public FakeSmartRedact Redact { get; } = new();
    public FakeOcr Ocr { get; } = new();
    public FakeTextEdit TextEdit { get; } = new();
    public FakeSettings Settings { get; } = new();
    public FakeStyleRepository Styles { get; } = new();
    public ShotDocument Doc { get; }
    public EditorSession S { get; }
    public List<EditorMessage> Messages { get; } = new();

    public TestKit(int width = 200, int height = 150, Argb? color = null, bool withServices = true, Func<EditorServices, EditorServices>? configure = null, BgraImage? image = null)
    {
        Doc = ShotDocument.FromImage(image ?? BgraImage.CreateFilled(width, height, color ?? Argb.White));
        var services = new EditorServices(Renderer, Effects, new TestCodec()) { Settings = Settings, Styles = Styles };
        if (withServices)
        {
            services = services with
            {
                Inpainting = Inpainting,
                SmartMove = SmartMove,
                MagicWand = Wand,
                Simplify = Simplify,
                SmartRedact = Redact,
                Ocr = Ocr,
                TextEdit = TextEdit,
            };
        }
        if (configure is not null) services = configure(services);
        S = new EditorSession(Doc, services);
        S.Message += (_, m) => Messages.Add(m);
    }

    public Page Page => S.ActivePage;

    public T Add<T>(T a) where T : Annotation
    {
        S.Execute(new AddAnnotationCommand(Page.Id, a));
        return (T)Page.Find(a.Id)!;
    }

    public Annotation Get(Guid id) => Page.Find(id) ?? throw new InvalidOperationException("missing " + id);

    public BgraImage Base() => Internal_ComposeBase();

    private BgraImage Internal_ComposeBase()
    {
        var size = new SizeI((int)Math.Ceiling(Page.CanvasSize.Width), (int)Math.Ceiling(Page.CanvasSize.Height));
        var dst = new BgraImage(size.Width, size.Height);
        if (Page.Background.BaseImage is { } bi) dst.Blit(Doc.Assets.GetImage(bi.Asset), (int)Math.Round(bi.Offset.X), (int)Math.Round(bi.Offset.Y));
        return dst;
    }

    public static PointerInput P(double x, double y, ModifierKeys mods = ModifierKeys.None, double zoom = 1, PointerButton button = PointerButton.Left, int clicks = 1)
        => new(new Point2(x, y), button, mods, clicks) { Zoom = zoom };

    public static PointerInput Hover(double x, double y, ModifierKeys mods = ModifierKeys.None, double zoom = 1)
        => new(new Point2(x, y), PointerButton.None, mods, 0) { Zoom = zoom };

    public void Click(double x, double y, ModifierKeys mods = ModifierKeys.None, double zoom = 1, PointerButton button = PointerButton.Left)
    {
        S.PointerDown(P(x, y, mods, zoom, button));
        S.PointerUp(P(x, y, mods, zoom, button));
    }

    public void Drag(double x0, double y0, double x1, double y1, ModifierKeys mods = ModifierKeys.None, double zoom = 1, int steps = 4)
    {
        S.PointerDown(P(x0, y0, mods, zoom));
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            S.PointerMove(P(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, mods, zoom));
        }
        S.PointerUp(P(x1, y1, mods, zoom));
    }

    public void Key(VirtualKey key, ModifierKeys mods = ModifierKeys.None) => S.KeyDown(new KeyInput(key, mods));

    public Task Idle() => S.WhenIdleAsync();

    public void Dispose() => S.Dispose();
}
