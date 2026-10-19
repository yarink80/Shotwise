using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Shotwise.Core.Capture;
using Shotwise.Core.Imaging;
using Shotwise.Vision.Stitching;
using Stitcher = Shotwise.Vision.Stitching.Stitcher;

namespace Shotwise.Vision.Tests;

public class StitcherTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "shotwise-stitch-tests-" + Guid.NewGuid().ToString("N"));

    private Stitcher Make(long budget = 256L * 1024 * 1024) => new(NullLogger<Stitcher>.Instance, _tmp, budget);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* ignore */ }
    }

    /// <summary>Scrolls a viewport of <paramref name="viewH"/> over the page in steps; header/footer overlays stay fixed.</summary>
    private static List<BgraImage> ScrollFrames(Mat page, int viewH, int step, int header = 0, int footer = 0, bool up = false)
    {
        var frames = new List<BgraImage>();
        var maxOff = page.Rows - viewH;
        var offsets = new List<int>();
        for (var o = 0; o < maxOff; o += step) offsets.Add(o);
        offsets.Add(maxOff);
        if (up) offsets.Reverse();
        foreach (var off in offsets)
        {
            using var frame = new Mat(page, new Rect(0, off, page.Cols, viewH)).Clone();
            if (header > 0) Cv2.Rectangle(frame, new Rect(0, 0, frame.Cols, header), TestImages.Bgr(30, 60, 120), -1);
            if (header > 0) Cv2.PutText(frame, "STICKY HEADER", new Point(8, header - 8), HersheyFonts.HersheySimplex, 0.6, TestImages.Bgr(255, 255, 255), 1);
            if (footer > 0) Cv2.Rectangle(frame, new Rect(0, viewH - footer, frame.Cols, footer), TestImages.Bgr(60, 60, 60), -1);
            frames.Add(TestImages.ToImage(frame));
        }
        return frames;
    }

    [Fact]
    public void VerticalScroll_ReconstructsThePage()
    {
        using var page = TestImages.TexturedPage(420, 2400, 1);
        var frames = ScrollFrames(page, 600, 230);
        using var st = Make().Create(StitchDirection.Vertical);
        foreach (var f in frames) Assert.True(st.AddFrame(f));
        var result = st.Result();
        Assert.Equal(page.Cols, result.Width);
        Assert.Equal(page.Rows, result.Height);
        Assert.True(TestImages.MeanAbsDiff(TestImages.ToImage(page), result) < 1.5, "stitched page differs");
    }

    [Fact]
    public void StickyHeaderAndFooter_AppearOnce()
    {
        using var page = TestImages.TexturedPage(420, 2400, 2);
        const int header = 56, footer = 36, view = 600;
        var frames = ScrollFrames(page, view, 200, header, footer);
        using var st = (IncrementalStitcher)Make().Create(StitchDirection.Vertical);
        foreach (var f in frames) Assert.True(st.AddFrame(f));
        var (top, bottom) = st.StaticBands;
        Assert.InRange(top, header - 4, header + 4);
        Assert.InRange(bottom, footer - 4, footer + 4);

        var result = st.Result();
        // exactly one header (frame 0) on top and one footer (last frame) at the bottom; the page body in between, no repeats
        Assert.Equal(page.Rows, result.Height);
        Assert.Equal(frames[0].GetPixel(10, 10), result.GetPixel(10, 10));
        Assert.Equal(frames[^1].GetPixel(10, view - 10), result.GetPixel(10, result.Height - 10));
        using var expectedMid = new Mat(page, new Rect(0, header, page.Cols, page.Rows - header - footer)).Clone();
        var mid = result.Crop(new Shotwise.Core.Geometry.RectI(0, header, page.Cols, page.Rows - header - footer));
        Assert.True(TestImages.MeanAbsDiff(TestImages.ToImage(expectedMid), mid) < 1.5);
    }

    [Fact]
    public void ScrollingUp_IsHandled()
    {
        using var page = TestImages.TexturedPage(420, 1800, 3);
        var frames = ScrollFrames(page, 600, 220, up: true);
        using var st = Make().Create(StitchDirection.Vertical);
        foreach (var f in frames) Assert.True(st.AddFrame(f));
        var result = st.Result();
        Assert.Equal(page.Rows, result.Height);
        Assert.True(TestImages.MeanAbsDiff(TestImages.ToImage(page), result) < 1.5);
    }

    [Fact]
    public void HorizontalPan_ReconstructsPanorama()
    {
        using var tall = TestImages.TexturedPage(300, 2000, 4);
        using var pano = new Mat();
        Cv2.Transpose(tall, pano);                         // 2000 x 300 wide strip
        var frames = new List<BgraImage>();
        for (var x = 0; x + 500 <= pano.Cols; x += 190)
            frames.Add(TestImages.ToImage(new Mat(pano, new Rect(x, 0, 500, pano.Rows)).Clone()));
        var last = pano.Cols - 500;
        if ((last % 190) != 0) frames.Add(TestImages.ToImage(new Mat(pano, new Rect(last, 0, 500, pano.Rows)).Clone()));
        using var st = Make().Create(StitchDirection.Horizontal);
        foreach (var f in frames) Assert.True(st.AddFrame(f));
        var result = st.Result();
        Assert.Equal(pano.Cols, result.Width);
        Assert.Equal(pano.Rows, result.Height);
        Assert.True(TestImages.MeanAbsDiff(TestImages.ToImage(pano), result) < 1.5);
    }

    [Fact]
    public void AutoDirection_DetectsHorizontal()
    {
        using var tall = TestImages.TexturedPage(300, 1500, 5);
        using var pano = new Mat();
        Cv2.Transpose(tall, pano);
        using var st = Make().Create(StitchDirection.Auto);
        var added = 0;
        for (var x = 0; x + 500 <= pano.Cols; x += 180)
            if (st.AddFrame(TestImages.ToImage(new Mat(pano, new Rect(x, 0, 500, pano.Rows)).Clone()))) added++;
        Assert.True(added >= 4);
        var r = st.Result();
        Assert.True(r.Width > 1000 && r.Height == pano.Rows);
    }

    [Fact]
    public void NoMovementAndUnrelatedFrames_AreRejected()
    {
        using var page = TestImages.TexturedPage(420, 1500, 6);
        var frames = ScrollFrames(page, 600, 200);
        using var st = Make().Create(StitchDirection.Vertical);
        Assert.True(st.AddFrame(frames[0]));
        Assert.False(st.AddFrame(frames[0]), "identical frame = no movement");
        Assert.True(st.AddFrame(frames[1]));
        Assert.False(st.AddFrame(frames[1]), "identical frame = no movement");

        using var other = TestImages.TexturedPage(420, 600, 999);
        Assert.False(st.AddFrame(TestImages.ToImage(other)), "no overlap with previous frame");
        Assert.True(st.AddFrame(frames[2]), "stitcher recovers after a rejected frame");
        Assert.Equal(600 + 400, st.Result().Height);
    }

    [Fact]
    public void WrongSizedFrame_IsRejected()
    {
        using var page = TestImages.TexturedPage(420, 1500, 7);
        var frames = ScrollFrames(page, 600, 200);
        using var st = Make().Create(StitchDirection.Vertical);
        st.AddFrame(frames[0]);
        using var small = new Mat(page, new Rect(0, 0, 300, 500)).Clone();
        Assert.False(st.AddFrame(TestImages.ToImage(small)));
    }

    [Fact]
    public void LongCapture_SpillsToDisk_AndStillMatches()
    {
        using var page = TestImages.TexturedPage(300, 6000, 8);
        var frames = ScrollFrames(page, 500, 220);
        using var st = (IncrementalStitcher)Make(budget: 200_000).Create(StitchDirection.Vertical);
        foreach (var f in frames) Assert.True(st.AddFrame(f));
        Assert.True(st.SpilledPieces > 0, "expected strips to be written to disk");
        Assert.True(Directory.Exists(_tmp) && Directory.GetDirectories(_tmp).Length == 1);
        var result = st.Result();
        Assert.Equal(page.Rows, result.Height);
        Assert.True(TestImages.MeanAbsDiff(TestImages.ToImage(page), result) < 1.5);
        st.Dispose();
        Assert.Empty(Directory.GetDirectories(_tmp));            // temp strips removed on dispose
    }

    [Fact]
    public void Preview_IsDownscaled_AndSingleFrameWorks()
    {
        using var page = TestImages.TexturedPage(420, 2400, 9);
        var frames = ScrollFrames(page, 600, 230);
        using var st = Make().Create(StitchDirection.Vertical);
        st.AddFrame(frames[0]);
        Assert.Equal(600, st.Result().Height);
        Assert.Equal(300, st.Preview(300).Height);
        foreach (var f in frames.Skip(1)) st.AddFrame(f);
        var p = st.Preview(400);
        Assert.Equal(400, Math.Max(p.Width, p.Height));
        Assert.Equal(400, p.Height);
        Assert.True(p.Width < 100);
    }

    [Fact]
    public void Result_WithoutFrames_Throws()
    {
        using var st = Make().Create(StitchDirection.Vertical);
        Assert.Throws<Shotwise.Core.ShotwiseException>(() => st.Result());
    }

    [Fact]
    public void Disposed_Throws()
    {
        var st = Make().Create(StitchDirection.Vertical);
        st.Dispose();
        Assert.Throws<ObjectDisposedException>(() => st.AddFrame(new BgraImage(10, 10)));
    }

    [Fact]
    public void PhasePeaks_FindsKnownShift()
    {
        using var page = TestImages.TexturedPage(300, 800, 10);
        using var a = new Mat(); using var b = new Mat();
        Cv2.CvtColor(new Mat(page, new Rect(0, 100, 300, 500)), a, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(new Mat(page, new Rect(0, 160, 300, 500)), b, ColorConversionCodes.BGR2GRAY);
        Assert.True(ScrollRegistration.TryRegister(a, b, out var dy, out var score));
        Assert.Equal(60, dy);                                  // b is 60 px further down the page -> prev(y+60) == curr(y)
        Assert.True(score > 0.95);
    }

    [Fact]
    public void FrameStore_SpillsAndRoundTrips()
    {
        using var fs = new FrameStore(_tmp, 10);
        var small = fs.Add(new byte[] { 1, 2 });
        var big = fs.Add(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        Assert.Equal(1, fs.SpilledCount);
        Assert.Equal(new byte[] { 1, 2 }, fs.Get(small));
        Assert.Equal(99, fs.Get(big)[99]);
    }
}
