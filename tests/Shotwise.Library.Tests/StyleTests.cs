using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using Shotwise.Core.Styles;
using Shotwise.Library.Styles;

namespace Shotwise.Library.Tests;

public class StyleTests
{
    private static readonly ToolKind[] StyledTools =
    {
        ToolKind.Arrow, ToolKind.Line, ToolKind.Shape, ToolKind.Callout, ToolKind.Text, ToolKind.Stamp, ToolKind.Step, ToolKind.Pen,
        ToolKind.Highlighter, ToolKind.Magnify, ToolKind.Blur, ToolKind.Cursor, ToolKind.Fill, ToolKind.Eraser, ToolKind.Selection,
        ToolKind.MagicWand, ToolKind.CutOut, ToolKind.Crop,
    };

    internal static string? FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shotwise.sln"))) dir = dir.Parent;
        return dir is null ? null : Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }

    private static StyleRepository NewRepo(TempDir t, string? content = null)
        => new(content ?? t.Sub("no-content"), t.Sub("appdata"));

    [Fact]
    public void BuiltInThemes_AreFiveWithStylesForEveryStyledTool()
    {
        var themes = BuiltInContent.CreateThemes();
        Assert.Equal(new[] { "Basic", "Bold", "Dark Mode", "Pastel", "High Contrast" }, themes.Select(t => t.Name));
        foreach (var t in themes)
        {
            Assert.True(t.IsBuiltIn);
            Assert.Equal(8, t.ThemeColors.Count);
            Assert.Equal(t.QuickStyles.Count, t.QuickStyles.Select(q => q.Id).Distinct().Count());
            foreach (var tool in StyledTools)
                Assert.True(t.StylesFor(tool).Count() >= 2, $"{t.Name} lacks styles for {tool}");
            Assert.NotEmpty(t.Favorites);
            foreach (var q in t.QuickStyles)
            {
                Assert.True(q.IsBuiltIn);
                if (q.Prototype is null) Assert.NotNull(q.Options); else Assert.Null(q.Options);
            }
            Assert.IsType<ArrowAnnotation>(t.StylesFor(ToolKind.Arrow).First().Prototype);
            Assert.IsType<CalloutAnnotation>(t.StylesFor(ToolKind.Callout).First().Prototype);
            Assert.All(t.StylesFor(ToolKind.Callout).Select(q => ((CalloutAnnotation)q.Prototype!).ShapePresetId), id => Assert.Contains(id, CalloutPresets.All));
        }
        Assert.Single(themes, t => t.IsDefault);
        // High-contrast really is high contrast: callout text vs fill luminance difference is large.
        var hc = (CalloutAnnotation)themes[4].StylesFor(ToolKind.Callout).First().Prototype!;
        Assert.True(Math.Abs(hc.Fill.Color.Luminance - hc.Text.Runs[0].Color.Luminance) > 0.6);
    }

    [Fact]
    public void BuiltInThemesAndPalettes_RoundTripThroughShotwiseJson()
    {
        foreach (var t in BuiltInContent.CreateThemes())
        {
            var json = JsonSerializer.Serialize(t, ShotwiseJson.Indented);
            var back = JsonSerializer.Deserialize<Theme>(json, ShotwiseJson.Compact)!;
            Assert.Equal(json, JsonSerializer.Serialize(back, ShotwiseJson.Indented));
            Assert.Equal(t.QuickStyles.Count, back.QuickStyles.Count);
            Assert.Equal(t.ThemeColors, back.ThemeColors);
            for (var i = 0; i < t.QuickStyles.Count; i++)
                Assert.Equal(t.QuickStyles[i].Prototype?.GetType(), back.QuickStyles[i].Prototype?.GetType());
        }
        foreach (var p in BuiltInContent.CreatePalettes())
        {
            var json = JsonSerializer.Serialize(p, ShotwiseJson.Indented);
            var back = JsonSerializer.Deserialize<Palette>(json, ShotwiseJson.Compact)!;
            Assert.Equal(json, JsonSerializer.Serialize(back, ShotwiseJson.Indented));
            Assert.True(back.Colors.Count >= 8);
        }
        Assert.Equal(3, BuiltInContent.CreatePalettes().Count);
    }

    /// <summary>
    /// The shipped Content/themes + Content/palettes files must equal the in-code definitions. Run with
    /// SHOTWISE_REGEN_CONTENT=1 to (re)write them.
    /// </summary>
    [Fact]
    public void ShippedContentFiles_MatchGenerator()
    {
        var contentDir = FindRepoFile("src", "Shotwise.App", "Content");
        if (contentDir is null) return; // not running from a source checkout
        var regen = Environment.GetEnvironmentVariable("SHOTWISE_REGEN_CONTENT") == "1";
        var mismatches = new List<string>();
        void Check(string folder, string name, string json)
        {
            var path = Path.Combine(contentDir, folder, name);
            if (regen) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json, new System.Text.UTF8Encoding(false)); return; }
            if (!File.Exists(path) || File.ReadAllText(path) != json) mismatches.Add(path);
        }
        foreach (var t in BuiltInContent.CreateThemes())
            Check("themes", BuiltInContent.FileNameFor(t) + StyleRepository.ThemeExtension, JsonSerializer.Serialize(t, ShotwiseJson.Indented));
        foreach (var p in BuiltInContent.CreatePalettes())
            Check("palettes", BuiltInContent.PaletteFileName(p) + StyleRepository.PaletteExtension, JsonSerializer.Serialize(p, ShotwiseJson.Indented));
        Assert.True(mismatches.Count == 0, "Content out of date (run with SHOTWISE_REGEN_CONTENT=1): " + string.Join(", ", mismatches));
    }

    [Fact]
    public async Task Repository_LoadsShippedContentFiles()
    {
        var contentDir = FindRepoFile("src", "Shotwise.App", "Content");
        if (contentDir is null || !Directory.Exists(Path.Combine(contentDir, "themes"))) return;
        using var t = new TempDir();
        var repo = NewRepo(t, contentDir);
        var themes = await repo.GetThemesAsync();
        Assert.Equal(5, themes.Count);
        Assert.Equal(3, repo.Palettes.Count);
        Assert.Equal(BuiltInContent.CreateThemes().Select(x => x.Id), themes.Select(x => x.Id));
    }

    [Fact]
    public async Task Repository_FallsBackToInCodeDefaultsWhenContentMissing()
    {
        using var t = new TempDir();
        var repo = NewRepo(t);
        var themes = await repo.GetThemesAsync();
        Assert.Equal(5, themes.Count);
        Assert.Equal("Basic", repo.ActiveTheme.Name);
        Assert.Equal(3, repo.Palettes.Count);
        Assert.All(repo.Palettes, p => Assert.True(p.IsBuiltIn));
    }

    [Fact]
    public async Task ActiveTheme_PersistsAcrossInstances_AndRaisesChanged()
    {
        using var t = new TempDir();
        var repo = NewRepo(t);
        var changed = 0;
        repo.Changed += (_, _) => changed++;
        await repo.SetActiveThemeAsync(BuiltInContent.DarkId);
        Assert.Equal("Dark Mode", repo.ActiveTheme.Name);
        Assert.True(changed > 0);
        Assert.Equal("Dark Mode", NewRepo(t).ActiveTheme.Name);
        await Assert.ThrowsAsync<ShotwiseException>(() => repo.SetActiveThemeAsync("does-not-exist"));
    }

    [Fact]
    public async Task ThemeExportImport_RoundTrips_AndNeverOverwritesExistingIds()
    {
        using var t = new TempDir();
        var repo = NewRepo(t);
        var src = (await repo.GetThemesAsync()).First(x => x.Id == BuiltInContent.BoldId);
        var file = t.Sub("bold.shottheme");
        await repo.ExportAsync(src, file);

        var imported = await repo.ImportAsync(file);
        Assert.NotEqual(src.Id, imported.Id);          // id collided with the built-in -> fresh id
        Assert.False(imported.IsBuiltIn);
        Assert.Equal(src.Name, imported.Name);
        Assert.Equal(src.QuickStyles.Count, imported.QuickStyles.Count);
        Assert.Equal(src.ThemeColors, imported.ThemeColors);
        Assert.Equal(src.FontFamily, imported.FontFamily);
        Assert.Equal(src.DropShadow, imported.DropShadow);
        Assert.Equal(6, (await repo.GetThemesAsync()).Count);
        Assert.Equal(6, (await NewRepo(t).GetThemesAsync()).Count); // persisted in the user themes folder

        // The exported file itself is a clean user file.
        var raw = JsonSerializer.Deserialize<Theme>(File.ReadAllText(file), ShotwiseJson.Compact)!;
        Assert.False(raw.IsBuiltIn);
        Assert.Equal("shottheme", raw.Format);

        await repo.DeleteThemeAsync(imported.Id);
        Assert.Equal(5, (await repo.GetThemesAsync()).Count);
    }

    [Fact]
    public async Task ThemeImport_RejectsGarbageWrongFormatAndNewerVersion()
    {
        using var t = new TempDir();
        var repo = NewRepo(t);
        var bad = t.Sub("bad.shottheme");
        File.WriteAllText(bad, "{ not json");
        Assert.Equal(ErrorCode.ThemeImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => repo.ImportAsync(bad))).Code);
        File.WriteAllText(bad, "{\"format\":\"shottemplate\",\"name\":\"x\"}");
        Assert.Equal(ErrorCode.ThemeImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => repo.ImportAsync(bad))).Code);
        File.WriteAllText(bad, "{\"format\":\"shottheme\",\"name\":\"x\",\"version\":99}");
        Assert.Equal(ErrorCode.NewerFormatVersion, (await Assert.ThrowsAsync<ShotwiseException>(() => repo.ImportAsync(bad))).Code);
        await Assert.ThrowsAsync<ShotwiseException>(() => repo.ImportAsync(t.Sub("missing.shottheme")));
        Assert.Equal(5, (await repo.GetThemesAsync()).Count);
    }

    [Fact]
    public async Task EditingBuiltInTheme_CreatesOverride_RestoreDefaultsReverts()
    {
        using var t = new TempDir();
        var repo = NewRepo(t);
        var basic = (await repo.GetThemesAsync()).First(x => x.Id == BuiltInContent.BasicId);
        var edited = JsonSerializer.Deserialize<Theme>(JsonSerializer.Serialize(basic, ShotwiseJson.Compact), ShotwiseJson.Compact)!;
        edited.FontFamily = "Consolas";
        edited.QuickStyles.RemoveAll(q => q.Tool == ToolKind.Crop);
        await repo.SaveThemeAsync(edited);

        var again = NewRepo(t);
        var reloaded = (await again.GetThemesAsync()).First(x => x.Id == BuiltInContent.BasicId);
        Assert.Equal("Consolas", reloaded.FontFamily);
        Assert.True(reloaded.IsBuiltIn);
        Assert.Empty(reloaded.StylesFor(ToolKind.Crop));
        Assert.Equal(5, (await again.GetThemesAsync()).Count);

        // A custom theme survives "restore defaults"; the override does not.
        await again.SaveThemeAsync(new Theme { Name = "Mine", ThemeColors = { Argb.Red } });
        await again.RestoreDefaultsAsync();
        var final = await again.GetThemesAsync();
        Assert.Equal(6, final.Count);
        Assert.Equal("Segoe UI", final.First(x => x.Id == BuiltInContent.BasicId).FontFamily);
        Assert.NotEmpty(final.First(x => x.Id == BuiltInContent.BasicId).StylesFor(ToolKind.Crop));
        await Assert.ThrowsAsync<ShotwiseException>(() => again.DeleteThemeAsync(BuiltInContent.BasicId));
    }

    [Fact]
    public async Task Palettes_SaveDeleteAndBuiltInsAreImmutable()
    {
        using var t = new TempDir();
        var repo = NewRepo(t);
        var mine = new Palette { Name = "Brand", Colors = { Argb.Red, Argb.Blue } };
        await repo.SavePaletteAsync(mine);
        Assert.Equal(4, repo.Palettes.Count);
        Assert.Equal(4, NewRepo(t).Palettes.Count);
        // Saving over a built-in id forks a copy.
        var builtIn = repo.Palettes.First(p => p.IsBuiltIn);
        await repo.SavePaletteAsync(new Palette { Id = builtIn.Id, Name = "Forked", Colors = { Argb.Green } });
        Assert.Equal(5, repo.Palettes.Count);
        Assert.Equal(builtIn.Colors.Count, repo.Palettes.First(p => p.Id == builtIn.Id).Colors.Count);
        await Assert.ThrowsAsync<ShotwiseException>(() => repo.DeletePaletteAsync(builtIn.Id));
        await repo.DeletePaletteAsync(mine.Id);
        Assert.Equal(4, repo.Palettes.Count);
    }
}
