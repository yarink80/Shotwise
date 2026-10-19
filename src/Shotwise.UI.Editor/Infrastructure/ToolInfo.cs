using Shotwise.Core.Styles;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Display names / descriptions and the default order of the editor tools.</summary>
internal static class ToolInfo
{
    /// <summary>
    /// Default tool order: the primary tools of the top bar first (DESIGN-SYSTEM §4), then the ones that live in the
    /// "More" flyout by default (<see cref="EditorLayout.DefaultHiddenTools"/>). Ctrl+1..9 map to the first nine pinned tools.
    /// </summary>
    public static readonly ToolKind[] DefaultOrder =
    {
        ToolKind.Select, ToolKind.Arrow, ToolKind.Text, ToolKind.Callout, ToolKind.Shape, ToolKind.Stamp, ToolKind.Step, ToolKind.Blur,
        ToolKind.Crop, ToolKind.Selection, ToolKind.Favorites,
        ToolKind.Move, ToolKind.Line, ToolKind.Pen, ToolKind.Highlighter, ToolKind.Fill, ToolKind.Eraser, ToolKind.Magnify,
        ToolKind.CutOut, ToolKind.MagicWand, ToolKind.Simplify, ToolKind.GrabText, ToolKind.Cursor,
    };

    public static string Name(ToolKind k) => k switch
    {
        ToolKind.Select => ES.T("Tool_Select", "Select"),
        ToolKind.Move => ES.T("Tool_Move", "Move"),
        ToolKind.Arrow => ES.T("Tool_Arrow", "Arrow"),
        ToolKind.Line => ES.T("Tool_Line", "Line"),
        ToolKind.Shape => ES.T("Tool_Shape", "Shape"),
        ToolKind.Callout => ES.T("Tool_Callout", "Callout"),
        ToolKind.Text => ES.T("Tool_Text", "Text"),
        ToolKind.Stamp => ES.T("Tool_Stamp", "Stamp"),
        ToolKind.Step => ES.T("Tool_Step", "Step"),
        ToolKind.Pen => ES.T("Tool_Pen", "Pen"),
        ToolKind.Highlighter => ES.T("Tool_Highlighter", "Highlighter"),
        ToolKind.Fill => ES.T("Tool_Fill", "Fill"),
        ToolKind.Eraser => ES.T("Tool_Eraser", "Eraser"),
        ToolKind.Magnify => ES.T("Tool_Magnify", "Magnify"),
        ToolKind.Blur => ES.T("Tool_Blur", "Blur"),
        ToolKind.Crop => ES.T("Tool_Crop", "Crop"),
        ToolKind.CutOut => ES.T("Tool_CutOut", "Cut Out"),
        ToolKind.Selection => ES.T("Tool_Selection", "Selection"),
        ToolKind.MagicWand => ES.T("Tool_MagicWand", "Magic Wand"),
        ToolKind.Simplify => ES.T("Tool_Simplify", "Simplify"),
        ToolKind.GrabText => ES.T("Tool_GrabText", "Grab Text"),
        ToolKind.Cursor => ES.T("Tool_Cursor", "Cursor"),
        ToolKind.Favorites => ES.T("Tool_Favorites", "Favorites"),
        _ => k.ToString(),
    };

    public static string Description(ToolKind k) => k switch
    {
        ToolKind.Select => ES.T("ToolDesc_Select", "Select, move, resize and rotate objects."),
        ToolKind.Move => ES.T("ToolDesc_Move", "Move objects; Smart Move detects interface elements in the image."),
        ToolKind.Arrow => ES.T("ToolDesc_Arrow", "Draw arrows, straight or curved."),
        ToolKind.Line => ES.T("ToolDesc_Line", "Draw lines and curves."),
        ToolKind.Shape => ES.T("ToolDesc_Shape", "Draw rectangles, ellipses and polygons."),
        ToolKind.Callout => ES.T("ToolDesc_Callout", "Add text in a callout shape with tails."),
        ToolKind.Text => ES.T("ToolDesc_Text", "Add rich text."),
        ToolKind.Stamp => ES.T("ToolDesc_Stamp", "Place stamps and icons."),
        ToolKind.Step => ES.T("ToolDesc_Step", "Add numbered or lettered steps."),
        ToolKind.Pen => ES.T("ToolDesc_Pen", "Draw freehand."),
        ToolKind.Highlighter => ES.T("ToolDesc_Highlighter", "Highlight an area."),
        ToolKind.Fill => ES.T("ToolDesc_Fill", "Flood fill similar pixels."),
        ToolKind.Eraser => ES.T("ToolDesc_Eraser", "Erase pixels."),
        ToolKind.Magnify => ES.T("ToolDesc_Magnify", "Enlarge part of the image in a lens."),
        ToolKind.Blur => ES.T("ToolDesc_Blur", "Blur, pixelate or cover sensitive areas."),
        ToolKind.Crop => ES.T("ToolDesc_Crop", "Crop the image."),
        ToolKind.CutOut => ES.T("ToolDesc_CutOut", "Remove a strip and join the remaining parts."),
        ToolKind.Selection => ES.T("ToolDesc_Selection", "Select pixels, then move, copy or delete them."),
        ToolKind.MagicWand => ES.T("ToolDesc_MagicWand", "Select pixels by colour."),
        ToolKind.Simplify => ES.T("ToolDesc_Simplify", "Cover detail with simplified shapes."),
        ToolKind.GrabText => ES.T("ToolDesc_GrabText", "Recognise text in the image."),
        ToolKind.Cursor => ES.T("ToolDesc_Cursor", "Insert a mouse cursor."),
        ToolKind.Favorites => ES.T("ToolDesc_Favorites", "Quick styles you starred."),
        _ => "",
    };

    /// <summary>Tooltip with name and the Ctrl+digit hotkey when the tool is among the first nine toolbar slots.</summary>
    /// <para>General ▸ Tooltips: <see cref="Shotwise.Core.Settings.TooltipDetail.TextOnly"/> shows name + hotkey; the
    /// detailed setting adds the tool description and a usage hint.</para>
    public static string Tooltip(ToolKind k, int slot, Shotwise.Core.Settings.TooltipDetail detail = Shotwise.Core.Settings.TooltipDetail.TextAndAnimation)
    {
        var head = slot is >= 0 and < 9 ? $"{Name(k)} (Ctrl+{slot + 1})" : Name(k);
        if (detail == Shotwise.Core.Settings.TooltipDetail.TextOnly) return head;
        var hint = IsAnnotationTool(k) ? ES.T("Tip_DrawHint", "Drag on the canvas to draw; pick a Quick Style to change its look.") : "";
        return string.IsNullOrEmpty(hint) ? $"{head}\n{Description(k)}" : $"{head}\n{Description(k)}\n{hint}";
    }

    /// <summary>Ordered tool list from preferences (unknown entries dropped, missing tools appended).</summary>
    public static List<ToolKind> Resolve(IEnumerable<string> saved)
    {
        var list = new List<ToolKind>();
        foreach (var s in saved)
            if (Enum.TryParse<ToolKind>(s, out var k) && !list.Contains(k)) list.Add(k);
        foreach (var k in DefaultOrder)
            if (!list.Contains(k)) list.Add(k);
        return list;
    }

    public static bool IsAnnotationTool(ToolKind k) => Shotwise.Editor.EditorSession.AnnotationKindFor(k) is not null;
}
