using System.IO.Compression;
using System.Text;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Library.Stamps;
using Shotwise.Library.Templates;

namespace Shotwise.Library.Tests;

public class StampAndTemplateTests
{
    private const string Svg = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='10' height='10'/></svg>";

    private static void WritePack(string root, string folder, string packageInfo, params string[] files)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, StampCatalog.PackageInfoFile), packageInfo);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), Svg);
    }

    private static (StampCatalog Cat, string Builtin, string User) Setup(TempDir t)
    {
        var builtin = t.Sub("content\\stamps");
        var user = t.Sub("appdata\\stamps");
        WritePack(builtin, "arrows", """
            { "version": 1, "assetCategory": "stamps", "category": { "en": "Arrows", "de": "Pfeile", "fr": "Fleches" },
              "stamps": [
                { "filename": "arrow-right.svg", "keywords": { "en": ["arrow", "right", "direction"], "de": ["pfeil", "rechts"] } },
                { "filename": "arrow-left.svg",  "keywords": { "en": ["arrow", "left"] } },
                { "filename": "missing.svg" },
                { "filename": "../escape.svg" }
              ] }
            """, "arrow-right.svg", "arrow-left.svg");
        WritePack(builtin, "symbols", """
            { "version": 1, "category": "Symbols",
              "stamps": [ { "filename": "check.svg", "name": { "en": "Check mark", "de": "Haken" }, "keywords": { "en": "tick, done, ok", "de": ["erledigt", "häkchen"] } } ] }
            """, "check.svg");
        File.WriteAllText(Path.Combine(builtin, "arrows", "..", "escape.svg"), Svg); // exists but must be ignored (outside the pack)
        return (new StampCatalog(builtin, user), builtin, user);
    }

    [Fact]
    public async Task Categories_ListLocalizedNamesAndCounts()
    {
        using var t = new TempDir();
        var (cat, _, _) = Setup(t);
        var cats = await cat.GetCategoriesAsync();
        Assert.Equal(new[] { "arrows", "symbols" }, cats.Select(c => c.Id).OrderBy(x => x));
        Assert.Equal(2, cats.First(c => c.Id == "arrows").Count); // missing + traversal entries dropped
        Assert.All(cats, c => Assert.False(c.IsUser));
        Assert.Equal("Symbols", cats.First(c => c.Id == "symbols").Name); // plain string category = "en"
    }

    [Fact]
    public async Task Search_UsesLocalizedKeywordsWithCultureFallback()
    {
        using var t = new TempDir();
        var (cat, _, _) = Setup(t);

        // Exact culture, neutral fallback (de-AT -> de), and English fallback for stamps without German keywords.
        // ("pfeil" is a German keyword of arrow-right and also part of the German category name "Pfeile", so
        // arrow-left matches too, but ranks lower.)
        foreach (var culture in new[] { "de-DE", "de-AT", "de" })
        {
            var r = (await cat.SearchAsync("pfeil", null, culture)).Select(s => s.Id).ToList();
            Assert.Equal(new[] { "arrows/arrow-right", "arrows/arrow-left" }, r);
        }
        Assert.Single(await cat.SearchAsync("rechts", null, "de-AT"));
        var arrowDe = await cat.SearchAsync("arrow", null, "de-DE"); // English keywords still match (fallback)
        Assert.Equal(2, arrowDe.Count);
        // Unknown culture falls back to English.
        Assert.Equal(2, (await cat.SearchAsync("arrow", null, "xx-YY")).Count);
        Assert.Empty(await cat.SearchAsync("pfeil", null, "xx-YY")); // German keywords are not searched for other cultures
        // French has category name only: keywords fall back to English.
        Assert.Single(await cat.SearchAsync("direction", null, "fr-FR"));
        // Diacritics are folded; keyword lists given as strings are split.
        Assert.Single(await cat.SearchAsync("hakchen", null, "de-DE"));
        Assert.Single(await cat.SearchAsync("häkchen", null, "de-DE"));
        Assert.Single(await cat.SearchAsync("tick", "symbols", "en-US"));
        Assert.Single(await cat.SearchAsync("done", null, "en"));
        // Localized display name + keywords come back resolved for the culture.
        var haken = (await cat.SearchAsync("haken", null, "de-DE")).Single();
        Assert.Equal("Haken", haken.Name);
        Assert.Contains("erledigt", haken.Keywords);
        Assert.Equal("Check mark", (await cat.SearchAsync("check", null, "en-GB")).Single().Name);
        // Name / file-stem match without keywords; multiple tokens are ANDed; category filter applies.
        Assert.Single(await cat.SearchAsync("arrow left", null, "en"));
        Assert.Empty(await cat.SearchAsync("arrow", "symbols", "en"));
        // Exact keyword outranks partial match.
        Assert.Equal("arrows/arrow-right", (await cat.SearchAsync("right", null, "en")).First().Id);
        // Empty query lists everything (optionally per category).
        Assert.Equal(3, (await cat.SearchAsync("", null, "en")).Count);
        Assert.Equal(2, (await cat.SearchAsync("", "arrows", "en")).Count);
        Assert.Equal("image/svg+xml", haken.MediaType);
    }

    [Fact]
    public async Task Load_ReturnsBytesAndRejectsUnknownIds()
    {
        using var t = new TempDir();
        var (cat, _, _) = Setup(t);
        Assert.Equal(Svg, Encoding.UTF8.GetString(await cat.LoadAsync("arrows/arrow-left")));
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => cat.LoadAsync("arrows/../../../etc/passwd"));
        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public async Task Favorites_PersistAndAreSearchable()
    {
        using var t = new TempDir();
        var (cat, builtin, user) = Setup(t);
        var changed = 0;
        cat.Changed += (_, _) => changed++;
        cat.ToggleFavorite("symbols/check");
        cat.ToggleFavorite("nope/nothing"); // ignored
        Assert.Equal(1, changed);
        var fav = await cat.SearchAsync("", StampCatalog.FavoritesCategoryId, "en");
        Assert.Equal("symbols/check", fav.Single().Id);
        Assert.True(fav.Single().IsFavorite);

        var cat2 = new StampCatalog(builtin, user); // new instance reads the persisted file
        Assert.True((await cat2.SearchAsync("check", null, "en")).Single().IsFavorite);
        cat2.ToggleFavorite("symbols/check");
        Assert.Empty(await cat2.SearchAsync("", StampCatalog.FavoritesCategoryId, "en"));
    }

    private static string MakePack(string path, string info, params (string Name, string Data)[] files)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Put(string n, string d) { var e = zip.CreateEntry(n); using var w = new StreamWriter(e.Open(), new UTF8Encoding(false)); w.Write(d); }
        if (info is not null) Put("packageinfo.json", info);
        foreach (var (n, d) in files) Put(n, d);
        return path;
    }

    [Fact]
    public async Task ImportPack_AddsUserCategory_AndReimportPreservesExisting()
    {
        using var t = new TempDir();
        var (cat, _, user) = Setup(t);
        var pack = MakePack(t.Sub("stickers.shotstamps"), """
            { "version": 1, "id": "stickers", "category": { "en": "Stickers", "es": "Pegatinas" },
              "stamps": [ { "filename": "star.svg", "keywords": { "en": ["star"], "es": ["estrella"] } },
                          { "filename": "evil.exe", "keywords": { "en": ["evil"] } } ] }
            """, ("star.svg", Svg), ("evil.exe", "MZ"));
        var changed = 0;
        cat.Changed += (_, _) => changed++;
        await cat.ImportPackAsync(pack);
        Assert.True(changed > 0);
        var cats = await cat.GetCategoriesAsync();
        var c = cats.Single(x => x.IsUser);
        Assert.Equal("user.stickers", c.Id);
        Assert.Equal(1, c.Count);
        Assert.Equal("user.stickers/star", (await cat.SearchAsync("estrella", null, "es-ES")).Single().Id);
        Assert.True((await cat.SearchAsync("star", null, "en")).Single().IsUser);
        Assert.False(File.Exists(Path.Combine(user, "stickers", "evil.exe")));
        Assert.Equal(Svg, Encoding.UTF8.GetString(await cat.LoadAsync("user.stickers/star")));

        // Survives restart; reimport creates a separate pack and keeps user additions.
        var again = new StampCatalog(t.Sub("content\\stamps"), user);
        Assert.Equal(4, (await again.SearchAsync("", null, "en")).Count);
        File.WriteAllText(Path.Combine(user, "stickers", "user-added.svg"), Svg);
        await again.ImportPackAsync(pack);
        Assert.Equal(5, (await again.SearchAsync("", null, "en")).Count);
        Assert.True(File.Exists(Path.Combine(user, "stickers", "user-added.svg")));
    }

    [Fact]
    public async Task ImportPack_RejectsMaliciousAndMalformedPacks()
    {
        using var t = new TempDir();
        var (cat, _, user) = Setup(t);
        // Entry name traversal in the zip itself.
        var traversal = MakePack(t.Sub("t1.shotstamps"), """{ "stamps": [ { "filename": "a.svg" } ] }""", ("a.svg", Svg), ("../../evil.svg", Svg));
        Assert.Equal(ErrorCode.PathTraversal, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportPackAsync(traversal))).Code);
        // No packageinfo.json.
        var none = MakePack(t.Sub("t2.shotstamps"), null!, ("a.svg", Svg));
        Assert.Equal(ErrorCode.StampImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportPackAsync(none))).Code);
        // packageinfo points outside the pack: nothing usable remains.
        var escape = MakePack(t.Sub("t3.shotstamps"), """{ "stamps": [ { "filename": "../x.svg" }, { "filename": "C:/x.svg" } ] }""", ("x.svg", Svg));
        Assert.Equal(ErrorCode.StampImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportPackAsync(escape))).Code);
        // Corrupt JSON and non-zip.
        var badJson = MakePack(t.Sub("t4.shotstamps"), "{ nope", ("a.svg", Svg));
        Assert.Equal(ErrorCode.StampImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportPackAsync(badJson))).Code);
        var junk = t.Sub("t5.shotstamps");
        File.WriteAllText(junk, "not a zip");
        Assert.Equal(ErrorCode.StampImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportPackAsync(junk))).Code);
        // Newer version.
        var newer = MakePack(t.Sub("t6.shotstamps"), """{ "version": 9, "stamps": [ { "filename": "a.svg" } ] }""", ("a.svg", Svg));
        Assert.Equal(ErrorCode.NewerFormatVersion, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportPackAsync(newer))).Code);

        Assert.False(Directory.Exists(user) && Directory.GetDirectories(user).Any());
        Assert.False(File.Exists(t.Sub("evil.svg")));
        Assert.Single((await cat.GetCategoriesAsync()).Where(c => c.Id == "arrows"));
    }

    [Fact]
    public async Task AddUserStamp_CreatesMyStampsCategory()
    {
        using var t = new TempDir();
        var (cat, _, user) = Setup(t);
        var info = await cat.AddUserStampAsync(Encoding.UTF8.GetBytes(Svg), "image/svg+xml", "My Logo");
        Assert.True(info.IsUser);
        Assert.Equal("user.my-stamps/my-logo", info.Id);
        Assert.Equal("My Logo", info.Name);
        Assert.Equal("My Stamps", (await cat.GetCategoriesAsync()).Single(c => c.IsUser).Name);
        Assert.Single(await cat.SearchAsync("logo", null, "en"));
        var second = await cat.AddUserStampAsync(Encoding.UTF8.GetBytes(Svg), "image/svg+xml", "My Logo");
        Assert.NotEqual(info.Id, second.Id);
        Assert.Equal(2, (await new StampCatalog(t.Sub("content\\stamps"), user).SearchAsync("logo", null, "en")).Count);
        await Assert.ThrowsAsync<ShotwiseException>(() => cat.AddUserStampAsync(Array.Empty<byte>(), "image/png", "x"));
        await Assert.ThrowsAsync<ShotwiseException>(() => cat.AddUserStampAsync(new byte[] { 1 }, "application/x-msdownload", "x"));
    }

    [Fact]
    public void PackageInfo_ParserIsTolerantAndWritesCanonicalForm()
    {
        var pkg = StampPackageInfo.Parse(Encoding.UTF8.GetBytes("""
            { "Version": 1, "AssetCategory": "stamps", "Category": { "en-US": "Arrows" },
              // comments allowed
              "Stamps": [ { "Filename": "a.svg", "Keywords": ["x", "y"] }, 5, { "filename": "b.svg", "keywords": "p q r" } ], }
            """));
        Assert.Equal("Arrows", pkg.Category["en-US"]);
        Assert.Equal(2, pkg.Stamps.Count);
        Assert.Equal(new[] { "x", "y" }, pkg.Stamps[0].Keywords["en"]);
        Assert.Equal(new[] { "p", "q", "r" }, pkg.Stamps[1].Keywords["en"]);
        var json = System.Text.Json.JsonSerializer.Serialize(pkg.ToJsonModel(), Shotwise.Core.Serialization.ShotwiseJson.Indented);
        Assert.Contains("\"filename\": \"a.svg\"", json);
        Assert.Contains("\"assetCategory\": \"stamps\"", json);
        Assert.Equal(2, StampPackageInfo.Parse(Encoding.UTF8.GetBytes(json)).Stamps.Count);
    }

    // ------------------------------------------------------------------ templates

    private static TemplateDefinition Template(string id, string name)
    {
        var t = new TemplateDefinition { Id = id, Name = name, Category = "Docs", MaxImages = 1 };
        t.LocalizedNames["de"] = name + " (de)";
        t.Keywords.Add("test");
        var page = new Page { CanvasSize = new Size2(400, 300) };
        var section = new TemplateSection { Type = SectionType.Body, Bounds = new Rect2(0, 0, 400, 300) };
        section.Graphics.Add(new DropZoneAnnotation { Start = new Point2(10, 10), End = new Point2(200, 100), SectionId = section.Id });
        page.Sections.Add(section);
        t.Prototype = page;
        t.Assets.Add(new EmbeddedAsset(AssetId.New(), "image/png", Samples.Png(4, 4)));
        return t;
    }

    [Fact]
    public async Task Templates_BuiltInAndUser_ImportExportDelete()
    {
        using var t = new TempDir();
        var builtinDir = t.Sub("content\\templates");
        Directory.CreateDirectory(builtinDir);
        var builtin = Template("builtin.one", "Built-in one");
        await new TemplateCatalog(builtinDir, t.Sub("tmp-user")).ExportAsync(builtin, Path.Combine(builtinDir, "one.shottemplate"));
        File.WriteAllText(Path.Combine(builtinDir, "broken.shottemplate"), "{ nope");

        var cat = new TemplateCatalog(builtinDir, t.Sub("appdata\\templates"));
        var changed = 0;
        cat.Changed += (_, _) => changed++;
        var list = await cat.GetTemplatesAsync();
        Assert.Single(list);
        Assert.True(list[0].IsBuiltIn);
        Assert.Equal("Built-in one (de)", list[0].DisplayName("de-DE"));

        var userFile = t.Sub("mine.shottemplate");
        await cat.ExportAsync(Template("user.a", "Mine"), userFile);
        await cat.ImportAsync(userFile);
        Assert.True(changed > 0);
        list = await cat.GetTemplatesAsync();
        Assert.Equal(2, list.Count);
        var mine = list.Single(x => !x.IsBuiltIn);
        Assert.Equal("user.a", mine.Id);
        Assert.Single(mine.Assets);
        Assert.Equal(1, mine.Prototype.Sections.Count);
        Assert.Single(mine.Prototype.Sections[0].Graphics);
        Assert.NotNull(mine.Instantiate());

        // Importing again does not overwrite; it creates a copy with a new id.
        await cat.ImportAsync(userFile);
        Assert.Equal(3, (await cat.GetTemplatesAsync()).Count);
        Assert.Equal(3, (await new TemplateCatalog(builtinDir, t.Sub("appdata\\templates")).GetTemplatesAsync()).Count);

        await cat.DeleteAsync("user.a");
        Assert.Equal(2, (await cat.GetTemplatesAsync()).Count);
        await Assert.ThrowsAsync<ShotwiseException>(() => cat.DeleteAsync("builtin.one"));

        // Bad imports.
        var bad = t.Sub("bad.shottemplate");
        File.WriteAllText(bad, "garbage");
        Assert.Equal(ErrorCode.TemplateImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportAsync(bad))).Code);
        File.WriteAllText(bad, "{\"format\":\"shottheme\"}");
        Assert.Equal(ErrorCode.TemplateImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportAsync(bad))).Code);
        File.WriteAllText(bad, "{\"format\":\"shottemplate\",\"version\":42,\"name\":\"x\"}");
        Assert.Equal(ErrorCode.TemplateIncompatible, (await Assert.ThrowsAsync<ShotwiseException>(() => cat.ImportAsync(bad))).Code);
    }
}
