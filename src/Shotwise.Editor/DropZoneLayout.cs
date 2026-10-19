using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;

namespace Shotwise.Editor;

/// <summary>
/// Placement of drop-zone content (EDT-14). Semantics shared with the renderer:
/// the content image is scaled by <c>CoverScale(zone, image) × ContentZoom</c> (zoom 1 = the image just covers the
/// zone), centred on the zone centre, shifted by <see cref="DropZoneAnnotation.ContentOffset"/> (canvas px) and clipped
/// to the zone (rounded by <see cref="DropZoneAnnotation.CornerRadius"/>).
/// </summary>
public static class DropZoneLayout
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 20;

    /// <summary>Scale at which the image exactly covers the zone.</summary>
    public static double CoverScale(Rect2 zone, Size2 image)
        => image.Width <= 0 || image.Height <= 0 ? 1 : Math.Max(zone.Width / image.Width, zone.Height / image.Height);

    /// <summary>Scale at which the whole image fits inside the zone.</summary>
    public static double FitScale(Rect2 zone, Size2 image)
        => image.Width <= 0 || image.Height <= 0 ? 1 : Math.Min(zone.Width / image.Width, zone.Height / image.Height);

    /// <summary>ContentZoom that makes the image fit (letterboxed) instead of cover.</summary>
    public static double FitZoom(Rect2 zone, Size2 image) => Math.Clamp(FitScale(zone, image) / CoverScale(zone, image), MinZoom, MaxZoom);

    /// <summary>Destination rectangle of the content image in canvas pixels (before clipping to the zone).</summary>
    public static Rect2 ContentRect(DropZoneAnnotation zone, Size2 image)
    {
        var z = zone.Bounds;
        var s = CoverScale(z, image) * Math.Clamp(zone.ContentZoom, MinZoom, MaxZoom);
        var w = image.Width * s;
        var h = image.Height * s;
        var c = z.Center;
        return new Rect2(c.X + zone.ContentOffset.X - w / 2, c.Y + zone.ContentOffset.Y - h / 2, w, h);
    }
}
