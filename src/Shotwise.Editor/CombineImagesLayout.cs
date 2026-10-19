using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Editor;

public enum CombineLayoutKind
{
    /// <summary>Near-square grid of equal cells (images fit inside their cell).</summary>
    Grid,
    /// <summary>One image per row (stacked vertically, common width).</summary>
    Rows,
    /// <summary>One image per column (side by side, common height).</summary>
    Columns,
    /// <summary>First image large on the left, the others stacked on the right.</summary>
    FeatureLeft,
    /// <summary>First image large on top, the others in a row below.</summary>
    FeatureTop,
}

/// <summary>Options of the Combine Images layout generator (EDT-13).</summary>
public sealed record CombineLayoutOptions
{
    public CombineLayoutKind Kind { get; init; } = CombineLayoutKind.Grid;
    /// <summary>Gap between slots (px).</summary>
    public double Spacing { get; init; } = 16;
    /// <summary>Outer margin (px).</summary>
    public double Margin { get; init; } = 16;
    public Argb Background { get; init; } = Argb.White;
    /// <summary>Optional caption shown in a footer strip.</summary>
    public string? Caption { get; init; }
    public double CaptionHeight { get; init; } = 56;
    public double CaptionFontSize { get; init; } = 24;
    public Argb CaptionColor { get; init; } = Argb.Black;
    /// <summary>Upper bound for the common row height / column width / cell side (px) so huge captures stay manageable.</summary>
    public double MaxSlotSide { get; init; } = 1600;
    public double SlotCornerRadius { get; init; }
}

/// <summary>Computed layout: canvas size, one slot per image (input order) and the caption strip.</summary>
public sealed record CombineLayoutResult(Size2 CanvasSize, IReadOnlyList<Rect2> Slots, Rect2? CaptionRect);

