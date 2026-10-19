using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;

namespace Shotwise.Library.Tests;

public class CatalogTests
{
    [Fact]
    public async Task AddCapture_WritesShotxInAutosaveLayout_AndIndexesIt()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var when = new DateTimeOffset(2026, 3, 9, 14, 5, 7, TimeSpan.Zero).ToLocalTime();
        var doc = Samples.Doc(app: "My App: Pro", tags: new[] { "work" }, when: when);
        var item = await lib.AddCaptureAsync(doc, Samples.Image(1000, 500));

        Assert.True(File.Exists(item.Path));
        Assert.EndsWith(".shotx", item.Path);
        Assert.StartsWith(h.Root, item.Path, StringComparison.OrdinalIgnoreCase);
        var rel = Path.GetRelativePath(h.Root, item.Path).Split(Path.DirectorySeparatorChar);
        Assert.Equal(3, rel.Length);
        Assert.Equal(when.ToString("yyyy"), rel[0]);
        Assert.Equal(when.ToString("MM"), rel[1]);
        Assert.Contains("My_App", rel[2]);
        Assert.DoesNotContain(':', rel[2]);
        Assert.Equal(MediaKind.Image, item.Kind);
        Assert.Equal(800, item.Width);
        Assert.Equal(600, item.Height);
        Assert.Equal("My App: Pro", item.AppName);
        Assert.Contains("work", item.Tags);
        Assert.Equal("docs.example.com", item.Website);
        Assert.Equal(doc.FilePath, item.Path);

