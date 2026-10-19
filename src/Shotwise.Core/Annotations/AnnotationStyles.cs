using Shotwise.Core.Imaging;

namespace Shotwise.Core.Annotations;

/// <summary>Stroke dash pattern. (Named StrokeDash to avoid clashing with System.Windows.Media.DashStyle.)</summary>
public enum StrokeDash { Solid, Dash, Dot, DashDot }

public enum LineJoin { Round, Miter, Bevel }

public enum FillKind { None, Solid, Gradient }

public enum GradientKind { Linear, Radial }

/// <summary>Immutable stroke description. Width 0 means "no outline".</summary>
public sealed record StrokeStyle(Argb Color, double Width = 3, StrokeDash Dash = StrokeDash.Solid, LineJoin Join = LineJoin.Round)
{
    public static StrokeStyle Default { get; } = new(Argb.Red, 3);
    public static StrokeStyle None { get; } = new(Argb.Transparent, 0);
    public bool IsVisible => Width > 0 && Color.A > 0;
}

/// <summary>Gradient colour stop; offset 0..1.</summary>
public sealed record ColorStop(double Offset, Argb Color);

/// <summary>Linear (angle in degrees, 0 = left→right) or radial (centre→edge) gradient.</summary>
public sealed record GradientFill(GradientKind Kind, double AngleDegrees, IReadOnlyList<ColorStop> Stops)
{
    public static GradientFill Linear(Argb from, Argb to, double angle = 90) => new(GradientKind.Linear, angle, new[] { new ColorStop(0, from), new ColorStop(1, to) });
    public static GradientFill Radial(Argb inner, Argb outer) => new(GradientKind.Radial, 0, new[] { new ColorStop(0, inner), new ColorStop(1, outer) });
}

/// <summary>Immutable fill description: none, solid colour or gradient.</summary>
public sealed record FillStyle(FillKind Kind, Argb Color, GradientFill? Gradient = null)
{
    public static FillStyle None { get; } = new(FillKind.None, Argb.Transparent);
    public static FillStyle Solid(Argb c) => new(FillKind.Solid, c);
    public static FillStyle FromGradient(GradientFill g) => new(FillKind.Gradient, g.Stops.Count > 0 ? g.Stops[0].Color : Argb.Transparent, g);
    public bool IsVisible => Kind switch { FillKind.None => false, FillKind.Solid => Color.A > 0, _ => Gradient is { Stops.Count: > 0 } };
}

/// <summary>Drop shadow: colour, light angle (degrees, 0 = shadow to the right, 90 = down), offset distance, blur radius, opacity 0..1.</summary>
public sealed record ShadowStyle(Argb Color, double AngleDegrees = 45, double Distance = 5, double Blur = 8, double Opacity = 0.6)
{
    public static ShadowStyle Default { get; } = new(Argb.Black);

    /// <summary>Offset vector derived from angle + distance.</summary>
    public (double Dx, double Dy) Offset
    {
        get
        {
            var r = AngleDegrees * Math.PI / 180.0;
            return (Math.Cos(r) * Distance, Math.Sin(r) * Distance);
        }
    }
}
