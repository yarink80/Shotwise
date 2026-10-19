using System.IO.Compression;
using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Library;
using Shotwise.Core.Serialization;
using Shotwise.Library.Catalog;

namespace Shotwise.Library.Tests;

public class MoveBackupTests
{
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _a;
        public SyncProgress(Action<double> a) => _a = a;
        public void Report(double value) => _a(value);
    }

    private static async Task<List<LibraryItem>> Seed(LibraryService lib, int n = 3)
    {
        var list = new List<LibraryItem>();
        for (var i = 0; i < n; i++)
            list.Add(await lib.AddCaptureAsync(Samples.Doc(app: "App" + i, tags: new[] { "seed" }, when: new DateTimeOffset(2026, 1 + i, 3, 9, 0, 0, TimeSpan.Zero)), Samples.Image(60, 60)));
        return list;
    }

    // ------------------------------------------------------------------ move

    [Fact]
    public async Task MoveLibrary_CopiesFilesSwitchesRootAndKeepsCatalog()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var items = await Seed(lib);
        await lib.SetFavoriteAsync(items[0].Id, true);
        var newRoot = Path.Combine(h.Base, "moved");
        var reports = new List<double>();
        await lib.MoveLibraryAsync(newRoot, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(Path.GetFullPath(newRoot), lib.LibraryRoot);
        Assert.Equal(1.0, reports.Last());
        Assert.True(reports.SequenceEqual(reports.OrderBy(x => x)));
        var after = await lib.QueryAsync(new LibraryQuery());
        Assert.Equal(3, after.Count);
        Assert.All(after, i => { Assert.StartsWith(newRoot, i.Path, StringComparison.OrdinalIgnoreCase); Assert.True(File.Exists(i.Path)); });
        Assert.True((await lib.GetAsync(items[0].Id))!.IsFavorite);
        Assert.Empty(Directory.Exists(h.Root) ? Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories) : Array.Empty<string>());
        // New captures land in the new root.
        var fresh = await lib.AddCaptureAsync(Samples.Doc(app: "Post"), Samples.Image(20, 20));
        Assert.StartsWith(newRoot, fresh.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MoveLibrary_MergesWithExistingContentAndRenamesConflicts()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var items = await Seed(lib, 2);
        var newRoot = Path.Combine(h.Base, "merge");
        // Pre-existing: an unrelated image, and a *different* file occupying the same relative path as item 0.
        var rel0 = Path.GetRelativePath(h.Root, items[0].Path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(newRoot, rel0))!);
        File.WriteAllBytes(Path.Combine(newRoot, rel0), Samples.Png(10, 10));
        File.WriteAllBytes(Path.Combine(newRoot, "preexisting.png"), Samples.Png(11, 11));

        await lib.MoveLibraryAsync(newRoot, null, CancellationToken.None);
        var all = await lib.QueryAsync(new LibraryQuery());
        Assert.Equal(4, all.Count); // 2 moved + conflicting file kept + pre-existing
        Assert.Contains(all, i => i.Name == "preexisting");
        var moved0 = await lib.GetAsync(items[0].Id);
        Assert.NotNull(moved0);
        Assert.NotEqual(Path.Combine(newRoot, rel0), moved0!.Path, StringComparer.OrdinalIgnoreCase); // renamed, not overwritten
        Assert.Equal(800, moved0.Width);
        Assert.NotNull(await lib.FindByPathAsync(Path.Combine(newRoot, rel0)));            // the pre-existing file is indexed too
        Assert.Equal(Samples.Png(10, 10), File.ReadAllBytes(Path.Combine(newRoot, rel0))); // and was not overwritten
    }

    [Fact]
    public async Task MoveLibrary_CancelledMidway_RollsBackCompletely()
    {
        using var h = new LibraryHarness();
        var lib = h.Service;
        await lib.InitializeAsync();
        var items = await Seed(lib, 4);
        var newRoot = Path.Combine(h.Base, "never");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(v => { if (v > 0) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lib.MoveLibraryAsync(newRoot, progress, cts.Token));

        Assert.Equal(Path.GetFullPath(h.Root), lib.LibraryRoot);
        Assert.All(items, i => Assert.True(File.Exists(i.Path)));
        Assert.True(!Directory.Exists(newRoot) || !Directory.GetFiles(newRoot, "*", SearchOption.AllDirectories).Any());
        Assert.Equal(4, (await lib.QueryAsync(new LibraryQuery())).Count);
        // Library keeps working afterwards.
        await lib.AddCaptureAsync(Samples.Doc(app: "after"), Samples.Image(10, 10));
    }

    [Fact]
    public async Task MoveLibrary_RejectsNestedFolders()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.MoveLibraryAsync(Path.Combine(h.Root, "inner"), null, CancellationToken.None));
        Assert.Equal(ErrorCode.LibraryLocationInvalid, ex.Code);
    }

    // ------------------------------------------------------------------ backup / restore

    [Fact]
    public async Task BackupRestore_RoundTripPreservesFilesAndCatalogState()
    {
        using var dir = new TempDir();
        using var a = new LibraryHarness();
        await a.Service.InitializeAsync();
        var items = await Seed(a.Service, 3);
        await a.Service.AddTagsAsync(new[] { items[1].Id }, new[] { "special" });
        await a.Service.SetFavoriteAsync(items[2].Id, true);
        await a.Service.SetOcrTextAsync(items[0].Id, "restorable ocr text");
        await a.Service.AddShareHistoryAsync(items[1].Id, new ShareHistoryEntry(DateTimeOffset.Now, "imgur", "Imgur", "https://i.example/z", null));
        var vid = Path.Combine(a.Base, "clip.mp4");
        File.WriteAllBytes(vid, new byte[4096]);
        var video = await a.Service.AddVideoAsync(new VideoCaptureResult(vid, TimeSpan.FromSeconds(3), new Size2(640, 360),
            DocumentMetadata.Create() with { AppName = "Recorder" }, false));

        var archive = Path.Combine(dir.Path, "backup.shotarchive");
        var reports = new List<double>();
        await a.Service.BackupAsync(archive, new SyncProgress(reports.Add), CancellationToken.None);
        Assert.True(File.Exists(archive));
        Assert.Equal(1.0, reports.Last());
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("catalog.json"));
            Assert.Equal(4, zip.Entries.Count(e => e.FullName.StartsWith("files/") && !e.FullName.EndsWith(".shotmeta")));
        }

        using var b = new LibraryHarness();
        await b.Service.InitializeAsync();
        await b.Service.RestoreAsync(archive, null, CancellationToken.None);

        var restored = await b.Service.QueryAsync(new LibraryQuery());
        Assert.Equal(4, restored.Count);
        Assert.All(restored, r => Assert.StartsWith(b.Root, r.Path, StringComparison.OrdinalIgnoreCase));
        var r1 = (await b.Service.GetAsync(items[1].Id))!;
        Assert.Contains("special", r1.Tags);
        Assert.True((await b.Service.GetAsync(items[2].Id))!.IsFavorite);
        Assert.Single(await b.Service.QueryAsync(new LibraryQuery(Text: "restorable")));
        Assert.Single(await b.Service.GetShareHistoryAsync(items[1].Id));
        var rv = (await b.Service.GetAsync(video.Id))!;
        Assert.Equal(MediaKind.Video, rv.Kind);
        Assert.Equal(TimeSpan.FromSeconds(3), rv.Duration);
        Assert.Equal("Recorder", rv.AppName);

        // Restoring twice is idempotent (identical files are skipped).
        await b.Service.RestoreAsync(archive, null, CancellationToken.None);
        Assert.Equal(4, (await b.Service.QueryAsync(new LibraryQuery())).Count);
    }

    private static string MakeArchive(string path, IEnumerable<(string Name, byte[] Data)> entries, bool manifest = true, bool catalog = true,
        CatalogFile? cat = null, string manifestFormat = ArchiveManifest.FileFormat)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Put(string name, byte[] data) { var e = zip.CreateEntry(name); using var s = e.Open(); s.Write(data); }
        if (manifest) Put("manifest.json", JsonSerializer.SerializeToUtf8Bytes(new ArchiveManifest { Format = manifestFormat }, ShotwiseJson.Compact));
        if (catalog) Put("catalog.json", JsonSerializer.SerializeToUtf8Bytes(cat ?? new CatalogFile(), ShotwiseJson.Compact));
        foreach (var (n, d) in entries) Put(n, d);
        return path;
    }

    public static IEnumerable<object[]> MaliciousEntryNames() => new[]
    {
        new object[] { "files/../evil.png" },
        new object[] { "files/2026/../../evil.png" },
        new object[] { "../evil.png" },
        new object[] { "/abs/evil.png" },
        new object[] { "files/C:/evil.png" },
        new object[] { "files/2026\\evil.png" },
        new object[] { "files/CON.png" },
        new object[] { "files/a/./b.png" },
        new object[] { "files/trailing-dot." },
    };

    [Theory]
    [MemberData(nameof(MaliciousEntryNames))]
    public async Task Restore_RejectsTraversalAndUnsafeNames_BeforeWritingAnything(string badName)
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var archive = MakeArchive(Path.Combine(h.Base, "evil.shotarchive"), new[]
        {
            ("files/2026/ok.png", Samples.Png()),
            (badName, Samples.Png()),
        });
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(archive, null, CancellationToken.None));
        Assert.True(ex.Code is ErrorCode.PathTraversal or ErrorCode.InvalidFileFormat or ErrorCode.CorruptFile, ex.Code.ToString());
        Assert.False(File.Exists(Path.Combine(h.Base, "evil.png")));
        Assert.False(File.Exists(Path.Combine(h.Base, "lib", "evil.png")));
        Assert.False(File.Exists(Path.Combine(h.Root, "2026", "ok.png"))); // nothing extracted
        Assert.Empty(await h.Service.QueryAsync(new LibraryQuery()));
    }

    [Fact]
    public async Task Restore_RejectsCatalogWithTraversalPaths()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var cat = new CatalogFile();
        cat.Items.Add(new CatalogEntry { Id = Guid.NewGuid(), Path = "../../outside.png", Kind = MediaKind.Image, Name = "x" });
        var archive = MakeArchive(Path.Combine(h.Base, "evil2.shotarchive"), Array.Empty<(string, byte[])>(), cat: cat);
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(archive, null, CancellationToken.None));
        Assert.Equal(ErrorCode.PathTraversal, ex.Code);
    }

    [Fact]
    public async Task Restore_RejectsMissingManifestWrongFormatUnknownEntriesAndNonMedia()
    {
        using var h = new LibraryHarness();
        await h.Service.InitializeAsync();
        var noManifest = MakeArchive(Path.Combine(h.Base, "a.shotarchive"), Array.Empty<(string, byte[])>(), manifest: false);
        Assert.Equal(ErrorCode.InvalidFileFormat, (await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(noManifest, null, CancellationToken.None))).Code);
        var noCatalog = MakeArchive(Path.Combine(h.Base, "b.shotarchive"), Array.Empty<(string, byte[])>(), catalog: false);
        Assert.Equal(ErrorCode.InvalidFileFormat, (await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(noCatalog, null, CancellationToken.None))).Code);
        var wrong = MakeArchive(Path.Combine(h.Base, "c.shotarchive"), Array.Empty<(string, byte[])>(), manifestFormat: "zip");
        Assert.Equal(ErrorCode.InvalidFileFormat, (await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(wrong, null, CancellationToken.None))).Code);
        var stray = MakeArchive(Path.Combine(h.Base, "d.shotarchive"), new[] { ("payload.exe", new byte[10]) });
        Assert.Equal(ErrorCode.InvalidFileFormat, (await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(stray, null, CancellationToken.None))).Code);
        var exe = MakeArchive(Path.Combine(h.Base, "e.shotarchive"), new[] { ("files/2026/run.exe", new byte[10]) });
        Assert.Equal(ErrorCode.PathTraversal, (await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(exe, null, CancellationToken.None))).Code);
        var notZip = Path.Combine(h.Base, "f.shotarchive");
        File.WriteAllText(notZip, "this is not a zip");
        Assert.Equal(ErrorCode.CorruptFile, (await Assert.ThrowsAsync<ShotwiseException>(() => h.Service.RestoreAsync(notZip, null, CancellationToken.None))).Code);
        Assert.Empty(await h.Service.QueryAsync(new LibraryQuery()));
    }

    [Fact]
    public async Task Restore_KeepsExistingDifferentFile_UnderNewName()
    {
        using var a = new LibraryHarness();
        await a.Service.InitializeAsync();
        var items = await Seed(a.Service, 1);
        var archive = Path.Combine(a.Base, "x.shotarchive");
        await a.Service.BackupAsync(archive, null, CancellationToken.None);

        using var b = new LibraryHarness();
        var rel = Path.GetRelativePath(a.Root, items[0].Path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(b.Root, rel))!);
        File.WriteAllBytes(Path.Combine(b.Root, rel), Samples.Png(5, 5)); // same name, different content
        await b.Service.InitializeAsync();
        await b.Service.RestoreAsync(archive, null, CancellationToken.None);
        Assert.Equal(2, (await b.Service.QueryAsync(new LibraryQuery())).Count);
        Assert.Equal(Samples.Png(5, 5), File.ReadAllBytes(Path.Combine(b.Root, rel))); // the existing file was not overwritten
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(Path.Combine(b.Root, rel))!), f => f.Contains("restored"));
    }
}
