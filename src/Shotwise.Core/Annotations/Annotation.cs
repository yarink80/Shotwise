using System.Text.Json;
using System.Text.Json.Serialization;
using Shotwise.Core.Geometry;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Annotations;

/// <summary>Discriminator for polymorphic annotation JSON (written as camelCase "kind").</summary>
public enum AnnotationKind { Arrow, Line, Shape, Callout, Text, Stamp, Step, Pen, Highlight, Magnify, Blur, Image, Cursor, DropZone, SimplifyShape }

/// <summary>
/// Base class of every vector object on a page. List order in <c>Page.Annotations</c> is z-order (0 = bottom).
/// <para><see cref="Start"/>/<see cref="End"/> define the (unrotated) bounding box or line endpoints; point-list kinds
/// (pen, polygon, freehand highlight) use their own point lists. Rotation is applied around the centre of <see cref="Bounds"/>.</para>
/// <para>JSON: polymorphic via <c>"kind"</c>; unknown fields are preserved in <see cref="Extra"/> and written back.</para>
/// </summary>
[JsonConverter(typeof(AnnotationJsonConverter))]
public abstract class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonIgnore] public abstract AnnotationKind Kind { get; }

    public Point2 Start { get; set; }
    public Point2 End { get; set; }
    public double RotationDegrees { get; set; }
    public StrokeStyle Stroke { get; set; } = StrokeStyle.Default;
    public FillStyle Fill { get; set; } = FillStyle.None;
    /// <summary>Whole-object opacity 0..1.</summary>
    public double Opacity { get; set; } = 1.0;
    public ShadowStyle? Shadow { get; set; }
    public bool IsLocked { get; set; }
    public Guid? GroupId { get; set; }
    /// <summary>When true, Undo All does not remove this object (template scaffolding).</summary>
    public bool IgnoresUndoAll { get; set; }

    /// <summary>Unknown JSON fields preserved for forward compatibility.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Definition bounds in canvas pixels, before rotation, excluding caps/shadow/stroke (see IDocumentRenderer.MeasureBounds for visual bounds).</summary>
    [JsonIgnore] public abstract Rect2 Bounds { get; }

    /// <summary>Axis-aligned bounds after applying <see cref="RotationDegrees"/>.</summary>
    [JsonIgnore] public Rect2 RotatedBounds => GeometryMath.RotatedBounds(Bounds, RotationDegrees);

    /// <summary>Deep copy that keeps the same <see cref="Id"/> (used for before/after undo snapshots).</summary>
    public abstract Annotation DeepClone();

    /// <summary>Deep copy with a fresh <see cref="Id"/> (duplicate/paste). Group ids are cleared.</summary>
    public Annotation CloneWithNewId()
    {
        var c = DeepClone();
        c.Id = Guid.NewGuid();
        c.GroupId = null;
        return c;
    }

    /// <summary>Rigidly moves every geometric field of the object (used by crop/canvas/effect offsets and drags).</summary>
    public virtual void Translate(double dx, double dy)
    {
        Start = Start.Offset(dx, dy);
        End = End.Offset(dx, dy);
    }

    /// <summary>Clone helper for derived classes: memberwise copy + copy of <see cref="Extra"/>.</summary>
    protected T CloneBase<T>() where T : Annotation
    {
        var c = (T)MemberwiseClone();
        c.Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra);
        return c;
    }

    protected static List<Point2> TranslatePoints(List<Point2> pts, double dx, double dy)
    {
        for (var i = 0; i < pts.Count; i++) pts[i] = pts[i].Offset(dx, dy);
        return pts;
    }

    public override string ToString() => $"{Kind} {Id:N} {Bounds}";
}

public static class AnnotationKindExtensions
{
    /// <summary>JSON discriminator string (camelCase enum name).</summary>
    public static string ToJsonName(this AnnotationKind k) => JsonNamingPolicy.CamelCase.ConvertName(k.ToString());

    public static bool TryParseJsonName(string? s, out AnnotationKind kind)
        => Enum.TryParse(s, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    public static Type ClrType(this AnnotationKind k) => k switch
    {
        AnnotationKind.Arrow => typeof(ArrowAnnotation),
        AnnotationKind.Line => typeof(LineAnnotation),
        AnnotationKind.Shape => typeof(ShapeAnnotation),
        AnnotationKind.Callout => typeof(CalloutAnnotation),
        AnnotationKind.Text => typeof(TextAnnotation),
        AnnotationKind.Stamp => typeof(StampAnnotation),
        AnnotationKind.Step => typeof(StepAnnotation),
        AnnotationKind.Pen => typeof(PenAnnotation),
        AnnotationKind.Highlight => typeof(HighlightAnnotation),
        AnnotationKind.Magnify => typeof(MagnifyAnnotation),
        AnnotationKind.Blur => typeof(BlurAnnotation),
        AnnotationKind.Image => typeof(ImageAnnotation),
        AnnotationKind.Cursor => typeof(CursorAnnotation),
        AnnotationKind.DropZone => typeof(DropZoneAnnotation),
        AnnotationKind.SimplifyShape => typeof(SimplifyShapeAnnotation),
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>Creates a default instance of the given kind.</summary>
    public static Annotation CreateDefault(this AnnotationKind k) => (Annotation)Activator.CreateInstance(k.ClrType())!;
}
