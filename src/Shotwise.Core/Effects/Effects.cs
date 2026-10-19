using System.Text.Json.Serialization;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Effects;

public enum EdgeStyle { DropShadow, Fade, Torn, Wave, Saw, Sharktooth, Bevel }

[Flags]
public enum EdgeSides { None = 0, Top = 1, Bottom = 2, Left = 4, Right = 8, All = Top | Bottom | Left | Right }

/// <summary>Shadow direction: 8 compass directions (where the shadow falls) or all sides.</summary>
public enum ShadowDirection { TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight, AllSides }

public enum FilterKind { Grayscale, Monochrome, Invert, Sharpen, Soften, Emboss, Sepia }

[Flags]
public enum CaptionFields
{
    None = 0,
    AppName = 1,
    AppVersion = 2,
    OsVersion = 4,
    Timestamp = 8,
    WindowTitle = 16,
    Url = 32,
    CustomText = 64,
}

/// <summary>
/// Data-only effect description, processed by <see cref="IEffectProcessor"/>.
/// JSON: polymorphic with discriminator <c>"type"</c> (camelCase names, e.g. "border", "pageCurl"); see <see cref="EffectSpecJsonConverter"/>.
/// </summary>
[JsonConverter(typeof(EffectSpecJsonConverter))]
public abstract record EffectSpec
{
    public bool Enabled { get; init; } = true;
}

public sealed record BorderEffect(Argb Color, double Width, bool Outside = true) : EffectSpec;
public sealed record EdgeEffect(EdgeStyle Style, EdgeSides Sides, double Size, bool WithShadow, bool WithBorder) : EffectSpec;
public sealed record ShadowEffect(Argb Color, ShadowDirection Direction, double Size, double Blur, double Opacity) : EffectSpec;
public sealed record PerspectiveEffect(bool Shear, double Horizontal, double Vertical, bool Border, bool Shadow) : EffectSpec;
public sealed record PageCurlEffect(Corner Corner, double Size) : EffectSpec;
public sealed record ColorAdjustEffect(double Brightness, double Contrast, double Hue, double Saturation, double Gamma) : EffectSpec;
public sealed record ColorReplaceEffect(
    [property: JsonConverter(typeof(ColorPairListJsonConverter))] IReadOnlyList<(Argb From, Argb To)> Pairs,
    double Tolerance) : EffectSpec;
public sealed record FilterEffect(FilterKind Kind, double Intensity, double Opacity = 1) : EffectSpec;
public sealed record WatermarkEffect(string ImagePath, double Scale, Anchor9 Position, bool Underlay, double Transparency, bool Emboss, double EmbossDepth, double EmbossAngle) : EffectSpec;
public sealed record CaptionEffect(CaptionFields Fields, string? CustomText, bool PromptAtCapture, string FontFamily, double FontSize, Argb TextColor, Argb BackColor, bool Outside, Anchor9 Position) : EffectSpec;
public sealed record ResizeEffect(double? Percent, int? Width, int? Height, bool KeepAspect, double? Dpi, bool Smooth = true) : EffectSpec;
public sealed record RotateEffect(double Degrees, bool ResizeCanvas, bool FlipH, bool FlipV) : EffectSpec;
public sealed record TrimEffect(bool Auto, int Left, int Top, int Right, int Bottom, double Tolerance = 0) : EffectSpec;
public sealed record SpotlightMagnifyEffect(Rect2 Region, double DimOpacity, double Blur, double Magnify) : EffectSpec;
public sealed record TransparencyEffect(Argb Color, double Tolerance) : EffectSpec;

/// <summary>
/// Placeholder for an effect whose <c>"type"</c> this build does not know (forward compatibility).
/// The original JSON is kept in <see cref="Raw"/> and written back unchanged; processors must pass the image through.
/// </summary>
public sealed record UnknownEffect(string TypeName, System.Text.Json.JsonElement Raw) : EffectSpec;

/// <summary>A named, user-saved combination of effects (Quick Effects gallery).</summary>
public sealed record QuickEffect(string Id, string Name, IReadOnlyList<EffectSpec> Effects);

/// <summary>Result of applying an effect: new image + offset of the original content inside it (for annotation shifting).</summary>
public sealed record EffectResult(BgraImage Image, Point2 ContentOffset);

/// <summary>Raster effect engine (implemented in Rendering).</summary>
public interface IEffectProcessor
{
    EffectResult Apply(BgraImage src, EffectSpec effect, DocumentMetadata? md);
    EffectResult ApplyAll(BgraImage src, IEnumerable<EffectSpec> effects, DocumentMetadata? md);
    BgraImage Preview(BgraImage src, EffectSpec e, int maxSide);
}

/// <summary>Our own default parameters for each effect (used by "add effect" menus and preset defaults).</summary>
public static class EffectDefaults
{
    public static BorderEffect Border => new(Argb.FromRgb(0x40, 0x40, 0x40), 2);
    public static EdgeEffect Edge => new(EdgeStyle.Torn, EdgeSides.Bottom, 12, true, false);
    public static ShadowEffect Shadow => new(Argb.Black, ShadowDirection.BottomRight, 10, 10, 0.5);
    public static PerspectiveEffect Perspective => new(false, 20, 0, false, true);
    public static PageCurlEffect PageCurl => new(Corner.BottomRight, 60);
    public static ColorAdjustEffect ColorAdjust => new(0, 0, 0, 0, 1.0);
    public static ColorReplaceEffect ColorReplace => new(Array.Empty<(Argb, Argb)>(), 0.1);
    public static FilterEffect Filter => new(FilterKind.Grayscale, 1.0);
    public static WatermarkEffect Watermark => new("", 1.0, Anchor9.BottomRight, false, 0.5, false, 2, 45);
    public static CaptionEffect Caption => new(CaptionFields.AppName | CaptionFields.Timestamp, null, false, "Segoe UI", 12, Argb.Black, Argb.White, true, Anchor9.BottomCenter);
    public static ResizeEffect Resize => new(100, null, null, true, null);
    public static RotateEffect Rotate => new(90, true, false, false);
    public static TrimEffect Trim => new(true, 0, 0, 0, 0, 0.02);
    public static TransparencyEffect Transparency => new(Argb.White, 0.05);
}
