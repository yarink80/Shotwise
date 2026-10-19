using System.Text.Json;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using Shotwise.Core.Styles;

namespace Shotwise.Library.Styles;

/// <summary>
/// Our own built-in themes and palettes (original colour/style designs). The same definitions are written to
/// <c>Shotwise.App/Content/themes/*.shottheme</c> and <c>Content/palettes/*.shotpalette</c> (see the content generator test);
/// <see cref="StyleRepository"/> loads those files and falls back to these in-code definitions when the content folder is absent.
/// </summary>
public static class BuiltInContent
{
    public const string BasicId = "builtin.basic";
    public const string BoldId = "builtin.bold";
    public const string DarkId = "builtin.dark-mode";
    public const string PastelId = "builtin.pastel";
    public const string HighContrastId = "builtin.high-contrast";

    private static Argb C(uint rgb) => Argb.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private sealed record Spec(string Id, string File, string Name, string Font, bool DropShadow, double Scale, bool BoldText,
        uint[] Colors, uint Accent, uint Accent2, uint Ink, uint OnAccent, uint CalloutFill, uint CalloutStroke, uint CalloutText);

    private static readonly Spec[] Specs =
    {
        new(BasicId, "basic", "Basic", "Segoe UI", false, 1.0, false,
            new uint[] { 0xE53935, 0x1E88E5, 0x43A047, 0xFB8C00, 0x8E24AA, 0x263238, 0xFDD835, 0xFFFFFF },
            0xE53935, 0x1E88E5, 0x263238, 0xFFFFFF, 0xFFF9C4, 0xF9A825, 0x263238),
        new(BoldId, "bold", "Bold", "Segoe UI", true, 1.7, true,
            new uint[] { 0xFF1744, 0x2979FF, 0x00C853, 0xFF9100, 0xD500F9, 0x000000, 0xFFEA00, 0xFFFFFF },
            0xFF1744, 0x2979FF, 0x000000, 0xFFFFFF, 0xFFEA00, 0x000000, 0x000000),
        new(DarkId, "dark-mode", "Dark Mode", "Segoe UI", false, 1.0, false,
            new uint[] { 0xFF6E6E, 0x64B5F6, 0x81C784, 0xFFB74D, 0xBA68C8, 0xECEFF1, 0xFFF176, 0x121212 },
            0x64B5F6, 0xFFB74D, 0xECEFF1, 0x0D1117, 0x263238, 0x64B5F6, 0xECEFF1),
        new(PastelId, "pastel", "Pastel", "Segoe UI", false, 0.9, false,
            new uint[] { 0xF48FB1, 0x90CAF9, 0xA5D6A7, 0xFFCC80, 0xCE93D8, 0x5E5E73, 0xFFF59D, 0xFFFFFF },
            0xEF8E9A, 0x7FB7D9, 0x5E5E73, 0x3D3D4E, 0xFFF3E0, 0xFFB74D, 0x4A4A5C),
        new(HighContrastId, "high-contrast", "High Contrast", "Arial", false, 1.9, true,
            new uint[] { 0x000000, 0xFFFFFF, 0xFFFF00, 0x00FFFF, 0xFF00FF, 0x00FF00, 0xFF0000, 0x0000FF },
            0x000000, 0x0000FF, 0x000000, 0xFFFFFF, 0xFFFF00, 0x000000, 0x000000),
    };

    /// <summary>File name (without extension) for a built-in theme id.</summary>
    public static string FileNameFor(Theme t) => Specs.FirstOrDefault(s => s.Id == t.Id)?.File ?? t.Id;

    public static IReadOnlyList<Theme> CreateThemes() => Specs.Select(Build).ToList();

