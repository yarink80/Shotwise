using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shotwise-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class TestData
{
    public static BgraImage Gradient(int w, int h)
    {
        var img = new BgraImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                img.SetPixel(x, y, Argb.FromArgb((byte)(128 + (x * 127 / Math.Max(1, w - 1))), (byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), 64));
        return img;
    }

    /// <summary>A document containing one annotation of every kind plus a template section.</summary>
    public static ShotDocument AllKindsDocument()
    {
        var doc = ShotDocument.FromImage(Gradient(64, 48), DocumentMetadata.Create(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)) with
        {
            AppName = "Notepad",
            WindowTitle = "Untitled",
            Url = "https://example.com/a?b=c",
            Tags = new[] { "alpha", "beta" },
            CursorPosition = new PointI(10, 20),
            OcrText = "Hello",
        });
        var page = doc.Pages[0];
        var stampAsset = doc.Assets.Add(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"), "image/svg+xml");
        var imageAsset = doc.Assets.AddImage(Gradient(8, 8));

        page.Annotations.Add(new ArrowAnnotation { Start = new(1, 2), End = new(30, 40), StartCap = ArrowCap.Diamond, EndCap = ArrowCap.Triangle, Bezier = true, Control1 = new(5, 5), Control2 = new(9, 9), Shadow = ShadowStyle.Default, Stroke = new StrokeStyle(Argb.Blue, 4, StrokeDash.DashDot) });
        page.Annotations.Add(new LineAnnotation { Start = new(0, 0), End = new(10, 0), DoubleLine = true, EndCap = ArrowCap.Square });
        page.Annotations.Add(new ShapeAnnotation { Shape = ShapeKind.Polygon, Points = { new(0, 0), new(10, 0), new(5, 8) }, Fill = FillStyle.FromGradient(GradientFill.Linear(Argb.Red, Argb.Yellow, 45)) });
        page.Annotations.Add(new CalloutAnnotation
        {
            Start = new(2, 2), End = new(40, 20), ShapePresetId = CalloutPresets.Cloud,
            Tails = { new CalloutTail(new(50, 50), 12, true) },
            Text = new RichText(new[] { new TextRun { Text = "Hi ", Bold = true }, new TextRun { Text = "there\nline2", Italic = true, Color = Argb.Red, FontSize = 22 } }),
            TextStyle = new TextLayoutStyle { HAlign = HorizontalAlign.Right, VAlign = VerticalAlign.Bottom, OutlineColor = Argb.White, OutlineWidth = 2, Padding = new Thickness2(1, 2, 3, 4) },
        });
        page.Annotations.Add(new TextAnnotation { Start = new(5, 5), End = new(60, 30), Text = RichText.FromPlain("Text box"), DeselectBehavior = TextDeselectBehavior.Keep, RotationDegrees = 15, Opacity = 0.5 });
        page.Annotations.Add(new StampAnnotation { Start = new(1, 1), End = new(17, 17), Asset = stampAsset, StampId = "arrows/right" });
        page.Annotations.Add(new StepAnnotation { Start = new(3, 3), End = new(23, 23), Style = StepStyle.SquarePointer, Sequence = StepSequence.Upper, Value = 28, PointerTip = new(40, 40) });
        page.Annotations.Add(new PenAnnotation { Points = { new(1, 1), new(2, 3), new(4, 4) }, Nib = PenNib.Diagonal, Smoothing = false, IsLocked = true });
        page.Annotations.Add(new HighlightAnnotation { Points = new List<Point2> { new(0, 10), new(30, 10) } });
        page.Annotations.Add(new MagnifyAnnotation { Start = new(10, 10), End = new(30, 30), Scale = 3, SourceRect = new Rect2(1, 2, 3, 4), LensShape = ShapeKind.RoundedRectangle });
        page.Annotations.Add(new BlurAnnotation { Start = new(0, 0), End = new(10, 10), Mode = BlurKind.Solid, SolidColor = Argb.Purple, Category = RedactCategory.Email });
        page.Annotations.Add(new ImageAnnotation { Start = new(0, 0), End = new(8, 8), Asset = imageAsset, SourceCrop = new RectI(1, 1, 4, 4) });
        page.Annotations.Add(new CursorAnnotation { Start = new(20, 20), End = new(32, 40), Cursor = CursorKind.IBeam, Scale = 1.5 });
        var group = Guid.NewGuid();
        page.Annotations.Add(new SimplifyShapeAnnotation { Start = new(0, 40), End = new(50, 44), Shape = SimplifyShapeKind.TextCenter, PaletteIndex = 3, GroupId = group });
        var section = new TemplateSection { Type = SectionType.Header, Bounds = new Rect2(0, 0, 64, 10), Background = Argb.LightGray };
        section.Graphics.Add(new DropZoneAnnotation { SectionId = section.Id, Start = new(1, 1), End = new(20, 9), Content = imageAsset, ContentZoom = 1.25, ContentOffset = new(2, 3) });
        page.Sections.Add(section);
        page.StepCounters = new StepCounters(4, 2, 7);
        page.CornerRadius = 6;
        page.CanvasShadow = ShadowStyle.Default with { Blur = 12 };
        return doc;
    }
}
