using System.IO.Compression;
using System.Text;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using Shotwise.Core.Vision;
using Xunit;

namespace Shotwise.Core.Tests;

/// <summary>Builds a synthetic .snagx (invented content following the documented container structure) and imports it.</summary>
public class SnagxImporterTests
{
    private const string PageId = "{00000000-0000-0000-0000-000000000001}";

    private static string Rtf(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes("{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red0\\green0\\blue0;}\\f0\\fs20 " + text + "\\par}"));

    private static string BuildSnagx(TempDir tmp, string objectsJson, string sectionsJson = "[]", string? extraPage = null)
    {
        var path = tmp.File("sample.snagx");
        var bg = PngCodec.Encode(TestData.Gradient(40, 30));
        var thumb = PngCodec.Encode(TestData.Gradient(20, 15));
        var stampPng = Convert.ToBase64String(PngCodec.Encode(TestData.Gradient(6, 6)));
        objectsJson = objectsJson.Replace("$IMG", stampPng);
        var page = $$"""
        {
          "Version": "1.0", "SoftwareVersion": "1.0.0", "ExportedFromPlatform": "win",
          "CaptureCanvasWidth": 40, "CaptureCanvasHeight": 30, "DPI": 120,
          "CaptureBackgroundColor": "#FF102030",
          "CaptureBackgroundImage": "{{PageId}}.png",
          "CaptureBackgroundLocation": "0,0",
          "CornerRadiusRatio": 0,
          "CursorPosition": "12,13",
          "StepToolSequenceNumeric": 4, "StepToolSequenceUppercaseLetter": 2, "StepToolSequenceLowercaseLetter": 1,
          "SimplifySettings": { "ActiveColorPaletteName": "ImageColors", "LockAllObjects": true, "Opacity": 100, "ShowOriginal": false, "SuiDetail": 6 },
          "SmartRedactProperties": { "SmartRedactEnabled": true, "RedactionTypesEnabled": ["Email", "PhoneNumber", "WebUrl"] },
          "CaptureObjects": {{objectsJson}},
          "Sections": {{sectionsJson}}
          {{extraPage ?? ""}}
        }
        """;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "thumbnail.png", thumb, CompressionLevel.NoCompression);
        Add(zip, "index.json", Encoding.UTF8.GetBytes($"{{\"Pages\":[\"{PageId}.json\"],\"Version\":\"1.0\"}}"));
        Add(zip, $"{PageId}.json", Encoding.UTF8.GetBytes(page));
        Add(zip, $"{PageId}.png", bg, CompressionLevel.NoCompression);
        Add(zip, "metadata.json", Encoding.UTF8.GetBytes("""
            {"Version":"1.0","CaptureDate":"2025-11-05T10:20:30Z","CaptureId":"x","LanguageCode":"en-US","OperatingSystemVersion":"10.0.26100",
             "AppCompany":"Example Corp","AppName":"Example App","AppVersion":"1.2.3","WebURL":"https://example.org/page","WindowName":"Example Window"}
            """));
        return path;
    }

    private static void Add(ZipArchive zip, string name, byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        var e = zip.CreateEntry(name, level);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    private const string AllObjects = """
    [
      {"ToolMode":"Arrow","ObjectID":"{11111111-1111-1111-1111-111111111111}","ObjectPriority":3,"StartPoint":"1,2","EndPoint":"30.5,20","ForegroundColor":"#FFFF0000","StrokeWidth":5,"ArrowStart":"Flat","ArrowEnd":"EquilateralArrow","DashType":"Dash","Opacity":80,"DropShadowEnabled":true,"ShadowColor":"#FF000000","ShadowBlur":6,"ShadowDirectionX":3,"ShadowDirectionY":4,"ShadowOpacity":60,"IsLocked":1},
      {"ToolMode":"Text","ObjectPriority":1,"StartPoint":"2,2","EndPoint":"38,10","RTFEncodedText":"$RTF","FontFamily":"Arial","FontSize":14,"TextSelectionBold":true,"TextSelectionColor":"#FF0000FF","ToolHorizontalAlign":"Center","ToolVerticalAlign":"Middle","ToolPadding":4,"TextDeselectBehavior":"JustDeselect"},
      {"ToolMode":"Shape","ObjectPriority":2,"ToolShape":"RoundedRectangle","StartPoint":"30,25","EndPoint":"5,5","ForegroundColor":"#FF00FF00","BackgroundColor":"#00000000","StrokeWidth":2.5,"CornerRadiusRatio":0.2,"RotationAngle":12},
      {"ToolMode":"Step","ObjectPriority":4,"StepStyle":"NSCirclePointed","StepSequenceType":"UppercaseLetter","PlainText":"C","StartPoint":"10,10","EndPoint":"30,25","BackgroundColor":"#FFE53935","FontSize":12},
      {"ToolMode":"Callout","ObjectPriority":5,"CalloutShape":"CTBasicThoughtBubble1","StartPoint":"1,1","EndPoint":"20,10","PlainText":"Think","BackgroundColor":"#FFFFFFFF","ForegroundColor":"#FF000000","CalloutTails":["35,28"],"TailWidth":9},
      {"ToolMode":"Pen","ObjectPriority":6,"PointsArray":["1,1","2,2","3,5"],"PenShape":"Round","StrokeWidth":"3"},
      {"ToolMode":"Highlight","ObjectPriority":7,"StartPoint":"0,0","EndPoint":"10,4","ForegroundColor":"#80FFFF00","Opacity":0},
      {"ToolMode":"Magnify","ObjectPriority":8,"ToolShape":"Ellipse","StartPoint":"20,10","EndPoint":"30,20","MagnifyScale":200,"MagnifyOffset":"5,0","MagnifyConnectorType":"SingleLine"},
      {"ToolMode":"Blur","ObjectPriority":9,"StartPoint":"0,20","EndPoint":"8,28","BlurType":"Pixelate","BlurSmoothIntensity":30},
      {"ToolMode":"Image","ObjectPriority":10,"Image":"$IMG","ImagePosition":"4,4","ImageSize":"6,6"},
      {"ToolMode":"Crop","ObjectPriority":11,"StartPoint":"0,0","EndPoint":"1,1"},
      {"ToolMode":"Hologram","ObjectPriority":12}
    ]
    """;

    [Fact]
    public async Task Import_MapsSupportedObjects_SkipsUnknown()
    {
        using var tmp = new TempDir();
        var path = BuildSnagx(tmp, AllObjects.Replace("$RTF", Rtf("Hello \\b world\\b0 \\'e9")));
        var importer = new SnagxImporter();
        Assert.True(importer.CanImport(path));
        var doc = await importer.ImportAsync(path);

        var page = Assert.Single(doc.Pages);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), page.Id);
        Assert.Equal(new Size2(40, 30), page.CanvasSize);
        Assert.Equal(120, page.Dpi);
        Assert.Equal(new Argb(0xFF102030), page.Background.Color);
        var bg = doc.Assets.GetImage(page.Background.BaseImage!.Asset);
        Assert.Equal(40, bg.Width);
        Assert.Equal(new StepCounters(4, 2, 1), page.StepCounters);
        Assert.Equal(6, page.Simplify.Detail);
        Assert.True(page.SmartRedact.Enabled);
        Assert.Equal(new[] { RedactCategory.Email, RedactCategory.Phone, RedactCategory.Url }, page.SmartRedact.Categories);

        Assert.Equal(new PointI(12, 13), doc.Metadata.CursorPosition);
        Assert.Equal("Example App", doc.Metadata.AppName);
        Assert.Equal("Example Window", doc.Metadata.WindowTitle);
        Assert.Equal("https://example.org/page", doc.Metadata.Url);
        Assert.Equal(new DateTimeOffset(2025, 11, 5, 10, 20, 30, TimeSpan.Zero), doc.Metadata.CaptureDate);

        // ordered by ObjectPriority: Text(1), Shape(2), Arrow(3), Step(4), Callout, Pen, Highlight, Magnify, Blur, Image
        Assert.Equal(new[] { AnnotationKind.Text, AnnotationKind.Shape, AnnotationKind.Arrow, AnnotationKind.Step, AnnotationKind.Callout,
            AnnotationKind.Pen, AnnotationKind.Highlight, AnnotationKind.Magnify, AnnotationKind.Blur, AnnotationKind.Image }, page.Annotations.Select(a => a.Kind));

        var arrow = (ArrowAnnotation)page.Annotations[2];
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), arrow.Id);
        Assert.Equal(new Point2(30.5, 20), arrow.End);
        Assert.Equal(ArrowCap.None, arrow.StartCap);
        Assert.Equal(ArrowCap.Triangle, arrow.EndCap);
        Assert.Equal(StrokeDash.Dash, arrow.Stroke.Dash);
        Assert.Equal(5, arrow.Stroke.Width);
        Assert.Equal(0.8, arrow.Opacity, 6);
        Assert.True(arrow.IsLocked);
        Assert.NotNull(arrow.Shadow);
        Assert.Equal(5, arrow.Shadow!.Distance, 6);

        var text = (TextAnnotation)page.Annotations[0];
        Assert.Equal("Hello worldé", text.Text.PlainText); // control words consume their delimiter space
        Assert.Equal("Arial", text.Text.Runs[0].FontFamily);
        Assert.True(text.Text.Runs[0].Bold);
        Assert.Equal(new Argb(0xFF0000FF), text.Text.Runs[0].Color);
        Assert.Equal(HorizontalAlign.Center, text.TextStyle.HAlign);
        Assert.Equal(VerticalAlign.Middle, text.TextStyle.VAlign);
        Assert.Equal(TextDeselectBehavior.Keep, text.DeselectBehavior);

        var shape = (ShapeAnnotation)page.Annotations[1];
        Assert.Equal(ShapeKind.RoundedRectangle, shape.Shape);
        Assert.Equal(new Rect2(5, 5, 25, 20), shape.Bounds);
        Assert.Equal(FillKind.None, shape.Fill.Kind);
        Assert.Equal(12, shape.RotationDegrees);

        var step = (StepAnnotation)page.Annotations[3];
        Assert.Equal(StepStyle.CirclePointer, step.Style);
        Assert.Equal(StepSequence.Upper, step.Sequence);
        Assert.Equal(3, step.Value);
        Assert.Equal("C", step.Label);
        Assert.Equal(new Point2(30, 25), step.PointerTip);

        var callout = (CalloutAnnotation)page.Annotations[4];
        Assert.Equal(CalloutPresets.ThoughtBubble, callout.ShapePresetId);
        Assert.Equal("Think", callout.Text.PlainText);
        Assert.Equal(new Point2(35, 28), Assert.Single(callout.Tails).Tip);

        var pen = (PenAnnotation)page.Annotations[5];
        Assert.Equal(3, pen.Points.Count);
        Assert.Equal(3, pen.Stroke.Width);

        var hl = (HighlightAnnotation)page.Annotations[6];
        Assert.Null(hl.Points);
        Assert.Equal(new Argb(0x80FFFF00), hl.Fill.Color);
        Assert.Equal(1.0, hl.Opacity);

        var mag = (MagnifyAnnotation)page.Annotations[7];
        Assert.Equal(2.0, mag.Scale);
        Assert.Equal(new Rect2(17.5, 12.5, 5, 5), mag.SourceRect);

        var blur = (BlurAnnotation)page.Annotations[8];
        Assert.Equal(BlurKind.Pixelate, blur.Mode);
        Assert.Equal(30, blur.Intensity);

        var img = (ImageAnnotation)page.Annotations[9];
        Assert.Equal(new Rect2(4, 4, 6, 6), img.Bounds);
        Assert.Equal(6, doc.Assets.GetImage(img.Asset).Width);

        Assert.Equal(1, importer.LastSkipped["Crop"]);
        Assert.Equal(1, importer.LastSkipped["Hologram"]);
        Assert.True(doc.IsDirty);
        Assert.Null(doc.FilePath);
    }

    [Fact]
    public async Task Import_TemplateSections_WithDropZones()
    {
        using var tmp = new TempDir();
        var sections = """
        [
          {"ToolMode":"Select","SectionType":"SectionTypeHeader","SectionID":"{22222222-2222-2222-2222-222222222222}","StartPoint":"0,0","EndPoint":"40,8",
           "SectionGraphics":[{"ToolMode":"Text","PlainText":"Title","StartPoint":"1,1","EndPoint":"39,7"}]},
          {"ToolMode":"RepeatableSection","SectionType":"SectionTypeBody","StartPoint":"0,8","EndPoint":"40,30",
           "SectionGraphics":[{"ToolMode":"DropZone","StartPoint":"2,10","EndPoint":"38,28","IsLocked":true,"DropZoneGroupId":1}]}
        ]
        """;
        var path = BuildSnagx(tmp, "[]", sections);
        var doc = await new SnagxImporter().ImportAsync(path);
        var page = doc.Pages[0];
        Assert.Equal(2, page.Sections.Count);
        Assert.Equal(SectionType.Header, page.Sections[0].Type);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), page.Sections[0].Id);
        Assert.Equal(SectionType.Repeatable, page.Sections[1].Type);
        var dz = Assert.IsType<DropZoneAnnotation>(Assert.Single(page.Sections[1].Graphics));
        Assert.Equal(page.Sections[1].Id, dz.SectionId);
        Assert.True(dz.IsLocked);

        // imported document saves as .shotx and reloads
        var shotx = tmp.File("converted.shotx");
        await new ShotxSerializer().SaveAsync(doc, shotx, TestData.Gradient(4, 4));
        var re = await new ShotxSerializer().LoadAsync(shotx);
        Assert.Equal(2, re.Pages[0].Sections.Count);
        Assert.Equal("Title", ((TextAnnotation)re.Pages[0].Sections[0].Graphics[0]).Text.PlainText);
    }

    [Fact]
    public async Task Import_RejectsTraversalInBackgroundReference()
    {
        using var tmp = new TempDir();
        var path = tmp.File("bad.snagx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "index.json", Encoding.UTF8.GetBytes("{\"Pages\":[\"p.json\"],\"Version\":\"1.0\"}"));
            Add(zip, "p.json", Encoding.UTF8.GetBytes("{\"CaptureBackgroundImage\":\"../../secret.png\"}"));
        }
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => new SnagxImporter().ImportAsync(path));
        Assert.Equal(ErrorCode.PathTraversal, ex.Code);
    }

    [Fact]
    public async Task Import_MissingIndex_IsInvalidFormat()
    {
        using var tmp = new TempDir();
        var path = tmp.File("noindex.snagx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) Add(zip, "thumbnail.png", PngCodec.Encode(TestData.Gradient(2, 2)));
        Assert.Equal(ErrorCode.InvalidFileFormat, (await Assert.ThrowsAsync<ShotwiseException>(() => new SnagxImporter().ImportAsync(path))).Code);
        Assert.False(new SnagxImporter().CanImport(tmp.File("nope.png")));
    }

    [Fact]
    public void Rtf_PlainText_Extraction()
    {
        Assert.Equal("Line1\nLine2\ttab", RtfText.ToPlainText("{\\rtf1\\ansi{\\fonttbl{\\f0 Segoe UI;}}\\f0 Line1\\par Line2\\tab tab\\par}"));
        Assert.Equal("AéB", RtfText.ToPlainText("{\\rtf1\\uc1 A\\u233?B}"));
        Assert.Equal("{x}\\", RtfText.ToPlainText("{\\rtf1 \\{x\\}\\\\}"));
        Assert.Equal("plain", RtfText.ToPlainText("plain"));
        Assert.Equal("visible", RtfText.ToPlainText("{\\rtf1{\\*\\generator Something;}visible}"));
    }
}
