using Microsoft.Extensions.DependencyInjection;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Editor.Tools;
using Xunit;
using static Shotwise.Editor.Tests.TestKit;

namespace Shotwise.Editor.Tests;

public class SessionBehaviourTests
{
    // ------------------------------------------------------------------ keyboard

    [Fact]
    public void ArrowKeys_Nudge1And10_MergedIntoOneUndoStep()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.S.Selection.Select(a.Id);
        var pos = k.S.Undo.Position;
        k.Key(VirtualKey.Right);
        k.Key(VirtualKey.Right);
        k.Key(VirtualKey.Down, ModifierKeys.Shift);
        k.Key(VirtualKey.Left);
        Assert.Equal(new Point2(11, 20), k.Get(a.Id).Start);
        Assert.Equal(pos + 1, k.S.Undo.Position);
        k.S.Undo.Undo();
        Assert.Equal(new Point2(10, 10), k.Get(a.Id).Start);
    }

    [Fact]
    public void Nudge_SkipsLockedObjects_CtrlArrowIgnored()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30), IsLocked = true });
        k.S.Selection.Select(a.Id);
        k.Key(VirtualKey.Right);
        Assert.Equal(new Point2(10, 10), k.Get(a.Id).Start);
        var e = new KeyInput(VirtualKey.Right, ModifierKeys.Control);
        k.S.KeyDown(e);
        Assert.False(e.Handled);
    }

    [Fact]
    public void Delete_And_Backspace_DeleteSelection_EscDeselects()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        var b = k.Add(new ShapeAnnotation { Start = new(50, 10), End = new(70, 30) });
        k.S.Selection.Select(a.Id);
        k.Key(VirtualKey.Delete);
        Assert.Null(k.Page.Find(a.Id));
        k.S.Selection.Select(b.Id);
        k.Key(VirtualKey.Escape);
        Assert.Empty(k.S.Selection.Ids);
        k.S.Selection.Select(b.Id);
        k.Key(VirtualKey.Back);
        Assert.Empty(k.Page.Annotations);
    }

    // ------------------------------------------------------------------ canvas

    [Fact]
    public void ResizeCanvas_WithAnchor_ShiftsContent()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.S.ResizeCanvas(new Size2(300, 250), Anchor9.Center, Argb.Black);
        Assert.Equal(new Size2(300, 250), k.Page.CanvasSize);
        Assert.Equal(new Point2(60, 60), k.Get(a.Id).Start);
        Assert.Equal(Argb.Black, k.Page.Background.Color);
        k.S.ResizeCanvas(new Size2(0, 10), Anchor9.TopLeft, null); // ignored
        Assert.Equal(new Size2(300, 250), k.Page.CanvasSize);
    }

    [Fact]
    public void AutoExpandCanvas_OnDragBeyondEdges_IncludingNegativeSide()
    {
        using var k = new TestKit();
        k.S.SnappingEnabled = false;
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        var b = k.Add(new ShapeAnnotation { Start = new(100, 100), End = new(120, 120) });
        k.Drag(20, 20, 0, -5); // moves a to (-10,-15)
        Assert.Equal(new Size2(210, 165), k.Page.CanvasSize);
        Assert.Equal(new Point2(0, 0), k.Get(a.Id).Start);
        Assert.Equal(new Point2(110, 115), k.Get(b.Id).Start);
        Assert.Equal(new Point2(10, 15), k.Page.Background.BaseImage!.Offset);
        Assert.Equal(1, k.S.Undo.History.Count(h => h == "Move"));
        k.S.Undo.Undo();
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
        Assert.Equal(new Point2(10, 10), k.Get(a.Id).Start);
    }

    [Fact]
    public void AutoExpandCanvas_Off_KeepsCanvas()
    {
        using var k = new TestKit();
        k.S.SnappingEnabled = false;
        k.S.AutoExpandCanvas = false;
        var a = k.Add(new ShapeAnnotation { Start = new(170, 10), End = new(190, 30) });
        k.Drag(180, 20, 230, 20);
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
        Assert.Equal(new Point2(220, 10), k.Get(a.Id).Start);
    }

    [Fact]
    public void AutoExpand_FollowsPreference()
    {
        using var k = new TestKit();
        k.Settings.Current.Editor.AutoExpandCanvas = false;
        Assert.False(k.S.AutoExpandCanvas);
        k.S.ActiveTool = ToolKind.Shape;
        k.S.SnappingEnabled = false;
        k.Drag(150, 100, 260, 200);
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
    }

    [Fact]
    public void CanvasColour_AndStyle_AreUndoable()
    {
        using var k = new TestKit();
        k.S.SetCanvasColor(Argb.Red);
        k.S.SetCanvasStyle(12, ShadowStyle.Default);
        Assert.Equal(Argb.Red, k.Page.Background.Color);
        Assert.Equal(12, k.Page.CornerRadius);
        k.S.Undo.Undo();
        Assert.Equal(0, k.Page.CornerRadius);
        k.S.Undo.Undo();
        Assert.Equal(Argb.Transparent, k.Page.Background.Color);
        var pos = k.S.Undo.Position;
        k.S.SetCanvasColor(Argb.Transparent); // unchanged → no entry
        Assert.Equal(pos, k.S.Undo.Position);
    }

    // ------------------------------------------------------------------ snapping

    [Fact]
    public void Move_SnapsToCanvasCentre_ShowsGuide()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) }); // centre (20,20)
        k.S.PointerDown(P(20, 20));
        k.S.PointerMove(P(98, 50)); // centre would be (98,50) → snaps to canvas centre x = 100
        Assert.Equal(100, k.Get(a.Id).Bounds.Center.X, 6);
        Assert.Contains(k.S.GetOverlay(1).OfType<OverlayLine>(), l => l.Style == OverlayStyle.Guide && l.From.X == 100 && l.To.X == 100);
        k.S.PointerUp(P(98, 50));
        Assert.Equal(100, k.Get(a.Id).Bounds.Center.X, 6);
        Assert.DoesNotContain(k.S.GetOverlay(1).OfType<OverlayLine>(), l => l.Style == OverlayStyle.Guide);
    }

    [Fact]
    public void Move_SnapsToOtherObjectEdge_ThresholdScalesWithZoom()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 100), End = new(30, 120) });
        k.Add(new ShapeAnnotation { Start = new(150, 10), End = new(170, 30) });
        k.Drag(20, 110, 20 + 128, 110); // a centre would be 148 → snaps to the other object's left edge 150
        Assert.Equal(140, k.Get(a.Id).Bounds.Left, 6);
        k.S.Undo.Undo();
        // at 400 % zoom the threshold is 1.5 px: 3 px away does not snap
        k.Drag(20, 110, 20 + 137, 110, zoom: 4);
        Assert.Equal(147, k.Get(a.Id).Bounds.Left, 6);
    }

    [Fact]
    public void SnappingDisabled_NoSnap()
    {
        using var k = new TestKit();
        k.S.SnappingEnabled = false;
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.Drag(20, 20, 98, 50);
        Assert.Equal(98, k.Get(a.Id).Bounds.Center.X, 6);
    }

    [Fact]
    public void Create_SnapsPointToCanvasEdge()
    {
        using var k = new TestKit();
        k.S.ActiveTool = ToolKind.Shape;
        k.Drag(3, 4, 60, 147);
        Assert.Equal(new Rect2(0, 0, 60, 150), k.Page.Annotations.Single().Bounds);
    }

    [Fact]
    public void SnapEngine_Unit()
    {
        var canvas = new Rect2(0, 0, 200, 100);
        var r = Internal.SnapEngine.SnapRect(new Rect2(97, 10, 10, 10), canvas, Array.Empty<Rect2>(), 5);
        Assert.Equal(-2, r.Dx, 6); // centre 102 → canvas centre 100
        Assert.Equal(0, r.Dy, 6);  // nothing within 5 px vertically
        Assert.Single(r.Guides);
        var p = Internal.SnapEngine.SnapPoint(new Point2(148, 3), canvas, new[] { new Rect2(150, 40, 20, 20) }, 5);
        Assert.Equal(2, p.Dx, 6);
        Assert.Equal(-3, p.Dy, 6);
        Assert.Equal(2, p.Guides.Count);
        var none = Internal.SnapEngine.SnapRect(new Rect2(30, 30, 10, 10), canvas, Array.Empty<Rect2>(), 5, snapX: false, snapY: false);
        Assert.Equal(Internal.SnapResult.None.Dx, none.Dx);
        Assert.Empty(none.Guides);
    }

    // ------------------------------------------------------------------ effects

    [Fact]
    public async Task ApplyEffect_IsUndoable_OffsetsAnnotations()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        await k.S.ApplyEffectAsync(new BorderEffect(Argb.Black, 2));
        Assert.Equal(new Size2(204, 154), k.Page.CanvasSize);
        Assert.Equal(new Point2(12, 12), k.Get(a.Id).Start);
        k.S.Undo.Undo();
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
        Assert.Equal(new Point2(10, 10), k.Get(a.Id).Start);
    }

    [Fact]
    public async Task ApplyEffect_Spotlight_UsesPixelSelectionRegion()
    {
        using var k = new TestKit();
        k.S.SetPixelSelection(Mask.FromRect(200, 150, new RectI(20, 30, 40, 50)), PixelSelectionShape.Rectangle, null);
        await k.S.ApplyEffectAsync(new SpotlightMagnifyEffect(Rect2.Empty, 0.5, 2, 1));
        Assert.Equal(new Rect2(20, 30, 40, 50), ((SpotlightMagnifyEffect)k.Effects.Last!).Region);
    }

    [Fact]
    public async Task ApplyEffect_Spotlight_WithoutSelection_Warns()
    {
        using var k = new TestKit();
        await k.S.ApplyEffectAsync(new SpotlightMagnifyEffect(Rect2.Empty, 0.5, 2, 1));
        Assert.Equal(0, k.Effects.Calls);
        Assert.Contains(k.Messages, m => m.Kind == EditorMessageKind.Warning);
    }

    [Fact]
    public async Task ApplyEffect_StaleResult_IsDiscarded()
    {
        using var k = new TestKit();
        k.Effects.Delay = TimeSpan.FromMilliseconds(300);
        var t = k.S.ApplyEffectAsync(new BorderEffect(Argb.Black, 2));
        k.S.Crop(new Rect2(0, 0, 50, 50));
        await t;
        Assert.Equal(new Size2(50, 50), k.Page.CanvasSize);
        Assert.Equal("Crop", k.S.Undo.UndoLabel);
    }

    [Fact]
    public async Task Background_BusyState_AndCancellation()
    {
        using var k = new TestKit();
        k.Effects.Delay = TimeSpan.FromMilliseconds(200);
        var changes = 0;
        k.S.BusyChanged += (_, _) => changes++;
        var t = k.S.ApplyEffectAsync(new BorderEffect(Argb.Black, 2));
        Assert.True(k.S.IsBusy);
        k.S.CancelBackgroundWork();
        await t;
        Assert.False(k.S.IsBusy);
        Assert.True(changes >= 2);
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
    }

    [Fact]
    public async Task Background_Failure_IsReportedAsMessage()
    {
        using var k = new TestKit(configure: s => s with { Ocr = new ThrowingOcr() });
        Assert.Null(await k.S.GrabTextAsync());
        Assert.Contains(k.Messages, m => m.Kind == EditorMessageKind.Error && m.Code == ErrorCode.OcrFailed);
    }

    private sealed class ThrowingOcr : Shotwise.Core.Vision.IOcrService
    {
        public IReadOnlyList<string> AvailableLanguages => Array.Empty<string>();
        public string EngineName => "throw";
        public Task<Shotwise.Core.Vision.OcrResult> RecognizeAsync(BgraImage img, Rect2? region = null, string? language = null, CancellationToken ct = default)
            => throw new ShotwiseException(ErrorCode.OcrFailed, "boom");
    }

    // ------------------------------------------------------------------ pages / tools / misc

    [Fact]
    public void ActivePage_Switch_ClearsSelection_RaisesEvent()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.S.Selection.Select(a.Id);
        var page2 = new Page { CanvasSize = new Size2(50, 50) };
        k.Doc.Pages.Add(page2);
        var raised = false;
        k.S.ActivePageChanged += (_, _) => raised = true;
        k.S.ActivePage = page2;
        Assert.True(raised);
        Assert.Empty(k.S.Selection.Ids);
        Assert.Throws<ArgumentException>(() => k.S.ActivePage = new Page());
    }

    [Fact]
    public void ToolSwitch_CancelsGesture_RaisesEvent()
    {
        using var k = new TestKit();
        k.S.ActiveTool = ToolKind.Arrow;
        var raised = 0;
        k.S.ActiveToolChanged += (_, _) => raised++;
        k.S.PointerDown(P(10, 10));
        k.S.PointerMove(P(60, 60));
        k.S.ActiveTool = ToolKind.Shape;
        Assert.Empty(k.Page.Annotations);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Invalidated_RaisedOnChanges()
    {
        using var k = new TestKit();
        var n = 0;
        k.S.Invalidated += (_, _) => n++;
        k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        Assert.True(n > 0);
    }

    [Fact]
    public void SelectionChanged_Raised()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        var n = 0;
        k.S.SelectionChanged += (_, _) => n++;
        k.S.Selection.Select(a.Id);
        k.S.DeselectAll();
        Assert.Equal(2, n);
    }

    [Fact]
    public void UndoRemovingSelectedObject_DropsItFromSelection()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.S.Selection.Select(a.Id);
        k.S.Undo.Undo();
        Assert.Empty(k.S.Selection.Ids);
    }

    [Fact]
    public void CreatePrototype_FromStyle_FreshIdentity()
    {
        using var k = new TestKit();
        var proto = new ShapeAnnotation { Stroke = new StrokeStyle(Argb.Blue, 5), IsLocked = true, GroupId = Guid.NewGuid(), RotationDegrees = 30 };
        k.S.SetToolStyle(ToolKind.Shape, new QuickStyle { Tool = ToolKind.Shape, Prototype = proto });
        var a = k.S.CreatePrototype(ToolKind.Shape)!;
        Assert.NotEqual(proto.Id, a.Id);
        Assert.False(a.IsLocked);
        Assert.Null(a.GroupId);
        Assert.Equal(0, a.RotationDegrees);
        Assert.Equal(5, a.Stroke.Width);
        Assert.Null(k.S.CreatePrototype(ToolKind.Crop));
        Assert.Equal(AnnotationKind.Highlight, EditorSession.AnnotationKindFor(ToolKind.Highlighter));
    }

    [Fact]
    public void ToolOptions_JsonRoundTrip_ViaQuickStyle()
    {
        using var k = new TestKit();
        k.S.ToolOptions.Fill.Tolerance = 42;
        k.S.ToolOptions.Fill.Color = Argb.Orange;
        var json = k.S.ToolOptions.ToJson(ToolKind.Fill)!.Value;
        using var k2 = new TestKit();
        k2.S.SetToolStyle(ToolKind.Fill, new QuickStyle { Tool = ToolKind.Fill, Options = json });
        Assert.Equal(42, k2.S.ToolOptions.Fill.Tolerance);
        Assert.Equal(Argb.Orange, k2.S.ToolOptions.Fill.Color);
        Assert.Null(k.S.ToolOptions.ToJson(ToolKind.Arrow));
        Assert.Null(k.S.ToolOptions.ToJson(ToolKind.Stamp));
        foreach (var t in new[] { ToolKind.Eraser, ToolKind.Selection, ToolKind.MagicWand, ToolKind.Crop, ToolKind.CutOut, ToolKind.Move, ToolKind.Simplify, ToolKind.GrabText, ToolKind.Pen, ToolKind.Highlighter })
            Assert.True(k2.S.ToolOptions.Apply(t, k.S.ToolOptions.ToJson(t)!.Value));
    }

    [Fact]
    public void ToolStyleMemory_PersistsToFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shotwise-editor-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, ToolStyleMemory.FileName);
        try
        {
            using var m1 = new ToolStyleMemory(path) { SaveDelay = TimeSpan.FromHours(1) };
            m1.Set(ToolKind.Arrow, new QuickStyle { Tool = ToolKind.Arrow, Name = "Big", Prototype = new ArrowAnnotation { Stroke = new StrokeStyle(Argb.Red, 12) } });
            Assert.False(File.Exists(path)); // debounced
            m1.Flush();
            Assert.True(File.Exists(path));
            var m2 = new ToolStyleMemory(path);
            var s = m2.Get(ToolKind.Arrow);
            Assert.NotNull(s);
            Assert.Equal("Big", s!.Name);
            Assert.Equal(12, s.Prototype!.Stroke.Width);
            Assert.IsType<ArrowAnnotation>(s.Prototype);
            m2.Clear();
            Assert.Null(new ToolStyleMemory(path).Get(ToolKind.Arrow));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ToolStyleMemory_DebouncedSave_WritesAfterDelay()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shotwise-editor-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, ToolStyleMemory.FileName);
        try
        {
            using var m = new ToolStyleMemory(path) { SaveDelay = TimeSpan.FromMilliseconds(30) };
            for (var i = 0; i < 5; i++) m.Set(ToolKind.Pen, new QuickStyle { Tool = ToolKind.Pen, Name = "p" + i });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(File.Exists(path));
            m.Flush();
            Assert.Equal("p4", new ToolStyleMemory(path).Get(ToolKind.Pen)!.Name);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ToolStyleMemory_CorruptFile_IsIgnored()
    {
        var path = Path.Combine(Path.GetTempPath(), "shotwise-editor-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        try
        {
            var m = new ToolStyleMemory(path);
            Assert.Null(m.Get(ToolKind.Arrow));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DependencyInjection_RegistersFactoryAndMemory()
    {
        var services = new ServiceCollection();
        services.AddShotwiseEditor();
        services.AddSingleton<IDocumentRenderer>(new TestRenderer());
        services.AddSingleton<IEffectProcessor>(new TestEffects());
        services.AddSingleton<IImageCodec>(new TestCodec());
        var sp = new MiniProvider(services);
        Assert.IsType<DefaultToolFactory>(sp.GetRequiredService<IToolFactory>());
        Assert.IsType<ToolStyleMemory>(sp.GetRequiredService<IToolStyleMemory>());
        var es = EditorServices.FromServiceProvider(sp);
        Assert.NotNull(es.StyleMemory);
        Assert.NotNull(es.Tools);
        Assert.Null(es.SmartMove);
        using var session = new EditorSession(ShotDocument.Create(new Size2(10, 10), Argb.White), es);
        session.ActiveTool = ToolKind.Pen;
        Assert.IsType<PenTool>(session.CurrentTool);
    }

    /// <summary>Minimal singleton resolver (the test project references DI abstractions only).</summary>
    private sealed class MiniProvider : IServiceProvider
    {
        private readonly IServiceCollection _services;
        private readonly Dictionary<Type, object?> _cache = new();

        public MiniProvider(IServiceCollection services) => _services = services;

        public object? GetService(Type serviceType)
        {
            if (_cache.TryGetValue(serviceType, out var cached)) return cached;
            var d = _services.LastOrDefault(x => x.ServiceType == serviceType);
            if (d is null) return null;
            var obj = d.ImplementationInstance ?? d.ImplementationFactory?.Invoke(this) ?? Activator.CreateInstance(d.ImplementationType!);
            _cache[serviceType] = obj;
            return obj;
        }
    }

    [Fact]
    public void Overlay_PixelSelection_TextEdit_Primitives()
    {
        using var k = new TestKit();
        k.S.SetPixelSelection(Mask.FromRect(200, 150, new RectI(0, 0, 10, 10)), PixelSelectionShape.Rectangle, null);
        var o = k.S.GetOverlay(2);
        Assert.Contains(o, p => p is OverlayImageTint t && t.Mask is not null);
        Assert.Contains(o, p => p is OverlayRect r && r.Style == OverlayStyle.Marquee);
        var t = k.Add(new TextAnnotation { Start = new(10, 10), End = new(100, 40), Text = RichText.FromPlain("x") });
        k.S.BeginEditText(t.Id);
        Assert.Contains(k.S.GetOverlay(1), p => p is OverlayRect r && r.Rect == new Rect2(10, 10, 90, 30) && r.Style == OverlayStyle.Hover);
    }

    [Fact]
    public void Overlay_RotateHandle_ScreenConstantOffset()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(60, 60), End = new(100, 100) });
        k.S.Selection.Select(a.Id);
        var at1 = k.S.GetOverlay(1).OfType<OverlayHandle>().Single(h => h.Kind == HandleKind.Rotate).Center;
        var at2 = k.S.GetOverlay(2).OfType<OverlayHandle>().Single(h => h.Kind == HandleKind.Rotate).Center;
        Assert.Equal(60 - 24, at1.Y, 6);
        Assert.Equal(60 - 12, at2.Y, 6);
    }

    [Fact]
    public void Overlay_ObjectHandlesHidden_ForPixelAndRasterTools()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(60, 60), End = new(100, 100) });
        k.S.Selection.Select(a.Id);
        k.S.ActiveTool = ToolKind.Fill;
        Assert.Empty(k.S.GetOverlay(1).OfType<OverlayHandle>());
        Assert.Contains(k.S.GetOverlay(1).OfType<OverlayRect>(), r => r.Style == OverlayStyle.Selection);
        k.S.ActiveTool = ToolKind.Arrow;
        Assert.NotEmpty(k.S.GetOverlay(1).OfType<OverlayHandle>());
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var k = new TestKit();
        k.S.Dispose();
        k.S.Dispose();
    }
}
