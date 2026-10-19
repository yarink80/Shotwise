using Shotwise.Core.Annotations;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.UI.Capture.Coordinator;

/// <summary>
/// Turns recorded steps into a step-guide capture: a tall white page with, for every click, a numbered <see cref="StepAnnotation"/>
/// badge, a caption <see cref="TextAnnotation"/> ("Left click &lt;element&gt;") and the screenshot scaled to the page width.
/// The base raster carries the screenshots; badges and captions are editable annotations (CAP-08).
/// </summary>
public static class StepGuideBuilder
{
    public const int PageWidth = 960;
    public const int Margin = 40;
    public const int RowHeader = 40;
    public const int RowGap = 40;
    public const int TitleHeight = 70;

    public static CaptureResult Build(IReadOnlyList<StepCaptured> steps, DocumentMetadata metadata, string title, Func<StepCaptured, string> describe)
    {
        ArgumentNullException.ThrowIfNull(steps);
        int contentWidth = PageWidth - 2 * Margin;

        // Scale every screenshot to the content width (never upscale) and measure the page.
        var scaled = new List<BgraImage>(steps.Count);
        int y = Margin + TitleHeight;
        var rows = new List<(int Top, int ImageTop, StepCaptured Step, BgraImage Image)>();
        foreach (var s in steps.OrderBy(s => s.Index))
        {
            var img = s.Image;
            if (img.Width > contentWidth)
            {
                int h = Math.Max(1, (int)Math.Round(img.Height * (contentWidth / (double)img.Width)));
                img = ImageOps.ResizeArea(img, contentWidth, h);
            }
            scaled.Add(img);
            rows.Add((y, y + RowHeader, s, img));
            y += RowHeader + img.Height + RowGap;
        }
        int pageHeight = Math.Max(y - RowGap + Margin, Margin * 2 + TitleHeight);

        var canvas = BgraImage.CreateFilled(PageWidth, pageHeight, Argb.White);
        var annotations = new List<Annotation>();

        var titleText = new TextAnnotation
        {
            Start = new Point2(Margin, Margin),
            End = new Point2(PageWidth - Margin, Margin + TitleHeight - 10),
            Text = RichText.FromPlain(title, "Segoe UI", 28, Argb.FromRgb(0x20, 0x20, 0x20), bold: true),
            PlaceholderText = title,
        };
        annotations.Add(titleText);

        int number = 0;
        foreach (var (top, imageTop, step, image) in rows)
        {
            number++;
            int x = Margin + (contentWidth - image.Width) / 2;
            canvas.Blit(image, x, imageTop);

            // Badge at the click point when the capture bounds are known (CONTRACT A5/A23); otherwise in the row header.
            var badgeAt = new Point2(Margin, top);
            if (ClickInImage(step, image) is { } click)
                badgeAt = new Point2(x + click.X - 16, imageTop + click.Y - 16);
            annotations.Add(new StepAnnotation
            {
                Start = badgeAt,
                End = new Point2(badgeAt.X + 32, badgeAt.Y + 32),
                Style = StepStyle.Circle,
                Sequence = StepSequence.Number,
                Value = number,
                Fill = FillStyle.Solid(Argb.FromRgb(0x1E, 0x88, 0xE5)),
            });
            annotations.Add(new TextAnnotation
            {
                Start = new Point2(Margin + 44, top),
                End = new Point2(PageWidth - Margin, top + 34),
                Text = RichText.FromPlain(describe(step), "Segoe UI", 18, Argb.FromRgb(0x20, 0x20, 0x20)),
            });
        }
        return new CaptureResult(canvas, metadata, annotations);
    }

    /// <summary>The click position in the (possibly downscaled) step image, or null when it cannot be mapped.</summary>
    public static Point2? ClickInImage(StepCaptured step, BgraImage scaledImage)
    {
        if (step.CaptureBoundsPx is not { Width: > 0, Height: > 0 } b) return null;
        double sx = scaledImage.Width / (double)b.Width, sy = scaledImage.Height / (double)b.Height;
        double cx = (step.ClickPx.X - b.X) * sx, cy = (step.ClickPx.Y - b.Y) * sy;
        if (cx < 0 || cy < 0 || cx > scaledImage.Width || cy > scaledImage.Height) return null;
        return new Point2(cx, cy);
    }
}
