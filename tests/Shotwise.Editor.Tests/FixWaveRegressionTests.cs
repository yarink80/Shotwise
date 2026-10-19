using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Editor.Tests;

/// <summary>Regression tests for the P4 fix wave (FX-6: R5-#1, R5-#3, R5-#4, EDT-03).</summary>
public class FixWaveRegressionTests
{
    // ------------------------------------------------------------------ R5-#1 save revision

    [Fact]
    public void CompleteSave_WithEditsAfterSnapshot_StaysDirty()
    {
        using var k = new TestKit();
        k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var snapshot = k.S.Revision;            // serializer snapshot taken here
        k.Add(new ShapeAnnotation { Start = new(60, 60), End = new(90, 90) }); // edit while the write is in flight
        Assert.False(k.S.CompleteSave(snapshot));
        Assert.True(k.S.HasUnsavedChanges);
        Assert.True(k.Doc.IsDirty);
    }

    [Fact]
    public void CompleteSave_StaleSave_StaysDirtyEvenWhenUndoReturnsToOldCleanPoint()
    {
        using var k = new TestKit();
        k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var snapshot = k.S.Revision;
        k.S.Undo.Undo(); // history position == initial clean position, but the file holds the shape
        Assert.False(k.S.CompleteSave(snapshot));
        Assert.True(k.S.Undo.IsClean);
        Assert.True(k.S.HasUnsavedChanges);
        // a newer save that completes without interleaved edits makes the document clean again
        Assert.True(k.S.CompleteSave(k.S.Revision));
        Assert.False(k.S.HasUnsavedChanges);
        Assert.False(k.Doc.IsDirty);
    }

    [Fact]
    public void CompleteSave_NoInterleavedEdits_MarksClean_AndDoesNotBumpRevision()
    {
        using var k = new TestKit();
        k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var rev = k.S.Revision;
        Assert.True(k.S.CompleteSave(rev));
        Assert.Equal(rev, k.S.Revision);
        Assert.False(k.S.HasUnsavedChanges);
    }

