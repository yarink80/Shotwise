using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Editor.Tests;

public class ArrangeTests
{
    private static (TestKit K, ShapeAnnotation A, ShapeAnnotation B, ShapeAnnotation C) Three()
    {
        var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        var b = k.Add(new ShapeAnnotation { Start = new(50, 20), End = new(70, 50) });
        var c = k.Add(new ShapeAnnotation { Start = new(120, 40), End = new(160, 60) });
        return (k, a, b, c);
    }

    private static Guid[] Order(TestKit k) => k.Page.Annotations.Select(a => a.Id).ToArray();

    [Fact]
    public void ZOrder_Commands_ReorderAndUndo()
    {
        var (k, a, b, c) = Three();
        using var _ = k;
        k.S.Selection.Select(a.Id);
        k.S.BringForward();
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, Order(k));
        k.S.BringToFront();
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, Order(k));
        k.S.SendBackward();
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, Order(k));
        k.S.SendToBack();
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, Order(k));
        k.S.Undo.Undo();
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, Order(k));
        var pos = k.S.Undo.Position;
        k.S.Selection.Select(c.Id);
        k.S.BringToFront(); // already top → no-op, no history entry
        Assert.Equal(pos, k.S.Undo.Position);
    }

    [Fact]
    public void GroupUngroup_RoundTrip()
    {
        var (k, a, b, c) = Three();
        using var _ = k;
        k.S.Selection.Set(new[] { a.Id, b.Id });
        k.S.Group();
        var g = k.Get(a.Id).GroupId;
        Assert.NotNull(g);
        Assert.Equal(g, k.Get(b.Id).GroupId);
        Assert.Null(k.Get(c.Id).GroupId);
        Assert.Equal(new[] { a.Id, b.Id }, k.S.ExpandToGroups(new[] { b.Id }).OrderBy(x => x == b.Id ? 1 : 0).ToArray());
        k.S.Ungroup();
        Assert.Null(k.Get(a.Id).GroupId);
        k.S.Undo.Undo();
        Assert.Equal(g, k.Get(b.Id).GroupId);
    }

    [Fact]
    public void Group_RequiresTwoObjects()
    {
        var (k, a, _, _) = Three();
        using var __ = k;
        k.S.Selection.Select(a.Id);
        var pos = k.S.Undo.Position;
        k.S.Group();
        Assert.Equal(pos, k.S.Undo.Position);
    }

    [Theory]
    [InlineData(AlignKind.Left, 10.0, double.NaN)]
    [InlineData(AlignKind.Right, 160.0, double.NaN)]
    [InlineData(AlignKind.Top, double.NaN, 10.0)]
    [InlineData(AlignKind.Bottom, double.NaN, 60.0)]
    public void Align_ToUnionBounds(AlignKind kind, double edgeX, double edgeY)
    {
        var (k, a, b, c) = Three();
        using var _ = k;
        k.S.Selection.Set(new[] { a.Id, b.Id, c.Id });
        k.S.Align(kind);
        foreach (var x in k.Page.Annotations)
        {
            if (!double.IsNaN(edgeX)) Assert.Equal(edgeX, kind == AlignKind.Left ? x.Bounds.Left : x.Bounds.Right, 6);
            if (!double.IsNaN(edgeY)) Assert.Equal(edgeY, kind == AlignKind.Top ? x.Bounds.Top : x.Bounds.Bottom, 6);
        }
        Assert.Equal(1, k.S.Undo.History.Count(h => h.StartsWith("Align")));
    }

    [Fact]
    public void Align_CenterAndMiddle()
    {
        var (k, a, b, c) = Three();
        using var _ = k;
        k.S.Selection.Set(new[] { a.Id, b.Id, c.Id });
        k.S.Align(AlignKind.Center);
        Assert.All(k.Page.Annotations, x => Assert.Equal(85, x.Bounds.Center.X, 6));
        k.S.Align(AlignKind.Middle);
        Assert.All(k.Page.Annotations, x => Assert.Equal(35, x.Bounds.Center.Y, 6));
    }

    [Fact]
    public void Align_SingleObject_AlignsToCanvas_UsingRotatedBounds()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(50, 50), End = new(90, 60), RotationDegrees = 90 });
        k.S.Selection.Select(a.Id);
        k.S.Align(AlignKind.Left);
        Assert.Equal(0, k.Get(a.Id).RotatedBounds.Left, 6);
        k.S.Align(AlignKind.Bottom);
        Assert.Equal(150, k.Get(a.Id).RotatedBounds.Bottom, 6);
    }

    [Fact]
    public void Align_SkipsLockedObjects()
    {
        var (k, a, b, c) = Three();
        using var _ = k;
        k.S.Selection.Set(new[] { a.Id, b.Id, c.Id });
        k.S.ModifyAnnotations(new[] { c.Id }, x => x.IsLocked = true, "lock");
        k.S.Align(AlignKind.Right);
        Assert.Equal(new Point2(120, 40), k.Get(c.Id).Start);
        Assert.Equal(70, k.Get(a.Id).Bounds.Right, 6);
    }

    [Fact]
    public void Distribute_EvenlySpacesCentres()
    {
        var (k, a, b, c) = Three();
        using var _ = k;
        k.S.Selection.Set(new[] { a.Id, b.Id, c.Id });
        k.S.Distribute(horizontal: true);
        Assert.Equal(20, k.Get(a.Id).Bounds.Center.X, 6);
        Assert.Equal(80, k.Get(b.Id).Bounds.Center.X, 6);
        Assert.Equal(140, k.Get(c.Id).Bounds.Center.X, 6);
        k.S.Distribute(horizontal: false);
        Assert.Equal(35, k.Get(b.Id).Bounds.Center.Y, 6);
    }

    [Fact]
    public void Distribute_NeedsThree()
    {
        var (k, a, b, _) = Three();
        using var __ = k;
        k.S.Selection.Set(new[] { a.Id, b.Id });
        var pos = k.S.Undo.Position;
        k.S.Distribute(true);
        Assert.Equal(pos, k.S.Undo.Position);
    }

    [Fact]
    public void Flip_MirrorsAboutSelectionCentre()
    {
        using var k = new TestKit();
        var a = k.Add(new ArrowAnnotation { Start = new(10, 10), End = new(50, 30) });
        k.S.Selection.Select(a.Id);
        k.S.FlipSelection(horizontal: true);
        var r = k.Get(a.Id);
        Assert.Equal(new Point2(50, 10), r.Start);
        Assert.Equal(new Point2(10, 30), r.End);
        k.S.FlipSelection(horizontal: false);
        Assert.Equal(new Point2(50, 30), k.Get(a.Id).Start);
    }

    [Fact]
    public void Lock_Unlock_AreUndoable()
    {
        var (k, a, _, _) = Three();
        using var __ = k;
        k.S.Selection.Select(a.Id);
        k.S.Lock(true);
        Assert.True(k.Get(a.Id).IsLocked);
        k.S.Lock(false);
        Assert.False(k.Get(a.Id).IsLocked);
        k.S.Undo.Undo();
        Assert.True(k.Get(a.Id).IsLocked);
    }

    [Fact]
    public void SelectAll_Invert_Delete_Undo()
    {
        var (k, a, b, c) = Three();
        using var _ = k;
        k.S.Selection.Select(a.Id);
        k.S.InvertSelection();
        Assert.Equal(new HashSet<Guid> { b.Id, c.Id }, k.S.Selection.ToSet());
        k.S.SelectAll();
        Assert.Equal(3, k.S.Selection.Count);
        k.S.DeleteSelection();
        Assert.Empty(k.Page.Annotations);
        Assert.Empty(k.S.Selection.Ids);
        k.S.Undo.Undo();
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, Order(k));
    }

    [Fact]
    public void Duplicate_OffsetsBy10_NewIds_SelectsCopies()
    {
        var (k, a, b, _) = Three();
        using var __ = k;
        k.S.Selection.Set(new[] { a.Id, b.Id });
        k.S.Duplicate();
        Assert.Equal(5, k.Page.Annotations.Count);
        var copies = k.S.SelectedAnnotations();
        Assert.Equal(2, copies.Count);
        Assert.DoesNotContain(copies, x => x.Id == a.Id || x.Id == b.Id);
        Assert.Contains(copies, x => x.Start == new Point2(20, 20));
        Assert.Equal("Paste", k.S.Undo.UndoLabel);
    }

    [Fact]
    public void Duplicate_RemapsGroups()
    {
        var (k, a, b, _) = Three();
        using var __ = k;
        k.S.Selection.Set(new[] { a.Id, b.Id });
        k.S.Group();
        k.S.Duplicate();
        var copies = k.S.SelectedAnnotations();
        Assert.Single(copies.Select(x => x.GroupId).Distinct());
        Assert.NotEqual(k.Get(a.Id).GroupId, copies[0].GroupId);
    }

    [Fact]
    public void Flatten_Selection_MergesIntoBase_ViaRenderer()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30), Fill = FillStyle.Solid(Argb.Red) });
        var b = k.Add(new ShapeAnnotation { Start = new(100, 10), End = new(130, 30), Fill = FillStyle.Solid(Argb.Blue) });
        k.S.Selection.Select(a.Id);
        k.S.Flatten(all: false);
        Assert.Null(k.Page.Find(a.Id));
        Assert.NotNull(k.Page.Find(b.Id));
        Assert.Contains(a.Id, k.Renderer.LastOptions!.Only!);
        Assert.Equal(Argb.Red, k.Base().GetPixel(20, 20));
        Assert.Equal(Argb.White, k.Base().GetPixel(110, 20));
        k.S.FlattenAll();
        Assert.Empty(k.Page.Annotations);
        Assert.Equal(Argb.Blue, k.Base().GetPixel(110, 20));
        k.S.Undo.Undo();
        k.S.Undo.Undo();
        Assert.Equal(2, k.Page.Annotations.Count);
        Assert.Equal(Argb.White, k.Base().GetPixel(20, 20));
    }

    [Fact]
    public void ModifySelection_WithMergeKey_MergesIntoOneStep()
    {
        var (k, a, _, _) = Three();
        using var __ = k;
        k.S.Selection.Select(a.Id);
        var pos = k.S.Undo.Position;
        for (var i = 1; i <= 5; i++) k.S.ModifySelection(x => x.Opacity = i / 10.0, "Opacity", "opacity-slider");
        Assert.Equal(pos + 1, k.S.Undo.Position);
        Assert.Equal(0.5, k.Get(a.Id).Opacity, 6);
        k.S.Undo.Undo();
        Assert.Equal(1, k.Get(a.Id).Opacity, 6);
    }

    [Fact]
    public void ApplyStyleToSelection_OnlyMatchingKinds()
    {
        using var k = new TestKit();
        var s = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        var l = k.Add(new LineAnnotation { Start = new(50, 50), End = new(90, 50) });
        k.S.Selection.Set(new[] { s.Id, l.Id });
        k.S.ApplyStyleToSelection(new Shotwise.Core.Styles.QuickStyle { Tool = Shotwise.Core.Styles.ToolKind.Shape, Prototype = new ShapeAnnotation { Stroke = new StrokeStyle(Argb.Blue, 9), Shape = ShapeKind.Ellipse } });
        Assert.Equal(9, k.Get(s.Id).Stroke.Width);
        Assert.Equal(ShapeKind.Ellipse, ((ShapeAnnotation)k.Get(s.Id)).Shape);
        Assert.NotEqual(9, k.Get(l.Id).Stroke.Width);
    }

    [Fact]
    public void CreateQuickStyleFromSelection_CopiesPrototype()
    {
        using var k = new TestKit();
        var s = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30), Stroke = new StrokeStyle(Argb.Green, 4) });
        k.S.Selection.Select(s.Id);
        var q = k.S.CreateQuickStyleFromSelection("Mine");
        Assert.NotNull(q);
        Assert.Equal(Shotwise.Core.Styles.ToolKind.Shape, q!.Tool);
        Assert.Equal(4, q.Prototype!.Stroke.Width);
        Assert.NotEqual(s.Id, q.Prototype.Id);
    }
}
