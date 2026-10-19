using System.Text;
using System.Text.Json.Serialization;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Annotations;

public enum HorizontalAlign { Left, Center, Right, Justify }
public enum VerticalAlign { Top, Middle, Bottom }
public enum TextDeselectBehavior { DeleteIfEmpty, Keep }

/// <summary>A run of uniformly formatted text. Paragraph break = "\n" inside <see cref="Text"/>.</summary>
public sealed record TextRun
{
    public string Text { get; init; } = "";
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 16;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }
    public Argb Color { get; init; } = Argb.Black;
}

/// <summary>Minimal rich-text model: an ordered list of runs.</summary>
public sealed class RichText
{
    public List<TextRun> Runs { get; set; } = new();

    public RichText() { }
    public RichText(IEnumerable<TextRun> runs) => Runs = runs.ToList();

    /// <summary>Concatenated text of all runs.</summary>
    [JsonIgnore]
    public string PlainText
    {
        get
        {
            if (Runs.Count == 1) return Runs[0].Text;
            var sb = new StringBuilder();
            foreach (var r in Runs) sb.Append(r.Text);
            return sb.ToString();
        }
    }

    [JsonIgnore] public bool IsEmpty => Runs.All(r => string.IsNullOrWhiteSpace(r.Text));

    public static RichText FromPlain(string text, string fontFamily = "Segoe UI", double fontSize = 16, Argb? color = null, bool bold = false)
        => new(new[] { new TextRun { Text = text ?? "", FontFamily = fontFamily, FontSize = fontSize, Color = color ?? Argb.Black, Bold = bold } });

    public RichText Clone() => new(Runs);

    /// <summary>Returns a copy with <paramref name="mutate"/> applied to every run (e.g. change font for whole box).</summary>
    public RichText MapRuns(Func<TextRun, TextRun> mutate) => new(Runs.Select(mutate));

    /// <summary>Merges adjacent runs with identical formatting.</summary>
    public RichText Normalize()
    {
        var outp = new List<TextRun>();
        foreach (var r in Runs)
        {
            if (r.Text.Length == 0) continue;
            if (outp.Count > 0 && outp[^1] with { Text = "" } == r with { Text = "" })
                outp[^1] = outp[^1] with { Text = outp[^1].Text + r.Text };
            else outp.Add(r);
        }
        return new RichText(outp);
    }
}

/// <summary>Box-level text layout options for Text and Callout annotations.</summary>
public sealed record TextLayoutStyle
{
    public HorizontalAlign HAlign { get; init; } = HorizontalAlign.Left;
    public VerticalAlign VAlign { get; init; } = VerticalAlign.Top;
    public Thickness2 Padding { get; init; } = new(8);
    public Argb? OutlineColor { get; init; }
    public double OutlineWidth { get; init; }
    public double LineSpacing { get; init; } = 1.0;
    /// <summary>True: box grows with content; false: fixed box, text wraps/clips.</summary>
    public bool AutoSize { get; init; } = true;

    public static TextLayoutStyle Default { get; } = new();
}
