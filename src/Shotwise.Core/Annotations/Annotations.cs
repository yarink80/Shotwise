using System.Text.Json.Serialization;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Core.Annotations;

public enum ArrowCap { None, Arrow, OpenArrow, Triangle, Round, Square, Diamond, TBar }
public enum ShapeKind { Rectangle, RoundedRectangle, Ellipse, Polygon }
public enum StepStyle { Circle, Square, CirclePointer, SquarePointer }
public enum StepSequence { Number, Upper, Lower }
public enum PenNib { Circle, Square, Horizontal, Vertical, Diagonal }
public enum BlurKind { Gaussian, Pixelate, Solid }
public enum CursorKind { Arrow, Hand, IBeam, Wait, Cross, Move }
public enum SimplifyShapeKind { Rectangle, RoundedRectangle, Ellipse, TextLeft, TextCenter, TextRight, Placeholder }

/// <summary>Callout tail: tip point in canvas pixels, base width, curved or straight.</summary>
public sealed record CalloutTail(Point2 Tip, double Width = 20, bool Curved = false);

public sealed class ArrowAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Arrow;
    public ArrowCap StartCap { get; set; } = ArrowCap.None;
    public ArrowCap EndCap { get; set; } = ArrowCap.Arrow;
    public double StartCapScale { get; set; } = 1.0;
    public double EndCapScale { get; set; } = 1.0;
    public bool Bezier { get; set; }
    public Point2? Control1 { get; set; }
    public Point2? Control2 { get; set; }

    [JsonIgnore] public override Rect2 Bounds => LineBounds(Start, End, Control1, Control2);

    public override Annotation DeepClone() => CloneBase<ArrowAnnotation>();

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        Control1 = Control1?.Offset(dx, dy);
        Control2 = Control2?.Offset(dx, dy);
    }

    internal static Rect2 LineBounds(Point2 s, Point2 e, Point2? c1, Point2? c2)
    {
        var r = Rect2.FromPoints(s, e);
        if (c1 is { } a) r = r.Union(a);
        if (c2 is { } b) r = r.Union(b);
        return r;
    }
}

public sealed class LineAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Line;
    public bool DoubleLine { get; set; }
    public bool Bezier { get; set; }
    public Point2? Control1 { get; set; }
    public Point2? Control2 { get; set; }
    /// <summary>Only <see cref="ArrowCap.None"/>, <see cref="ArrowCap.Round"/> and <see cref="ArrowCap.Square"/> are meaningful for lines.</summary>
    public ArrowCap StartCap { get; set; } = ArrowCap.Round;
    public ArrowCap EndCap { get; set; } = ArrowCap.Round;

    [JsonIgnore] public override Rect2 Bounds => ArrowAnnotation.LineBounds(Start, End, Control1, Control2);
    public override Annotation DeepClone() => CloneBase<LineAnnotation>();

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        Control1 = Control1?.Offset(dx, dy);
        Control2 = Control2?.Offset(dx, dy);
    }
}

public sealed class ShapeAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Shape;
    public ShapeKind Shape { get; set; } = ShapeKind.Rectangle;
    public double CornerRadius { get; set; } = 12;
    /// <summary>Polygon vertices (only for <see cref="ShapeKind.Polygon"/>).</summary>
    public List<Point2> Points { get; set; } = new();

    [JsonIgnore]
    public override Rect2 Bounds => Shape == ShapeKind.Polygon && Points.Count > 0 ? Rect2.FromPoints(Points) : Rect2.FromPoints(Start, End);

    public override Annotation DeepClone()
    {
        var c = CloneBase<ShapeAnnotation>();
        c.Points = new List<Point2>(Points);
        return c;
    }

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        TranslatePoints(Points, dx, dy);
    }
}

public sealed class CalloutAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Callout;
    /// <summary>Id of a callout shape preset in the renderer's geometry library (e.g. "rectangle", "speech", "cloud").</summary>
    public string ShapePresetId { get; set; } = "rounded-rectangle";
    public List<CalloutTail> Tails { get; set; } = new();
    public RichText Text { get; set; } = new();
    public TextLayoutStyle TextStyle { get; set; } = TextLayoutStyle.Default with { HAlign = HorizontalAlign.Center, VAlign = VerticalAlign.Middle };
    public string? PlaceholderText { get; set; }

    [JsonIgnore]
    public override Rect2 Bounds => Rect2.FromPoints(Start, End);

    public override Annotation DeepClone()
    {
        var c = CloneBase<CalloutAnnotation>();
        c.Tails = new List<CalloutTail>(Tails);
        c.Text = Text.Clone();
        return c;
    }

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        for (var i = 0; i < Tails.Count; i++) Tails[i] = Tails[i] with { Tip = Tails[i].Tip.Offset(dx, dy) };
    }
}

