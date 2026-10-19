using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Core.Tests;

public class UndoStackTests
{
    private static (ShotDocument Doc, Page Page, UndoStack Undo) New()
    {
        var doc = ShotDocument.FromImage(TestData.Gradient(20, 10));
        return (doc, doc.Pages[0], new UndoStack(doc));
    }

    [Fact]
    public void AddUndoRedo_RestoresState_AndTracksDirty()
    {
        var (doc, page, undo) = New();
        var changes = 0;
        undo.Changed += (_, _) => changes++;
        var arrow = new ArrowAnnotation { Start = new(0, 0), End = new(5, 5) };
        undo.Execute(new AddAnnotationCommand(page.Id, arrow), doc);
        Assert.Single(page.Annotations);
        Assert.True(doc.IsDirty);
        Assert.True(undo.CanUndo);
        Assert.Equal("Add Arrow", undo.UndoLabel);

        undo.Undo();
        Assert.Empty(page.Annotations);
        Assert.False(doc.IsDirty);
        Assert.True(undo.CanRedo);

        undo.Redo();
        Assert.Single(page.Annotations);
        Assert.Equal(arrow.Id, page.Annotations[0].Id);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Execute_ClearsRedoBranch()
    {
        var (doc, page, undo) = New();
        undo.Execute(new AddAnnotationCommand(page.Id, new LineAnnotation()), doc);
        undo.Execute(new AddAnnotationCommand(page.Id, new LineAnnotation()), doc);
        undo.Undo();
        undo.Execute(new AddAnnotationCommand(page.Id, new ShapeAnnotation()), doc);
        Assert.False(undo.CanRedo);
        Assert.Equal(2, undo.History.Count);
        Assert.Equal(2, undo.Position);
    }

    [Fact]
    public void Modify_WithMergeKey_MergesIntoOneEntry()
    {
        var (doc, page, undo) = New();
        var s = new ShapeAnnotation { Start = new(0, 0), End = new(10, 10), Opacity = 1 };
        undo.Execute(new AddAnnotationCommand(page.Id, s), doc);
        for (var i = 1; i <= 5; i++)
        {
            var v = 1 - i * 0.1;
            undo.Execute(ModifyAnnotationsCommand.Create(page, new[] { s.Id }, a => a.Opacity = v, "Opacity", mergeKey: "slider-1"), doc);
        }
        Assert.Equal(2, undo.History.Count);
        Assert.Equal(0.5, page.Annotations[0].Opacity, 6);
        undo.Undo();
        Assert.Equal(1.0, page.Annotations[0].Opacity, 6);
        undo.Redo();
        Assert.Equal(0.5, page.Annotations[0].Opacity, 6);

        // different key → separate entry
        undo.Execute(ModifyAnnotationsCommand.Create(page, new[] { s.Id }, a => a.Opacity = 0.2, "Opacity", mergeKey: "slider-2"), doc);
        Assert.Equal(3, undo.History.Count);
    }

    [Fact]
    public void Merge_DoesNotCrossSavePoint()
    {
        var (doc, page, undo) = New();
        var s = new ShapeAnnotation();
        undo.Execute(new AddAnnotationCommand(page.Id, s), doc);
        undo.Execute(ModifyAnnotationsCommand.Create(page, new[] { s.Id }, a => a.Opacity = 0.9, "Opacity", "k"), doc);
        undo.MarkClean();
        Assert.False(doc.IsDirty);
        undo.Execute(ModifyAnnotationsCommand.Create(page, new[] { s.Id }, a => a.Opacity = 0.8, "Opacity", "k"), doc);
        Assert.Equal(3, undo.History.Count);
        Assert.True(doc.IsDirty);
        undo.Undo();
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void Remove_ThenUndo_RestoresOriginalOrder()
    {
        var (doc, page, undo) = New();
        var a = new LineAnnotation(); var b = new LineAnnotation(); var c = new LineAnnotation();
        page.Annotations.AddRange(new Annotation[] { a, b, c });
        undo.Execute(new RemoveAnnotationsCommand(page.Id, new[] { a.Id, c.Id }), doc);
        Assert.Equal(new[] { b.Id }, page.Annotations.Select(x => x.Id));
        undo.Undo();
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, page.Annotations.Select(x => x.Id));
    }

    [Fact]
    public void Reorder_And_ZOrderHelpers()
    {
        var (doc, page, undo) = New();
        var a = new LineAnnotation(); var b = new LineAnnotation(); var c = new LineAnnotation();
        page.Annotations.AddRange(new Annotation[] { a, b, c });
        var order = page.Annotations.Select(x => x.Id).ToList();
        var sel = new HashSet<Guid> { a.Id };
        undo.Execute(new ReorderAnnotationsCommand(page.Id, ZOrder.BringToFront(order, sel)), doc);
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, page.Annotations.Select(x => x.Id));
        undo.Undo();
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, page.Annotations.Select(x => x.Id));
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, ZOrder.BringForward(order, sel));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, ZOrder.SendBackward(order, sel));
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, ZOrder.SendToBack(order, new HashSet<Guid> { c.Id }));
    }

    [Fact]
    public void GroupUngroup_RoundTrips()
    {
        var (doc, page, undo) = New();
        var a = new LineAnnotation(); var b = new LineAnnotation();
        page.Annotations.AddRange(new Annotation[] { a, b });
        var g = SetGroupCommand.Group(page.Id, new[] { a.Id, b.Id });
        undo.Execute(g, doc);
        Assert.All(page.Annotations, x => Assert.Equal(g.GroupId, x.GroupId));
        undo.Execute(SetGroupCommand.Ungroup(page.Id, new[] { a.Id, b.Id }), doc);
        Assert.All(page.Annotations, x => Assert.Null(x.GroupId));
        undo.Undo();
        Assert.All(page.Annotations, x => Assert.Equal(g.GroupId, x.GroupId));
    }

    [Fact]
    public void ResizeCanvas_ShiftsContent_ByAnchor_AndUndoes()
    {
        var (doc, page, undo) = New();
        var s = new ShapeAnnotation { Start = new(1, 1), End = new(2, 2) };
        page.Annotations.Add(s);
        undo.Execute(new ResizeCanvasCommand(page.Id, new Size2(40, 30), Anchor9.Center, Argb.Black), doc);
        Assert.Equal(new Size2(40, 30), page.CanvasSize);
        Assert.Equal(new Point2(11, 11), page.Annotations[0].Start);
        Assert.Equal(new Point2(10, 10), page.Background.BaseImage!.Offset);
        Assert.Equal(Argb.Black, page.Background.Color);
        undo.Undo();
        Assert.Equal(new Size2(20, 10), page.CanvasSize);
        Assert.Equal(new Point2(1, 1), page.Annotations[0].Start);
        Assert.Equal(Point2.Zero, page.Background.BaseImage!.Offset);
    }

    [Fact]
    public void Crop_NonDestructive_ShiftsAndRestores()
    {
        var (doc, page, undo) = New();
        var pen = new PenAnnotation { Points = { new(5, 5), new(6, 6) } };
        page.Annotations.Add(pen);
        undo.Execute(new CropCommand(page.Id, new Rect2(2, 3, 10, 5)), doc);
        Assert.Equal(new Size2(10, 5), page.CanvasSize);
        Assert.Equal(new Point2(3, 2), ((PenAnnotation)page.Annotations[0]).Points[0]);
        Assert.Equal(new Point2(-2, -3), page.Background.BaseImage!.Offset);
        undo.Undo();
        Assert.Equal(new Point2(5, 5), ((PenAnnotation)page.Annotations[0]).Points[0]);
        Assert.Equal(new Size2(20, 10), page.CanvasSize);
    }

    [Fact]
    public void ApplyEffect_SwapsRaster_OffsetsAnnotations_RetainsAssets()
    {
        var (doc, page, undo) = New();
        var before = page.Background.BaseImage!.Asset;
        var t = new TextAnnotation { Start = new(1, 1), End = new(5, 5) };
        page.Annotations.Add(t);
        var after = doc.Assets.AddImage(TestData.Gradient(24, 14));
        undo.Execute(new ApplyEffectCommand(page.Id, EffectDefaults.Border, after, new Size2(24, 14), new Point2(2, 2)), doc);
        Assert.Equal(after, page.Background.BaseImage!.Asset);
        Assert.Equal(new Point2(3, 3), page.Annotations[0].Start);
        Assert.True(doc.Assets.GetRefCount(after) > 0);
        Assert.True(doc.Assets.GetRefCount(before) > 0);

        undo.Undo();
        Assert.Equal(before, page.Background.BaseImage!.Asset);
        Assert.Equal(new Size2(20, 10), page.CanvasSize);
        Assert.Equal(new Point2(1, 1), page.Annotations[0].Start);

        // dropping the redo branch releases the after asset; prune can then delete it
        undo.Execute(new AddAnnotationCommand(page.Id, new LineAnnotation()), doc);
        Assert.Equal(0, doc.Assets.GetRefCount(after));
        doc.Assets.Prune(doc.ReferencedAssets());
        Assert.False(doc.Assets.Contains(after));
        Assert.True(doc.Assets.Contains(before));
    }

    [Fact]
    public void Flatten_RendersOnce_RemovesAnnotations_UndoRestores()
    {
        var (doc, page, undo) = New();
        var a = new ShapeAnnotation(); var b = new ShapeAnnotation();
        page.Annotations.AddRange(new Annotation[] { a, b });
        var renders = 0;
        var cmd = new FlattenAnnotationsCommand(page.Id, new[] { a.Id }, p => { renders++; return TestData.Gradient(20, 10); });
        undo.Execute(cmd, doc);
        Assert.Equal(new[] { b.Id }, page.Annotations.Select(x => x.Id));
        undo.Undo();
        Assert.Equal(new[] { a.Id, b.Id }, page.Annotations.Select(x => x.Id));
        undo.Redo();
        Assert.Equal(1, renders);
    }

    [Fact]
    public void CutOut_ShiftsAnnotationsBeyondStrip()
    {
        var (doc, page, undo) = New();
        page.CanvasSize = new Size2(20, 100);
        var above = new ShapeAnnotation { Start = new(0, 0), End = new(4, 4) };
        var below = new ShapeAnnotation { Start = new(0, 80), End = new(4, 84) };
        page.Annotations.AddRange(new Annotation[] { above, below });
        var joined = doc.Assets.AddImage(TestData.Gradient(20, 70));
        undo.Execute(new CutOutCommand(page.Id, CutOutOrientation.Horizontal, 40, 70, joined, CutOutEdgeStyle.Wave), doc);
        Assert.Equal(new Size2(20, 70), page.CanvasSize);
        Assert.Equal(new Point2(0, 0), page.Annotations[0].Start);
        Assert.Equal(new Point2(0, 50), page.Annotations[1].Start);
        undo.Undo();
        Assert.Equal(new Point2(0, 80), page.Annotations[1].Start);
        Assert.Equal(new Size2(20, 100), page.CanvasSize);
    }

    [Fact]
    public void UndoAll_KeepsIgnoresUndoAllObjects()
    {
        var (doc, page, undo) = New();
        var zone = new DropZoneAnnotation { Start = new(0, 0), End = new(5, 5) }; // IgnoresUndoAll = true by default
        undo.Execute(new AddAnnotationCommand(page.Id, zone), doc);
        undo.Execute(new AddAnnotationCommand(page.Id, new ArrowAnnotation()), doc);
        undo.UndoAll();
        Assert.Equal(0, undo.Position);
        var remaining = Assert.Single(page.Annotations);
        Assert.Equal(zone.Id, remaining.Id);
        Assert.False(undo.CanRedo);
    }

    [Fact]
    public void UndoAll_WithoutPinnedObjects_KeepsRedo()
    {
        var (doc, page, undo) = New();
        undo.Execute(new AddAnnotationCommand(page.Id, new ArrowAnnotation()), doc);
        undo.Execute(new AddAnnotationCommand(page.Id, new ArrowAnnotation()), doc);
        undo.UndoAll();
        Assert.Empty(page.Annotations);
        Assert.True(undo.CanRedo);
        undo.GoTo(2);
        Assert.Equal(2, page.Annotations.Count);
    }

    [Fact]
    public void MaxDepth_DropsOldest()
    {
        var (doc, page, undo) = New();
        undo.MaxDepth = 3;
        for (var i = 0; i < 5; i++) undo.Execute(new AddAnnotationCommand(page.Id, new LineAnnotation()), doc);
        Assert.Equal(3, undo.History.Count);
        Assert.Equal(3, undo.Position);
        while (undo.CanUndo) undo.Undo();
        Assert.Equal(2, page.Annotations.Count);
    }

    [Fact]
    public void Composite_UndoesInReverse_AndStepCounters()
    {
        var (doc, page, undo) = New();
        var step = new StepAnnotation { Value = page.StepCounters.Number };
        var comp = new CompositeCommand("Add step", new IEditCommand[]
        {
            new AddAnnotationCommand(page.Id, step),
            new SetStepCountersCommand(page.Id, page.StepCounters.Advance(StepSequence.Number)),
        });
        undo.Execute(comp, doc);
        Assert.Equal(2, page.StepCounters.Number);
        Assert.Single(page.Annotations);
        undo.Undo();
        Assert.Equal(1, page.StepCounters.Number);
        Assert.Empty(page.Annotations);
    }

    [Fact]
    public void Clear_ReleasesAssets()
    {
        var (doc, page, undo) = New();
        var after = doc.Assets.AddImage(TestData.Gradient(3, 3));
        undo.Execute(new ReplaceBaseImageCommand(page.Id, new BaseImageRef(after, Point2.Zero), "Fill"), doc);
        Assert.True(doc.Assets.GetRefCount(after) > 0);
        undo.Clear();
        Assert.Equal(0, doc.Assets.GetRefCount(after));
        Assert.False(undo.CanUndo);
    }
}