    public static IReadOnlyList<Palette> CreatePalettes() => new[]
    {
        new Palette
        {
            Id = "builtin.spectrum", Name = "Spectrum", IsBuiltIn = true,
            Colors = new uint[] { 0xE53935, 0xFB8C00, 0xFDD835, 0x7CB342, 0x00897B, 0x1E88E5, 0x3949AB, 0x8E24AA, 0xD81B60, 0x6D4C41 }.Select(C).ToList(),
        },
        new Palette
        {
            Id = "builtin.earth-tones", Name = "Earth Tones", IsBuiltIn = true,
            Colors = new uint[] { 0x3E2723, 0x6D4C41, 0xA1887F, 0xD7CCC8, 0x556B2F, 0x8A9A5B, 0xC2B280, 0xB5651D, 0x8B3A3A, 0x2F4F4F }.Select(C).ToList(),
        },
        new Palette
        {
            Id = "builtin.grayscale", Name = "Grayscale", IsBuiltIn = true,
            Colors = new uint[] { 0x000000, 0x242424, 0x484848, 0x6C6C6C, 0x909090, 0xB4B4B4, 0xD8D8D8, 0xFFFFFF }.Select(C).ToList(),
        },
    };

    public static string PaletteFileName(Palette p) => p.Id.StartsWith("builtin.", StringComparison.Ordinal) ? p.Id["builtin.".Length..] : p.Id;

    // ------------------------------------------------------------------ theme construction

