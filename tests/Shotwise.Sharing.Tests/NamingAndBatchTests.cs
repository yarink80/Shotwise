using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Naming;

namespace Shotwise.Sharing.Tests;

public class NamingTests
{
    private static readonly NamingContext Ctx = new()
    {
        Timestamp = new DateTimeOffset(2026, 10, 17, 14, 5, 9, TimeSpan.Zero),
        App = "Notepad", Window = "Untitled - Notepad", Title = "My title", Preset = "Quick", Width = 800, Height = 600, Counter = 7,
    };

    private static NamingSettings Settings(string pattern, string prefix = "") => new() { Pattern = pattern, Prefix = prefix };

    [Fact]
    public void DateTimeTokensUseSettingsFormatsAndCustomFormats()
    {
        var s = Settings("{date}_{time}");
        Assert.Equal("2026-10-17_14-05-09", NamingEngine.Expand(s.Pattern, Ctx, s, false));
        Assert.Equal("20261017-1405", NamingEngine.Expand("{date:yyyyMMdd}-{time:HHmm}", Ctx, s, false));
    }

    [Fact]
    public void AppWindowTitlePresetAndSizeTokens()
    {
        var s = Settings("{app}|{window}|{title}|{preset}|{width}x{height}");
        Assert.Equal("Notepad|Untitled - Notepad|My title|Quick|800x600", NamingEngine.Expand(s.Pattern, Ctx, s, false));
    }

    [Fact]
    public void CounterUsesSettingsDigitsOrTokenDigits()
    {
        var s = Settings("{counter}");
        s.CounterDigits = 4;
        Assert.Equal("0007", NamingEngine.Expand("{counter}", Ctx, s, false));
        Assert.Equal("007", NamingEngine.Expand("{counter:3}", Ctx, s, false));
        Assert.Equal("7", NamingEngine.Expand("{counter:1}", Ctx, s, false));
    }

    [Fact]
    public void DoNotIncludeCaptureSourceDropsAppWindowAndTitleAndTidiesSeparators()
    {
        var s = Settings("{app}_{window}_{date}", "Shot_");
        var name = NamingEngine.BuildName(Ctx, s, omitCaptureSource: true);
        Assert.Equal("Shot_2026-10-17", name);
        Assert.DoesNotContain("Notepad", name);
    }

    [Fact]
    public void UnknownTokensAreKeptAndInvalidCharactersReplaced()
    {
        var s = Settings("{bogus}");
        Assert.Equal("{bogus}", NamingEngine.Expand(s.Pattern, Ctx, s, false));
        var ctx = Ctx with { Window = "a/b:c*d?" };
        var name = NamingEngine.BuildName(ctx, Settings("{window}"), false);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void ReservedDeviceNamesAndEmptyNamesAreHandled()
    {
        Assert.Equal("_CON", NamingEngine.Sanitize("CON"));
        Assert.Equal("Shotwise", NamingEngine.BuildName(new NamingContext(), Settings("{app}"), false));
    }

    [Fact]
    public void LongNamesAreShortened()
    {
        var name = NamingEngine.Sanitize(new string('a', 400));
        Assert.True(name.Length <= NamingEngine.MaxBaseNameLength);
    }

    [Fact]
    public void UniquePathAppendsNumberUnlessOverwrite()
    {
        using var dir = new TempDir();
        var first = dir.File("shot.png");
        Assert.Equal(first, NamingEngine.UniquePath(dir.Path, "shot", "png", overwrite: true));
        Assert.Equal(Path.Combine(dir.Path, "shot (2).png"), NamingEngine.UniquePath(dir.Path, "shot", ".png"));
    }

    [Fact]
    public void CounterIsPersistedThroughSettings()
    {
        using var dir = new TempDir();
        var svc = new SettingsService(Path.Combine(dir.Path, "settings.json"));
        var counter = new NamingCounter(svc);
        Assert.Equal(1, counter.Next());
        Assert.Equal(2, counter.Next());
        Assert.Equal(3, svc.Current.Share.Naming.NextCounter);
        Assert.Equal(3, new NamingCounter(svc).Next());
    }
}

public class MaterializeTests
{
    private static (ShareService Service, FakeCodec Codec, SettingsService Settings) Make(TempDir dir, Action<ShareSettings>? tweak = null)
    {
        var settings = new SettingsService(Path.Combine(dir.Path, "settings.json"));
        settings.Update(s =>
        {
            s.Share.Naming.Prefix = "T_";
            s.Share.Naming.Pattern = "{app}_{counter}";
            tweak?.Invoke(s.Share);
        });
        var codec = new FakeCodec();
        var svc = new ShareService(Array.Empty<IShareDestination>(), new AppPaths(dir.Path), settings, renderer: new FakeRenderer(), codec: codec);
        return (svc, codec, settings);
    }

