using Shotwise.Core.Geometry;
using Shotwise.Core.Vision;

namespace Shotwise.Vision.Tests;

public class TableExtractorTests
{
    // Builds OCR output for a grid: columns at fixed x with the given cell strings (multi-word cells have normal word spacing).
    private static OcrResult Grid(string[][] rows, double[] colX, double rowPitch = 24, double charW = 7, double h = 14)
    {
        var lines = new List<OcrLine>();
        for (var r = 0; r < rows.Length; r++)
        {
            var y = 10 + r * rowPitch;
            var words = new List<OcrWord>();
            for (var c = 0; c < rows[r].Length; c++)
            {
                var x = colX[c];
                foreach (var token in rows[r][c].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var w = token.Length * charW;
                    words.Add(new OcrWord(token, new Rect2(x, y, w, h), 1));
                    x += w + charW * 0.6;
                }
            }
            if (words.Count == 0) continue;
            lines.Add(new OcrLine(string.Join(" ", words.Select(w => w.Text)), new Rect2(words[0].Bounds.X, y, words[^1].Bounds.Right - words[0].Bounds.X, h), words));
        }
        return new OcrResult("en", "", lines, 0);
    }

    [Fact]
    public void ThreeColumnTable_IsReconstructed()
    {
        var ocr = Grid(new[]
        {
            new[] { "Item", "Unit Price", "Quantity" },
            new[] { "Red Apple", "1.20", "10" },
            new[] { "Green Pear", "0.95", "250" },
        }, new[] { 20.0, 220, 420 });

        var t = new TableExtractor().Extract(ocr);
        Assert.Equal(3, t.RowCount);
        Assert.Equal(3, t.ColumnCount);
        Assert.Equal("Red Apple", t.Rows[1][0]);
        Assert.Equal("Unit Price", t.Rows[0][1]);
        Assert.Equal("250", t.Rows[2][2]);
        var tsv = t.ToTsv();
        Assert.Contains("Green Pear\t0.95\t250", tsv);
    }

    [Fact]
    public void MissingCells_AreEmptyStrings()
    {
        var ocr = Grid(new[]
        {
            new[] { "Name", "City", "Age" },
            new[] { "Ann", "", "31" },
            new[] { "Bob", "Paris", "" },
        }, new[] { 20.0, 220, 420 });
        var t = new TableExtractor().Extract(ocr);
        Assert.Equal(3, t.ColumnCount);
        Assert.Equal("", t.Rows[1][1]);
        Assert.Equal("31", t.Rows[1][2]);
        Assert.Equal("Paris", t.Rows[2][1]);
        Assert.Equal("", t.Rows[2][2]);
    }

    [Fact]
    public void PlainParagraph_IsOneColumn()
    {
        var ocr = Grid(new[]
        {
            new[] { "the quick brown fox jumps" },
            new[] { "over the lazy dog again" },
        }, new[] { 20.0 });
        var t = new TableExtractor().Extract(ocr);
        Assert.Equal(1, t.ColumnCount);
        Assert.Equal(2, t.RowCount);
        Assert.Equal("the quick brown fox jumps", t.Rows[0][0]);
    }

    [Fact]
    public void Html_IsEncoded()
    {
        var ocr = Grid(new[] { new[] { "a<b", "x&y" } }, new[] { 10.0, 200 });
        var html = new TableExtractor().Extract(ocr).ToHtml();
        Assert.Contains("<td>a&lt;b</td>", html);
        Assert.Contains("<td>x&amp;y</td>", html);
    }

    [Fact]
    public void Empty_ReturnsEmptyTable()
        => Assert.True(new TableExtractor().Extract(OcrResult.Empty()).IsEmpty);

    [Fact]
    public void GapThreshold_SplitsPopulations()
    {
        var gaps = new List<double> { 4, 5, 4, 6, 5, 60, 70, 65 };
        var th = TableExtractor.ChooseGapThreshold(gaps, 14);
        Assert.InRange(th, 7, 60);
    }
}
