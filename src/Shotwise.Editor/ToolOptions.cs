using System.Text.Json;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using Shotwise.Core.Styles;

namespace Shotwise.Editor;

/// <summary>What erased / vacated pixels become.</summary>
public enum EraseMode
{
    /// <summary>Pixels are painted with the current canvas colour.</summary>
    CanvasColor,
    /// <summary>Pixels become transparent (the canvas colour, if any, shows through).</summary>
    Transparent,
}

/// <summary>Background left behind when pixels are moved, cut or deleted.</summary>
public enum SelectionBackground { Transparent, AutoFill }

public enum SimplifyToolMode { Draw, Move }

/// <summary>Fill tool: flood fill of flattened pixels.</summary>
public sealed class FillToolOptions
{
    public Argb Color { get; set; } = Argb.Red;
    /// <summary>Fill with transparency instead of <see cref="Color"/>.</summary>
    public bool Transparent { get; set; }
    /// <summary>Colour tolerance 0..100 %.</summary>
    public double Tolerance { get; set; } = 10;
    public double Opacity { get; set; } = 1;
    /// <summary>Global (non-contiguous) colour fill.</summary>
    public bool Global { get; set; }
}

public sealed class EraserToolOptions
{
    public double Width { get; set; } = 20;
    public EraseMode Mode { get; set; } = EraseMode.Transparent;
    public bool Antialias { get; set; } = true;
    public double Opacity { get; set; } = 1;
}

/// <summary>Selection (pixel) tool.</summary>
public sealed class PixelSelectionOptions
{
    public PixelSelectionShape Shape { get; set; } = PixelSelectionShape.Rectangle;
    /// <summary>Snap rectangle edges to object/canvas edges while dragging.</summary>
    public bool SnapToObject { get; set; }
    public SelectionBackground Background { get; set; } = SelectionBackground.AutoFill;
    /// <summary>Quality (slower) auto-fill instead of speed.</summary>
    public bool HighQuality { get; set; }
}

public sealed class MagicWandOptions
{
    /// <summary>Colour tolerance 0..100 %.</summary>
    public double Tolerance { get; set; } = 15;
    /// <summary>Global colour selection (all matching pixels, not only connected ones).</summary>
    public bool GlobalColor { get; set; }
    public SelectionBackground Background { get; set; } = SelectionBackground.AutoFill;
    public bool HighQuality { get; set; }
    /// <summary>Morphological refine radius passed to <c>IMagicWandService.Refine</c> (0 = none).</summary>
    public int RefineRadius { get; set; }
}

public sealed class CropToolOptions
{
    /// <summary>Locked aspect ratio (width/height), null = free.</summary>
    public double? AspectRatio { get; set; }
}

public sealed class CutOutToolOptions
{
    public CutOutOrientation Orientation { get; set; } = CutOutOrientation.Horizontal;
    public CutOutEdgeStyle Edge { get; set; } = CutOutEdgeStyle.Tear;
}

public sealed class MoveToolOptions
{
    /// <summary>Smart Move: detect UI components in the flattened image and make them movable.</summary>
    public bool SmartMove { get; set; }
    public bool AutoTolerance { get; set; } = true;
    /// <summary>Detection tolerance (detail) 0..1 when <see cref="AutoTolerance"/> is off.</summary>
    public double Tolerance { get; set; } = 0.5;
    public bool HighQuality { get; set; }
}

public sealed class SimplifyToolOptions
{
    public SimplifyToolMode Mode { get; set; } = SimplifyToolMode.Draw;
    public SimplifyShapeKind Shape { get; set; } = SimplifyShapeKind.Rectangle;
    public int PaletteIndex { get; set; }
    public string? PlaceholderId { get; set; }
}

public sealed class GrabTextToolOptions
{
    /// <summary>Edit Text mode: recognised text becomes editable text objects over an inpainted background.</summary>
    public bool EditText { get; set; }
    public string? Language { get; set; }
}

public sealed class PenToolOptions
{
    /// <summary>When false the stroke is flattened into the image right after drawing.</summary>
    public bool CreateAsVector { get; set; } = true;
    /// <summary>Point-reduction tolerance (canvas px) applied when the stroke ends and smoothing is on.</summary>
    public double SimplifyTolerance { get; set; } = 0.75;
}

public sealed class HighlighterToolOptions
{
    /// <summary>Freehand stroke instead of a rectangle.</summary>
    public bool Freehand { get; set; }
}

