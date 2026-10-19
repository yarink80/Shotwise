using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;

namespace Shotwise.UI.Capture.Selection;

/// <summary>What the user chose in the All-in-One mini toolbar (or <see cref="Default"/> for presets with a fixed mode).</summary>
public enum SelectionAction { Default, CaptureImage, RecordVideo, Scrolling, Panoramic, GrabText }

/// <summary>Parameters of one interactive selection.</summary>
public sealed record SelectionRequest(
    SelectionType Type,
    CaptureMode Mode,
    SelectionOptions Options,
    bool ShowMagnifier = true,
    bool SimplifiedCrosshair = false,
    bool ShowGuides = true,
    bool ShowHelpHint = true,
    bool IncludeCursorInSnapshot = false)
{
    /// <summary>All-in-One shows the post-selection mini toolbar and hover highlighting for windows.</summary>
    public bool IsAllInOne => Mode == CaptureMode.AllInOne;

    /// <summary>
    /// Whether a drawn region/window stays editable (handles, nudging) with an action toolbar until confirmed. All-in-One shows the
    /// full toolbar; video shows a Record/Cancel toolbar so the area can be fine-tuned before recording starts. Image selections commit
    /// on mouse release (fast path) unless confirmed otherwise.
    /// </summary>
    public bool AdjustBeforeCommit => IsAllInOne || Mode == CaptureMode.Video;

    /// <summary>Whether hovering highlights a top-level window (click selects it).</summary>
    public bool HoverWindows => Type is SelectionType.Window or SelectionType.ScrollingWindow || (IsAllInOne && Type == SelectionType.Region);

    public bool HoverElements => Type == SelectionType.Object;
}

/// <summary>Outcome of the selection overlay.</summary>
public sealed record SelectionResult(
    SelectionType Type,
    RectI RegionPx,
    WindowInfo? Window,
    ElementInfo? Element,
    IReadOnlyList<PointI>? PolygonPx,
    IReadOnlyList<RectI>? Areas,
    SelectionAction Action,
    ScrollDirection ScrollDirection,
    MonitorInfo? Monitor,
    bool WholeMonitor = false,
    bool AllMonitors = false)
{
    public bool IsWindowSelection => Window is not null && Element is null;
}

/// <summary>Standard capture sizes used by the selection guides (closest-resolution snapping).</summary>
public static class StandardResolutions
{
    public static IReadOnlyList<SizeI> All { get; } = new SizeI[]
    {
        new(320, 240), new(512, 512), new(640, 360), new(640, 480), new(800, 600), new(854, 480), new(960, 540), new(1024, 576),
        new(1024, 768), new(1024, 1024), new(1152, 864), new(1280, 720), new(1280, 800), new(1280, 960), new(1280, 1024),
        new(1366, 768), new(1440, 900), new(1600, 900), new(1600, 1200), new(1680, 1050), new(1920, 1080), new(1920, 1200),
        new(2048, 1152), new(2048, 2048), new(2560, 1080), new(2560, 1440), new(2560, 1600), new(3440, 1440), new(3840, 2160),
        new(4096, 2160), new(5120, 2880), new(7680, 4320),
    };

    /// <summary>Closest standard size (Euclidean distance); with a locked aspect only sizes of that aspect are considered.</summary>
    public static SizeI? FindClosest(int width, int height, AspectLock aspect = AspectLock.None)
    {
        SizeI? best = null;
        double bestD = double.MaxValue;
        foreach (var s in All)
        {
            if (aspect != AspectLock.None && !MatchesAspect(s, aspect)) continue;
            var d = Math.Sqrt(Math.Pow(s.Width - width, 2) + Math.Pow(s.Height - height, 2));
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    public static bool MatchesAspect(SizeI s, AspectLock a)
    {
        var (rw, rh) = Ratio(a);
        if (rw == 0) return true;
        return Math.Abs((double)s.Width / s.Height - (double)rw / rh) < 0.01;
    }

    public static (int W, int H) Ratio(AspectLock a) => a switch
    {
        AspectLock.Square => (1, 1),
        AspectLock.FourThree => (4, 3),
        AspectLock.SixteenNine => (16, 9),
        _ => (0, 0),
    };
}