public sealed class TextAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Text;
    public RichText Text { get; set; } = new();
    public TextLayoutStyle TextStyle { get; set; } = TextLayoutStyle.Default;
    public TextDeselectBehavior DeselectBehavior { get; set; } = TextDeselectBehavior.DeleteIfEmpty;
    public string? PlaceholderText { get; set; }

    public TextAnnotation() { Stroke = StrokeStyle.None; }

    [JsonIgnore] public override Rect2 Bounds => Rect2.FromPoints(Start, End);

    public override Annotation DeepClone()
    {
        var c = CloneBase<TextAnnotation>();
        c.Text = Text.Clone();
        return c;
    }
}

public sealed class StampAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Stamp;
    /// <summary>Vector/raster stamp content (svg/pdf/png) in the document asset store.</summary>
    public AssetId Asset { get; set; }
    /// <summary>Catalog id the stamp came from (for "add to favourites" etc.).</summary>
    public string? StampId { get; set; }
    public bool PreserveAspect { get; set; } = true;

    public StampAnnotation() { Stroke = StrokeStyle.None; }

    [JsonIgnore] public override Rect2 Bounds => Rect2.FromPoints(Start, End);
    public override Annotation DeepClone() => CloneBase<StampAnnotation>();
}

public sealed class StepAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Step;
    public StepStyle Style { get; set; } = StepStyle.Circle;
    public StepSequence Sequence { get; set; } = StepSequence.Number;
    public int Value { get; set; } = 1;
    public Argb TextColor { get; set; } = Argb.White;
    public string FontFamily { get; set; } = "Segoe UI";
    /// <summary>Tip of the pointer tail for pointer styles.</summary>
    public Point2? PointerTip { get; set; }

    public StepAnnotation()
    {
        Fill = FillStyle.Solid(Argb.Red);
        Stroke = StrokeStyle.None;
    }

    /// <summary>Badge label: "3", "C" or "c" (27 → "AA").</summary>
    [JsonIgnore] public string Label => FormatValue(Value, Sequence);

    public static string FormatValue(int value, StepSequence seq)
    {
        if (seq == StepSequence.Number) return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value < 1) value = 1;
        var baseChar = seq == StepSequence.Upper ? 'A' : 'a';
        var s = "";
        var n = value;
        while (n > 0) { n--; s = (char)(baseChar + n % 26) + s; n /= 26; }
        return s;
    }

    [JsonIgnore]
    public override Rect2 Bounds
    {
        get
        {
            var r = Rect2.FromPoints(Start, End);
            return PointerTip is { } t && (Style is StepStyle.CirclePointer or StepStyle.SquarePointer) ? r.Union(t) : r;
        }
    }

    public override Annotation DeepClone() => CloneBase<StepAnnotation>();

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        PointerTip = PointerTip?.Offset(dx, dy);
    }
}

public sealed class PenAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Pen;
    public List<Point2> Points { get; set; } = new();
    public PenNib Nib { get; set; } = PenNib.Circle;
    public bool Smoothing { get; set; } = true;
    public bool Antialias { get; set; } = true;

    [JsonIgnore] public override Rect2 Bounds => Points.Count > 0 ? Rect2.FromPoints(Points) : Rect2.FromPoints(Start, End);

    public override Annotation DeepClone()
    {
        var c = CloneBase<PenAnnotation>();
        c.Points = new List<Point2>(Points);
        return c;
    }

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        TranslatePoints(Points, dx, dy);
    }
}

/// <summary>Highlighter (multiply blend). Rectangle mode uses <see cref="Annotation.Fill"/>; freehand mode (Points != null) uses <see cref="Annotation.Stroke"/>.</summary>
public sealed class HighlightAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Highlight;
    /// <summary>Null = rectangle highlight spanning Start/End.</summary>
    public List<Point2>? Points { get; set; }

    public HighlightAnnotation()
    {
        Fill = FillStyle.Solid(Argb.HighlightYellow);
        Stroke = new StrokeStyle(Argb.HighlightYellow, 16);
    }

    [JsonIgnore] public override Rect2 Bounds => Points is { Count: > 0 } p ? Rect2.FromPoints(p) : Rect2.FromPoints(Start, End);

    public override Annotation DeepClone()
    {
        var c = CloneBase<HighlightAnnotation>();
        c.Points = Points is null ? null : new List<Point2>(Points);
        return c;
    }

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        if (Points is not null) TranslatePoints(Points, dx, dy);
    }
}

