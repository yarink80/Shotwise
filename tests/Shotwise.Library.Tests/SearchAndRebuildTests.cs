using Shotwise.Core;
using Shotwise.Core.Library;

namespace Shotwise.Library.Tests;

public class SearchAndRebuildTests
{
    [Fact]
    public async Task Fts_MatchesNameAppWindowUrlOcrCalloutAndTags()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var item = await lib.AddCaptureAsync(Samples.Doc(app: "Photoshop", window: "Invoice Draft", url: "https://billing.contoso.test/inv",
            ocr: "Total amount due 1234", callout: "Click Submit here", tags: new[] { "finance" }), Samples.Image(30, 30));
        await lib.AddCaptureAsync(Samples.Doc(app: "Notepad", window: "Todo", url: null), Samples.Image(30, 30));

        foreach (var term in new[] { "photoshop", "invoice", "billing", "contoso", "amount", "1234", "submit", "finance", "Photo", "inv draft" })
        {
            var res = await lib.QueryAsync(new LibraryQuery(Text: term));
            Assert.True(res.Count == 1 && res[0].Id == item.Id, $"term '{term}' returned {res.Count}");
        }
        Assert.Empty(await lib.QueryAsync(new LibraryQuery(Text: "nonexistentword")));
        // Quote / operator characters must not break the query.
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Text: "\"invoice\" ( * ^ -")));
        // Name (file name) is indexed too.
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Text: item.Name.Split('-').Last())));
        // Combined with other filters.
        Assert.Empty(await lib.QueryAsync(new LibraryQuery(Text: "invoice", App: "Notepad")));
        // Removing the tag removes it from the index.
        await lib.RemoveTagsAsync(new[] { item.Id }, new[] { "finance" });
        Assert.Empty(await lib.QueryAsync(new LibraryQuery(Text: "finance")));
    }

    [Fact]
    public async Task RebuildFromFiles_WhenCatalogMissing_RestoresItemsAndUserState()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var a = await lib.AddCaptureAsync(Samples.Doc(app: "A", callout: "findme callout", tags: new[] { "t1" }), Samples.Image(50, 50));
        var b = await lib.AddCaptureAsync(Samples.Doc(app: "B"), Samples.Image(50, 50));
        await lib.SetFavoriteAsync(b.Id, true);
        await lib.AddTagsAsync(new[] { b.Id }, new[] { "usertag" });
        await lib.SetOcrTextAsync(b.Id, "ocr words here");
        await lib.AddShareHistoryAsync(b.Id, new ShareHistoryEntry(DateTimeOffset.Now, "file", "File", null, null));

        lib = h.Restart(); // Dispose flushes the catalog mirror.
        File.Delete(h.CatalogPath);
        File.Delete(h.CatalogPath + "-wal");
        File.Delete(h.CatalogPath + "-shm");
        await lib.InitializeAsync();

        var all = await lib.QueryAsync(new LibraryQuery());
        Assert.Equal(2, all.Count);
        var a2 = await lib.FindByPathAsync(a.Path);
        var b2 = await lib.FindByPathAsync(b.Path);
        Assert.Equal(a.Id, a2!.Id); // ids restored from the mirror
        Assert.True(b2!.IsFavorite);
        Assert.Contains("usertag", b2.Tags);
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Text: "findme")));      // callout text re-derived from the file
        Assert.Single(await lib.QueryAsync(new LibraryQuery(Text: "ocr words")));   // OCR restored from the mirror
        Assert.Equal(600, a2.Height); // dimensions re-derived from the document (Samples default 800x600)
        Assert.Single(await lib.GetShareHistoryAsync(b.Id));
        Assert.True(File.Exists(a2.ThumbnailPath));
    }

    [Fact]
    public async Task RebuildFromFiles_WithoutMirror_StillIndexesFiles()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var a = await lib.AddCaptureAsync(Samples.Doc(app: "Solo", tags: new[] { "inmeta" }), Samples.Image(50, 50));
        lib = h.Restart();
        File.Delete(h.CatalogPath);
        Directory.Delete(Path.Combine(h.Root, LibraryService.StateFolderName), true);
        await lib.InitializeAsync();
        var a2 = await lib.FindByPathAsync(a.Path);
        Assert.NotNull(a2);
        Assert.Equal("Solo", a2!.AppName);
        Assert.Contains("inmeta", a2.Tags); // tags in document metadata survive
    }

    [Fact]
    public async Task CorruptCatalog_IsQuarantinedAndRebuilt()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        await lib.AddCaptureAsync(Samples.Doc(app: "Keep"), Samples.Image(50, 50));
        lib = h.Restart();
        File.Delete(h.CatalogPath + "-wal");
        File.Delete(h.CatalogPath + "-shm");
        File.WriteAllText(h.CatalogPath, new string('x', 5000)); // not a database
        await lib.InitializeAsync();
        var items = await lib.QueryAsync(new LibraryQuery());
        Assert.Single(items);
        Assert.Equal("Keep", items[0].AppName);
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(h.CatalogPath)!, "library.db.corrupt-*"));
    }

    [Fact]
    public async Task Reconcile_DropsMissingFilesAndAddsNewOnes()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var a = await lib.AddCaptureAsync(Samples.Doc(app: "Gone"), Samples.Image(20, 20));
        File.Delete(a.Path);
        var dropped = Path.Combine(h.Root, "2020", "01");
        Directory.CreateDirectory(dropped);
        File.WriteAllBytes(Path.Combine(dropped, "manual.png"), Samples.Png());
        await lib.ReconcileAsync(null, CancellationToken.None);
        var items = await lib.QueryAsync(new LibraryQuery());
        Assert.Single(items);
        Assert.Equal("manual", items[0].Name);
    }

    [Fact]
    public async Task Watcher_PicksUpExternalAddChangeAndDelete()
    {
        using var h = new LibraryHarness(watcher: true);
        var lib = h.Service;
        await lib.InitializeAsync();
        var added = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lib.Changed += (_, e) => { if (e.Kind == LibraryChangeKind.Added) added.TrySetResult(true); };

        var dir = Path.Combine(h.Root, "2024", "06");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "external.png");
        File.WriteAllBytes(file, Samples.Png(32, 16));

        Assert.True(await Samples.WaitUntil(async () => (await lib.FindByPathAsync(file)) is not null));
        Assert.True(await Task.WhenAny(added.Task, Task.Delay(5000)) == added.Task);
        var item = (await lib.FindByPathAsync(file))!;
        Assert.Equal(32, item.Width);

        // Content change -> re-indexed.
        File.WriteAllBytes(file, Samples.Png(64, 64));
        Assert.True(await Samples.WaitUntil(async () => (await lib.FindByPathAsync(file))?.Width == 64));

        // Rename -> old row gone, new row present.
        var renamed = Path.Combine(dir, "renamed.png");
        File.Move(file, renamed);
        Assert.True(await Samples.WaitUntil(async () => await lib.FindByPathAsync(file) is null && await lib.FindByPathAsync(renamed) is not null));

        // Delete -> row removed.
        File.Delete(renamed);
        Assert.True(await Samples.WaitUntil(async () => await lib.FindByPathAsync(renamed) is null));
        Assert.Empty(await lib.QueryAsync(new LibraryQuery()));

        // Deleting a whole folder removes everything under it.
        var f2 = Path.Combine(dir, "a.png");
        var f3 = Path.Combine(dir, "b.png");
        File.WriteAllBytes(f2, Samples.Png());
        File.WriteAllBytes(f3, Samples.Png(20, 20));
        Assert.True(await Samples.WaitUntil(async () => (await lib.QueryAsync(new LibraryQuery())).Count == 2));
        Directory.Delete(dir, true);
        Assert.True(await Samples.WaitUntil(async () => (await lib.QueryAsync(new LibraryQuery())).Count == 0));
    }

    [Fact]
    public async Task Watcher_IgnoresHiddenAndNonMediaFiles()
    {
        using var h = new LibraryHarness(watcher: true);
        var lib = h.Service;
        await lib.InitializeAsync();
        File.WriteAllText(Path.Combine(h.Root, "readme.txt"), "x");
        File.WriteAllBytes(Path.Combine(h.Root, ".hidden.png"), Samples.Png());
        File.WriteAllBytes(Path.Combine(h.Root, "visible.png"), Samples.Png());
        Assert.True(await Samples.WaitUntil(async () => (await lib.QueryAsync(new LibraryQuery())).Count >= 1));
        await Task.Delay(500);
        var items = await lib.QueryAsync(new LibraryQuery());
        Assert.Equal("visible", items.Single().Name);
    }
}