/// <summary>
/// Combine Images layout generator: computes slot rectangles for N images and builds a template-style document
/// (one section + drop zone per slot, content pre-filled, optional caption section) so slots can be swapped and their
/// content panned/zoomed with the regular drop-zone commands.
/// </summary>
public static class CombineImagesLayout
{
    public static CombineLayoutResult Compute(IReadOnlyList<Size2> images, CombineLayoutOptions o)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(o);
        if (images.Count == 0) throw new ArgumentException("At least one image is required.", nameof(images));
        var sizes = images.Select(s => new Size2(Math.Max(1, s.Width), Math.Max(1, s.Height))).ToList();
        var m = Math.Max(0, o.Margin);
        var g = Math.Max(0, o.Spacing);
        var slots = new List<Rect2>();
        double contentW, contentH;
        switch (o.Kind)
        {
            case CombineLayoutKind.Columns:
                {
                    var h = Math.Min(o.MaxSlotSide, sizes.Max(s => s.Height));
                    var x = m;
                    foreach (var s in sizes)
                    {
                        var w = s.Width * h / s.Height;
                        slots.Add(new Rect2(x, m, w, h));
                        x += w + g;
                    }
                    contentW = x - g - m;
                    contentH = h;
                    break;
                }
            case CombineLayoutKind.Rows:
                {
                    var w = Math.Min(o.MaxSlotSide, sizes.Max(s => s.Width));
                    var y = m;
                    foreach (var s in sizes)
                    {
                        var h = s.Height * w / s.Width;
                        slots.Add(new Rect2(m, y, w, h));
                        y += h + g;
                    }
                    contentW = w;
                    contentH = y - g - m;
                    break;
                }
            case CombineLayoutKind.FeatureLeft when sizes.Count > 1:
                {
                    var rest = sizes.Count - 1;
                    var bigH = Math.Min(o.MaxSlotSide, sizes[0].Height);
                    var bigW = sizes[0].Width * bigH / sizes[0].Height;
                    var cellH = (bigH - g * (rest - 1)) / rest;
                    var cellW = sizes.Skip(1).Max(s => s.Width * cellH / s.Height);
                    slots.Add(new Rect2(m, m, bigW, bigH));
                    for (var i = 0; i < rest; i++) slots.Add(new Rect2(m + bigW + g, m + i * (cellH + g), cellW, cellH));
                    contentW = bigW + g + cellW;
                    contentH = bigH;
                    break;
                }
            case CombineLayoutKind.FeatureTop when sizes.Count > 1:
                {
                    var rest = sizes.Count - 1;
                    var bigW = Math.Min(o.MaxSlotSide, sizes[0].Width);
                    var bigH = sizes[0].Height * bigW / sizes[0].Width;
                    var cellW = (bigW - g * (rest - 1)) / rest;
                    var cellH = sizes.Skip(1).Max(s => s.Height * cellW / s.Width);
                    slots.Add(new Rect2(m, m, bigW, bigH));
                    for (var i = 0; i < rest; i++) slots.Add(new Rect2(m + i * (cellW + g), m + bigH + g, cellW, cellH));
                    contentW = bigW;
                    contentH = bigH + g + cellH;
                    break;
                }
            default:
                {
                    var n = sizes.Count;
                    var cols = (int)Math.Ceiling(Math.Sqrt(n));
                    var rows = (int)Math.Ceiling(n / (double)cols);
                    var cellW = Math.Min(o.MaxSlotSide, sizes.Max(s => s.Width));
                    var cellH = Math.Min(o.MaxSlotSide, sizes.Max(s => s.Height));
                    for (var i = 0; i < n; i++)
                    {
                        var r = i / cols;
                        var c = i % cols;
                        slots.Add(new Rect2(m + c * (cellW + g), m + r * (cellH + g), cellW, cellH));
                    }
                    contentW = cols * cellW + (cols - 1) * g;
                    contentH = rows * cellH + (rows - 1) * g;
                    break;
                }
        }
        Rect2? caption = null;
        var height = contentH + 2 * m;
        if (!string.IsNullOrWhiteSpace(o.Caption))
        {
            caption = new Rect2(m, height - m + g, contentW, o.CaptionHeight);
            height += g + o.CaptionHeight;
        }
        var size = new Size2(Math.Ceiling(contentW + 2 * m), Math.Ceiling(height));
        return new CombineLayoutResult(size, slots.Select(Round).ToList(), caption is { } cr ? Round(cr) : null);
    }

    private static Rect2 Round(Rect2 r) => Rect2.FromLTRB(Math.Round(r.Left), Math.Round(r.Top), Math.Round(r.Right), Math.Round(r.Bottom));

    /// <summary>Builds the combined document: background colour, one Body section + filled drop zone per image, optional caption footer.</summary>
    public static ShotDocument CreateDocument(IReadOnlyList<BgraImage> images, CombineLayoutOptions o, IImageCodec? codec = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        var layout = Compute(images.Select(i => new Size2(i.Width, i.Height)).ToList(), o);
        var doc = ShotDocument.Create(layout.CanvasSize, o.Background);
        doc.Assets.Codec = codec;
        var page = doc.Pages[0];
        for (var i = 0; i < images.Count; i++)
        {
            var slot = layout.Slots[i];
            var section = new TemplateSection { Type = SectionType.Body, Bounds = slot };
            var asset = doc.Assets.AddImage(images[i]);
            var imgSize = new Size2(images[i].Width, images[i].Height);
            var zone = new DropZoneAnnotation
            {
                SectionId = section.Id,
                Start = slot.TopLeft,
                End = slot.BottomRight,
                Content = asset,
                CornerRadius = o.SlotCornerRadius,
                Stroke = StrokeStyle.None,
            };
            zone.ContentZoom = o.Kind == CombineLayoutKind.Grid ? DropZoneLayout.FitZoom(slot, imgSize) : 1;
            section.Graphics.Add(zone);
            page.Sections.Add(section);
        }
        if (layout.CaptionRect is { } cr)
        {
            var footer = new TemplateSection { Type = SectionType.Footer, Bounds = cr };
            footer.Graphics.Add(new TextAnnotation
            {
                Start = cr.TopLeft,
                End = cr.BottomRight,
                Text = RichText.FromPlain(o.Caption!, fontSize: o.CaptionFontSize, color: o.CaptionColor),
                TextStyle = TextLayoutStyle.Default with { HAlign = HorizontalAlign.Center, VAlign = VerticalAlign.Middle, AutoSize = false },
                DeselectBehavior = TextDeselectBehavior.Keep,
            });
            page.Sections.Add(footer);
        }
        return doc;
    }
}
