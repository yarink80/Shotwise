using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Editor;

public enum PixelSelectionShape { Rectangle, Ellipse, Polygon, Freehand, MagicWand, SmartComponent }

/// <summary>Current selection: annotation ids (object selection) and/or a pixel mask (raster selection).</summary>
public sealed class SelectionModel
{
    private readonly List<Guid> _ids = new();

    public event EventHandler? Changed;

    /// <summary>Selected annotation ids in selection order (last = primary).</summary>
    public IReadOnlyList<Guid> Ids => _ids;
    public int Count => _ids.Count;
    public bool IsEmpty => _ids.Count == 0 && PixelMask is null;
    public bool HasObjects => _ids.Count > 0;
    public Guid? Primary => _ids.Count > 0 ? _ids[^1] : null;
    public bool Contains(Guid id) => _ids.Contains(id);

    public IReadOnlySet<Guid> ToSet() => _ids.ToHashSet();

    public void Set(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.SequenceEqual(_ids)) return;
        _ids.Clear();
        _ids.AddRange(list);
        Raise();
    }

    public void Select(Guid id) => Set(new[] { id });

    public void Add(Guid id)
    {
        if (_ids.Contains(id)) return;
        _ids.Add(id);
        Raise();
    }

    public void Remove(Guid id)
    {
        if (_ids.Remove(id)) Raise();
    }

    public void Toggle(Guid id)
    {
        if (!_ids.Remove(id)) _ids.Add(id);
        Raise();
    }

    /// <summary>Removes ids that are no longer present (after undo/delete).</summary>
    public void Retain(IEnumerable<Guid> existing)
    {
        var set = existing.ToHashSet();
        if (_ids.RemoveAll(id => !set.Contains(id)) > 0) Raise();
    }

    public void ClearObjects()
    {
        if (_ids.Count == 0) return;
        _ids.Clear();
        Raise();
    }

    // ---------------------------------------------------------------- pixel selection

    /// <summary>Canvas-sized coverage mask of the pixel selection, or null.</summary>
    public Mask? PixelMask { get; private set; }
    public Rect2? PixelBounds { get; private set; }
    public PixelSelectionShape? PixelShape { get; private set; }
    /// <summary>Outline polygon of the pixel selection for the marching-ants overlay (null = use <see cref="PixelBounds"/>).</summary>
    public IReadOnlyList<Point2>? PixelOutline { get; private set; }
    public bool HasPixelSelection => PixelMask is not null;

    public void SetPixelSelection(Mask mask, Rect2 bounds, PixelSelectionShape shape) => SetPixelSelection(mask, bounds, shape, null);

    public void SetPixelSelection(Mask mask, Rect2 bounds, PixelSelectionShape shape, IReadOnlyList<Point2>? outline)
    {
        PixelMask = mask ?? throw new ArgumentNullException(nameof(mask));
        PixelBounds = bounds;
        PixelShape = shape;
        PixelOutline = outline;
        Raise();
    }

    public void ClearPixelSelection()
    {
        if (PixelMask is null) return;
        PixelMask = null;
        PixelBounds = null;
        PixelShape = null;
        PixelOutline = null;
        Raise();
    }

    public void Clear()
    {
        var had = _ids.Count > 0 || PixelMask is not null;
        _ids.Clear();
        PixelMask = null;
        PixelBounds = null;
        PixelShape = null;
        PixelOutline = null;
        if (had) Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