    private static ShotDocument Doc(int pages = 1)
    {
        var doc = ShotDocument.Create(new Size2(10, 8), Argb.White);
        doc.Metadata = doc.Metadata with { AppName = "Notepad", WindowTitle = "Doc" };
        for (var i = 1; i < pages; i++) doc.Pages.Add(new Page { CanvasSize = new Size2(10, 8) });
        return doc;
    }

    [Fact]
    public async Task ExportsSingleFileWithAutomaticName()
    {
        using var dir = new TempDir();
        var (svc, codec, _) = Make(dir);
        var files = await svc.MaterializeAsync(Doc(), "png", null, CancellationToken.None);
        var f = Assert.Single(files);
        Assert.Equal("T_Notepad_001.png", Path.GetFileName(f.Path));
        Assert.Equal("png", f.Format);
        Assert.Equal(10, f.Width);
        Assert.True(File.Exists(f.Path));
        Assert.Equal(ImageFormat.Png, codec.Saved.Single().Format);
        Assert.StartsWith(Path.Combine(dir.Path, "Temp", "share"), f.Path);
    }

    [Fact]
    public async Task OmitsCaptureSourceWhenPreferenceIsSet()
    {
        using var dir = new TempDir();
        var (svc, _, _) = Make(dir, s => s.DoNotIncludeCaptureSourceInNames = true);
        var f = Assert.Single(await svc.MaterializeAsync(Doc(), "jpg", null, CancellationToken.None));
        Assert.Equal("T_001.jpg", Path.GetFileName(f.Path));
        Assert.Equal("jpg", f.Format);
        Assert.DoesNotContain("Doc", f.Title); // title falls back to the generated name, not the window title
    }

    [Fact]
    public async Task MultiPageDocumentBecomesOneMultipageFileForPdf()
    {
        using var dir = new TempDir();
        var (svc, codec, _) = Make(dir);
        var files = await svc.MaterializeAsync(Doc(3), "pdf", null, CancellationToken.None);
        Assert.Single(files);
        Assert.Equal(3, codec.SavedMulti.Single().Pages);
        Assert.Equal("pdf", files[0].Format);
    }

    [Fact]
    public async Task MultiPageDocumentBecomesOneFilePerPageForPng()
    {
        using var dir = new TempDir();
        var (svc, _, _) = Make(dir);
        var files = await svc.MaterializeAsync(Doc(2), "png", null, CancellationToken.None);
        Assert.Equal(2, files.Count);
        Assert.EndsWith("_p1.png", files[0].Path);
        Assert.EndsWith("_p2.png", files[1].Path);
    }