/// <summary>Magnifier lens: lens geometry at Start/End shows <see cref="SourceRect"/> scaled.</summary>
public sealed class MagnifyAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Magnify;
    public ShapeKind LensShape { get; set; } = ShapeKind.Ellipse;
    public double Scale { get; set; } = 2.0;
    public Rect2 SourceRect { get; set; }
    public bool ShowConnector { get; set; } = true;

    public MagnifyAnnotation()
    {
        Stroke = new StrokeStyle(Argb.DarkGray, 2);
        Shadow = ShadowStyle.Default;
    }

    [JsonIgnore] public override Rect2 Bounds => Rect2.FromPoints(Start, End);
    public override Annotation DeepClone() => CloneBase<MagnifyAnnotation>();

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        SourceRect = SourceRect.Offset(dx, dy);
    }
}

public sealed class BlurAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Blur;
    public ShapeKind Region { get; set; } = ShapeKind.Rectangle;
    public BlurKind Mode { get; set; } = BlurKind.Gaussian;
    /// <summary>Strength 1..100 (blur radius / pixel block size scale).</summary>
    public double Intensity { get; set; } = 50;
    public Argb SolidColor { get; set; } = Argb.Black;
    /// <summary>Set when created by Smart Redact.</summary>
    public RedactCategory? Category { get; set; }

    public BlurAnnotation() { Stroke = StrokeStyle.None; }

    [JsonIgnore] public override Rect2 Bounds => Rect2.FromPoints(Start, End);
    public override Annotation DeepClone() => CloneBase<BlurAnnotation>();
}

public sealed class ImageAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Image;
    public AssetId Asset { get; set; }
    /// <summary>Optional crop of the source asset in asset pixels.</summary>
    public RectI? SourceCrop { get; set; }

    public ImageAnnotation() { Stroke = StrokeStyle.None; }

    [JsonIgnore] public override Rect2 Bounds => Rect2.FromPoints(Start, End);
    public override Annotation DeepClone() => CloneBase<ImageAnnotation>();
}

public sealed class CursorAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.Cursor;
    public CursorKind Cursor { get; set; } = CursorKind.Arrow;
    public double Scale { get; set; } = 1.0;

    public CursorAnnotation() { Stroke = StrokeStyle.None; }

    [JsonIgnore] public override Rect2 Bounds => Rect2.FromPoints(Start, End);
    public override Annotation DeepClone() => CloneBase<CursorAnnotation>();
}

/// <summary>Template drop zone: an image slot inside a template section; content can be panned/zoomed inside the zone.</summary>
public sealed class DropZoneAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.DropZone;
    public Guid SectionId { get; set; }
    public AssetId? Content { get; set; }
    public Point2 ContentOffset { get; set; }
    public double ContentZoom { get; set; } = 1;
    public double CornerRadius { get; set; }

    public DropZoneAnnotation()
    {
        Stroke = new StrokeStyle(Argb.Gray, 1, StrokeDash.Dash);
        IgnoresUndoAll = true;
    }

    [JsonIgnore] public override Rect2 Bounds => Rect2.FromPoints(Start, End);
    public override Annotation DeepClone() => CloneBase<DropZoneAnnotation>();
}

/// <summary>Shape produced by the Simplify tool (manual or auto) using a palette colour index.</summary>
public sealed class SimplifyShapeAnnotation : Annotation
{
    [JsonIgnore] public override AnnotationKind Kind => AnnotationKind.SimplifyShape;
    public SimplifyShapeKind Shape { get; set; } = SimplifyShapeKind.Rectangle;
    public int PaletteIndex { get; set; }
    /// <summary>Placeholder stamp id for <see cref="SimplifyShapeKind.Placeholder"/> (avatar, image, video, cursor, window-buttons…).</summary>
    public string? PlaceholderId { get; set; }

    public SimplifyShapeAnnotation() { Stroke = StrokeStyle.None; Fill = FillStyle.Solid(Argb.LightGray); }

    [JsonIgnore] public override Rect2 Bounds => Rect2.FromPoints(Start, End);
    public override Annotation DeepClone() => CloneBase<SimplifyShapeAnnotation>();
}