/// <summary>Current stamp for the Stamp tool (set by the stamp gallery).</summary>
public sealed class StampToolOptions
{
    public byte[]? Data { get; set; }
    public string? MediaType { get; set; }
    public string? StampId { get; set; }
    /// <summary>Size used when the stamp is placed by a click.</summary>
    public Size2 NaturalSize { get; set; } = new(64, 64);
    public bool HasStamp => Data is { Length: > 0 } && !string.IsNullOrEmpty(MediaType);
}

/// <summary>
/// Options of tools that have no annotation prototype (or extra behaviour flags). Panels bind to these and call
/// <see cref="EditorSession.NotifyToolOptionsChanged"/>. Quick styles of such tools persist them in
/// <see cref="QuickStyle.Options"/> (JSON of the matching options class).
/// </summary>
public sealed class EditorToolOptions
{
    public FillToolOptions Fill { get; private set; } = new();
    public EraserToolOptions Eraser { get; private set; } = new();
    public PixelSelectionOptions Selection { get; private set; } = new();
    public MagicWandOptions MagicWand { get; private set; } = new();
    public CropToolOptions Crop { get; private set; } = new();
    public CutOutToolOptions CutOut { get; private set; } = new();
    public MoveToolOptions Move { get; private set; } = new();
    public SimplifyToolOptions Simplify { get; private set; } = new();
    public GrabTextToolOptions GrabText { get; private set; } = new();
    public PenToolOptions Pen { get; private set; } = new();
    public HighlighterToolOptions Highlighter { get; private set; } = new();
    public StampToolOptions Stamp { get; private set; } = new();

    /// <summary>Options object owned by <paramref name="tool"/>, or null.</summary>
    public object? For(ToolKind tool) => tool switch
    {
        ToolKind.Fill => Fill,
        ToolKind.Eraser => Eraser,
        ToolKind.Selection => Selection,
        ToolKind.MagicWand => MagicWand,
        ToolKind.Crop => Crop,
        ToolKind.CutOut => CutOut,
        ToolKind.Move => Move,
        ToolKind.Simplify => Simplify,
        ToolKind.GrabText => GrabText,
        ToolKind.Pen => Pen,
        ToolKind.Highlighter => Highlighter,
        ToolKind.Stamp => Stamp,
        _ => null,
    };

    /// <summary>Serializes the tool's options for <see cref="QuickStyle.Options"/>.</summary>
    public JsonElement? ToJson(ToolKind tool)
    {
        var o = For(tool);
        if (o is null) return null;
        if (o is StampToolOptions) return null; // stamp payload is document content, not a style
        return JsonSerializer.SerializeToElement(o, o.GetType(), ShotwiseJson.Compact);
    }

    /// <summary>Loads the tool's options from quick-style JSON. Returns false when the JSON does not fit.</summary>
    public bool Apply(ToolKind tool, JsonElement json)
    {
        try
        {
            switch (tool)
            {
                case ToolKind.Fill: Fill = json.Deserialize<FillToolOptions>(ShotwiseJson.Compact) ?? Fill; return true;
                case ToolKind.Eraser: Eraser = json.Deserialize<EraserToolOptions>(ShotwiseJson.Compact) ?? Eraser; return true;
                case ToolKind.Selection: Selection = json.Deserialize<PixelSelectionOptions>(ShotwiseJson.Compact) ?? Selection; return true;
                case ToolKind.MagicWand: MagicWand = json.Deserialize<MagicWandOptions>(ShotwiseJson.Compact) ?? MagicWand; return true;
                case ToolKind.Crop: Crop = json.Deserialize<CropToolOptions>(ShotwiseJson.Compact) ?? Crop; return true;
                case ToolKind.CutOut: CutOut = json.Deserialize<CutOutToolOptions>(ShotwiseJson.Compact) ?? CutOut; return true;
                case ToolKind.Move: Move = json.Deserialize<MoveToolOptions>(ShotwiseJson.Compact) ?? Move; return true;
                case ToolKind.Simplify: Simplify = json.Deserialize<SimplifyToolOptions>(ShotwiseJson.Compact) ?? Simplify; return true;
                case ToolKind.GrabText: GrabText = json.Deserialize<GrabTextToolOptions>(ShotwiseJson.Compact) ?? GrabText; return true;
                case ToolKind.Pen: Pen = json.Deserialize<PenToolOptions>(ShotwiseJson.Compact) ?? Pen; return true;
                case ToolKind.Highlighter: Highlighter = json.Deserialize<HighlighterToolOptions>(ShotwiseJson.Compact) ?? Highlighter; return true;
                default: return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
