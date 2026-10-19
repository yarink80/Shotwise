using Shotwise.Library.Stamps;
using Shotwise.Library.Templates;

namespace Shotwise.Library.Tests;

/// <summary>Loads the real Content shipped by WP-UI-DIALOGS (stamps, templates) through our catalogs; skipped outside a source checkout.</summary>
public class ShippedContentTests
{
    [Fact]
    public async Task ShippedStamps_LoadSearchAndRead()
    {
        var dir = StyleTests.FindRepoFile("src", "Shotwise.App", "Content", "stamps");
        if (dir is null || !Directory.Exists(dir)) return;
        using var t = new TempDir();
        var cat = new StampCatalog(dir, t.Sub("user"));
        var cats = await cat.GetCategoriesAsync();
        Assert.True(cats.Count >= 5);
        Assert.All(cats, c => Assert.True(c.Count > 0));
        var all = await cat.SearchAsync("", null, "en");
        Assert.True(all.Count >= 120, $"only {all.Count} stamps");
        Assert.All(all, s => Assert.Equal("image/svg+xml", s.MediaType));
        var arrows = await cat.SearchAsync("arrows", null, "en");
        Assert.NotEmpty(arrows);
        var german = await cat.SearchAsync("pfeile", null, "de-DE");
        Assert.NotEmpty(german);
        var bytes = await cat.LoadAsync(all[0].Id);
        Assert.Contains("<svg", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task ShippedTemplates_LoadAndInstantiate()
    {
        var dir = StyleTests.FindRepoFile("src", "Shotwise.App", "Content", "templates");
        if (dir is null || !Directory.Exists(dir)) return;
        using var t = new TempDir();
        var cat = new TemplateCatalog(dir, t.Sub("user"));
        var list = await cat.GetTemplatesAsync();
        Assert.True(list.Count >= 8, $"only {list.Count} templates");
        Assert.All(list, tpl => { Assert.True(tpl.IsBuiltIn); Assert.NotNull(tpl.Instantiate()); });
    }
}
