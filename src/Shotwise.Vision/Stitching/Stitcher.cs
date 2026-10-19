using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;

namespace Shotwise.Vision.Stitching;

/// <summary><see cref="IStitcher"/> factory (VIS-07), used by scrolling and panoramic capture.</summary>
public sealed class Stitcher : IStitcher
{
    private readonly ILogger<Stitcher> _log;
    private readonly string _tempRoot;
    private readonly long _memoryBudget;

    public Stitcher(ILogger<Stitcher> log, IAppPaths? paths = null)
        : this(log, paths?.Temp ?? Path.Combine(Path.GetTempPath(), "Shotwise"), 256L * 1024 * 1024) { }

    internal Stitcher(ILogger<Stitcher> log, string tempRoot, long memoryBudgetBytes)
    {
        _log = log;
        _tempRoot = tempRoot;
        _memoryBudget = memoryBudgetBytes;
    }

    public IIncrementalStitcher Create(StitchDirection dir) => new IncrementalStitcher(dir, _log, _tempRoot, _memoryBudget);
}

/// <summary>
/// Incremental stitcher. Internally everything is processed in "vertical space": horizontal captures are transposed on entry and
/// results transposed back; captures that move in the opposite direction are flipped. Frames are registered against the previous
/// frame (phase correlation + NCC verification), static header/footer bands are detected once and excluded, new content is
/// appended as strips with a short cross-fade seam, strips live in a disk-backed <see cref="FrameStore"/>.
/// </summary>
internal sealed class IncrementalStitcher : IIncrementalStitcher
{
    private const int Feather = 6;

    private sealed record Piece(int Rows, int DataId, byte[]? Thumb, int ThumbRows);

    private readonly object _lock = new();
    private readonly ILogger _log;
    private readonly StitchDirection _requested;
    private readonly FrameStore _store;

    private bool _disposed;
    private bool _axisKnown;
    private bool _horizontal;
    private bool _reverse;
    private bool _committed;
    private Mat? _pending;                  // first raw frame, original orientation
    private int _rawW, _rawH;

    // committed state, "vertical space"
    private int _w, _h, _top, _bottom;
    private Mat? _header, _footer, _prevBodyGray, _tail;
    private readonly List<Piece> _pieces = new();
    private long _committedRows;
    private long _thumbRowsDone;
    private int _frames;

    public IncrementalStitcher(StitchDirection dir, ILogger log, string tempRoot, long memoryBudget)
    {
        _requested = dir;
        _log = log;
        _store = new FrameStore(tempRoot, memoryBudget);
        if (dir != StitchDirection.Auto) { _axisKnown = true; _horizontal = dir == StitchDirection.Horizontal; }
    }

    internal int FramesAccepted { get { lock (_lock) return _frames; } }
    internal int SpilledPieces { get { lock (_lock) return _store.SpilledCount; } }
    internal (int Top, int Bottom) StaticBands { get { lock (_lock) return (_top, _bottom); } }

