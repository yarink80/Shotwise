using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;
using Shotwise.Editor.Tools;
using Xunit;
using static Shotwise.Editor.Tests.TestKit;

namespace Shotwise.Editor.Tests;

public class VisionFlowTests
{
    private static BgraImage Ui()
    {
        var img = BgraImage.CreateFilled(120, 80, Argb.White);
        img.Blit(BgraImage.CreateFilled(30, 20, Argb.Blue), 10, 10);   // "button"
        img.Blit(BgraImage.CreateFilled(20, 20, Argb.Red), 70, 40);    // "icon"
        return img;
    }

    private static SmartComponent Button(bool canvasMask = true)
        => new(Guid.NewGuid(), new RectI(10, 10, 30, 20), canvasMask ? Mask.FromRect(120, 80, new RectI(10, 10, 30, 20)) : Mask.FromRect(30, 20, new RectI(0, 0, 30, 20)));

    // ------------------------------------------------------------------ Smart Move

    [Fact]
    public async Task SmartMove_Detect_AutoTolerance_StoresComponents_RaisesEvent()
    {
        using var k = new TestKit(image: Ui());
        k.SmartMove.Components.Add(Button());
        SmartComponentsDetectedEventArgs? ev = null;
        k.S.SmartComponentsDetected += (_, e) => ev = e;
        var n = await k.S.DetectSmartComponentsAsync();
        Assert.Equal(1, n);
        Assert.Equal(0.42, k.SmartMove.LastTolerance, 6);
        Assert.Equal(0.42, k.S.ToolOptions.Move.Tolerance, 6);
        Assert.Equal(1, ev!.Count);
        Assert.NotNull(k.S.SmartComponentAt(new Point2(20, 15)));
        Assert.Null(k.S.SmartComponentAt(new Point2(100, 5)));
    }

    [Fact]
    public async Task SmartMove_ManualTolerance_IsPassedToDetector()
    {
        using var k = new TestKit(image: Ui());
        k.S.ToolOptions.Move.AutoTolerance = false;
        k.S.ToolOptions.Move.Tolerance = 0.8;
        await k.S.DetectSmartComponentsAsync();
        Assert.Equal(0.8, k.SmartMove.LastTolerance, 6);
    }

    [Fact]
    public async Task SmartMove_NoComponents_ReportsMessage()
    {
        using var k = new TestKit(image: Ui());
        Assert.Equal(0, await k.S.DetectSmartComponentsAsync());
        Assert.Contains(k.Messages, m => m.Code == ErrorCode.NoComponentsFound);
    }

