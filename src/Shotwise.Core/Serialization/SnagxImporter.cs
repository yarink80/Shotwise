using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Core.Serialization;

/// <summary>
/// Best-effort importer for the third-party <c>.snagx</c> annotated-capture document format. The container is
/// a ZIP with <c>index.json</c> {Pages[], Version}, one <c>{GUID}.json</c> per page referencing a <c>{GUID}.png</c>
/// background, <c>metadata.json</c> and <c>thumbnail.png</c>. Page objects (<c>CaptureObjects[]</c>, template
/// <c>Sections[]</c>) are mapped to Shotwise annotations for Arrow, Line, Text, Shape, Step, Callout, Pen, Highlight,
/// Magnify, Blur, Image/Stamp and DropZone; anything else is skipped and logged. Numeric fields tolerate int/float/string;
/// colours are "#AARRGGBB"; points are "x,y" strings. The same safety limits as .shotx apply.
/// </summary>
public sealed class SnagxImporter : ISnagxImporter
{
    public const string FileExtension = ".snagx";

    private readonly ILogger<SnagxImporter> _log;
    private readonly IImageCodec? _codec;

    public SnagxImporter(ILogger<SnagxImporter>? logger = null, IImageCodec? codec = null)
    {
        _log = logger ?? NullLogger<SnagxImporter>.Instance;
        _codec = codec;
    }

    /// <summary>Annotation counts by tool mode that were skipped during the last import (diagnostics/tests).</summary>
    public IReadOnlyDictionary<string, int> LastSkipped { get; private set; } = new Dictionary<string, int>();

    public bool CanImport(string path)
    {
        if (!string.Equals(Path.GetExtension(path), FileExtension, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        try
        {
            Span<byte> sig = stackalloc byte[4];
            using var fs = File.OpenRead(path);
            return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public Task<ShotDocument> ImportAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new ShotwiseException(ErrorCode.FileNotFound, $"File not found: {path}") { Detail = path };
        return Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                var doc = Import(zip, ct);
                doc.FilePath = null; // imported: saving goes to .shotx
                doc.IsDirty = true;
                return doc;
            }
            catch (InvalidDataException ex) { throw new ShotwiseException(ErrorCode.CorruptFile, "The file is not a valid ZIP container.", ex); }
            catch (JsonException ex) { throw new ShotwiseException(ErrorCode.CorruptFile, $"Invalid JSON: {ex.Message}", ex); }
            catch (UnauthorizedAccessException ex) { throw new ShotwiseException(ErrorCode.AccessDenied, $"Cannot read '{path}'.", ex); }
        }, ct);
    }

    /// <summary>Imports from an already opened archive (testable entry point).</summary>
    public ShotDocument Import(ZipArchive zip, CancellationToken ct = default)
    {
        ZipSafety.Validate(zip);
        var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var doc = new ShotDocument();
        doc.Assets.Codec = _codec;

        var indexEntry = ZipSafety.Find(zip, "index.json") ?? throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Missing index.json — not a .snagx document.");
        using var index = ParseJson(indexEntry);
        var pageNames = new List<string>();
        if (Prop(index.RootElement, "Pages") is { ValueKind: JsonValueKind.Array } pagesEl)
            foreach (var p in pagesEl.EnumerateArray())
                if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s) pageNames.Add(s);
        if (pageNames.Count == 0) throw new ShotwiseException(ErrorCode.CorruptFile, "index.json lists no pages.");

        var metaEntry = ZipSafety.Find(zip, "metadata.json");
        if (metaEntry is not null)
        {
            using var meta = ParseJson(metaEntry);
            doc.Metadata = MapMetadata(meta.RootElement);
        }

        if (pageNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pageNames.Count)
            throw new ShotwiseException(ErrorCode.CorruptFile, "Duplicate page reference.");
        long referencedBytes = 0;
        foreach (var name in pageNames)
        {
            ct.ThrowIfCancellationRequested();
            if (!ZipSafety.IsSafeEntryName(name)) throw new ShotwiseException(ErrorCode.PathTraversal, $"Unsafe page reference '{name}'.") { Detail = name };
            var entry = ZipSafety.Find(zip, name) ?? throw new ShotwiseException(ErrorCode.CorruptFile, $"Missing page '{name}'.");
            using var pageJson = ParseJson(entry);
            referencedBytes = checked(referencedBytes + entry.Length);
            var backgroundName = Str(pageJson.RootElement, "CaptureBackgroundImage");
            if (backgroundName is not null && ZipSafety.Find(zip, backgroundName) is { } background)
                referencedBytes = checked(referencedBytes + background.Length);
            if (referencedBytes > ZipSafety.MaxTotalUncompressed)
                throw new ShotwiseException(ErrorCode.FileTooLarge, "Referenced data exceeds the document budget.");
            doc.Pages.Add(MapPage(zip, doc, pageJson.RootElement, Path.GetFileNameWithoutExtension(name), skipped));
        }

