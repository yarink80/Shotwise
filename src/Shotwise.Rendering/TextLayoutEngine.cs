using System.Collections.Concurrent;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Rendering;

/// <summary>One shaped, positioned piece of text ready to be drawn.</summary>
internal sealed record TextDrawItem(
    SKTypeface Face, float Size, ushort[] Glyphs, SKPoint[] Positions, float X, float Baseline, float Advance,
    Argb Color, bool Underline, bool Strike, float UnderlineY, float UnderlineThickness, float StrikeY, float StrikeThickness);

/// <summary>Result of laying out rich text: public caret/line info plus internal draw items.</summary>
internal sealed record TextLayoutResult(TextLayoutInfo Info, IReadOnlyList<TextDrawItem> Items);

/// <summary>
/// Rich-text layout using HarfBuzz shaping: paragraphs, word wrapping, alignment (incl. justify), line spacing and per-character caret stops.
/// Coordinates are relative to the text box origin (padding included).
/// </summary>
internal static class TextLayoutEngine
{
    private static readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), SKTypeface> Faces = new();
    private static readonly ConcurrentDictionary<SKTypeface, SKShaper> Shapers = new();
    private static readonly object ShapeLock = new();

    public static SKTypeface GetTypeface(string? family, bool bold, bool italic)
    {
        var key = (family ?? "Segoe UI", bold, italic);
        return Faces.GetOrAdd(key, k =>
        {
            var style = new SKFontStyle(k.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                k.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            return SKTypeface.FromFamilyName(k.Family, style) ?? SKTypeface.Default;
        });
    }

    private static SKFont MakeFont(SKTypeface face, float size) => new(face, size)
    {
        Subpixel = true,
        Edging = SKFontEdging.SubpixelAntialias,
        Hinting = SKFontHinting.None,
        LinearMetrics = true,
    };

    private sealed class Token
    {
        public int Run;
        public int StartIndex;
        public string Text = "";
        public bool IsSpace;
        public SKTypeface Face = SKTypeface.Default;
        public float Size;
        public ushort[] Glyphs = Array.Empty<ushort>();
        public SKPoint[] Positions = Array.Empty<SKPoint>();
        public float[] CharX = new float[1]; // Length+1 caret offsets relative to token start
        public float Advance;
        public float X;       // laid-out x
        public float Extra;   // justify extra advance (spaces)
    }

    private sealed class Line
    {
        public readonly List<Token> Tokens = new();
        public int StartIndex;
        public int Length;
        public float Width;          // content width without trailing spaces
        public float FullWidth;      // including trailing spaces
        public float Ascent, Descent, Height;
        public bool ParagraphEnd;
        public float Top, Baseline, X;
        public int RunHint;
    }

    public static TextLayoutResult Layout(RichText text, TextLayoutStyle style, double maxWidth, double boxHeight = 0)
    {
        var runs = text.Runs;
        var pad = style.Padding;
        var wrapW = maxWidth > 0 ? Math.Max(1, maxWidth - pad.Horizontal) : double.PositiveInfinity;
        var lines = new List<Line>();

        var paraTokens = new List<Token>();
        var idx = 0;
        var paraStart = 0;
        var lastRun = 0;
        for (var ri = 0; ri < runs.Count; ri++)
        {
            var run = runs[ri];
            var s = run.Text ?? "";
            var i = 0;
            lastRun = ri;
            while (i < s.Length)
            {
                var nl = s.IndexOfAny(new[] { '\n', '\r' }, i);
                var segEnd = nl < 0 ? s.Length : nl;
                if (segEnd > i) TokenizeInto(paraTokens, s.Substring(i, segEnd - i), ri, idx + i, run);
                if (nl < 0) break;
                var term = s[nl] == '\r' && nl + 1 < s.Length && s[nl + 1] == '\n' ? 2 : 1;
                WrapParagraph(paraTokens, wrapW, runs, ri, lines, paraStart);
                paraTokens.Clear();
                paraStart = idx + nl + term;
                i = nl + term;
            }
            idx += s.Length;
        }
        WrapParagraph(paraTokens, wrapW, runs, lastRun, lines, paraStart);

        return Finish(lines, runs, style, maxWidth, boxHeight, wrapW);
    }

    private static void TokenizeInto(List<Token> dst, string seg, int runIdx, int startIndex, TextRun run)
    {
        var i = 0;
        while (i < seg.Length)
        {
            var isSpace = char.IsWhiteSpace(seg[i]);
            var j = i + 1;
            while (j < seg.Length && char.IsWhiteSpace(seg[j]) == isSpace) j++;
            var t = new Token { Run = runIdx, StartIndex = startIndex + i, Text = seg.Substring(i, j - i), IsSpace = isSpace, Size = (float)Math.Max(1, run.FontSize) };
            Shape(t, run);
            dst.Add(t);
            i = j;
        }
    }

    private static SKTypeface PickFace(SKTypeface face, string text, TextRun run)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value <= 0x20) continue;
            if (face.ContainsGlyph(rune.Value)) continue;
            var fb = SKFontManager.Default.MatchCharacter(run.FontFamily, run.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal, run.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright, null, rune.Value);
            if (fb is not null) return fb;
        }
        return face;
    }

    private static void Shape(Token t, TextRun run)
    {
        var face = PickFace(GetTypeface(run.FontFamily, run.Bold, run.Italic), t.Text, run);
        t.Face = face;
        using var font = MakeFont(face, t.Size);
        SKShaper shaper;
        SKShaper.Result res;
        lock (ShapeLock)
        {
            shaper = Shapers.GetOrAdd(face, f => new SKShaper(f));
            res = shaper.Shape(t.Text, font);
        }
        var n = res.Codepoints.Length;
        t.Glyphs = new ushort[n];
        t.Positions = new SKPoint[n];
        for (var i = 0; i < n; i++)
        {
            t.Glyphs[i] = (ushort)res.Codepoints[i];
            t.Positions[i] = new SKPoint(res.Points[i].X, 0);
        }
        t.Advance = res.Width;
        if (t.Text.Length == 0) { t.CharX = new float[] { 0 }; return; }

        // Per-character caret offsets from cluster boundaries.
        var charX = new float[t.Text.Length + 1];
        if (n == 0)
        {
            t.CharX = charX;
            return;
        }
        // cluster start -> x; clusters are UTF-16 indices (SkiaSharp.HarfBuzz uses UTF-16 buffers).
        var clusterStartX = new SortedDictionary<int, float>();
        for (var i = 0; i < n; i++)
        {
            var c = (int)res.Clusters[i];
            if (!clusterStartX.ContainsKey(c)) clusterStartX[c] = res.Points[i].X;
            else clusterStartX[c] = Math.Min(clusterStartX[c], res.Points[i].X);
        }
        var keys = clusterStartX.Keys.ToList();
        // RTL runs have decreasing x; take the extreme per cluster boundaries using min x as start (LTR) – adequate for shaping needs here.
        for (var k = 0; k < keys.Count; k++)
        {
            var cs = keys[k];
            var ce = k + 1 < keys.Count ? keys[k + 1] : t.Text.Length;
            var xs = clusterStartX[cs];
            var xe = k + 1 < keys.Count ? clusterStartX[keys[k + 1]] : t.Advance;
            var cnt = Math.Max(1, ce - cs);
            for (var q = 0; q < cnt && cs + q < charX.Length; q++)
                charX[cs + q] = xs + (xe - xs) * q / cnt;
        }
        charX[t.Text.Length] = t.Advance;
        // enforce monotonic non-decreasing for LTR
        for (var i = 1; i < charX.Length; i++) if (charX[i] < charX[i - 1]) charX[i] = charX[i - 1];
        t.CharX = charX;
    }

    private static void WrapParagraph(List<Token> tokens, double wrapW, IReadOnlyList<TextRun> runs, int hintRun, List<Line> lines, int paraStart)
    {
        var start = lines.Count;
        var cur = NewLine(hintRun);
        float x = 0;
        // split overlong tokens
        if (!double.IsPositiveInfinity(wrapW))
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.IsSpace || t.Advance <= wrapW) continue;
                var parts = SplitToken(t, (float)wrapW, runs);
                tokens.RemoveAt(i);
                tokens.InsertRange(i, parts);
                i += parts.Count - 1;
            }
        }
        foreach (var t in tokens)
        {
            if (!t.IsSpace && cur.Tokens.Count > 0 && x + t.Advance > wrapW + 0.01 && cur.Tokens.Any(q => !q.IsSpace))
            {
                CloseLine(cur, runs);
                lines.Add(cur);
                cur = NewLine(t.Run);
                x = 0;
            }
            cur.Tokens.Add(t);
            x += t.Advance;
        }
        cur.ParagraphEnd = true;
        CloseLine(cur, runs);
        lines.Add(cur);
        // Assign start indices and lengths (tokens carry absolute indices).
        for (var i = start; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Tokens.Count > 0)
            {
                l.StartIndex = l.Tokens[0].StartIndex;
                var last = l.Tokens[^1];
                l.Length = last.StartIndex + last.Text.Length - l.StartIndex;
            }
            else
            {
                l.StartIndex = paraStart;
                l.Length = 0;
            }
        }
    }

    private static Line NewLine(int hintRun) => new() { RunHint = hintRun };

    private static void CloseLine(Line l, IReadOnlyList<TextRun> runs)
    {
        float asc = 0, desc = 0, lead = 0;
        float width = 0, full = 0;
        foreach (var t in l.Tokens)
        {
            using var font = MakeFont(t.Face, t.Size);
            var m = font.Metrics;
            asc = Math.Max(asc, -m.Ascent);
            desc = Math.Max(desc, m.Descent);
            lead = Math.Max(lead, m.Leading);
            full += t.Advance;
        }
        if (l.Tokens.Count == 0)
        {
            var run = runs.Count > 0 ? runs[Math.Clamp(l.RunHint, 0, runs.Count - 1)] : new TextRun();
            using var font = MakeFont(GetTypeface(run.FontFamily, run.Bold, run.Italic), (float)Math.Max(1, run.FontSize));
            var m = font.Metrics;
            asc = -m.Ascent; desc = m.Descent; lead = m.Leading;
        }
        // trailing whitespace is excluded from alignment width
        width = full;
        for (var i = l.Tokens.Count - 1; i >= 0 && l.Tokens[i].IsSpace; i--) width -= l.Tokens[i].Advance;
        l.Ascent = asc;
        l.Descent = desc;
        l.Height = asc + desc + lead;
        l.Width = width;
        l.FullWidth = full;
    }

    private static List<Token> SplitToken(Token t, float wrapW, IReadOnlyList<TextRun> runs)
    {
        var run = runs[t.Run];
        var res = new List<Token>();
        var i = 0;
        var text = t.Text;
        while (i < text.Length)
        {
            var j = i + 1;
            while (j < text.Length && t.CharX[j + 1 > text.Length ? text.Length : j + 1] - t.CharX[i] <= wrapW) j++;
            if (j < text.Length && char.IsLowSurrogate(text[j])) j++;
            var piece = text.Substring(i, j - i);
            var nt = new Token { Run = t.Run, StartIndex = t.StartIndex + i, Text = piece, IsSpace = false, Size = t.Size };
            Shape(nt, run);
            res.Add(nt);
            i = j;
        }
        return res;
    }

    private static TextLayoutResult Finish(List<Line> lines, IReadOnlyList<TextRun> runs, TextLayoutStyle style, double maxWidth, double boxHeight, double wrapW)
    {
        var pad = style.Padding;
        var maxLine = lines.Count == 0 ? 0 : lines.Max(l => l.Width);
        var layoutW = double.IsPositiveInfinity(wrapW) ? maxLine : wrapW;
        var spacing = (float)Math.Max(0.1, style.LineSpacing);

        // vertical positions
        float y = 0;
        foreach (var l in lines)
        {
            var natural = l.Height;
            var h = natural * spacing;
            l.Top = y;
            l.Baseline = y + l.Ascent + (h - natural) * 0.5f;
            l.Height = h;
            y += h;
        }
        var contentH = y;
        double yOff = pad.Top;
        if (boxHeight > 0)
        {
            var avail = boxHeight - pad.Vertical;
            if (style.VAlign == VerticalAlign.Middle && contentH < avail) yOff += (avail - contentH) / 2;
            else if (style.VAlign == VerticalAlign.Bottom && contentH < avail) yOff += avail - contentH;
        }

        var infoLines = new List<TextLineInfo>();
        var items = new List<TextDrawItem>();
        for (var li = 0; li < lines.Count; li++)
        {
            var l = lines[li];
            // alignment
            double x0 = pad.Left;
            var align = style.HAlign;
            var extraPerSpace = 0f;
            if (align == HorizontalAlign.Justify)
            {
                var lastNonSpace = l.Tokens.FindLastIndex(t => !t.IsSpace);
                var inner = l.Tokens.Take(lastNonSpace + 1).Count(t => t.IsSpace);
                if (!l.ParagraphEnd && inner > 0 && !double.IsPositiveInfinity(wrapW) && layoutW > l.Width)
                    extraPerSpace = (float)((layoutW - l.Width) / inner);
                else align = HorizontalAlign.Left;
            }
            if (align == HorizontalAlign.Center) x0 += (layoutW - l.Width) / 2;
            else if (align == HorizontalAlign.Right) x0 += layoutW - l.Width;
            var lastIdx = l.Tokens.FindLastIndex(t => !t.IsSpace);
            var carets = new List<double>();
            var cx = (float)x0;
            var drawn = 0;
            foreach (var (t, ti) in l.Tokens.Select((t, i) => (t, i)))
            {
                t.X = cx;
                var adv = t.Advance;
                if (t.IsSpace && ti < lastIdx && extraPerSpace > 0)
                {
                    // spread extra over the characters of the space token
                    adv += extraPerSpace;
                    t.Extra = extraPerSpace;
                }
                for (var c = 0; c < t.Text.Length; c++)
                {
                    var rel = t.CharX[c];
                    if (t.Extra > 0 && t.Text.Length > 0) rel += t.Extra * c / t.Text.Length;
                    carets.Add(cx + rel);
                    drawn++;
                }
                cx += adv;
            }
            carets.Add(l.Tokens.Count == 0 ? x0 : cx);
            // Ensure Length+1 entries
            while (carets.Count < l.Length + 1) carets.Add(carets[^1]);
            if (carets.Count > l.Length + 1) carets.RemoveRange(l.Length + 1, carets.Count - l.Length - 1);
            _ = drawn;

            var top = yOff + l.Top;
            var baseline = yOff + l.Baseline;
            l.X = (float)x0;
            var lineW = (cx - (float)x0);
            infoLines.Add(new TextLineInfo(l.StartIndex, l.Length, new Rect2(x0, top, Math.Max(lineW, 0), l.Height), baseline, carets));

            foreach (var t in l.Tokens)
            {
                if (t.Glyphs.Length == 0) continue;
                var run = runs[Math.Clamp(t.Run, 0, runs.Count - 1)];
                using var font = MakeFont(t.Face, t.Size);
                var m = font.Metrics;
                var ulPos = m.UnderlinePosition ?? t.Size * 0.12f;
                var ulTh = Math.Max(1f, m.UnderlineThickness ?? t.Size / 14f);
                var stPos = m.StrikeoutPosition ?? -t.Size * 0.3f;
                var stTh = Math.Max(1f, m.StrikeoutThickness ?? t.Size / 14f);
                items.Add(new TextDrawItem(t.Face, t.Size, t.Glyphs, t.Positions, t.X, (float)baseline, t.Advance + t.Extra, run.Color,
                    run.Underline, run.Strikethrough, ulPos, ulTh, stPos, stTh));
            }
        }

        var contentW = lines.Count == 0 ? 0 : lines.Max(l => l.FullWidth > 0 ? l.Width : 0);
        // Content size reports the text extents plus padding (what an auto-sizing box needs).
        var size = new Size2(contentW + pad.Horizontal, contentH + pad.Vertical);
        if (lines.Count == 0)
        {
            var run = runs.Count > 0 ? runs[0] : new TextRun();
            using var font = MakeFont(GetTypeface(run.FontFamily, run.Bold, run.Italic), (float)Math.Max(1, run.FontSize));
            var m = font.Metrics;
            var h = (-m.Ascent + m.Descent + m.Leading) * spacing;
            size = new Size2(pad.Horizontal, h + pad.Vertical);
            infoLines.Add(new TextLineInfo(0, 0, new Rect2(pad.Left, pad.Top, 0, h), pad.Top - m.Ascent, new[] { (double)pad.Left }));
        }
        return new TextLayoutResult(new TextLayoutInfo(size, infoLines), items);
    }

    /// <summary>Draws laid-out text (with optional outline) at <paramref name="origin"/> (the text box top-left).</summary>
    public static void Draw(SKCanvas canvas, TextLayoutResult layout, SKPoint origin, TextLayoutStyle style, SKRect? clip = null)
    {
        if (layout.Items.Count == 0) return;
        var restore = -1;
        if (clip is { } c)
        {
            restore = canvas.Save();
            canvas.ClipRect(c, SKClipOperation.Intersect, true);
        }
        foreach (var it in layout.Items)
        {
            using var font = MakeFont(it.Face, it.Size);
            using var builder = new SKTextBlobBuilder();
            builder.AddPositionedRun(it.Glyphs, font, it.Positions);
            using var blob = builder.Build();
            if (blob is null) continue;
            var x = origin.X + it.X;
            var y = origin.Y + it.Baseline;
            if (style.OutlineColor is { } oc && style.OutlineWidth > 0 && oc.A > 0)
            {
                using var op = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)style.OutlineWidth * 2, StrokeJoin = SKStrokeJoin.Round, Color = oc.ToSk() };
                canvas.DrawText(blob, x, y, op);
            }
            using var paint = new SKPaint { IsAntialias = true, Color = it.Color.ToSk() };
            canvas.DrawText(blob, x, y, paint);
            if (it.Underline) canvas.DrawRect(x, y + it.UnderlineY - it.UnderlineThickness / 2, it.Advance, it.UnderlineThickness, paint);
            if (it.Strike) canvas.DrawRect(x, y + it.StrikeY - it.StrikeThickness / 2, it.Advance, it.StrikeThickness, paint);
        }
        if (restore >= 0) canvas.RestoreToCount(restore);
    }
}
