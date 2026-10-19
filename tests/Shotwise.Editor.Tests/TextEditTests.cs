using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Xunit;
using static Shotwise.Editor.Tests.TestKit;

namespace Shotwise.Editor.Tests;

public class TextEditTests
{
    private static TestKit TextKit()
    {
        var k = new TestKit();
        k.S.SnappingEnabled = false;
        k.S.ActiveTool = ToolKind.Text;
        return k;
    }

    [Fact]
    public void Click_CreatesPendingBox_RaisesBeginTextEdit()
    {
        using var k = TextKit();
        TextEditRequest? req = null;
        k.S.BeginTextEdit += (_, r) => req = r;
        k.Click(20, 20);
        Assert.NotNull(req);
        Assert.True(req!.IsNew);
        Assert.Equal(new Point2(20, 20), req.Bounds.TopLeft);
        Assert.Equal(req.AnnotationId, k.S.EditingAnnotationId);
        Assert.Single(k.Page.Annotations);  // present for layout, not yet in history
        Assert.Equal(0, k.S.Undo.Position);
        Assert.Contains(k.S.GetOverlay(1).OfType<OverlayRect>(), r => r.Style == OverlayStyle.Hover);
    }

    [Fact]
    public void Commit_AddsOneUndoStep_AutoSizesFromLayout()
    {
        using var k = TextKit();
        k.Click(20, 20);
        k.S.UpdateTextEdit(RichText.FromPlain("Hel"));
        k.S.CommitTextEdit(RichText.FromPlain("Hello world", fontSize: 20));
        var t = (TextAnnotation)k.Page.Annotations.Single();
        Assert.Equal("Hello world", t.Text.PlainText);
        // fake layout: 11 chars * 20 * 0.5 = 110 wide (+16 padding), 1 line * 24 high (+16)
        Assert.Equal(40, t.Bounds.Height, 6);
        Assert.True(t.Bounds.Width >= 126 - 1e-9);
        Assert.Equal(1, k.S.Undo.Position);
        Assert.Null(k.S.ActiveTextEdit);
        Assert.Equal(new[] { t.Id }, k.S.Selection.Ids);
    }

    [Fact]
    public void Commit_ExplicitContentSize_Wins()
    {
        using var k = TextKit();
        k.Click(20, 20);
        k.S.CommitTextEdit(RichText.FromPlain("abc"), new Size2(300, 50));
        Assert.Equal(new Rect2(20, 20, 316, 66), k.Page.Annotations.Single().Bounds);
    }

    [Fact]
    public void Commit_EmptyNewText_IsDiscarded_NoHistory()
    {
        using var k = TextKit();
        k.Click(20, 20);
        k.S.CommitTextEdit(new RichText());
        Assert.Empty(k.Page.Annotations);
        Assert.Equal(0, k.S.Undo.Position);
        Assert.Empty(k.S.Selection.Ids);
    }

    [Fact]
    public void Cancel_NewText_RemovesBox()
    {
        using var k = TextKit();
        k.Click(20, 20);
        k.S.CancelTextEdit();
        Assert.Empty(k.Page.Annotations);
        Assert.Null(k.S.EditingAnnotationId);
    }

    [Fact]
    public void EditExisting_ModifiesAsOneStep_CancelReverts()
    {
        using var k = TextKit();
        var t = k.Add(new TextAnnotation { Start = new(10, 10), End = new(100, 40), Text = RichText.FromPlain("old"), TextStyle = TextLayoutStyle.Default with { AutoSize = false } });
        var pos = k.S.Undo.Position;
        k.Click(20, 20); // Text tool click on existing text → edit
        Assert.Equal(t.Id, k.S.EditingAnnotationId);
        k.S.UpdateTextEdit(RichText.FromPlain("live"));
        Assert.Equal("live", ((TextAnnotation)k.Get(t.Id)).Text.PlainText);
        k.S.CancelTextEdit();
        Assert.Equal("old", ((TextAnnotation)k.Get(t.Id)).Text.PlainText);
        Assert.Equal(pos, k.S.Undo.Position);

        Assert.True(k.S.BeginEditText(t.Id));
        k.S.UpdateTextEdit(RichText.FromPlain("new"));
        k.S.CommitTextEdit(RichText.FromPlain("new"));
        Assert.Equal("new", ((TextAnnotation)k.Get(t.Id)).Text.PlainText);
        Assert.Equal(pos + 1, k.S.Undo.Position);
        Assert.Equal("Edit text", k.S.Undo.UndoLabel);
        k.S.Undo.Undo();
        Assert.Equal("old", ((TextAnnotation)k.Get(t.Id)).Text.PlainText);
    }

