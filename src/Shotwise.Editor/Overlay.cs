using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Editor;

/// <summary>
/// Visual style of an overlay primitive. Stroke widths are in <b>screen</b> pixels (the view divides by zoom),
/// so handles and outlines stay crisp at any zoom.
/// </summary>
public sealed record OverlayStyle(Argb Stroke, double StrokeWidth = 1, Argb? Fill = null, bool Dashed = false)
{
    public static OverlayStyle Selection { get; } = new(Argb.FromRgb(0x1E, 0x88, 0xE5), 1);
    public static OverlayStyle Hover { get; } = new(Argb.FromRgb(0x1E, 0x88, 0xE5), 1, null, true);
    public static OverlayStyle Marquee { get; } = new(Argb.FromRgb(0x1E, 0x88, 0xE5), 1, new Argb(0x221E88E5), true);
    public static OverlayStyle Guide { get; } = new(Argb.FromRgb(0xE9, 0x1E, 0x63), 1);
    public static OverlayStyle Handle { get; } = new(Argb.FromRgb(0x1E, 0x88, 0xE5), 1, Argb.White);
    public static OverlayStyle Crop { get; } = new(Argb.White, 1, null, true);
}

public enum HandleKind
{
    TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left,
    Rotate, Start, End, Control1, Control2, TailTip, PointerTip, Vertex, LensSource, Move,
}

/// <summary>Something the view draws above the document (handles, marquee, crop frame, guides…). Coordinates are canvas pixels.</summary>
public abstract record OverlayPrimitive
{
    public OverlayStyle Style { get; init; } = OverlayStyle.Selection;
}

public sealed record OverlayRect(Rect2 Rect, double RotationDegrees = 0) : OverlayPrimitive;

public sealed record OverlayEllipse(Rect2 Bounds, double RotationDegrees = 0) : OverlayPrimitive;

public sealed record OverlayPath(IReadOnlyList<Point2> Points, bool Closed) : OverlayPrimitive;

public sealed record OverlayLine(Point2 From, Point2 To) : OverlayPrimitive;

/// <summary>Interactive handle drawn at a fixed screen size (≈ 8 px) centred on <paramref name="Center"/>. <paramref name="Id"/> is tool-defined.</summary>
public sealed record OverlayHandle(string Id, Point2 Center, HandleKind Kind) : OverlayPrimitive
{
    public const double ScreenSize = 8;
}

/// <summary>Text label (size readouts, step numbers while dragging) — font size in screen pixels.</summary>
public sealed record OverlayText(Point2 Anchor, string Text, double FontSize = 12) : OverlayPrimitive
{
    public Argb Foreground { get; init; } = Argb.White;
    public Argb Background { get; init; } = new(0xCC202020);
}

/// <summary>
/// Tinted area: dims outside a crop, highlights a cut-out band or a pixel selection. Without a mask the whole
/// <paramref name="Rect"/> is tinted; with a mask (canvas-sized, indexed by canvas pixel) only covered pixels inside
/// <paramref name="Rect"/> are tinted (<paramref name="InvertMask"/>: uncovered ones).
/// </summary>
public sealed record OverlayImageTint(Rect2 Rect, Argb Tint, Mask? Mask = null, bool InvertMask = false) : OverlayPrimitive;

/// <summary>Live raster preview (e.g. pixels being moved by the selection tool).</summary>
public sealed record OverlayImage(BgraImage Image, Rect2 Destination, double Opacity = 1) : OverlayPrimitive;