    [Fact]
    public void Revision_ChangesOnMergedEdits()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.S.Selection.Select(a.Id);
        k.S.MoveSelection(1, 0, "nudge");
        var rev = k.S.Revision;
        var position = k.S.Undo.Position;
        k.S.MoveSelection(1, 0, "nudge"); // merges into the previous command: same position, new content
        Assert.Equal(position, k.S.Undo.Position);
        Assert.NotEqual(rev, k.S.Revision);
        Assert.False(k.S.CompleteSave(rev));
    }

    // ------------------------------------------------------------------ R5-#3 section deletion undo

    [Fact]
    public void DeleteSectionGraphic_ThenLayoutUndo_ThenDeleteUndo_RestoresIntoDocument()
    {
        using var k = new TestKit(200, 300);
        k.S.SnappingEnabled = false;
        var header = new TemplateSection { Type = SectionType.Header, Bounds = new Rect2(0, 0, 200, 100) };
        var body = new TemplateSection { Type = SectionType.Body, Bounds = new Rect2(0, 100, 200, 100) };
        var caption = new TextAnnotation { Start = new(120, 110), End = new(190, 130), Text = RichText.FromPlain("Step") };
        body.Graphics.Add(caption);
        k.Page.Sections.Add(header);
        k.Page.Sections.Add(body);

        k.S.Selection.Select(caption.Id);
        k.S.DeleteSelection();
        Assert.Null(k.Page.Find(caption.Id));

        k.S.MoveSection(body.Id, -1);   // layout command: sections replaced by clones on undo
        k.S.Undo.Undo();                // undo the layout operation
        k.S.Undo.Undo();                // undo the deletion

        var restored = k.Page.Find(caption.Id);
        Assert.NotNull(restored);
        var owner = k.Page.Sections.Single(s => s.Graphics.Any(g => g.Id == caption.Id));
        Assert.Equal(body.Id, owner.Id);
        Assert.Equal(new Point2(120, 110), restored!.Start);

        k.S.Undo.Redo();                // delete again works against the live list
        Assert.Null(k.Page.Find(caption.Id));
    }

    [Fact]
    public void DeleteTopLevel_UndoRedo_RestoresOrder()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(1, 1), End = new(5, 5) });
        var b = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(15, 15) });
        var c = k.Add(new ShapeAnnotation { Start = new(20, 20), End = new(25, 25) });
        k.S.Selection.Set(new[] { a.Id, c.Id });
        k.S.DeleteSelection();
        Assert.Single(k.Page.Annotations);
        k.S.Undo.Undo();
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, k.Page.Annotations.Select(x => x.Id));
    }

    // ------------------------------------------------------------------ R5-#4 raster lifetime

    [Fact]
    public void DroppedRedoBranch_ReleasesEditorRaster()
    {
        using var k = new TestKit(200, 150);
        var initial = k.Doc.Assets.Count;
        k.S.CutOut(CutOutOrientation.Horizontal, 10, 20);
        Assert.Equal(initial + 1, k.Doc.Assets.Count);
        k.S.Undo.Undo();
        Assert.Equal(initial + 1, k.Doc.Assets.Count); // still reachable through redo
        k.Add(new ShapeAnnotation { Start = new(1, 1), End = new(5, 5) }); // drops the redo branch
        Assert.Equal(initial, k.Doc.Assets.Count);
        Assert.Equal(0, k.S.EditorRasterCount);
    }

    [Fact]
    public void HistoryDepthEviction_BoundsRasterAssets_AndUndoStillWorks()
    {
        using var k = new TestKit(200, 150);
        k.S.Undo.MaxDepth = 1;
        var initial = k.Doc.Assets.Count;
        for (var i = 0; i < 6; i++) k.S.CutOut(CutOutOrientation.Horizontal, 5, 10);
        // original capture + the last command's before/after rasters
        Assert.Equal(initial + 2, k.Doc.Assets.Count);
        Assert.Equal(150 - 6 * 5, (int)k.Page.CanvasSize.Height);
        k.S.Undo.Undo();
        Assert.Equal(150 - 5 * 5, (int)k.Page.CanvasSize.Height);
        Assert.NotNull(k.Doc.Assets.GetImage(k.Page.Background.BaseImage!.Asset));
    }

    [Fact]
    public void RasterSharedWithAnnotation_IsNeverPruned()
    {
        using var k = new TestKit(200, 150);
        k.S.CutOut(CutOutOrientation.Horizontal, 10, 20);
        var raster = k.Page.Background.BaseImage!.Asset;
        // an image annotation that happens to reference the same (deduplicated) bytes
        var img = k.Add(new ImageAnnotation { Asset = raster, Start = new(0, 0), End = new(20, 20) });
        k.S.Selection.Select(img.Id);
        k.S.DeleteSelection();
        k.S.Undo.Undo();        // image back
        k.S.Undo.Undo();        // delete undone... (image add undone)
        k.S.Undo.Undo();        // cut-out undone
        k.Add(new ShapeAnnotation { Start = new(1, 1), End = new(5, 5) }); // drops the whole redo branch
        Assert.True(k.Doc.Assets.Contains(raster));
    }

    // ------------------------------------------------------------------ EDT-03 duplicate objects

    [Fact]
    public void Duplicate_CopiesSelectionOffset_OneUndoStep()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.S.Selection.Select(a.Id);
        var position = k.S.Undo.Position;
        k.S.Duplicate();
        Assert.Equal(2, k.Page.Annotations.Count);
        Assert.Equal(position + 1, k.S.Undo.Position);
        var copy = k.Page.Annotations[1];
        Assert.NotEqual(a.Id, copy.Id);
        Assert.Equal(new Point2(20, 20), copy.Start);
        Assert.Equal(copy.Id, Assert.Single(k.S.Selection.Ids));
    }
}