    [Fact]
    public async Task SmartMove_Unavailable_ReportsFeatureUnavailable()
    {
        using var k = new TestKit(image: Ui(), withServices: false);
        Assert.Equal(0, await k.S.DetectSmartComponentsAsync());
        Assert.Contains(k.Messages, m => m.Kind == EditorMessageKind.FeatureUnavailable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SmartMove_Lift_CreatesImageObject_InpaintsHole_OneUndoStep(bool canvasMask)
    {
        using var k = new TestKit(image: Ui());
        var comp = Button(canvasMask);
        k.SmartMove.Components.Add(comp);
        await k.S.DetectSmartComponentsAsync();
        var pos = k.S.Undo.Position;
        var id = await k.S.LiftSmartComponentAsync(comp);
        Assert.NotNull(id);
        var img = Assert.IsType<ImageAnnotation>(k.Get(id!.Value));
        Assert.Equal(new Rect2(10, 10, 30, 20), img.Bounds);
        var pixels = k.Doc.Assets.GetImage(img.Asset);
        Assert.Equal(Argb.Blue, pixels.GetPixel(5, 5));
        Assert.Equal(k.Inpainting.FillColor, k.Base().GetPixel(20, 15));
        Assert.Equal(Argb.Red, k.Base().GetPixel(75, 45));
        Assert.Equal(pos + 1, k.S.Undo.Position);
        Assert.Equal(new[] { id.Value }, k.S.Selection.Ids);
        Assert.Empty(k.S.SmartComponents);
        k.S.Undo.Undo();
        Assert.Null(k.Page.Find(id.Value));
        Assert.Equal(Argb.Blue, k.Base().GetPixel(20, 15));
    }

    [Fact]
    public async Task SmartMove_OtherComponentsSurviveLift_UndoInvalidatesCache()
    {
        using var k = new TestKit(image: Ui());
        var a = Button();
        var b = new SmartComponent(Guid.NewGuid(), new RectI(70, 40, 20, 20), Mask.FromRect(120, 80, new RectI(70, 40, 20, 20)));
        k.SmartMove.Components.AddRange(new[] { a, b });
        await k.S.DetectSmartComponentsAsync();
        await k.S.LiftSmartComponentAsync(a);
        Assert.Single(k.S.SmartComponents);
        k.S.Undo.Undo();
        Assert.Empty(k.S.SmartComponents);
    }

    [Fact]
    public async Task MoveTool_SmartMove_PressOnComponent_LiftsAndDrags()
    {
        using var k = new TestKit(image: Ui());
        k.S.SnappingEnabled = false;
        k.SmartMove.Components.Add(Button());
        k.S.ToolOptions.Move.SmartMove = true;
        k.S.ActiveTool = ToolKind.Move;
        await k.Idle(); // detection on activation
        Assert.Single(k.S.SmartComponents);
        k.S.PointerMove(Hover(20, 15));
        Assert.Equal(CursorHint.Hand, k.S.CurrentCursor);
        Assert.Contains(k.S.GetOverlay(1).OfType<OverlayRect>(), r => r.Rect == new Rect2(10, 10, 30, 20) && r.Style == OverlayStyle.Selection);
        k.S.PointerDown(P(20, 15));
        k.S.PointerMove(P(60, 35));
        await k.Idle();
        k.S.PointerMove(P(60, 35));
        k.S.PointerUp(P(60, 35));
        var obj = Assert.Single(k.Page.Annotations);
        Assert.Equal(new Point2(50, 30), obj.Start);
        Assert.Equal("Move", k.S.Undo.UndoLabel);
    }

    [Fact]
    public async Task MoveTool_ToggleSmartMove_TriggersDetection()
    {
        using var k = new TestKit(image: Ui());
        k.S.ActiveTool = ToolKind.Move;
        await k.Idle();
        Assert.Equal(0, k.SmartMove.DetectCalls);
        k.S.ToolOptions.Move.SmartMove = true;
        k.S.NotifyToolOptionsChanged();
        await k.Idle();
        Assert.Equal(1, k.SmartMove.DetectCalls);
    }

    // ------------------------------------------------------------------ Smart Redact

    private static void AddHits(TestKit k)
    {
        k.Redact.Hits.Add(new RedactionHit(RedactCategory.Email, new Rect2(10, 10, 50, 10), "a@b.c"));
        k.Redact.Hits.Add(new RedactionHit(RedactCategory.Phone, new Rect2(10, 30, 40, 10), "555"));
        k.Redact.Hits.Add(new RedactionHit(RedactCategory.Face, new Rect2(70, 40, 30, 30), null));
    }

    [Fact]
    public async Task SmartRedact_CoversHits_WithCategoryBlurs()
    {
        using var k = new TestKit(image: Ui());
        AddHits(k);
        RedactionsFoundEventArgs? found = null;
        k.S.RedactionsFound += (_, e) => found = e;
        Assert.Equal(3, await k.S.SmartRedactAsync());
        var blurs = k.Page.Annotations.OfType<BlurAnnotation>().ToList();
        Assert.Equal(3, blurs.Count);
        Assert.All(blurs, b => Assert.NotNull(b.Category));
        Assert.Equal(ShapeKind.Ellipse, blurs.Single(b => b.Category == RedactCategory.Face).Region);
        Assert.Equal(new Rect2(8, 8, 54, 14), blurs.Single(b => b.Category == RedactCategory.Email).Bounds);
        Assert.Equal(BlurKind.Pixelate, blurs[0].Mode);
        Assert.True(k.Page.SmartRedact.Enabled);
        Assert.Equal(1, found!.Counts[RedactCategory.Phone]);
        Assert.Equal(1, k.S.Undo.Position);
    }

    [Fact]
    public async Task SmartRedact_CategoryToggle_ModeChange_Clear_Done()
    {
        using var k = new TestKit(image: Ui());
        AddHits(k);
        await k.S.SmartRedactAsync();
        k.S.SetSmartRedactCategory(RedactCategory.Phone, false);
        Assert.Equal(2, k.Page.Annotations.Count);
        Assert.DoesNotContain(RedactCategory.Phone, k.Page.SmartRedact.Categories);
        k.S.SetSmartRedactCategory(RedactCategory.Phone, true);
        Assert.Equal(3, k.Page.Annotations.Count);
        k.S.SetSmartRedactMode(BlurKind.Solid, Argb.Black);
        Assert.All(k.Page.Annotations.Cast<BlurAnnotation>(), b => Assert.Equal(BlurKind.Solid, b.Mode));
        Assert.Equal(BlurKind.Solid, k.Page.SmartRedact.Mode);
        k.S.FinishSmartRedact();
        Assert.False(k.Page.SmartRedact.Enabled);
        Assert.Equal(3, k.Page.Annotations.Count);
        var manual = k.Add(new BlurAnnotation { Start = new(0, 0), End = new(5, 5) });
        k.S.ClearSmartRedact();
        Assert.Equal(new[] { manual.Id }, k.Page.Annotations.Select(a => a.Id));
        k.S.Undo.Undo();
        Assert.Equal(4, k.Page.Annotations.Count);
    }

    [Fact]
    public async Task SmartRedact_RerunReplacesPreviousRedactions()
    {
        using var k = new TestKit(image: Ui());
        AddHits(k);
        await k.S.SmartRedactAsync();
        await k.S.SmartRedactAsync(new HashSet<RedactCategory> { RedactCategory.Email });
        Assert.Single(k.Page.Annotations);
    }

    [Fact]
    public async Task SmartRedact_NoHits_ReportsMessage()
    {
        using var k = new TestKit(image: Ui());
        Assert.Equal(0, await k.S.SmartRedactAsync());
        Assert.Contains(k.Messages, m => m.Code == ErrorCode.NoRedactionsFound);
        Assert.Empty(k.Page.Annotations);
    }

    [Fact]
    public void ApplyRedactionHits_Directly()
    {
        using var k = new TestKit(image: Ui());
        k.S.ApplyRedactionHits(new[] { new RedactionHit(RedactCategory.CreditCard, new Rect2(5, 5, 60, 10), "4111") }, BlurKind.Gaussian, Argb.Gray);
        var b = (BlurAnnotation)k.Page.Annotations.Single();
        Assert.Equal(BlurKind.Gaussian, b.Mode);
        Assert.Equal(RedactCategory.CreditCard, b.Category);
    }

    // ------------------------------------------------------------------ Simplify

    [Fact]
    public async Task AutoSimplify_GeneratesMarkedShapes_WithSettings()
    {
        using var k = new TestKit(image: Ui());
        k.Page.Simplify = k.Page.Simplify with { Opacity = 0.6, LockObjects = true, Detail = 4 };
        Assert.Equal(3, await k.S.AutoSimplifyAsync());
        Assert.Equal(4, k.Simplify.LastDetail);
        Assert.Equal(1, k.Simplify.DominantCalls); // ImageColors palette
        var shapes = k.Page.Annotations.Cast<SimplifyShapeAnnotation>().ToList();
        Assert.All(shapes, s => Assert.True(EditorSession.IsAutoSimplifyShape(s)));
        Assert.All(shapes, s => Assert.True(s.IsLocked));
        Assert.All(shapes, s => Assert.Equal(0.6, s.Opacity, 6));
        Assert.Equal(Argb.Green, shapes[1].Fill.Color); // palette index 1 from the dominant colours
        Assert.True(k.Page.Simplify.AutoSimplifyEnabled);
        Assert.Equal(1, k.S.Undo.Position);
    }

    [Fact]
    public async Task AutoSimplify_RegenerateReplaces_DisableKeepsManualShapes()
    {
        using var k = new TestKit(image: Ui());
        var manual = k.Add(new SimplifyShapeAnnotation { Start = new(0, 60), End = new(20, 70) });
        await k.S.AutoSimplifyAsync();
        k.Simplify.ShapeCount = 2;
        await k.S.SetSimplifySettingsAsync(k.Page.Simplify with { Detail = 9 }, regenerate: true);
        Assert.Equal(9, k.Simplify.LastDetail);
        Assert.Equal(3, k.Page.Annotations.Count);
        k.S.DisableAutoSimplify();
        Assert.Equal(new[] { manual.Id }, k.Page.Annotations.Select(a => a.Id));
        Assert.False(k.Page.Simplify.AutoSimplifyEnabled);
    }

    [Fact]
    public async Task SimplifySettings_OpacityAppliesToAutoShapes_WithoutRegenerate()
    {
        using var k = new TestKit(image: Ui());
        await k.S.AutoSimplifyAsync();
        var calls = k.Simplify.GenerateCalls;
        await k.S.SetSimplifySettingsAsync(k.Page.Simplify with { Opacity = 0.25, LockObjects = false }, regenerate: false);
        Assert.Equal(calls, k.Simplify.GenerateCalls);
        Assert.All(k.Page.Annotations, a => Assert.Equal(0.25, a.Opacity, 6));
        Assert.All(k.Page.Annotations, a => Assert.False(a.IsLocked));
        k.S.Undo.Undo();
        Assert.All(k.Page.Annotations, a => Assert.Equal(1.0, a.Opacity, 6));
    }

    [Theory]
    [InlineData(SimplifyPaletteKind.Light)]
    [InlineData(SimplifyPaletteKind.Dark)]
    [InlineData(SimplifyPaletteKind.Grayscale)]
    public async Task AutoSimplify_BuiltInPalettes_DoNotQueryImage(SimplifyPaletteKind kind)
    {
        using var k = new TestKit(image: Ui());
        k.Page.Simplify = k.Page.Simplify with { Palette = kind };
        await k.S.AutoSimplifyAsync();
        Assert.Equal(0, k.Simplify.DominantCalls);
        Assert.Equal(EditorSession.BuiltInSimplifyPalette(kind).Colors, k.Simplify.LastPalette!.Colors);
        Assert.Equal(8, k.S.SimplifyPalette.Colors.Count);
    }

    [Fact]
    public async Task AutoSimplify_CustomPalette()
    {
        using var k = new TestKit(image: Ui());
        k.Page.Simplify = k.Page.Simplify with { Palette = SimplifyPaletteKind.Custom, CustomColors = new[] { Argb.Orange } };
        await k.S.AutoSimplifyAsync();
        Assert.All(k.Page.Annotations, a => Assert.Equal(Argb.Orange, a.Fill.Color));
    }

    // ------------------------------------------------------------------ Grab Text / Edit Text

    [Fact]
    public async Task GrabText_RaisesEvent_UsesPixelSelectionRegion()
    {
        using var k = new TestKit(image: Ui());
        TextRecognizedEventArgs? ev = null;
        k.S.TextRecognized += (_, e) => ev = e;
        k.S.ActiveTool = ToolKind.Selection;
        k.Drag(5, 5, 50, 40);
        k.S.ToolOptions.GrabText.Language = "en-US";
        var r = await k.S.GrabTextAsync();
        Assert.Equal("Hello", r!.Text);
        Assert.Equal(new Rect2(5, 5, 45, 35), k.Ocr.LastRegion);
        Assert.Equal("en-US", k.Ocr.LastLanguage);
        Assert.Same(r, ev!.Result);
    }

    [Fact]
    public async Task GrabTextTool_DragRegion_ClickWholePage()
    {
        using var k = new TestKit(image: Ui());
        k.S.ActiveTool = ToolKind.GrabText;
        k.Drag(10, 10, 60, 50);
        await k.Idle();
        Assert.Equal(new Rect2(10, 10, 50, 40), k.Ocr.LastRegion);
        k.Click(30, 30);
        await k.Idle();
        Assert.Null(k.Ocr.LastRegion);
    }

    [Fact]
    public async Task GrabText_Unavailable()
    {
        using var k = new TestKit(image: Ui(), withServices: false);
        Assert.Null(await k.S.GrabTextAsync());
        Assert.Contains(k.Messages, m => m.Kind == EditorMessageKind.FeatureUnavailable);
    }

    [Fact]
    public async Task EditText_CreatesStyledTextObjects_InpaintsOriginal()
    {
        using var k = new TestKit(image: Ui());
        Assert.Equal(1, await k.S.EditTextAsync());
        var t = Assert.IsType<TextAnnotation>(k.Page.Annotations.Single());
        Assert.Equal("Total: 42", t.Text.PlainText);
        Assert.True(t.Text.Runs[0].Bold);
        Assert.Equal("Arial", t.Text.Runs[0].FontFamily);
        Assert.Equal(14, t.Text.Runs[0].FontSize);
        Assert.Equal(new Rect2(20, 20, 60, 14), t.Bounds);
        Assert.Equal(TextDeselectBehavior.Keep, t.DeselectBehavior);
        Assert.Equal(1, k.Inpainting.Calls);
        Assert.Equal(k.Inpainting.FillColor, k.Base().GetPixel(30, 25));
        Assert.Equal(1, k.S.Undo.Position);
        k.S.Undo.Undo();
        Assert.Empty(k.Page.Annotations);
    }

    [Fact]
    public async Task EditTextMode_GrabTextTool()
    {
        using var k = new TestKit(image: Ui());
        k.S.ActiveTool = ToolKind.GrabText;
        k.S.ToolOptions.GrabText.EditText = true;
        k.Click(30, 30);
        await k.Idle();
        Assert.Single(k.Page.Annotations);
    }
}