        // Thumbnail: PNG, longest side <= 320 px (preview was 1000x500).
        Assert.True(File.Exists(item.ThumbnailPath));
        var thumb = PngCodec.Decode(File.ReadAllBytes(item.ThumbnailPath));
        Assert.Equal(320, thumb.Width);
        Assert.True(thumb.Height <= 320);
    }

    [Fact]
    public async Task SameTimestampAndApp_GetUniqueFileNames()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var when = DateTimeOffset.Now;
        var a = await h.Service.AddCaptureAsync(Samples.Doc(when: when), Samples.Image(50, 50));
        var b = await h.Service.AddCaptureAsync(Samples.Doc(when: when), Samples.Image(50, 50));
        Assert.NotEqual(a.Path, b.Path);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public async Task Crud_GetFindRenameFavoriteOcrDelete()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var events = new List<LibraryChangeKind>();
        lib.Changed += (_, e) => { lock (events) events.Add(e.Kind); };

        var item = await lib.AddCaptureAsync(Samples.Doc(), Samples.Image(100, 100));
        Assert.Equal(item.Id, (await lib.GetAsync(item.Id))!.Id);
        Assert.Equal(item.Id, (await lib.FindByPathAsync(item.Path))!.Id);
        Assert.Null(await lib.GetAsync(Guid.NewGuid()));

        await lib.SetFavoriteAsync(item.Id, true);
        Assert.True((await lib.GetAsync(item.Id))!.IsFavorite);
        Assert.Single(await lib.QueryAsync(new LibraryQuery(FavoritesOnly: true)));

        await lib.SetOcrTextAsync(item.Id, "quarterly revenue chart");
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Text: "revenue")));

        await lib.RenameAsync(item.Id, "Renamed: shot?");
        var renamed = (await lib.GetAsync(item.Id))!;
        Assert.Equal("Renamed_shot", renamed.Name);
        Assert.True(File.Exists(renamed.Path));
        Assert.False(File.Exists(item.Path));
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Text: "renamed")));
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Text: "revenue"))); // OCR survives rename

        await lib.DeleteAsync(new[] { item.Id }, toRecycleBin: false);
        Assert.Null(await lib.GetAsync(item.Id));
        Assert.False(File.Exists(renamed.Path));
        Assert.False(File.Exists(renamed.ThumbnailPath));
        Assert.Contains(LibraryChangeKind.Added, events);
        Assert.Contains(LibraryChangeKind.Updated, events);
        Assert.Contains(LibraryChangeKind.Removed, events);
    }

    [Fact]
    public async Task Delete_ToRecycleBin_RemovesFileAndRow()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var item = await h.Service.AddCaptureAsync(Samples.Doc(), Samples.Image(30, 30));
        await h.Service.DeleteAsync(new[] { item.Id }, toRecycleBin: true);
        Assert.False(File.Exists(item.Path));
        Assert.Null(await h.Service.GetAsync(item.Id));
    }

    [Fact]
    public async Task Update_ReindexesAfterEdit()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var doc = Samples.Doc();
        var item = await lib.AddCaptureAsync(doc, Samples.Image(100, 100));
        Assert.Empty(await lib.QueryAsync(new LibraryQuery(Text: "zebra")));

        doc.Pages[0].Annotations.Add(new CalloutAnnotation { Start = new Point2(1, 1), End = new Point2(50, 50), Text = Shotwise.Core.Annotations.RichText.FromPlain("zebra crossing") });
        await new Shotwise.Core.Serialization.ShotxSerializer().SaveAsync(doc, item.Path, Samples.Image(100, 100));
        await lib.UpdateAsync(item, doc);
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Text: "zebra")));
    }

    [Fact]
    public async Task ImportFile_CopiesIntoLibrary_AndIndexesPng()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var src = Path.Combine(h.Base, "outside.png");
        File.WriteAllBytes(src, Samples.Png(64, 48));
        var item = await h.Service.ImportFileAsync(src);
        Assert.True(File.Exists(src)); // original untouched
        Assert.StartsWith(h.Root, item.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, item.Width);
        Assert.Equal(48, item.Height);
        Assert.Equal(MediaKind.Image, item.Kind);
        Assert.True(File.Exists(item.ThumbnailPath));
        Assert.Single(await h.Service.QueryAsync(new LibraryQuery(Text: "outside")));

        await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.ImportFileAsync(Path.Combine(h.Base, "missing.png")));
        var txt = Path.Combine(h.Base, "note.txt");
        File.WriteAllText(txt, "x");
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.ImportFileAsync(txt));
        Assert.Equal(ErrorCode.UnsupportedFormat, ex.Code);
    }

    [Fact]
    public async Task AddVideo_MovesFileIntoLibrary_WithSidecarMetadata()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var tmp = Path.Combine(h.Base, "rec.mp4");
        File.WriteAllBytes(tmp, new byte[2048]);
        var meta = DocumentMetadata.Create() with { AppName = "Player", WindowTitle = "Clip" };
        var item = await h.Service.AddVideoAsync(new VideoCaptureResult(tmp, TimeSpan.FromSeconds(12.5), new Size2(1280, 720), meta, false));
        Assert.False(File.Exists(tmp));
        Assert.True(File.Exists(item.Path));
        Assert.EndsWith(".mp4", item.Path);
        Assert.Equal(MediaKind.Video, item.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12.5), item.Duration);
        Assert.Equal(1280, item.Width);
        Assert.Equal("Player", item.AppName);
        Assert.Single(await h.Service.QueryAsync(new LibraryQuery(Kind: MediaKind.Video)));

        var gifSrc = Path.Combine(h.Base, "anim.gif");
        File.WriteAllBytes(gifSrc, new byte[100]);
        var gif = await h.Service.AddVideoAsync(new VideoCaptureResult(gifSrc, TimeSpan.FromSeconds(2), new Size2(100, 100), meta, true));
        Assert.Equal(MediaKind.Gif, gif.Kind);
    }

    [Fact]
    public async Task Query_SortsAndPages()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var names = new[] { "delta", "alpha", "charlie", "bravo" };
        for (var i = 0; i < names.Length; i++)
            await lib.AddCaptureAsync(Samples.Doc(app: names[i], when: baseTime.AddDays(i), w: 100 + i * 50, h: 100), Samples.Image(40, 40));

        var newest = await lib.QueryAsync(new LibraryQuery());
        Assert.Equal(new[] { "bravo", "charlie", "alpha", "delta" }, newest.Select(i => i.AppName));
        var oldest = await lib.QueryAsync(new LibraryQuery(Sort: LibrarySort.OldestFirst));
        Assert.Equal("delta", oldest[0].AppName);
        var byName = await lib.QueryAsync(new LibraryQuery(Sort: LibrarySort.NameAscending));
        Assert.True(byName.Select(i => i.Name).SequenceEqual(byName.Select(i => i.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
        var page = await lib.QueryAsync(new LibraryQuery(Sort: LibrarySort.OldestFirst, Skip: 1, Take: 2));
        Assert.Equal(new[] { "alpha", "charlie" }, page.Select(i => i.AppName));
        var ranged = await lib.QueryAsync(new LibraryQuery(From: DateOnly.FromDateTime(baseTime.AddDays(1).LocalDateTime), To: DateOnly.FromDateTime(baseTime.AddDays(2).LocalDateTime)));
        Assert.Equal(2, ranged.Count);
    }

    [Fact]
    public async Task GroupCounts_ByDateAppWebsiteTagKind()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var d1 = new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2025, 12, 31, 10, 0, 0, TimeSpan.Zero);
        var a = await lib.AddCaptureAsync(Samples.Doc(app: "Chrome", url: "https://a.example.com/x", tags: new[] { "bug" }, when: d1), Samples.Image(20, 20));
        await lib.AddCaptureAsync(Samples.Doc(app: "chrome", url: "https://a.example.com/y", tags: new[] { "bug", "ui" }, when: d1.AddMinutes(5)), Samples.Image(20, 20));
        await lib.AddCaptureAsync(Samples.Doc(app: null, url: null, when: d2), Samples.Image(20, 20));

        var byApp = await lib.GroupCountsAsync(LibraryGroupBy.App);
        Assert.Contains(byApp, x => x.Key.Equals("chrome", StringComparison.OrdinalIgnoreCase) && x.Count == 2);
        Assert.Contains(byApp, x => x.Key == "" && x.Count == 1);
        var bySite = await lib.GroupCountsAsync(LibraryGroupBy.Website);
        Assert.Contains(bySite, x => x.Key == "a.example.com" && x.Count == 2);
        Assert.Contains(bySite, x => x.Key == "" && x.Count == 1);
        var byTag = await lib.GroupCountsAsync(LibraryGroupBy.Tag);
        Assert.Contains(byTag, x => x.Key == "bug" && x.Count == 2);
        Assert.Contains(byTag, x => x.Key == "ui" && x.Count == 1);
        var byYear = await lib.GroupCountsAsync(LibraryGroupBy.Year);
        Assert.Equal(2, byYear.Count);
        var byMonth = await lib.GroupCountsAsync(LibraryGroupBy.Month);
        Assert.Equal(2, byMonth.Count);
        var byKind = await lib.GroupCountsAsync(LibraryGroupBy.Kind);
        Assert.Equal(("Image", 3), byKind.Single());
        var byDate = await lib.GroupCountsAsync(LibraryGroupBy.Date);
        Assert.Equal(2, byDate.Count);

        // Filters line up with the groups.
        Assert.Equal(2, (await lib.QueryAsync(new LibraryQuery(App: "Chrome"))).Count);
        Assert.Equal(2, (await lib.QueryAsync(new LibraryQuery(Website: "a.example.com"))).Count);
        Assert.Single(await lib.QueryAsync(new LibraryQuery(App: "")));
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Tag: "ui")));
        Assert.NotNull(a);
    }

    [Fact]
    public async Task Tags_AddRemoveAndList()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var a = await lib.AddCaptureAsync(Samples.Doc(), Samples.Image(20, 20));
        var b = await lib.AddCaptureAsync(Samples.Doc(app: "X"), Samples.Image(20, 20));
        await lib.AddTagsAsync(new[] { a.Id, b.Id }, new[] { "  Alpha ", "beta", "ALPHA", "" });
        Assert.Equal(new[] { "Alpha", "beta" }, await lib.AllTagsAsync());
        Assert.Equal(2, (await lib.QueryAsync(new LibraryQuery(Tag: "alpha"))).Count);
        await lib.RemoveTagsAsync(new[] { a.Id }, new[] { "alpha" });
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Tag: "alpha")));
        Assert.Equal(new[] { "beta" }, (await lib.GetAsync(a.Id))!.Tags);
        // Tag text is searchable.
        Assert.Equal(2, (await lib.QueryAsync(new LibraryQuery(Text: "beta"))).Count);
    }

    [Fact]
    public async Task ShareHistory_PerItemAndGlobal()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var a = await lib.AddCaptureAsync(Samples.Doc(), Samples.Image(20, 20));
        var b = await lib.AddCaptureAsync(Samples.Doc(app: "Y"), Samples.Image(20, 20));
        var t = DateTimeOffset.Now;
        await lib.AddShareHistoryAsync(a.Id, new ShareHistoryEntry(t, "imgur", "Imgur", "https://i.example/1", "Shot"));
        await lib.AddShareHistoryAsync(a.Id, new ShareHistoryEntry(t.AddMinutes(1), "file", "File", null, null));
        await lib.AddShareHistoryAsync(b.Id, new ShareHistoryEntry(t.AddMinutes(2), "slack", "Slack", "https://s.example/2", null));
        var forA = await lib.GetShareHistoryAsync(a.Id);
        Assert.Equal(2, forA.Count);
        Assert.Equal("file", forA[0].DestinationId); // newest first
        Assert.Equal("https://i.example/1", forA[1].Link);
        Assert.Equal(3, (await lib.GetShareHistoryAsync()).Count);
    }
}