    public bool AddFrame(BgraImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is null && !_committed)
            {
                _pending = CvInterop.ToMatBgra(frame);
                _rawW = frame.Width; _rawH = frame.Height;
                _frames = 1;
                return true;
            }
            if (frame.Width != _rawW || frame.Height != _rawH) return false;
            using var raw = CvInterop.ToMatBgra(frame);
            try
            {
                var ok = _committed ? Append(raw) : CommitSecond(raw);
                if (ok) _frames++;
                return ok;
            }
            catch (OpenCVException ex)
            {
                _log.LogWarning(ex, "Stitch registration failed for a frame");
                return false;
            }
        }
    }

    private Mat Orient(Mat raw, bool flip)
    {
        var m = new Mat();
        if (_horizontal) Cv2.Transpose(raw, m); else raw.CopyTo(m);
        if (flip) Cv2.Flip(m, m, FlipMode.X);
        return m;
    }

    private static Mat Gray(Mat bgra)
    {
        var g = new Mat();
        Cv2.CvtColor(bgra, g, ColorConversionCodes.BGRA2GRAY);
        return g;
    }

    private bool CommitSecond(Mat rawCurr)
    {
        var pending = _pending!;
        if (!_axisKnown)
        {
            using var g0 = Gray(pending);
            using var g1 = Gray(rawCurr);
            var peaks = ScrollRegistration.PhasePeaks(g0, g1, 1);
            if (peaks.Count == 0) return false;
            var (dx, dy) = peaks[0];
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return false;
            _horizontal = Math.Abs(dx) > Math.Abs(dy);
            _axisKnown = true;
        }

        var flip = false;
        Mat p0 = Orient(pending, false), p1 = Orient(rawCurr, false);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var g0 = Gray(p0);
                using var g1 = Gray(p1);
                var bands = ScrollRegistration.DetectStaticBands(g0, g1);
                if (bands is null) { return false; }
                var (top, bottom) = bands.Value;
                var h = p0.Rows;
                using var b0 = g0.RowRange(top, h - bottom);
                using var b1 = g1.RowRange(top, h - bottom);
                if (!ScrollRegistration.TryRegister(b0, b1, out var dy, out var score)) return false;

                if (dy < 0)
                {
                    if (attempt == 1) return false;
                    // content moves the other way: work on vertically flipped frames from now on
                    Cv2.Flip(p0, p0, FlipMode.X);
                    Cv2.Flip(p1, p1, FlipMode.X);
                    flip = true;
                    continue;
                }

                // commit the session geometry
                _reverse = flip;
                _w = p0.Cols; _h = h; _top = top; _bottom = bottom;
                _header = top > 0 ? p0.RowRange(0, top).Clone() : null;
                _footer = bottom > 0 ? p1.RowRange(h - bottom, h).Clone() : null;
                using var body0 = p0.RowRange(top, h - bottom).Clone();
                var bh = body0.Rows;
                var f = Math.Min(Feather, bh / 4);
                var b0bytes = CvInterop.ToBytes(body0);
                var rowBytes = _w * 4;
                if (bh - f > 0) AddPiece(b0bytes.AsSpan(0, (bh - f) * rowBytes).ToArray(), bh - f);
                _tail = CvInterop.ToMatBgra(b0bytes.AsSpan((bh - f) * rowBytes).ToArray(), _w, f);

                using var body1 = p1.RowRange(top, h - bottom).Clone();
                if (!ApplyStep(body1, dy))
                {
                    ResetCommit();
                    return false;
                }
                _prevBodyGray = g1.RowRange(top, h - bottom).Clone();
                _committed = true;
                pending.Dispose(); _pending = null;
                _log.LogDebug("Stitch started: axis={Axis} reverse={Rev} bands=({Top},{Bottom}) first dy={Dy} score={Score:F3}",
                    _horizontal ? "H" : "V", _reverse, top, bottom, dy, score);
                return true;
            }
            return false;
        }
        finally { p0.Dispose(); p1.Dispose(); }
    }

    private void ResetCommit()
    {
        _pieces.Clear();
        _committedRows = 0; _thumbRowsDone = 0;
        _header?.Dispose(); _header = null;
        _footer?.Dispose(); _footer = null;
        _tail?.Dispose(); _tail = null;
        _reverse = false;
    }

    private bool Append(Mat rawCurr)
    {
        using var p = Orient(rawCurr, _reverse);
        using var gFull = Gray(p);
        using var gBody = gFull.RowRange(_top, _h - _bottom).Clone();
        if (!ScrollRegistration.TryRegister(_prevBodyGray!, gBody, out var dy, out _)) return false;
        if (dy <= 0) return false;                                  // moved backwards / no movement
        using var body = p.RowRange(_top, _h - _bottom).Clone();
        if (!ApplyStep(body, dy)) return false;
        _prevBodyGray!.Dispose();
        _prevBodyGray = gBody.Clone();
        if (_bottom > 0) { _footer?.Dispose(); _footer = p.RowRange(_h - _bottom, _h).Clone(); }
        return true;
    }

    /// <summary>
    /// S = old tail (f rows, cross-faded with the matching rows of the new frame) followed by dy fresh rows of the new frame.
    /// The first dy rows of S are committed, the last f rows become the new tail.
    /// </summary>
    private bool ApplyStep(Mat body, int dy)
    {
        var bh = body.Rows;
        var f = _tail!.Rows;
        if (dy < 1 || dy + f > bh) return false;
        var rowBytes = _w * 4;
        var cur = CvInterop.ToBytes(body);
        var tail = CvInterop.ToBytes(_tail);
        var total = f + dy;
        var s = new byte[total * rowBytes];
        var start = (bh - f - dy) * rowBytes;
        for (var r = 0; r < total; r++)
        {
            var d = r * rowBytes;
            var c = start + r * rowBytes;
            if (r < f)
            {
                var a = (r + 1) * 256 / (f + 1);                    // weight of the new frame, 0..256
                var t = r * rowBytes;
                for (var i = 0; i < rowBytes; i++) s[d + i] = (byte)((tail[t + i] * (256 - a) + cur[c + i] * a) >> 8);
            }
            else Buffer.BlockCopy(cur, c, s, d, rowBytes);
        }
        AddPiece(s.AsSpan(0, dy * rowBytes).ToArray(), dy);
        _tail!.Dispose();
        _tail = CvInterop.ToMatBgra(s.AsSpan(dy * rowBytes).ToArray(), _w, f);
        return true;
    }

    private void AddPiece(byte[] data, int rows)
    {
        if (rows <= 0) return;
        _committedRows += rows;
        var targetThumb = (long)Math.Round(_committedRows / 4.0);
        var thumbRows = (int)(targetThumb - _thumbRowsDone);
        byte[]? thumb = null;
        if (thumbRows > 0)
        {
            using var m = CvInterop.ToMatBgra(data, _w, rows);
            using var small = new Mat();
            Cv2.Resize(m, small, new Size(Math.Max(1, _w / 4), thumbRows), 0, 0, InterpolationFlags.Area);
            thumb = CvInterop.ToBytes(small);
            _thumbRowsDone += thumbRows;
        }
        _pieces.Add(new Piece(rows, _store.Add(data), thumb, thumbRows));
    }

    // ---- output ---------------------------------------------------------------------------

    public BgraImage Result()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null) return CvInterop.FromMat(_pending);
            if (!_committed) throw new ShotwiseException(ErrorCode.StitchingFailed, "No frames have been added to the stitcher.");
            return Assemble(thumbnails: false);
        }
    }

    public BgraImage Preview(int maxSide)
    {
        if (maxSide < 1) throw new ArgumentOutOfRangeException(nameof(maxSide));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            BgraImage full;
            if (_pending is not null) full = CvInterop.FromMat(_pending);
            else if (!_committed) throw new ShotwiseException(ErrorCode.StitchingFailed, "No frames have been added to the stitcher.");
            else
            {
                var rows = TotalRows();
                full = (long)_w * rows <= 16_000_000 ? Assemble(thumbnails: false) : Assemble(thumbnails: true);
            }
            var scale = (double)maxSide / Math.Max(full.Width, full.Height);
            if (scale >= 1) return full;
            using var m = CvInterop.ToMatBgra(full);
            using var small = new Mat();
            Cv2.Resize(m, small, new Size(Math.Max(1, (int)Math.Round(full.Width * scale)), Math.Max(1, (int)Math.Round(full.Height * scale))), 0, 0, InterpolationFlags.Area);
            return CvInterop.FromMat(small, full.DpiX, full.DpiY);
        }
    }

    private long TotalRows() => (_header?.Rows ?? 0) + _committedRows + (_tail?.Rows ?? 0) + (_footer?.Rows ?? 0);

    private BgraImage Assemble(bool thumbnails)
    {
        // geometry in vertical space (thumbnails are 1/4 scale in both axes)
        var w = thumbnails ? Math.Max(1, _w / 4) : _w;
        int HeaderRows() => _header is null ? 0 : (thumbnails ? Math.Max(1, _header.Rows / 4) : _header.Rows);
        int FooterRows() => _footer is null ? 0 : (thumbnails ? Math.Max(1, _footer.Rows / 4) : _footer.Rows);
        int TailRows() => _tail is null ? 0 : (thumbnails ? Math.Max(1, _tail.Rows / 4) : _tail.Rows);
        var rows = (long)HeaderRows() + FooterRows() + TailRows() + _pieces.Sum(p => thumbnails ? p.ThumbRows : p.Rows);
        if (rows <= 0) throw new ShotwiseException(ErrorCode.StitchingFailed, "Nothing to assemble.");

        // BgraImage enforces the pixel cap (throws ImageTooLarge)
        var swap = _horizontal;
        var outImg = swap ? new BgraImage((int)rows, w) : new BgraImage(w, (int)rows);
        var rowBytes = w * 4;
        var needsTransform = swap || _reverse;
        var canvas = needsTransform ? new byte[checked((int)(rows * rowBytes))] : outImg.Pixels;

        var y = 0;
        void Put(byte[] data, int n)
        {
            Buffer.BlockCopy(data, 0, canvas, y * rowBytes, n * rowBytes);
            y += n;
        }
        byte[] Small(Mat m)
        {
            if (!thumbnails) return CvInterop.ToBytes(m);
            using var s = new Mat();
            Cv2.Resize(m, s, new Size(w, Math.Max(1, m.Rows / 4)), 0, 0, InterpolationFlags.Area);
            return CvInterop.ToBytes(s);
        }

        if (_header is not null) Put(Small(_header), HeaderRows());
        foreach (var p in _pieces)
        {
            if (thumbnails) { if (p.Thumb is not null) Put(p.Thumb, p.ThumbRows); }
            else Put(_store.Get(p.DataId), p.Rows);
        }
        if (_tail is not null) Put(Small(_tail), TailRows());
        if (_footer is not null) Put(Small(_footer), FooterRows());

        if (needsTransform)
        {
            using var m = CvInterop.ToMatBgra(canvas, w, (int)rows);
            if (_reverse) Cv2.Flip(m, m, FlipMode.X);
            using var t = new Mat();
            if (swap) Cv2.Transpose(m, t); else m.CopyTo(t);
            Buffer.BlockCopy(CvInterop.ToBytes(t), 0, outImg.Pixels, 0, outImg.Pixels.Length);
        }
        return outImg;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Dispose();
            _header?.Dispose(); _footer?.Dispose(); _prevBodyGray?.Dispose(); _tail?.Dispose();
            _store.Dispose();
        }
    }
}
