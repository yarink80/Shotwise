using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using Xunit;

namespace Shotwise.Core.Tests;

public class ShotxSerializerTests
{
    private static string PageJson(Page p) => JsonSerializer.Serialize(p, ShotwiseJson.Compact);

    [Fact]
    public async Task RoundTrip_AllAnnotationKinds_PreservesEverything()
    {
        using var tmp = new TempDir();
        var doc = TestData.AllKindsDocument();
        var path = tmp.File("doc.shotx");
        var ser = new ShotxSerializer();

        await ser.SaveAsync(doc, path, TestData.Gradient(2000, 1000));
        Assert.Equal(path, doc.FilePath);
        Assert.False(doc.IsDirty);

        var loaded = await ser.LoadAsync(path);
        Assert.Equal(doc.Id, loaded.Id);
        Assert.Single(loaded.Pages);
        Assert.Equal(PageJson(doc.Pages[0]), PageJson(loaded.Pages[0]));
        Assert.Equal(15, Enum.GetValues<AnnotationKind>().Length);
        var kinds = loaded.Pages[0].AllAnnotations().Select(a => a.Kind).ToHashSet();
        Assert.Equal(Enum.GetValues<AnnotationKind>().ToHashSet(), kinds);

        Assert.Equal(doc.Metadata.AppName, loaded.Metadata.AppName);
        Assert.Equal(doc.Metadata.Url, loaded.Metadata.Url);
        Assert.Equal(doc.Metadata.Tags, loaded.Metadata.Tags);
        Assert.Equal(doc.Metadata.CursorPosition, loaded.Metadata.CursorPosition);
        Assert.Equal(doc.Metadata.CaptureDate, loaded.Metadata.CaptureDate);

        // assets survive byte-for-byte
        foreach (var id in doc.ReferencedAssets().Distinct())
            Assert.Equal(doc.Assets.Get(id), loaded.Assets.Get(id));

        // base image decodes through the built-in PNG codec
        var baseImg = loaded.Assets.GetImage(loaded.Pages[0].Background.BaseImage!.Asset);
        Assert.Equal(64, baseImg.Width);
        Assert.Equal(48, baseImg.Height);
    }

    [Fact]
    public async Task Save_WritesThumbnailFirstStoredAndBounded()
    {
        using var tmp = new TempDir();
        var path = tmp.File("t.shotx");
        await new ShotxSerializer().SaveAsync(TestData.AllKindsDocument(), path, TestData.Gradient(3000, 600));
        using var zip = ZipFile.OpenRead(path);
        var first = zip.Entries[0];
        Assert.Equal("thumbnail.png", first.FullName);
        Assert.Equal(first.Length, first.CompressedLength); // Stored
        using var s = first.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.True(PngCodec.TryReadSize(ms.ToArray(), out var w, out var h));
        Assert.Equal(1024, Math.Max(w, h));
        Assert.Equal(205, h); // 600 * 1024/3000 rounded
        Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
        Assert.Contains(zip.Entries, e => e.FullName == "metadata.json");
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("pages/") && e.FullName.EndsWith(".json"));
        var png = zip.Entries.First(e => e.FullName.StartsWith("assets/") && e.FullName.EndsWith(".png"));
        Assert.Equal(png.Length, png.CompressedLength);

        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Equal("shotx", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Json_UsesKindDiscriminatorFirst_HexColours_AndPointObjects()
    {
        var a = new ArrowAnnotation { Start = new(1.5, 2), End = new(3, 4), Stroke = new StrokeStyle(new Argb(0x80FF0000), 2) };
        var json = JsonSerializer.Serialize<Annotation>(a, ShotwiseJson.Compact);
        Assert.StartsWith("{\"kind\":\"arrow\"", json);
        Assert.Contains("\"#80FF0000\"", json);
        Assert.Contains("\"start\":{\"x\":1.5,\"y\":2}", json);
        Assert.Contains("\"endCap\":\"arrow\"", json);
        Assert.DoesNotContain("\"bounds\"", json);
    }

    [Fact]
    public void Json_UnknownFields_ArePreservedInExtra()
    {
        var json = "{\"start\":{\"x\":1,\"y\":2},\"futureField\":{\"a\":[1,2]},\"kind\":\"shape\",\"shape\":\"ellipse\",\"another\":5}";
        var a = JsonSerializer.Deserialize<Annotation>(json, ShotwiseJson.Compact);
        var shape = Assert.IsType<ShapeAnnotation>(a);
        Assert.Equal(ShapeKind.Ellipse, shape.Shape);
        Assert.NotNull(shape.Extra);
        Assert.True(shape.Extra!.ContainsKey("futureField"));
        Assert.False(shape.Extra.ContainsKey("kind"));
        var back = JsonSerializer.Serialize<Annotation>(shape, ShotwiseJson.Compact);
        Assert.Contains("\"futureField\":{\"a\":[1,2]}", back);
        Assert.Contains("\"another\":5", back);
        var clone = shape.DeepClone();
        Assert.True(clone.Extra!.ContainsKey("another"));
    }

    [Fact]
    public void Json_UnknownKind_IsSkippedInPages()
    {
        var page = new Page { CanvasSize = new(10, 10) };
        page.Annotations.Add(new TextAnnotation { Text = RichText.FromPlain("x") });
        var node = JsonNode.Parse(JsonSerializer.Serialize(page, ShotwiseJson.Compact))!;
        node["annotations"]!.AsArray().Add(JsonNode.Parse("{\"kind\":\"hologram\",\"start\":{\"x\":0,\"y\":0}}"));
        node["pageFutureField"] = "keep me";
        var back = JsonSerializer.Deserialize<Page>(node.ToJsonString(), ShotwiseJson.Compact)!;
        Assert.Single(back.Annotations);
        Assert.True(back.Extra!.ContainsKey("pageFutureField"));
    }

    [Fact]
    public async Task Load_RejectsPathTraversal()
    {
        using var tmp = new TempDir();
        var path = tmp.File("evil.shotx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddText(zip, "manifest.json", "{\"format\":\"shotx\",\"version\":1,\"pages\":[\"pages/a.json\"]}");
            AddText(zip, "../evil.txt", "x");
        }
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => new ShotxSerializer().LoadAsync(path));
        Assert.Equal(ErrorCode.PathTraversal, ex.Code);
    }

