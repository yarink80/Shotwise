using Shotwise.Core.Annotations;
using Shotwise.Core.Styles;

namespace Shotwise.Editor;

/// <summary>Copies style (not geometry/content) from a quick-style prototype onto an annotation.</summary>
public static class StyleApplier
{
    /// <summary>Tool that creates annotations of the given kind.</summary>
    public static ToolKind ToolFor(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Arrow => ToolKind.Arrow,
        AnnotationKind.Line => ToolKind.Line,
        AnnotationKind.Shape => ToolKind.Shape,
        AnnotationKind.Callout => ToolKind.Callout,
        AnnotationKind.Text => ToolKind.Text,
        AnnotationKind.Stamp => ToolKind.Stamp,
        AnnotationKind.Step => ToolKind.Step,
        AnnotationKind.Pen => ToolKind.Pen,
        AnnotationKind.Highlight => ToolKind.Highlighter,
        AnnotationKind.Magnify => ToolKind.Magnify,
        AnnotationKind.Blur => ToolKind.Blur,
        AnnotationKind.Cursor => ToolKind.Cursor,
        AnnotationKind.SimplifyShape => ToolKind.Simplify,
        _ => ToolKind.Select,
    };

    /// <summary>True when <paramref name="style"/> can be applied to <paramref name="target"/>.</summary>
    public static bool IsApplicable(QuickStyle style, Annotation target) => style.Prototype is { } p && p.Kind == target.Kind;

    /// <summary>Applies common + kind-specific style fields of <paramref name="prototype"/> to <paramref name="target"/>.</summary>
    public static void Apply(Annotation prototype, Annotation target)
    {
        target.Stroke = prototype.Stroke;
        target.Fill = prototype.Fill;
        target.Opacity = prototype.Opacity;
        target.Shadow = prototype.Shadow;
        switch ((prototype, target))
        {
            case (ArrowAnnotation p, ArrowAnnotation t):
                t.StartCap = p.StartCap; t.EndCap = p.EndCap; t.StartCapScale = p.StartCapScale; t.EndCapScale = p.EndCapScale;
                break;
            case (LineAnnotation p, LineAnnotation t):
                t.DoubleLine = p.DoubleLine; t.StartCap = p.StartCap; t.EndCap = p.EndCap;
                break;
            case (ShapeAnnotation p, ShapeAnnotation t):
                if (t.Shape != ShapeKind.Polygon && p.Shape != ShapeKind.Polygon) t.Shape = p.Shape;
                t.CornerRadius = p.CornerRadius;
                break;
            case (CalloutAnnotation p, CalloutAnnotation t):
                t.ShapePresetId = p.ShapePresetId;
                t.TextStyle = p.TextStyle;
                t.Text = ApplyRunFormat(p.Text, t.Text);
                for (var i = 0; i < t.Tails.Count; i++)
                    t.Tails[i] = t.Tails[i] with { Width = p.Tails.Count > 0 ? p.Tails[0].Width : t.Tails[i].Width, Curved = p.Tails.Count > 0 ? p.Tails[0].Curved : t.Tails[i].Curved };
                break;
            case (TextAnnotation p, TextAnnotation t):
                t.TextStyle = p.TextStyle;
                t.Text = ApplyRunFormat(p.Text, t.Text);
                break;
            case (StepAnnotation p, StepAnnotation t):
                t.Style = p.Style; t.TextColor = p.TextColor; t.FontFamily = p.FontFamily;
                break;
            case (PenAnnotation p, PenAnnotation t):
                t.Nib = p.Nib; t.Smoothing = p.Smoothing; t.Antialias = p.Antialias;
                break;
            case (MagnifyAnnotation p, MagnifyAnnotation t):
                t.LensShape = p.LensShape; t.Scale = p.Scale; t.ShowConnector = p.ShowConnector;
                break;
            case (BlurAnnotation p, BlurAnnotation t):
                t.Region = p.Region; t.Mode = p.Mode; t.Intensity = p.Intensity; t.SolidColor = p.SolidColor;
                break;
            case (CursorAnnotation p, CursorAnnotation t):
                t.Cursor = p.Cursor; t.Scale = p.Scale;
                break;
            case (SimplifyShapeAnnotation p, SimplifyShapeAnnotation t):
                t.PaletteIndex = p.PaletteIndex;
                break;
        }
    }

    /// <summary>Keeps the target's text but takes font/size/colour/emphasis from the prototype's first run.</summary>
    public static RichText ApplyRunFormat(RichText prototype, RichText target)
    {
        if (prototype.Runs.Count == 0) return target.Clone();
        var f = prototype.Runs[0];
        return target.MapRuns(r => f with { Text = r.Text });
    }
}