        LastSkipped = skipped;
        foreach (var (mode, count) in skipped)
            _log.LogInformation("snagx import: skipped {Count} unsupported object(s) of type '{Mode}'", count, mode);
        return doc;
    }

    // ------------------------------------------------------------------ page

    private Page MapPage(ZipArchive zip, ShotDocument doc, JsonElement p, string stem, Dictionary<string, int> skipped)
    {
        var page = new Page();
        if (TryGuid(Str(p, "PageID") ?? stem, out var pid)) page.Id = pid;
        page.Dpi = Num(p, "DPI") is { } dpi && dpi > 0 ? dpi : 96;

        var bgColor = Color(p, "CaptureBackgroundColor") ?? Argb.White;
        BaseImageRef? baseImage = null;
        var w = Num(p, "CaptureCanvasWidth");
        var h = Num(p, "CaptureCanvasHeight");
        var bgName = Str(p, "CaptureBackgroundImage");
        if (!string.IsNullOrEmpty(bgName))
        {
            if (!ZipSafety.IsSafeEntryName(bgName)) throw new ShotwiseException(ErrorCode.PathTraversal, $"Unsafe image reference '{bgName}'.") { Detail = bgName };
            var e = ZipSafety.Find(zip, bgName);
            if (e is null) _log.LogWarning("snagx import: background image {Name} missing", bgName);
            else
            {
                var bytes = ZipSafety.ReadAllBytes(e, ZipSafety.MaxBinaryEntryBytes);
                var asset = doc.Assets.Add(bytes, SniffMediaType(bytes, bgName));
                var offset = Point(p, "CaptureBackgroundLocation") ?? Point2.Zero;
                baseImage = new BaseImageRef(asset, offset);
                if ((w is null || h is null) && PngCodec.TryReadSize(bytes, out var pw, out var ph)) { w ??= pw; h ??= ph; }
            }
        }
        page.CanvasSize = new Size2(Math.Max(1, w ?? 800), Math.Max(1, h ?? 600));
        page.Background = new CanvasBackground(bgColor, Gradient(Prop(p, "GradientBackgroundColor")), baseImage);
        if (Num(p, "CornerRadiusRatio") is { } crr && crr > 0)
            page.CornerRadius = crr * Math.Min(page.CanvasSize.Width, page.CanvasSize.Height) / 2;

        // step counters: flat fields or StepToolSequence{...}
        var seq = Prop(p, "StepToolSequence");
        int Counter(string flat, string nested) => (int)(Num(p, flat) ?? (seq is { } s ? Num(s, nested) : null) ?? 1);
        page.StepCounters = new StepCounters(
            Math.Max(1, Counter("StepToolSequenceNumeric", "Numeric")),
            Math.Max(1, Counter("StepToolSequenceUppercaseLetter", "UppercaseLetter")),
            Math.Max(1, Counter("StepToolSequenceLowercaseLetter", "LowercaseLetter")));

        if (Prop(p, "SimplifySettings") is { ValueKind: JsonValueKind.Object } simp)
        {
            page.Simplify = page.Simplify with
            {
                Detail = (int)Math.Clamp(Num(simp, "SuiDetail") ?? 8, 1, 10),
                Opacity = Fraction(simp, "Opacity", "SimplifyOpacity") ?? 1.0,
                ShowOriginal = Bool(simp, "ShowOriginal") ?? false,
                LockObjects = Bool(simp, "LockAllObjects", "IsLocked") ?? true,
                Palette = (Str(simp, "ActiveColorPaletteName") ?? "") switch
                {
                    var n when n.Contains("Gray", StringComparison.OrdinalIgnoreCase) || n.Contains("Grey", StringComparison.OrdinalIgnoreCase) => SimplifyPaletteKind.Grayscale,
                    var n when n.Contains("Dark", StringComparison.OrdinalIgnoreCase) => SimplifyPaletteKind.Dark,
                    var n when n.Contains("Light", StringComparison.OrdinalIgnoreCase) => SimplifyPaletteKind.Light,
                    var n when n.Contains("Custom", StringComparison.OrdinalIgnoreCase) => SimplifyPaletteKind.Custom,
                    _ => SimplifyPaletteKind.ImageColors,
                },
            };
        }

        if (Prop(p, "SmartRedactProperties") is { ValueKind: JsonValueKind.Object } red)
        {
            var cats = new List<RedactCategory>();
            if (Prop(red, "RedactionTypesEnabled") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var c in arr.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && MapRedactCategory(c.GetString()) is { } rc) cats.Add(rc);
            page.SmartRedact = page.SmartRedact with { Enabled = Bool(red, "SmartRedactEnabled") ?? false, Categories = cats.Count > 0 ? cats : page.SmartRedact.Categories };
        }

        if (Point(p, "CursorPosition") is { } cur)
            doc.Metadata = doc.Metadata with { CursorPosition = cur.ToPointI() };

        if (Prop(p, "CaptureObjects") is { ValueKind: JsonValueKind.Array } objs)
            page.Annotations.AddRange(MapObjects(objs, doc, skipped, sectionId: null));

        if (Prop(p, "Sections") is { ValueKind: JsonValueKind.Array } sections)
        {
            foreach (var s in sections.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var sec = new TemplateSection();
                if (TryGuid(Str(s, "SectionID") ?? Str(s, "ObjectID"), out var sid)) sec.Id = sid;
                var mode = Str(s, "ToolMode") ?? "";
                sec.Type = mode.Equals("RepeatableSection", StringComparison.OrdinalIgnoreCase)
                    ? SectionType.Repeatable
                    : (Str(s, "SectionType") ?? "") switch
                    {
                        var t when t.Contains("Header", StringComparison.OrdinalIgnoreCase) => SectionType.Header,
                        var t when t.Contains("Footer", StringComparison.OrdinalIgnoreCase) => SectionType.Footer,
                        _ => SectionType.Body,
                    };
                var a = Point(s, "StartPoint") ?? Point2.Zero;
                var b = Point(s, "EndPoint") ?? new Point2(page.CanvasSize.Width, page.CanvasSize.Height);
                sec.Bounds = Rect2.FromPoints(a, b);
                if (Color(s, "BackgroundColor") is { A: > 0 } sb) sec.Background = sb;
                if (Prop(s, "SectionGraphics") is { ValueKind: JsonValueKind.Array } g)
                    sec.Graphics.AddRange(MapObjects(g, doc, skipped, sec.Id));
                page.Sections.Add(sec);
            }
        }
        return page;
    }

    private IEnumerable<Annotation> MapObjects(JsonElement array, ShotDocument doc, Dictionary<string, int> skipped, Guid? sectionId)
    {
        var mapped = new List<(double Priority, int Index, Annotation A)>();
        var i = 0;
        foreach (var o in array.EnumerateArray())
        {
            i++;
            if (o.ValueKind != JsonValueKind.Object) continue;
            var mode = Str(o, "ToolMode") ?? "";
            Annotation? a;
            try
            {
                a = MapObject(o, mode, doc, sectionId);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or ShotwiseException or JsonException)
            {
                _log.LogWarning(ex, "snagx import: failed to map '{Mode}' object", mode);
                a = null;
            }
            if (a is null)
            {
                var key = string.IsNullOrEmpty(mode) ? "(none)" : mode;
                skipped[key] = skipped.TryGetValue(key, out var n) ? n + 1 : 1;
                continue;
            }
            mapped.Add((Num(o, "ObjectPriority") ?? i, i, a));
        }
        return mapped.OrderBy(m => m.Priority).ThenBy(m => m.Index).Select(m => m.A);
    }

    private Annotation? MapObject(JsonElement o, string mode, ShotDocument doc, Guid? sectionId)
    {
        Annotation? a = mode.ToLowerInvariant() switch
        {
            "arrow" => MapArrow(o),
            "line" => MapLine(o),
            "shape" => MapShape(o),
            "text" => MapText(o),
            "callout" => MapCallout(o),
            "step" => MapStep(o),
            "pen" => MapPen(o),
            "highlight" or "highlighter" => MapHighlight(o),
            "magnify" => MapMagnify(o),
            "blur" => MapBlur(o),
            "image" => MapImage(o, doc, asStamp: false),
            "stamp" => MapImage(o, doc, asStamp: true),
            "dropzone" => MapDropZone(o, sectionId),
            _ => null,
        };
        if (a is null) return null;
        ApplyCommon(o, a);
        return a;
    }

    private static void ApplyCommon(JsonElement o, Annotation a)
    {
        if (TryGuid(Str(o, "ObjectID"), out var id)) a.Id = id;
        a.RotationDegrees = Num(o, "RotationAngle") ?? 0;
        a.IsLocked = Bool(o, "IsLocked") ?? false;
        a.IgnoresUndoAll = Bool(o, "IgnoresUndoAll") ?? a.IgnoresUndoAll;
        var op = Num(o, "Opacity");
        a.Opacity = op is null or <= 0 ? 1.0 : op > 1 ? Math.Clamp(op.Value / 100.0, 0, 1) : op.Value;
        if (Bool(o, "DropShadowEnabled") == true)
        {
            var dx = Num(o, "ShadowDirectionX") ?? 4;
            var dy = Num(o, "ShadowDirectionY") ?? 4;
            a.Shadow = new ShadowStyle(
                Color(o, "ShadowColor") ?? Argb.Black,
                GeometryMath.RadiansToDegrees(Math.Atan2(dy, dx)),
                Math.Sqrt(dx * dx + dy * dy),
                Num(o, "ShadowBlur") ?? 8,
                Fraction(o, "ShadowOpacity") ?? 0.6);
        }
    }

    private static StrokeStyle StrokeOf(JsonElement o, Argb fallback) => new(
        Color(o, "ForegroundColor") ?? fallback,
        Num(o, "StrokeWidth") ?? 3,
        (Str(o, "DashType") ?? "") switch
        {
            var d when d.Contains("DashDot", StringComparison.OrdinalIgnoreCase) => StrokeDash.DashDot,
            var d when d.Contains("Dash", StringComparison.OrdinalIgnoreCase) => StrokeDash.Dash,
            var d when d.Contains("Dot", StringComparison.OrdinalIgnoreCase) => StrokeDash.Dot,
            _ => StrokeDash.Solid,
        });

    private static FillStyle FillOf(JsonElement o, string colorField = "BackgroundColor")
    {
        if (Gradient(Prop(o, "GradientBackgroundColor")) is { } g) return FillStyle.FromGradient(g);
        return Color(o, colorField) is { A: > 0 } c ? FillStyle.Solid(c) : FillStyle.None;
    }

    private static void SetEnds(JsonElement o, Annotation a)
    {
        a.Start = Point(o, "StartPoint") ?? Point2.Zero;
        a.End = Point(o, "EndPoint") ?? a.Start.Offset(100, 100);
    }

    private static ArrowCap Cap(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return ArrowCap.None;
        var v = s.Replace("-", "").Replace("_", "").ToLowerInvariant();
        if (v is "none" or "flat" or "nocap" or "butt") return ArrowCap.None;
        if (v.Contains("tbar") || v == "t") return ArrowCap.TBar;
        if (v.Contains("diamond")) return ArrowCap.Diamond;
        if (v.Contains("square")) return ArrowCap.Square;
        if (v.Contains("round") || v.Contains("circle") || v.Contains("dot")) return ArrowCap.Round;
        if (v.Contains("open") || v.Contains("line")) return ArrowCap.OpenArrow;
        if (v.Contains("equilateral") || v.Contains("triangle")) return ArrowCap.Triangle;
        return ArrowCap.Arrow;
    }

    private static ArrowAnnotation MapArrow(JsonElement o)
    {
        var a = new ArrowAnnotation { StartCap = Cap(Str(o, "ArrowStart")), EndCap = Cap(Str(o, "ArrowEnd") ?? "Arrow") };
        SetEnds(o, a);
        a.Stroke = StrokeOf(o, Argb.Red);
        ApplyBezier(o, a.Start, a.End, (b, c1, c2) => { a.Bezier = b; a.Control1 = c1; a.Control2 = c2; });
        return a;
    }

    private static LineAnnotation MapLine(JsonElement o)
    {
        var a = new LineAnnotation
        {
            StartCap = Cap(Str(o, "LineStartCap")) is ArrowCap.Square ? ArrowCap.Square : ArrowCap.Round,
            EndCap = Cap(Str(o, "LineEndCap")) is ArrowCap.Square ? ArrowCap.Square : ArrowCap.Round,
            DoubleLine = (Str(o, "LineType") ?? "").Contains("Double", StringComparison.OrdinalIgnoreCase),
        };
        SetEnds(o, a);
        a.Stroke = StrokeOf(o, Argb.Red);
        ApplyBezier(o, a.Start, a.End, (b, c1, c2) => { a.Bezier = b; a.Control1 = c1; a.Control2 = c2; });
        return a;
    }

    private static void ApplyBezier(JsonElement o, Point2 s, Point2 e, Action<bool, Point2?, Point2?> set)
    {
        var bez = Bool(o, "BezierCurve", "Bezier") ?? false;
        if (!bez) return;
        var pts = Points(o, "ControlPoints") ?? Points(o, "PointsArray");
        Point2? c1 = null, c2 = null;
        if (pts is { Count: >= 4 }) { c1 = pts[1]; c2 = pts[2]; }
        else if (pts is { Count: 2 }) { c1 = pts[0]; c2 = pts[1]; }
        else if (pts is { Count: 1 }) { c1 = pts[0]; c2 = pts[0]; }
        set(true, c1 ?? GeometryMath.Lerp(s, e, 1 / 3.0), c2 ?? GeometryMath.Lerp(s, e, 2 / 3.0));
    }

    private static ShapeKind ShapeOf(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        var v when v.Contains("rounded") => ShapeKind.RoundedRectangle,
        var v when v.Contains("ellipse") || v.Contains("oval") || v.Contains("circle") => ShapeKind.Ellipse,
        var v when v.Contains("polygon") => ShapeKind.Polygon,
        _ => ShapeKind.Rectangle,
    };

    private static ShapeAnnotation MapShape(JsonElement o)
    {
        var a = new ShapeAnnotation { Shape = ShapeOf(Str(o, "ToolShape")) };
        SetEnds(o, a);
        var pts = Points(o, "PointsArray");
        if (pts is { Count: >= 3 } && (a.Shape == ShapeKind.Polygon || Str(o, "ToolShape") is null))
        {
            a.Shape = ShapeKind.Polygon;
            a.Points = pts.ToList();
        }
        a.Stroke = StrokeOf(o, Argb.Red);
        a.Fill = FillOf(o);
        if (Num(o, "CornerRadiusRatio") is { } r && r > 0)
        {
            var b = a.Bounds;
            a.CornerRadius = r * Math.Min(b.Width, b.Height) / 2;
            if (a.Shape == ShapeKind.Rectangle) a.Shape = ShapeKind.RoundedRectangle;
        }
        return a;
    }

    private static (RichText Text, TextLayoutStyle Style) TextOf(JsonElement o)
    {
        var plain = Str(o, "PlainText");
        if (string.IsNullOrEmpty(plain) && Str(o, "RTFEncodedText") is { Length: > 0 } b64)
        {
            try { plain = RtfText.ToPlainText(Encoding.UTF8.GetString(Convert.FromBase64String(b64))); }
            catch (FormatException) { plain = ""; }
        }
        var run = new TextRun
        {
            Text = (plain ?? "").Replace("\r\n", "\n").Replace('\r', '\n'),
            FontFamily = Str(o, "FontFamily") ?? Str(o, "FontName") ?? "Segoe UI",
            FontSize = Num(o, "FontSize") is { } fs && fs > 0 ? fs : 16,
            Bold = Bool(o, "TextSelectionBold") ?? false,
            Italic = Bool(o, "TextSelectionItalic") ?? false,
            Underline = Bool(o, "TextSelectionUnderline") ?? false,
            Strikethrough = Bool(o, "TextSelectionStrikethrough") ?? false,
            Color = Color(o, "TextSelectionColor") ?? Color(o, "ForegroundColor") ?? Argb.Black,
        };
        var style = new TextLayoutStyle
        {
            HAlign = (Str(o, "ToolHorizontalAlign") ?? "").ToLowerInvariant() switch
            {
                var v when v.Contains("center") => HorizontalAlign.Center,
                var v when v.Contains("right") => HorizontalAlign.Right,
                var v when v.Contains("justif") => HorizontalAlign.Justify,
                _ => HorizontalAlign.Left,
            },
            VAlign = (Str(o, "ToolVerticalAlign") ?? "").ToLowerInvariant() switch
            {
                var v when v.Contains("middle") || v.Contains("center") => VerticalAlign.Middle,
                var v when v.Contains("bottom") => VerticalAlign.Bottom,
                _ => VerticalAlign.Top,
            },
            Padding = new Thickness2(Num(o, "ToolPadding") ?? 8),
            OutlineColor = Color(o, "TextOutlineColor") is { A: > 0 } oc ? oc : null,
            OutlineWidth = Num(o, "TextOutlineWidth") ?? 0,
            LineSpacing = Num(o, "TextLineSpacing") is { } ls && ls > 0 ? (ls > 4 ? ls / 100.0 : ls) : 1.0,
        };
        return (new RichText(new[] { run }), style);
    }

    private static TextAnnotation MapText(JsonElement o)
    {
        var (text, style) = TextOf(o);
        var a = new TextAnnotation { Text = text, TextStyle = style, PlaceholderText = Str(o, "PlaceholderText") };
        a.DeselectBehavior = (Str(o, "TextDeselectBehavior") ?? "").Contains("Delete", StringComparison.OrdinalIgnoreCase) ? TextDeselectBehavior.DeleteIfEmpty : TextDeselectBehavior.Keep;
        SetEnds(o, a);
        a.Stroke = StrokeStyle.None;
        a.Fill = FillOf(o);
        return a;
    }

    private static string CalloutPreset(string? s)
    {
        var v = (s ?? "").ToLowerInvariant();
        if (v.Contains("thought")) return CalloutPresets.ThoughtBubble;
        if (v.Contains("cloud")) return CalloutPresets.Cloud;
        if (v.Contains("speech") || v.Contains("balloon")) return CalloutPresets.SpeechBubble;
        if (v.Contains("arrow")) return v.Contains("rect") ? CalloutPresets.RoundedRectangle : CalloutPresets.ArrowRight;
        if (v.Contains("ellipse") || v.Contains("oval")) return CalloutPresets.Ellipse;
        if (v.Contains("rounded")) return CalloutPresets.RoundedRectangle;
        if (v.Contains("rect")) return CalloutPresets.Rectangle;
        return CalloutPresets.RoundedRectangle;
    }

    private static CalloutAnnotation MapCallout(JsonElement o)
    {
        var (text, style) = TextOf(o);
        var a = new CalloutAnnotation { ShapePresetId = CalloutPreset(Str(o, "CalloutShape")), Text = text, TextStyle = style, PlaceholderText = Str(o, "PlaceholderText") };
        SetEnds(o, a);
        a.Stroke = StrokeOf(o, Argb.Black);
        a.Fill = FillOf(o);
        var tailWidth = Num(o, "TailWidth") ?? 20;
        if (Prop(o, "CalloutTails") is { ValueKind: JsonValueKind.Array } tails)
        {
            foreach (var t in tails.EnumerateArray())
            {
                Point2? tip = t.ValueKind == JsonValueKind.String && JsonReadHelpers.TryParsePoint(t.GetString(), out var tp) ? tp
                    : t.ValueKind == JsonValueKind.Object ? Point(t, "TailPoint") ?? Point(t, "TipPoint") ?? Point(t, "Tip") ?? Point(t, "EndPoint") ?? Point(t, "Point") : null;
                if (tip is { } tipPoint)
                    a.Tails.Add(new CalloutTail(tipPoint, t.ValueKind == JsonValueKind.Object ? Num(t, "TailWidth") ?? tailWidth : tailWidth,
                        (Str(o, "TailLineStyle") ?? "").Contains("Curve", StringComparison.OrdinalIgnoreCase)));
            }
        }
        return a;
    }

    private static StepAnnotation MapStep(JsonElement o)
    {
        var styleName = (Str(o, "StepStyle") ?? "").ToLowerInvariant();
        var pointed = styleName.Contains("point");
        var square = styleName.Contains("square");
        var a = new StepAnnotation
        {
            Style = (square, pointed) switch
            {
                (true, true) => StepStyle.SquarePointer,
                (true, false) => StepStyle.Square,
                (false, true) => StepStyle.CirclePointer,
                _ => StepStyle.Circle,
            },
            Sequence = (Str(o, "StepSequenceType") ?? "").ToLowerInvariant() switch
            {
                var v when v.Contains("upper") => StepSequence.Upper,
                var v when v.Contains("lower") => StepSequence.Lower,
                _ => StepSequence.Number,
            },
            FontFamily = Str(o, "FontFamily") ?? Str(o, "FontName") ?? "Segoe UI",
            TextColor = Color(o, "TextSelectionColor") ?? Argb.White,
        };
        var label = Str(o, "PlainText")?.Trim();
        if (!string.IsNullOrEmpty(label))
        {
            if (int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) a.Value = Math.Max(1, n);
            else if (label.All(char.IsAsciiLetter))
            {
                var v = 0;
                foreach (var ch in label.ToUpperInvariant()) v = v * 26 + (ch - 'A' + 1);
                a.Value = Math.Max(1, v);
            }
        }
        var start = Point(o, "StartPoint") ?? Point2.Zero;
        var end = Point(o, "EndPoint") ?? start.Offset(40, 40);
        if (pointed)
        {
            var size = Math.Max(24, (Num(o, "FontSize") ?? 16) * 2.2);
            a.Start = start.Offset(-size / 2, -size / 2);
            a.End = start.Offset(size / 2, size / 2);
            a.PointerTip = end;
        }
        else
        {
            a.Start = start;
            a.End = end;
        }
        var fill = Color(o, "BackgroundColor") is { A: > 0 } bg ? bg : Color(o, "ForegroundColor") ?? Argb.Red;
        a.Fill = FillStyle.Solid(fill);
        a.Stroke = StrokeStyle.None;
        return a;
    }

    private static PenAnnotation? MapPen(JsonElement o)
    {
        var pts = Points(o, "PointsArray");
        if (pts is null || pts.Count == 0) return null;
        var a = new PenAnnotation
        {
            Points = pts.ToList(),
            Nib = (Str(o, "PenShape") ?? "").ToLowerInvariant() switch
            {
                var v when v.Contains("square") => PenNib.Square,
                var v when v.Contains("horiz") => PenNib.Horizontal,
                var v when v.Contains("vert") => PenNib.Vertical,
                var v when v.Contains("diag") => PenNib.Diagonal,
                _ => PenNib.Circle,
            },
            Smoothing = Bool(o, "Smoothing") ?? true,
        };
        a.Start = pts[0];
        a.End = pts[^1];
        a.Stroke = StrokeOf(o, Argb.Red);
        return a;
    }

    private static HighlightAnnotation MapHighlight(JsonElement o)
    {
        var a = new HighlightAnnotation();
        SetEnds(o, a);
        var color = Color(o, "ForegroundColor") ?? Color(o, "BackgroundColor") ?? Argb.HighlightYellow;
        var pts = Points(o, "PointsArray");
        if (pts is { Count: >= 2 })
        {
            a.Points = pts.ToList();
            a.Stroke = new StrokeStyle(color, Num(o, "StrokeWidth") ?? 16);
        }
        else
        {
            a.Fill = FillStyle.Solid(color);
            a.Stroke = StrokeStyle.None;
        }
        return a;
    }

    private static MagnifyAnnotation MapMagnify(JsonElement o)
    {
        var a = new MagnifyAnnotation { LensShape = ShapeOf(Str(o, "ToolShape") ?? "Ellipse") };
        SetEnds(o, a);
        var scale = Num(o, "MagnifyScale") ?? 200;
        a.Scale = scale > 10 ? scale / 100.0 : Math.Max(1, scale);
        var lens = a.Bounds;
        var offset = Point(o, "MagnifyOffset") ?? Point2.Zero;
        var sw = lens.Width / a.Scale;
        var sh = lens.Height / a.Scale;
        var c = lens.Center - offset;
        a.SourceRect = new Rect2(c.X - sw / 2, c.Y - sh / 2, sw, sh);
        a.ShowConnector = !(Str(o, "MagnifyConnectorType") ?? "SingleLine").Equals("None", StringComparison.OrdinalIgnoreCase);
        a.Stroke = StrokeOf(o, Argb.DarkGray);
        return a;
    }

    private static BlurAnnotation MapBlur(JsonElement o)
    {
        var type = (Str(o, "BlurType") ?? Str(o, "RedactionType") ?? "").ToLowerInvariant();
        var a = new BlurAnnotation
        {
            Region = ShapeOf(Str(o, "ToolShape")),
            Mode = type.Contains("pixel") ? BlurKind.Pixelate : type.Contains("solid") || type.Contains("black") || type.Contains("shape") ? BlurKind.Solid : BlurKind.Gaussian,
            Intensity = Math.Clamp(Num(o, "BlurSmoothIntensity") ?? Num(o, "BlurIntensity") ?? 50, 1, 100),
            SolidColor = Color(o, "BackgroundColor") is { A: > 0 } bc ? bc : Argb.Black,
            Category = MapRedactCategory(Str(o, "RedactionCategory")),
        };
        SetEnds(o, a);
        return a;
    }

    private Annotation? MapImage(JsonElement o, ShotDocument doc, bool asStamp)
    {
        var b64 = Str(o, "Image");
        if (string.IsNullOrEmpty(b64)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
        var asset = doc.Assets.Add(bytes, SniffMediaType(bytes, null));
        Annotation a = asStamp || SniffMediaType(bytes, null) is "application/pdf" or "image/svg+xml"
            ? new StampAnnotation { Asset = asset }
            : new ImageAnnotation { Asset = asset };
        if (Point(o, "StartPoint") is { } s && Point(o, "EndPoint") is { } e) { a.Start = s; a.End = e; }
        else
        {
            var pos = Point(o, "ImagePosition") ?? Point2.Zero;
            var size = Point(o, "ImageSize");
            if (size is null && PngCodec.TryReadSize(bytes, out var w, out var h)) size = new Point2(w, h);
            a.Start = pos;
            a.End = pos + (size ?? new Point2(100, 100));
        }
        return a;
    }

    private static DropZoneAnnotation MapDropZone(JsonElement o, Guid? sectionId)
    {
        var a = new DropZoneAnnotation { SectionId = sectionId ?? Guid.Empty };
        SetEnds(o, a);
        a.Stroke = StrokeOf(o, Argb.Gray);
        a.Fill = FillOf(o);
        if (Num(o, "CornerRadiusRatio") is { } r && r > 0)
        {
            var b = a.Bounds;
            a.CornerRadius = r * Math.Min(b.Width, b.Height) / 2;
        }
        return a;
    }

    // ------------------------------------------------------------------ metadata

    private static DocumentMetadata MapMetadata(JsonElement m)
    {
        var date = Str(m, "CaptureDate") is { } ds && DateTimeOffset.TryParse(ds, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : DateTimeOffset.Now;
        return new DocumentMetadata(date, Str(m, "AppName"), Str(m, "AppCompany"), Str(m, "AppVersion"), Str(m, "WindowName"), Str(m, "WebURL"),
            Str(m, "OperatingSystemVersion"), Str(m, "LanguageCode"), Str(m, "OCRText") ?? Str(m, "OcrText"), Array.Empty<string>(), null, null);
    }

    private static RedactCategory? MapRedactCategory(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        "" => null,
        "email" => RedactCategory.Email,
        "phonenumber" or "phone" => RedactCategory.Phone,
        "creditcardnumber" or "creditcard" => RedactCategory.CreditCard,
        "socialsecuritynumber" or "ssn" => RedactCategory.Ssn,
        "ipaddress" => RedactCategory.IpAddress,
        "weburl" or "url" => RedactCategory.Url,
        "date" => RedactCategory.Date,
        "postaladdress" or "address" => RedactCategory.PostalAddress,
        "face" => RedactCategory.Face,
        _ => RedactCategory.Other,
    };

    // ------------------------------------------------------------------ tolerant JSON accessors

    private static JsonDocument ParseJson(ZipArchiveEntry e)
        => JsonDocument.Parse(ZipSafety.ReadAllBytes(e, ZipSafety.MaxJsonEntryBytes), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 });

    private static JsonElement? Prop(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        if (o.TryGetProperty(name, out var v)) return v;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement o, string name) => Prop(o, name) is { } v ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    } : null;

    private static double? Num(JsonElement o, string name)
    {
        if (Prop(o, name) is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    private static bool? Bool(JsonElement o, params string[] names)
    {
        foreach (var name in names)
        {
            if (Prop(o, name) is not { } v) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number: return v.GetDouble() != 0;
                case JsonValueKind.String:
                    var s = v.GetString();
                    if (bool.TryParse(s, out var b)) return b;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d != 0;
                    break;
            }
        }
        return null;
    }

    /// <summary>0..100 or 0..1 → 0..1.</summary>
    private static double? Fraction(JsonElement o, params string[] names)
    {
        foreach (var n in names)
            if (Num(o, n) is { } v) return v > 1 ? Math.Clamp(v / 100.0, 0, 1) : Math.Clamp(v, 0, 1);
        return null;
    }

    private static Argb? Color(JsonElement o, string name) => Str(o, name) is { } s && Argb.TryParse(s, out var c) ? c : null;

    private static Point2? Point(JsonElement o, string name)
    {
        if (Prop(o, name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.String) return JsonReadHelpers.TryParsePoint(v.GetString(), out var p) ? p : null;
        if (v.ValueKind == JsonValueKind.Object && Num(v, "X") is { } x && Num(v, "Y") is { } y) return new Point2(x, y);
        return null;
    }

    private static IReadOnlyList<Point2>? Points(JsonElement o, string name)
    {
        if (Prop(o, name) is not { ValueKind: JsonValueKind.Array } arr) return null;
        var list = new List<Point2>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String && JsonReadHelpers.TryParsePoint(e.GetString(), out var p)) list.Add(p);
            else if (e.ValueKind == JsonValueKind.Object && Num(e, "X") is { } x && Num(e, "Y") is { } y) list.Add(new Point2(x, y));
        }
        return list;
    }

    private static GradientFill? Gradient(JsonElement? g)
    {
        if (g is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (Prop(obj, "GradientColors") is not { ValueKind: JsonValueKind.Array } colors) return null;
        var cs = colors.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => Argb.TryParse(c.GetString(), out var a) ? (Argb?)a : null).Where(c => c is not null).Select(c => c!.Value).ToList();
        if (cs.Count < 2) return null;
        var stops = cs.Select((c, i) => new ColorStop(cs.Count == 1 ? 0 : (double)i / (cs.Count - 1), c)).ToList();
        var radial = (Str(obj, "GradientStyle") ?? "").Contains("Radial", StringComparison.OrdinalIgnoreCase);
        return new GradientFill(radial ? GradientKind.Radial : GradientKind.Linear, Num(obj, "GradientAngle") ?? 90, stops);
    }

    private static bool TryGuid(string? s, out Guid g)
    {
        g = default;
        return !string.IsNullOrWhiteSpace(s) && Guid.TryParse(s.Trim().Trim('{', '}'), out g);
    }

    private static string SniffMediaType(byte[] b, string? name)
    {
        if (PngCodec.IsPng(b)) return "image/png";
        if (b.Length >= 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return "application/pdf";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
        var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 256)).TrimStart();
        if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))) return "image/svg+xml";
        return name is null ? "application/octet-stream" : AssetStore.MediaTypeForExtension(Path.GetExtension(name));
    }
}