    [Fact]
    public async Task UnsupportedFormatThrows()
    {
        using var dir = new TempDir();
        var (svc, _, _) = Make(dir);
        await Assert.ThrowsAsync<Shotwise.Core.ShotwiseException>(() => svc.MaterializeAsync(Doc(), "xyz", null, CancellationToken.None));
    }
}

public class BatchExportTests
{
    [Fact]
    public async Task ConvertsFilesAppliesEffectsAndNamesOutput()
    {
        using var dir = new TempDir();
        var a = dir.File("in/alpha.png");
        var b = dir.File("in/beta.png");
        var codec = new FakeCodec();
        var fx = new FakeEffects();
        var exporter = new BatchExporter(codec, fx);
        var progress = new List<double>();
        var result = await exporter.ExportAsync(new BatchExportOptions(new[] { a, b }, Path.Combine(dir.Path, "out"), ImageFormat.Jpeg,
            Effects: new EffectSpec[] { EffectDefaults.Border, EffectDefaults.Shadow with { Enabled = false } }, NamePattern: "conv_{original}"),
            new Progress<double>(progress.Add), CancellationToken.None);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(2, fx.Applied); // one enabled effect per file
        Assert.Equal(new[] { "conv_alpha.jpg", "conv_beta.jpg" }, result.Items.Select(i => Path.GetFileName(i.Output!)).ToArray());
        Assert.All(codec.Saved, s => Assert.Equal(ImageFormat.Jpeg, s.Format));
    }

    [Fact]
    public async Task KeepsOriginalNameAndNeverOverwritesByDefault()
    {
        using var dir = new TempDir();
        var a = dir.File("in/alpha.png");
        var outDir = Path.Combine(dir.Path, "out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "alpha.png"), "existing");
        var exporter = new BatchExporter(new FakeCodec());
        var result = await exporter.ExportAsync(new BatchExportOptions(new[] { a }, outDir, ImageFormat.Png), null, CancellationToken.None);
        Assert.Equal("alpha (2).png", Path.GetFileName(result.Items[0].Output!));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(outDir, "alpha.png")));
    }

    [Fact]
    public async Task SkipModeSkipsExistingAndFailuresAreReportedPerFile()
    {
        using var dir = new TempDir();
        var a = dir.File("in/alpha.png");
        var outDir = Path.Combine(dir.Path, "out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "alpha.png"), "existing");
        var exporter = new BatchExporter(new ThrowingCodec());
        var result = await exporter.ExportAsync(new BatchExportOptions(new[] { a }, outDir, ImageFormat.Png, Overwrite: BatchOverwriteMode.Skip), null, CancellationToken.None);
        Assert.Equal(1, result.Skipped);

        var failing = await exporter.ExportAsync(new BatchExportOptions(new[] { a }, outDir, ImageFormat.Jpeg), null, CancellationToken.None);
        Assert.Equal(1, failing.Failed);
        Assert.False(string.IsNullOrEmpty(failing.Items[0].Error));
    }

    [Fact]
    public async Task CancellationStopsTheBatch()
    {
        using var dir = new TempDir();
        var files = Enumerable.Range(0, 3).Select(i => dir.File($"in/{i}.png")).ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exporter = new BatchExporter(new FakeCodec());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(new BatchExportOptions(files, Path.Combine(dir.Path, "out"), ImageFormat.Png), null, cts.Token));
    }

    private sealed class ThrowingCodec : IImageCodec
    {
        public BgraImage Decode(ReadOnlySpan<byte> data) => throw new InvalidDataException("bad");
        public BgraImage Load(string path) => throw new InvalidDataException("bad");
        public IReadOnlyList<BgraImage> LoadAllPages(string path) => throw new InvalidDataException("bad image");
        public byte[] Encode(BgraImage img, ImageFormat fmt, EncodeOptions? o = null) => throw new NotSupportedException();
        public void Save(BgraImage img, string path, ImageFormat fmt, EncodeOptions? o = null) => throw new NotSupportedException();
        public void SaveMultipage(IReadOnlyList<BgraImage> pages, string path, ImageFormat fmt, EncodeOptions? o = null) => throw new NotSupportedException();
        public IReadOnlyList<ImageFormatInfo> SupportedFormats => Array.Empty<ImageFormatInfo>();
    }
}
