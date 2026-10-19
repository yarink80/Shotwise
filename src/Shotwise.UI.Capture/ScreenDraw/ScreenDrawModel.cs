using System.Diagnostics;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;

namespace Shotwise.UI.Capture.ScreenDraw;

public enum DrawTool { Arrow, Square, Step }

/// <summary>One drawing in virtual-screen pixels.</summary>
public sealed record DrawItem(DrawTool Tool, Point2 A, Point2 B, Argb Color, long CreatedMs, int StepNumber);

/// <summary>
/// Thread-safe ScreenDraw document. The UI thread adds items; the recorder thread calls <see cref="Render"/> through
/// <see cref="IFrameOverlay"/> so every drawing is composited into the video. Items fade out after <see cref="FadeSeconds"/>.
/// </summary>
public sealed class ScreenDrawModel : IFrameOverlay
{
    public const double BaseStroke = 4;
    public const double StepDiameter = 34;
    private const long FadeOutMs = 600;
    private readonly object _gate = new();
    private readonly List<DrawItem> _items = new();
    private readonly long _t0 = Stopwatch.GetTimestamp();

    public Argb Color { get; set; } = Argb.Red;
    /// <summary>Seconds before a drawing starts to fade; 0 = never.</summary>
    public double FadeSeconds { get; set; } = 3;

    public long NowMs => (long)((Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency);

    public IReadOnlyList<DrawItem> Snapshot()
    {
        lock (_gate)
        {
            Prune();
            return _items.ToArray();
        }
    }

    public bool IsEmpty { get { lock (_gate) return _items.Count == 0; } }

    public int NextStepNumber()
    {
        lock (_gate)
        {
            Prune();
            var steps = _items.Where(i => i.Tool == DrawTool.Step).Select(i => i.StepNumber).ToList();
            return steps.Count == 0 ? 1 : steps.Max() + 1;
        }
    }

    public void Add(DrawItem item) { lock (_gate) _items.Add(item); }

    public void Undo() { lock (_gate) if (_items.Count > 0) _items.RemoveAt(_items.Count - 1); }

    public void Clear() { lock (_gate) _items.Clear(); }

    /// <summary>Opacity 0..1 of an item at <paramref name="nowMs"/> (1 while within the display time, then a short fade).</summary>
    public double OpacityOf(DrawItem item, long nowMs)
    {
        if (FadeSeconds <= 0) return 1;
        var age = nowMs - item.CreatedMs - (long)(FadeSeconds * 1000);
        if (age <= 0) return 1;
        return Math.Clamp(1 - (double)age / FadeOutMs, 0, 1);
    }

    private void Prune()
    {
        if (FadeSeconds <= 0) return;
        var now = NowMs;
        _items.RemoveAll(i => OpacityOf(i, now) <= 0);
    }

    /// <summary>Arrow head wings for a line from <paramref name="a"/> to <paramref name="b"/> (screen px).</summary>
    public static (Point2 Left, Point2 Right) ArrowHead(Point2 a, Point2 b, double size)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3) return (b, b);
        double ux = dx / len, uy = dy / len;
        const double ang = Math.PI / 7;
        (double, double) Rot(double x, double y, double t) => (x * Math.Cos(t) - y * Math.Sin(t), x * Math.Sin(t) + y * Math.Cos(t));
        var (lx, ly) = Rot(ux, uy, ang);
        var (rx, ry) = Rot(ux, uy, -ang);
        return (new Point2(b.X + lx * size, b.Y + ly * size), new Point2(b.X + rx * size, b.Y + ry * size));
    }

    /// <inheritdoc />
    public void Render(IOverlayCanvas canvas, TimeSpan timestamp, RectI capturePx)
    {
        if (capturePx.IsEmpty) return;
        var items = Snapshot();
        if (items.Count == 0) return;
        var now = NowMs;
        double sx = canvas.FrameSize.Width / (double)capturePx.Width;
        double sy = canvas.FrameSize.Height / (double)capturePx.Height;
        Point2 Map(Point2 p) => new((p.X - capturePx.X) * sx, (p.Y - capturePx.Y) * sy);

        foreach (var it in items)
        {
            var opacity = OpacityOf(it, now);
            if (opacity <= 0) continue;
            var color = it.Color.WithOpacity(opacity);
            var a = Map(it.A); var b = Map(it.B);
            double stroke = BaseStroke * sx;
            switch (it.Tool)
            {
                case DrawTool.Arrow:
                {
                    canvas.DrawLine(a, b, color, stroke);
                    var (l, r) = ArrowHead(a, b, 22 * sx);
                    canvas.DrawLine(b, l, color, stroke);
                    canvas.DrawLine(b, r, color, stroke);
                    break;
                }
                case DrawTool.Square:
                    canvas.DrawRectangle(Rect2.FromPoints(a, b), color, stroke, null, 3 * sx);
                    break;
                case DrawTool.Step:
                {
                    double d = StepDiameter * sx;
                    canvas.DrawEllipse(new Rect2(a.X - d / 2, a.Y - d / 2, d, d), null, 0, color);
                    var text = it.StepNumber.ToString();
                    double fs = 18 * sx;
                    canvas.DrawText(text, new Point2(a.X - fs * 0.30 * text.Length, a.Y - fs * 0.62), "Segoe UI", fs, Argb.White.WithOpacity(opacity), true);
                    break;
                }
            }
        }
    }
}
