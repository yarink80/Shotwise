using Shotwise.Core.Geometry;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>
/// Rebuilds a table from OCR word boxes (VIS-01): words are grouped into rows by vertical overlap, split into cells at
/// "large" horizontal gaps (threshold derived from the gap statistics of the page), and cells are assigned to columns whose
/// extents are the union of overlapping cell intervals.
/// </summary>
public sealed class TableExtractor : ITableExtractor
{
    private sealed record Cell(double Left, double Right, string Text);

    public TableResult Extract(OcrResult ocr)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        var words = ocr.Lines.SelectMany(l => l.Words).Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Bounds.Width > 0 && w.Bounds.Height > 0).ToList();
        if (words.Count == 0) return new TableResult(Array.Empty<IReadOnlyList<string>>());

        var rows = GroupRows(words);
        var medianH = Median(words.Select(w => w.Bounds.Height));

        // All inter-word gaps inside rows -> choose a cell-break threshold.
        var gaps = new List<double>();
        foreach (var row in rows)
            for (var i = 1; i < row.Count; i++) gaps.Add(row[i].Bounds.Left - row[i - 1].Bounds.Right);
        var threshold = ChooseGapThreshold(gaps, medianH);

        var cellRows = new List<List<Cell>>();
        foreach (var row in rows)
        {
            var cells = new List<Cell>();
            var cur = new List<OcrWord> { row[0] };
            for (var i = 1; i < row.Count; i++)
            {
                if (row[i].Bounds.Left - row[i - 1].Bounds.Right > threshold) { cells.Add(MakeCell(cur)); cur = new List<OcrWord>(); }
                cur.Add(row[i]);
            }
            cells.Add(MakeCell(cur));
            cellRows.Add(cells);
        }

        // Column extents = merged intervals of every cell.
        var intervals = cellRows.SelectMany(r => r).Select(c => (c.Left, c.Right)).OrderBy(i => i.Left).ToList();
        var columns = new List<(double L, double R)>();
        foreach (var iv in intervals)
        {
            if (columns.Count > 0 && iv.Left <= columns[^1].R) columns[^1] = (columns[^1].L, Math.Max(columns[^1].R, iv.Right));
            else columns.Add((iv.Left, iv.Right));
        }

        var table = new List<IReadOnlyList<string>>();
        foreach (var cells in cellRows)
        {
            var arr = new string[columns.Count];
            Array.Fill(arr, "");
            foreach (var c in cells)
            {
                var mid = (c.Left + c.Right) / 2;
                var idx = columns.FindIndex(col => mid >= col.L && mid <= col.R);
                if (idx < 0) idx = 0;
                arr[idx] = arr[idx].Length == 0 ? c.Text : arr[idx] + " " + c.Text;
            }
            table.Add(arr);
        }
        return new TableResult(table);
    }

    private static Cell MakeCell(List<OcrWord> ws)
        => new(ws[0].Bounds.Left, ws[^1].Bounds.Right, string.Join(" ", ws.Select(w => w.Text)));

    private static List<List<OcrWord>> GroupRows(List<OcrWord> words)
    {
        var sorted = words.OrderBy(w => w.Bounds.Center.Y).ToList();
        var rows = new List<List<OcrWord>>();
        var rowTop = 0.0;
        var rowBottom = 0.0;
        foreach (var w in sorted)
        {
            if (rows.Count > 0)
            {
                var overlap = Math.Min(rowBottom, w.Bounds.Bottom) - Math.Max(rowTop, w.Bounds.Top);
                var minH = Math.Min(rowBottom - rowTop, w.Bounds.Height);
                if (overlap > 0.5 * minH)
                {
                    rows[^1].Add(w);
                    rowTop = Math.Min(rowTop, w.Bounds.Top);
                    rowBottom = Math.Max(rowBottom, w.Bounds.Bottom);
                    continue;
                }
            }
            rows.Add(new List<OcrWord> { w });
            rowTop = w.Bounds.Top;
            rowBottom = w.Bounds.Bottom;
        }
        foreach (var r in rows) r.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
        return rows.OrderBy(r => r.Average(w => w.Bounds.Center.Y)).ToList();
    }

    /// <summary>
    /// Word spaces are ~0.2-0.5 line heights, column gutters >= ~1.5. With both populations present the threshold is placed in
    /// the widest natural break between them; otherwise a conservative default of 1.6 line heights is used.
    /// </summary>
    internal static double ChooseGapThreshold(List<double> gaps, double medianHeight)
    {
        var def = Math.Max(4, 1.6 * medianHeight);
        if (gaps.Count < 2) return def;
        var sorted = gaps.Select(g => Math.Max(0, g)).OrderBy(g => g).ToArray();
        var bestJump = 0.0;
        var best = def;
        for (var i = 1; i < sorted.Length; i++)
        {
            var lo = sorted[i - 1];
            var hi = sorted[i];
            if (lo > 1.2 * medianHeight) continue;            // the "small" population must look like word spacing
            if (hi < 0.9 * medianHeight) continue;            // the "large" population must be a real gutter
            var jump = hi - lo;
            if (jump > bestJump) { bestJump = jump; best = (lo + hi) / 2; }
        }
        return bestJump > 0.4 * medianHeight ? best : def;
    }

    private static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(v => v).ToArray();
        return a.Length == 0 ? 0 : a[a.Length / 2];
    }
}
