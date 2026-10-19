using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Shotwise.Core.Annotations;
using Shotwise.Core.Imaging;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Editor.Surface;

/// <summary>Conversion between Core <see cref="RichText"/> runs and WPF <see cref="FlowDocument"/>s (inline text editor).</summary>
internal static class RichTextConversion
{
    public static FlowDocument ToDocument(RichText text, TextLayoutStyle style)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(style.Padding.Left, style.Padding.Top, style.Padding.Right, style.Padding.Bottom),
            TextAlignment = style.HAlign switch
            {
                HorizontalAlign.Center => TextAlignment.Center,
                HorizontalAlign.Right => TextAlignment.Right,
                HorizontalAlign.Justify => TextAlignment.Justify,
                _ => TextAlignment.Left,
            },
            PageWidth = double.NaN,
        };
        var paragraph = NewParagraph(style);
        doc.Blocks.Add(paragraph);
        foreach (var run in text.Runs)
        {
            var parts = run.Text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    paragraph = NewParagraph(style);
                    doc.Blocks.Add(paragraph);
                }
                if (parts[i].Length == 0) continue;
                paragraph.Inlines.Add(ToWpfRun(parts[i], run));
            }
        }
        return doc;
    }

    private static Paragraph NewParagraph(TextLayoutStyle style) => new()
    {
        Margin = new Thickness(0),
        LineHeight = double.NaN,
        TextAlignment = style.HAlign switch
        {
            HorizontalAlign.Center => TextAlignment.Center,
            HorizontalAlign.Right => TextAlignment.Right,
            HorizontalAlign.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        },
    };

    private static Run ToWpfRun(string text, TextRun r)
    {
        var run = new Run(text)
        {
            FontFamily = new FontFamily(r.FontFamily),
            FontSize = r.FontSize,
            FontWeight = r.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = r.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = r.Color.ToBrush(),
        };
        var decorations = new TextDecorationCollection();
        if (r.Underline) decorations.Add(TextDecorations.Underline[0]);
        if (r.Strikethrough) decorations.Add(TextDecorations.Strikethrough[0]);
        if (decorations.Count > 0) run.TextDecorations = decorations;
        return run;
    }

    /// <summary>Reads the document back into runs; paragraph breaks become "\n".</summary>
    public static RichText FromDocument(FlowDocument doc, TextRun fallback)
    {
        var runs = new List<TextRun>();
        var blocks = doc.Blocks.OfType<Paragraph>().ToList();
        for (var i = 0; i < blocks.Count; i++)
        {
            AppendInlines(blocks[i].Inlines, runs, fallback);
            if (i < blocks.Count - 1) Append(runs, "\n", runs.Count > 0 ? runs[^1] : fallback);
        }
        if (runs.Count == 0) runs.Add(fallback with { Text = "" });
        return new RichText(runs).Normalize() is { Runs.Count: > 0 } n ? n : new RichText(runs);
    }

    private static void AppendInlines(InlineCollection inlines, List<TextRun> output, TextRun fallback)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    if (run.Text.Length > 0) Append(output, run.Text, FromWpfRun(run, fallback));
                    break;
                case LineBreak:
                    Append(output, "\n", output.Count > 0 ? output[^1] : fallback);
                    break;
                case Span span:
                    AppendInlines(span.Inlines, output, fallback);
                    break;
            }
        }
    }

    private static void Append(List<TextRun> output, string text, TextRun style)
    {
        if (output.Count > 0 && output[^1] with { Text = "" } == style with { Text = "" })
            output[^1] = output[^1] with { Text = output[^1].Text + text };
        else output.Add(style with { Text = text });
    }

    private static TextRun FromWpfRun(Run run, TextRun fallback)
    {
        var color = run.Foreground is SolidColorBrush b ? b.Color.ToArgb() : fallback.Color;
        var decorations = run.TextDecorations;
        return new TextRun
        {
            Text = run.Text,
            FontFamily = run.FontFamily?.Source ?? fallback.FontFamily,
            FontSize = run.FontSize,
            Bold = run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight(),
            Italic = run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique,
            Underline = decorations.Any(d => d.Location == TextDecorationLocation.Underline),
            Strikethrough = decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough),
            Color = color,
        };
    }
}