    private static Theme Build(Spec s)
    {
        var accent = C(s.Accent);
        var accent2 = C(s.Accent2);
        var ink = C(s.Ink);
        var onAccent = C(s.OnAccent);
        var calloutFill = C(s.CalloutFill);
        var calloutStroke = C(s.CalloutStroke);
        var calloutText = C(s.CalloutText);
        double W(double w) => Math.Round(w * s.Scale, 1);
        ShadowStyle? Sh(bool force = false) => s.DropShadow || force ? new ShadowStyle(Argb.Black, 45, 4, 8, 0.45) : null;
        var styles = new List<QuickStyle>();
        var counters = new Dictionary<ToolKind, int>();

        void Add(ToolKind tool, string name, Annotation? proto, bool fav = false, object? options = null)
        {
            counters.TryGetValue(tool, out var i);
            counters[tool] = i + 1;
            var qsId = $"{s.Id}.{tool.ToString().ToLowerInvariant()}.{i}";
            // Deterministic prototype id so the shipped JSON is reproducible byte for byte.
            if (proto is not null) proto.Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(qsId)));
            styles.Add(new QuickStyle
            {
                Id = qsId,
                Name = name,
                Tool = tool,
                Prototype = proto,
                Options = options is null ? null : JsonSerializer.SerializeToElement(options, ShotwiseJson.Compact),
                IsFavorite = fav,
                IsBuiltIn = true,
            });
        }

        TextRun Run(double size, bool bold, Argb color, bool italic = false)
            => new() { Text = "Text", FontFamily = s.Font, FontSize = size, Bold = bold || s.BoldText, Italic = italic, Color = color };

        // Arrow
        Add(ToolKind.Arrow, "Standard", new ArrowAnnotation { Stroke = new StrokeStyle(accent, W(4)), EndCap = ArrowCap.Arrow }, fav: true);
        Add(ToolKind.Arrow, "Thin", new ArrowAnnotation { Stroke = new StrokeStyle(accent2, W(2)), EndCap = ArrowCap.OpenArrow });
        Add(ToolKind.Arrow, "Heavy", new ArrowAnnotation { Stroke = new StrokeStyle(ink, W(7)), EndCap = ArrowCap.Triangle, EndCapScale = 1.2 });
        Add(ToolKind.Arrow, "Double Ended", new ArrowAnnotation { Stroke = new StrokeStyle(accent, W(4)), StartCap = ArrowCap.Arrow, EndCap = ArrowCap.Arrow });
        Add(ToolKind.Arrow, "Soft Shadow", new ArrowAnnotation { Stroke = new StrokeStyle(accent, W(4)), EndCap = ArrowCap.Arrow, Shadow = Sh(true) });
        Add(ToolKind.Arrow, "Dashed", new ArrowAnnotation { Stroke = new StrokeStyle(accent2, W(3), StrokeDash.Dash), EndCap = ArrowCap.Arrow });

        // Line
        Add(ToolKind.Line, "Standard", new LineAnnotation { Stroke = new StrokeStyle(accent, W(3)) });
        Add(ToolKind.Line, "Dashed", new LineAnnotation { Stroke = new StrokeStyle(accent, W(3), StrokeDash.Dash), StartCap = ArrowCap.None, EndCap = ArrowCap.None });
        Add(ToolKind.Line, "Dotted", new LineAnnotation { Stroke = new StrokeStyle(accent2, W(3), StrokeDash.Dot) });
        Add(ToolKind.Line, "Heavy", new LineAnnotation { Stroke = new StrokeStyle(ink, W(8)), StartCap = ArrowCap.Square, EndCap = ArrowCap.Square });
        Add(ToolKind.Line, "Double", new LineAnnotation { Stroke = new StrokeStyle(accent, W(3)), DoubleLine = true });

        // Shape
        Add(ToolKind.Shape, "Outline", new ShapeAnnotation { Shape = ShapeKind.RoundedRectangle, Stroke = new StrokeStyle(accent, W(3)), Fill = FillStyle.None });
        Add(ToolKind.Shape, "Tinted Fill", new ShapeAnnotation { Shape = ShapeKind.RoundedRectangle, Stroke = new StrokeStyle(accent, W(3)), Fill = FillStyle.Solid(accent.WithAlpha(0x59)) }, fav: true);
        Add(ToolKind.Shape, "Solid", new ShapeAnnotation { Shape = ShapeKind.Rectangle, Stroke = StrokeStyle.None, Fill = FillStyle.Solid(accent), Shadow = Sh() });
        Add(ToolKind.Shape, "Ellipse Outline", new ShapeAnnotation { Shape = ShapeKind.Ellipse, Stroke = new StrokeStyle(accent2, W(3)), Fill = FillStyle.None });
        Add(ToolKind.Shape, "Card", new ShapeAnnotation { Shape = ShapeKind.RoundedRectangle, CornerRadius = 16, Stroke = new StrokeStyle(calloutStroke, W(2)), Fill = FillStyle.Solid(calloutFill), Shadow = Sh(true) });
        Add(ToolKind.Shape, "Gradient", new ShapeAnnotation
        {
            Shape = ShapeKind.RoundedRectangle, Stroke = StrokeStyle.None,
            Fill = FillStyle.FromGradient(GradientFill.Linear(accent, accent2, 90)), Shadow = Sh(),
        });

        // Callout
        var calloutPresets = new (string Preset, string Name)[]
        {
            (CalloutPresets.RoundedRectangle, "Rounded Box"), (CalloutPresets.SpeechBubble, "Speech Bubble"), (CalloutPresets.Cloud, "Cloud"),
            (CalloutPresets.Burst, "Burst"), (CalloutPresets.Banner, "Banner"), (CalloutPresets.Rectangle, "Plain Box"),
        };
        foreach (var (preset, name) in calloutPresets)
        {
            var filled = preset == CalloutPresets.Burst ? accent : calloutFill;
            var textColor = preset == CalloutPresets.Burst ? onAccent : calloutText;
            Add(ToolKind.Callout, name, new CalloutAnnotation
            {
                ShapePresetId = preset,
                Stroke = new StrokeStyle(calloutStroke, W(2)),
                Fill = FillStyle.Solid(filled),
                Shadow = Sh(),
                Text = new RichText(new[] { Run(18, false, textColor) }),
            }, fav: preset == CalloutPresets.SpeechBubble);
        }

        // Text
        Add(ToolKind.Text, "Plain", new TextAnnotation { Text = new RichText(new[] { Run(18, false, ink) }) });
        Add(ToolKind.Text, "Heading", new TextAnnotation { Text = new RichText(new[] { Run(32, true, ink) }) }, fav: true);
        Add(ToolKind.Text, "Caption", new TextAnnotation { Text = new RichText(new[] { Run(13, false, ink, italic: true) }) });
        Add(ToolKind.Text, "Label", new TextAnnotation
        {
            Text = new RichText(new[] { Run(16, true, onAccent) }), Fill = FillStyle.Solid(accent),
            TextStyle = TextLayoutStyle.Default with { Padding = new(10, 6, 10, 6) },
        });
        Add(ToolKind.Text, "Outlined", new TextAnnotation
        {
            Text = new RichText(new[] { Run(28, true, Argb.White) }),
            TextStyle = TextLayoutStyle.Default with { OutlineColor = Argb.Black, OutlineWidth = 3 },
        });

        // Stamp
        Add(ToolKind.Stamp, "Plain", new StampAnnotation());
        Add(ToolKind.Stamp, "Drop Shadow", new StampAnnotation { Shadow = Sh(true) });
        Add(ToolKind.Stamp, "Faded", new StampAnnotation { Opacity = 0.6 });

        // Step
        Add(ToolKind.Step, "Circle", new StepAnnotation { Style = StepStyle.Circle, Fill = FillStyle.Solid(accent), TextColor = onAccent, FontFamily = s.Font }, fav: true);
        Add(ToolKind.Step, "Square", new StepAnnotation { Style = StepStyle.Square, Fill = FillStyle.Solid(accent2), TextColor = onAccent, FontFamily = s.Font });
        Add(ToolKind.Step, "Pointer Circle", new StepAnnotation { Style = StepStyle.CirclePointer, Fill = FillStyle.Solid(accent), TextColor = onAccent, FontFamily = s.Font });
        Add(ToolKind.Step, "Pointer Square", new StepAnnotation { Style = StepStyle.SquarePointer, Fill = FillStyle.Solid(accent2), TextColor = onAccent, FontFamily = s.Font });
        Add(ToolKind.Step, "Letters", new StepAnnotation { Style = StepStyle.Circle, Sequence = StepSequence.Upper, Fill = FillStyle.Solid(ink), TextColor = ink.Luminance > 0.5 ? Argb.Black : Argb.White, FontFamily = s.Font });

        // Pen
        Add(ToolKind.Pen, "Marker", new PenAnnotation { Stroke = new StrokeStyle(accent, W(6)), Nib = PenNib.Circle });
        Add(ToolKind.Pen, "Fine Liner", new PenAnnotation { Stroke = new StrokeStyle(ink, W(2)), Nib = PenNib.Circle });
        Add(ToolKind.Pen, "Brush", new PenAnnotation { Stroke = new StrokeStyle(accent2, W(10)), Nib = PenNib.Square });
        Add(ToolKind.Pen, "Chisel", new PenAnnotation { Stroke = new StrokeStyle(accent, W(12)), Nib = PenNib.Diagonal, Smoothing = false });

        // Highlighter (default Highlight* values are the rectangle-mode yellow; freehand mode uses Points != null)
        var hl = new (string Name, uint Rgb)[] { ("Yellow", 0xFFEB3B), ("Green", 0x76FF03), ("Pink", 0xFF80AB), ("Cyan", 0x18FFFF) };
        foreach (var (name, rgb) in hl)
        {
            var c = C(rgb).WithAlpha(0x80);
            Add(ToolKind.Highlighter, name, new HighlightAnnotation { Fill = FillStyle.Solid(c), Stroke = new StrokeStyle(c, 16) }, fav: name == "Yellow");
        }
        Add(ToolKind.Highlighter, "Freehand Marker", new HighlightAnnotation
        {
            Points = new(), Fill = FillStyle.None, Stroke = new StrokeStyle(C(0xFFEB3B).WithAlpha(0x80), 20),
        });

        // Magnify
        Add(ToolKind.Magnify, "Round Lens", new MagnifyAnnotation { LensShape = ShapeKind.Ellipse, Scale = 2, Stroke = new StrokeStyle(ink, W(2)), Shadow = Sh(true) });
        Add(ToolKind.Magnify, "Square Lens", new MagnifyAnnotation { LensShape = ShapeKind.Rectangle, Scale = 3, Stroke = new StrokeStyle(accent, W(3)), Shadow = Sh(true) });
        Add(ToolKind.Magnify, "Rounded Lens", new MagnifyAnnotation { LensShape = ShapeKind.RoundedRectangle, Scale = 2.5, Stroke = new StrokeStyle(accent2, W(3)), ShowConnector = false });

        // Blur
        Add(ToolKind.Blur, "Pixelate", new BlurAnnotation { Mode = BlurKind.Pixelate, Intensity = 50 });
        Add(ToolKind.Blur, "Soft Blur", new BlurAnnotation { Mode = BlurKind.Gaussian, Intensity = 60 });
        Add(ToolKind.Blur, "Solid Box", new BlurAnnotation { Mode = BlurKind.Solid, SolidColor = C(s.Ink == 0xECEFF1 ? 0x000000 : s.Ink) });
        Add(ToolKind.Blur, "Round Pixelate", new BlurAnnotation { Mode = BlurKind.Pixelate, Region = ShapeKind.Ellipse, Intensity = 70 });

        // Cursor
        Add(ToolKind.Cursor, "Arrow", new CursorAnnotation { Cursor = CursorKind.Arrow, Scale = 1.0 });
        Add(ToolKind.Cursor, "Large Hand", new CursorAnnotation { Cursor = CursorKind.Hand, Scale = 1.5 });

        // Tools without annotation prototypes keep their settings in Options (documented in WP-LIBRARY.md).
        Add(ToolKind.Fill, "Match Colour", null, options: new { color = accent.ToHex(), tolerance = 24, contiguous = true, opacity = 1.0 });
        Add(ToolKind.Fill, "Loose Fill", null, options: new { color = accent2.ToHex(), tolerance = 64, contiguous = true, opacity = 1.0 });
        Add(ToolKind.Fill, "Global Replace", null, options: new { color = accent.ToHex(), tolerance = 16, contiguous = false, opacity = 1.0 });
        Add(ToolKind.Eraser, "Round", null, options: new { size = 20, shape = "circle", hardness = 1.0 });
        Add(ToolKind.Eraser, "Soft", null, options: new { size = 40, shape = "circle", hardness = 0.3 });
        Add(ToolKind.Eraser, "Square", null, options: new { size = 24, shape = "square", hardness = 1.0 });
        Add(ToolKind.Selection, "Rectangle", null, options: new { shape = "rectangle", feather = 0, fixedRatio = (string?)null });
        Add(ToolKind.Selection, "Ellipse", null, options: new { shape = "ellipse", feather = 0, fixedRatio = (string?)null });
        Add(ToolKind.Selection, "Soft Edge", null, options: new { shape = "rectangle", feather = 8, fixedRatio = (string?)null });
        Add(ToolKind.MagicWand, "Precise", null, options: new { tolerance = 16, contiguous = true, antialias = true });
        Add(ToolKind.MagicWand, "Normal", null, options: new { tolerance = 40, contiguous = true, antialias = true });
        Add(ToolKind.MagicWand, "Broad", null, options: new { tolerance = 70, contiguous = false, antialias = true });
        Add(ToolKind.CutOut, "Clean", null, options: new { edge = CutOutEdgeStyle.None.ToString(), orientation = CutOutOrientation.Horizontal.ToString() });
        Add(ToolKind.CutOut, "Torn", null, options: new { edge = CutOutEdgeStyle.Tear.ToString(), orientation = CutOutOrientation.Horizontal.ToString() });
        Add(ToolKind.CutOut, "Wave", null, options: new { edge = CutOutEdgeStyle.Wave.ToString(), orientation = CutOutOrientation.Horizontal.ToString() });
        Add(ToolKind.CutOut, "Saw", null, options: new { edge = CutOutEdgeStyle.Saw.ToString(), orientation = CutOutOrientation.Vertical.ToString() });
        Add(ToolKind.Crop, "Free", null, options: new { aspect = "free" });
        Add(ToolKind.Crop, "16:9", null, options: new { aspect = "16:9" });
        Add(ToolKind.Crop, "4:3", null, options: new { aspect = "4:3" });
        Add(ToolKind.Crop, "Square", null, options: new { aspect = "1:1" });

        return new Theme
        {
            Id = s.Id,
            Name = s.Name,
            Version = 1,
            QuickStyles = styles,
            ThemeColors = s.Colors.Select(C).ToList(),
            FontFamily = s.Font,
            DropShadow = s.DropShadow,
            IsDefault = s.Id == BasicId,
            IsBuiltIn = true,
        };
    }
}