    [Fact]
    public async Task Load_RejectsPageReferenceOutsidePages()
    {
        using var tmp = new TempDir();
        var path = tmp.File("evil2.shotx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            AddText(zip, "manifest.json", "{\"format\":\"shotx\",\"version\":1,\"pages\":[\"../../x.json\"]}");
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => new ShotxSerializer().LoadAsync(path));
        Assert.Equal(ErrorCode.PathTraversal, ex.Code);
    }

    [Fact]
    public async Task Load_RejectsTooManyEntries()
    {
        using var tmp = new TempDir();
        var path = tmp.File("many.shotx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddText(zip, "manifest.json", "{\"format\":\"shotx\",\"version\":1}");
            for (var i = 0; i < ZipSafety.MaxEntries + 1; i++) zip.CreateEntry($"assets/x{i}.bin", CompressionLevel.NoCompression);
        }
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => new ShotxSerializer().LoadAsync(path));
        Assert.Equal(ErrorCode.TooManyEntries, ex.Code);
    }

    [Fact]
    public async Task Load_RejectsNewerVersion_AndNonShotx()
    {
        using var tmp = new TempDir();
        var newer = tmp.File("newer.shotx");
        using (var zip = ZipFile.Open(newer, ZipArchiveMode.Create))
            AddText(zip, "manifest.json", "{\"format\":\"shotx\",\"version\":99}");
        Assert.Equal(ErrorCode.NewerFormatVersion, (await Assert.ThrowsAsync<ShotwiseException>(() => new ShotxSerializer().LoadAsync(newer))).Code);

        var notZip = tmp.File("bad.shotx");
        await File.WriteAllTextAsync(notZip, "definitely not a zip");
        Assert.Equal(ErrorCode.CorruptFile, (await Assert.ThrowsAsync<ShotwiseException>(() => new ShotxSerializer().LoadAsync(notZip))).Code);

        var missing = tmp.File("missing.shotx");
        Assert.Equal(ErrorCode.FileNotFound, (await Assert.ThrowsAsync<ShotwiseException>(() => new ShotxSerializer().LoadAsync(missing))).Code);
    }

    [Fact]
    public async Task ReadMetadataAndThumbnail_WithoutFullLoad()
    {
        using var tmp = new TempDir();
        var path = tmp.File("m.shotx");
        var doc = TestData.AllKindsDocument();
        var ser = new ShotxSerializer();
        await ser.SaveAsync(doc, path, TestData.Gradient(20, 10));
        var md = await ser.ReadMetadataAsync(path);
        Assert.Equal("Notepad", md.AppName);
        var thumb = await ser.ReadThumbnailAsync(path);
        Assert.NotNull(thumb);
        Assert.True(PngCodec.IsPng(thumb));
        Assert.True(ShotxSerializer.IsShotxFile(path));
    }

    [Fact]
    public async Task Save_IsAtomic_ExistingFileReplaced()
    {
        using var tmp = new TempDir();
        var path = tmp.File("a.shotx");
        await File.WriteAllTextAsync(path, "old");
        await new ShotxSerializer().SaveAsync(TestData.AllKindsDocument(), path, TestData.Gradient(4, 4));
        Assert.True(ShotxSerializer.IsShotxFile(path));
        Assert.Single(Directory.GetFiles(tmp.Path)); // no temp leftovers
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name);
        using var s = e.Open();
        var b = Encoding.UTF8.GetBytes(text);
        s.Write(b, 0, b.Length);
    }
}
