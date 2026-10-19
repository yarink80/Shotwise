using Shotwise.Core.Commands;
using Shotwise.Core.Documents;

namespace Shotwise.Core.Media;

/// <summary>
/// Non-destructive edit decision list for a video/GIF: trim window + removed (cut) ranges.
/// <see cref="KeptRanges"/> = trim minus cuts, merged and ordered. Rendered by IVideoService.RenderAsync.
/// </summary>
public sealed class VideoEditList
{
    public VideoEditList() { }

    public VideoEditList(string sourcePath, TimeSpan duration)
    {
        SourcePath = sourcePath;
        Duration = duration;
        Trim = new TimeRange(TimeSpan.Zero, duration);
    }

    public string SourcePath { get; set; } = "";
    /// <summary>Full source duration.</summary>
    public TimeSpan Duration { get; set; }
    public TimeRange Trim { get; set; } = new(TimeSpan.Zero, TimeSpan.Zero);
    public List<TimeRange> Cuts { get; set; } = new();

    public event EventHandler? Changed;
    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>True when nothing is trimmed or cut.</summary>
    public bool IsIdentity => Trim.Start <= TimeSpan.Zero && Trim.End >= Duration && Cuts.Count == 0;

    /// <summary>Kept source ranges in order (trim window minus cuts).</summary>
    public IEnumerable<TimeRange> KeptRanges()
    {
        var trim = Trim.IsEmpty && Duration > TimeSpan.Zero ? new TimeRange(TimeSpan.Zero, Duration) : Trim;
        var cuts = MergedCuts(trim);
        var cursor = trim.Start;
        foreach (var c in cuts)
        {
            if (c.Start > cursor) yield return new TimeRange(cursor, c.Start);
            if (c.End > cursor) cursor = c.End;
        }
        if (trim.End > cursor) yield return new TimeRange(cursor, trim.End);
    }

    /// <summary>Total duration of the edited result.</summary>
    public TimeSpan KeptDuration => KeptRanges().Aggregate(TimeSpan.Zero, (a, r) => a + r.Duration);

    /// <summary>Whether adding <paramref name="cut"/> would still leave something (cannot cut the entire video).</summary>
    public bool CanAddCut(TimeRange cut)
    {
        if (cut.IsEmpty) return false;
        var probe = new VideoEditList { SourcePath = SourcePath, Duration = Duration, Trim = Trim, Cuts = new List<TimeRange>(Cuts) { cut } };
        return probe.KeptDuration > TimeSpan.Zero;
    }

    /// <summary>Maps a time on the edited timeline to source time.</summary>
    public TimeSpan ToSourceTime(TimeSpan edited)
    {
        var acc = TimeSpan.Zero;
        TimeRange? last = null;
        foreach (var r in KeptRanges())
        {
            if (edited < acc + r.Duration) return r.Start + (edited - acc);
            acc += r.Duration;
            last = r;
        }
        return last?.End ?? TimeSpan.Zero;
    }

    /// <summary>Maps source time to the edited timeline (times inside cuts snap to the cut start).</summary>
    public TimeSpan ToEditedTime(TimeSpan source)
    {
        var acc = TimeSpan.Zero;
        foreach (var r in KeptRanges())
        {
            if (source < r.Start) return acc;
            if (source < r.End) return acc + (source - r.Start);
            acc += r.Duration;
        }
        return acc;
    }

    public VideoEditList Clone() => new() { SourcePath = SourcePath, Duration = Duration, Trim = Trim, Cuts = new List<TimeRange>(Cuts) };

    private List<TimeRange> MergedCuts(TimeRange trim)
    {
        var sorted = Cuts.Select(c => c.Clamp(trim)).Where(c => !c.IsEmpty).OrderBy(c => c.Start).ToList();
        var merged = new List<TimeRange>();
        foreach (var c in sorted)
        {
            if (merged.Count > 0 && c.Start <= merged[^1].End)
                merged[^1] = new TimeRange(merged[^1].Start, c.End > merged[^1].End ? c.End : merged[^1].End);
            else merged.Add(c);
        }
        return merged;
    }
}

/// <summary>
/// Base for undoable video edits. They operate on the captured <see cref="VideoEditList"/> and ignore the document
/// argument, so they can live on a regular <see cref="UndoStack"/> (bound to the video's placeholder document).
/// </summary>
public abstract class VideoEditCommand : IEditCommand
{
    protected VideoEditCommand(VideoEditList edl, string label)
    {
        Edl = edl;
        Label = label;
    }

    protected VideoEditList Edl { get; }
    public string Label { get; }
    public void Do(ShotDocument doc) { Apply(); Edl.RaiseChanged(); }
    public void Undo(ShotDocument doc) { Revert(); Edl.RaiseChanged(); }
    protected abstract void Apply();
    protected abstract void Revert();
}

public sealed class SetTrimCommand : VideoEditCommand
{
    private readonly TimeRange _after;
    private TimeRange? _before;

    public SetTrimCommand(VideoEditList edl, TimeRange trim) : base(edl, "Trim")
    {
        if (trim.IsEmpty) throw new ShotwiseException(ErrorCode.CannotCutEntireMedia, "Trim range is empty.");
        _after = trim.Clamp(new TimeRange(TimeSpan.Zero, edl.Duration));
    }

    protected override void Apply() { _before = Edl.Trim; Edl.Trim = _after; }
    protected override void Revert() { if (_before is not null) Edl.Trim = _before; }
}

public sealed class AddCutCommand : VideoEditCommand
{
    private readonly TimeRange _cut;

    public AddCutCommand(VideoEditList edl, TimeRange cut) : base(edl, "Cut")
    {
        if (!edl.CanAddCut(cut)) throw new ShotwiseException(ErrorCode.CannotCutEntireMedia, "The entire video cannot be cut.");
        _cut = cut;
    }

    protected override void Apply() => Edl.Cuts.Add(_cut);
    protected override void Revert() => Edl.Cuts.Remove(_cut);
}

public sealed class RemoveCutCommand : VideoEditCommand
{
    private readonly TimeRange _cut;
    private int _index = -1;

    public RemoveCutCommand(VideoEditList edl, TimeRange cut) : base(edl, "Restore cut") => _cut = cut;

    protected override void Apply() { _index = Edl.Cuts.IndexOf(_cut); if (_index >= 0) Edl.Cuts.RemoveAt(_index); }
    protected override void Revert() { if (_index >= 0) Edl.Cuts.Insert(Math.Min(_index, Edl.Cuts.Count), _cut); }
}

public sealed class ClearEditsCommand : VideoEditCommand
{
    private VideoEditList? _before;

    public ClearEditsCommand(VideoEditList edl) : base(edl, "Clear edits") { }

    protected override void Apply()
    {
        _before = Edl.Clone();
        Edl.Cuts.Clear();
        Edl.Trim = new TimeRange(TimeSpan.Zero, Edl.Duration);
    }

    protected override void Revert()
    {
        if (_before is null) return;
        Edl.Trim = _before.Trim;
        Edl.Cuts.Clear();
        Edl.Cuts.AddRange(_before.Cuts);
    }
}