    [Fact]
    public void EditExisting_Unchanged_NoHistory()
    {
        using var k = TextKit();
        var t = k.Add(new TextAnnotation { Start = new(10, 10), End = new(100, 40), Text = RichText.FromPlain("same"), TextStyle = TextLayoutStyle.Default with { AutoSize = false } });
        var pos = k.S.Undo.Position;
        k.S.BeginEditText(t.Id);
        k.S.CommitTextEdit(RichText.FromPlain("same"));
        Assert.Equal(pos, k.S.Undo.Position);
    }

    [Fact]
    public void EditExisting_ClearedText_DeletesBox_UnlessKeep()
    {
        using var k = TextKit();
        var t = k.Add(new TextAnnotation { Start = new(10, 10), End = new(100, 40), Text = RichText.FromPlain("x") });
        k.S.BeginEditText(t.Id);
        k.S.CommitTextEdit(new RichText());
        Assert.Null(k.Page.Find(t.Id));
        k.S.Undo.Undo();
        Assert.NotNull(k.Page.Find(t.Id));

        var keep = k.Add(new TextAnnotation { Start = new(10, 60), End = new(100, 90), Text = RichText.FromPlain("y"), DeselectBehavior = TextDeselectBehavior.Keep });
        k.S.BeginEditText(keep.Id);
        k.S.CommitTextEdit(new RichText());
        Assert.NotNull(k.Page.Find(keep.Id));
    }

    [Fact]
    public void PointerDownElsewhere_RequestsEnd_ThenAutoCommitsLiveText()
    {
        using var k = TextKit();
        var ended = 0;
        k.S.EndTextEditRequested += (_, _) => ended++;
        k.Click(20, 20);
        k.S.UpdateTextEdit(RichText.FromPlain("typed"));
        k.S.ActiveTool = ToolKind.Select;
        Assert.Equal(1, ended);
        Assert.Null(k.S.ActiveTextEdit);
        Assert.Equal("typed", ((TextAnnotation)k.Page.Annotations.Single()).Text.PlainText);
        Assert.Equal(1, k.S.Undo.Position);
    }

    [Fact]
    public void ViewHandlingEndRequest_CommitsItsOwnText()
    {
        using var k = TextKit();
        k.S.EndTextEditRequested += (_, _) => k.S.CommitTextEdit(RichText.FromPlain("from view"));
        k.Click(20, 20);
        k.S.PointerDown(P(150, 120));
        Assert.Contains(k.Page.Annotations.OfType<TextAnnotation>(), t => t.Text.PlainText == "from view");
    }

    [Fact]
    public void Enter_OnSelectedText_StartsEdit_EscEnds()
    {
        using var k = new TestKit();
        var t = k.Add(new TextAnnotation { Start = new(10, 10), End = new(100, 40), Text = RichText.FromPlain("x") });
        k.S.Selection.Select(t.Id);
        k.Key(VirtualKey.Enter);
        Assert.Equal(t.Id, k.S.EditingAnnotationId);
        k.Key(VirtualKey.A); // typing goes to the view's editor, session ignores it
        Assert.Equal(t.Id, k.S.EditingAnnotationId);
        k.Key(VirtualKey.Escape);
        Assert.Null(k.S.EditingAnnotationId);
    }

    [Fact]
    public void BeginEditText_RejectsLockedAndNonText()
    {
        using var k = new TestKit();
        var locked = k.Add(new TextAnnotation { Start = new(10, 10), End = new(100, 40), IsLocked = true });
        var shape = k.Add(new ShapeAnnotation { Start = new(10, 60), End = new(50, 90) });
        Assert.False(k.S.BeginEditText(locked.Id));
        Assert.False(k.S.BeginEditText(shape.Id));
        Assert.False(k.S.BeginEditText(Guid.NewGuid()));
    }

    [Fact]
    public void DragWithTextTool_SetsBoxWidth()
    {
        using var k = TextKit();
        k.Drag(10, 10, 160, 20);
        k.S.CommitTextEdit(RichText.FromPlain("a"));
        Assert.Equal(150, k.Page.Annotations.Single().Bounds.Width, 6);
    }

    [Fact]
    public void Callout_DoubleClick_EditsText()
    {
        using var k = new TestKit();
        var c = k.Add(new CalloutAnnotation { Start = new(10, 10), End = new(100, 60), Text = RichText.FromPlain("c") });
        k.S.ActiveTool = ToolKind.Callout;
        k.S.DoubleClick(P(50, 30, clicks: 2));
        Assert.Equal(c.Id, k.S.EditingAnnotationId);
    }

    [Fact]
    public void UndoDuringEdit_ClearsEditOfRemovedAnnotation()
    {
        using var k = new TestKit();
        var t = k.Add(new TextAnnotation { Start = new(10, 10), End = new(100, 40), Text = RichText.FromPlain("x") });
        k.S.BeginEditText(t.Id);
        k.S.Undo.Undo(); // removes the text box
        Assert.Null(k.S.ActiveTextEdit);
    }
}
