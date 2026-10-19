using System.Text.Json;
using Shotwise.Core.Capture;
using Shotwise.Core.Effects;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Xunit;

namespace Shotwise.Core.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void Defaults_WhenFileMissing()
    {
        using var tmp = new TempDir();
        var svc = new SettingsService(new AppPaths(tmp.Path));
        Assert.Equal(AppTheme.Light, svc.Current.General.Theme);
        Assert.Equal(new Hotkey(VirtualKey.PrintScreen, ModifierKeys.None), svc.Current.Hotkeys.GlobalCapture);
        Assert.Equal(UsageDataMode.Off, svc.Current.Privacy.UsageData);
        Assert.Equal(8303, svc.Current.Mobile.Port);
        Assert.True(svc.Current.Library.AutosaveImages);
    }

    [Fact]
    public void Update_PersistsAtomically_AndReportsChangedSections()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        var svc = new SettingsService(paths);
        SettingsChangedEventArgs? args = null;
        svc.Changed += (_, e) => args = e;

        svc.Update(s =>
        {
            s.General.Theme = AppTheme.Dark;
            s.Hotkeys.RepeatLastCapture = Hotkey.Parse("Ctrl+Alt+R");
            s.Editor.MyColors.Add(Argb.Parse("#80112233"));
        });

        Assert.NotNull(args);
        Assert.True(args!.Affects("General"));
        Assert.True(args.Affects("Hotkeys"));
        Assert.True(args.Affects("Editor"));
        Assert.False(args.Affects("Library"));
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.Empty(Directory.GetFiles(paths.AppData, "*.tmp"));

        var json = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("\"theme\": \"dark\"", json);
        Assert.Contains("\"Ctrl+Alt+R\"", json);

        var reloaded = new SettingsService(paths);
        Assert.Equal(AppTheme.Dark, reloaded.Current.General.Theme);
        Assert.Equal(Hotkey.Parse("Ctrl+Alt+R"), reloaded.Current.Hotkeys.RepeatLastCapture);
        Assert.Equal(new Argb(0x80112233), Assert.Single(reloaded.Current.Editor.MyColors));
    }

    [Fact]
    public void Update_NoChange_DoesNotRaise()
    {
        using var tmp = new TempDir();
        var svc = new SettingsService(new AppPaths(tmp.Path));
        var raised = false;
        svc.Changed += (_, _) => raised = true;
        svc.Update(s => s.General.Theme = s.General.Theme);
        Assert.False(raised);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults_AndKeepsBackup()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        Directory.CreateDirectory(paths.AppData);
        File.WriteAllText(paths.SettingsFile, "{ this is not json");
        var svc = new SettingsService(paths);
        Assert.Equal(AppTheme.Light, svc.Current.General.Theme);
        Assert.Single(Directory.GetFiles(paths.AppData, "settings.corrupt-*.json"));
    }

    [Fact]
    public void UnknownSettings_ArePreserved_AndValuesNormalized()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        Directory.CreateDirectory(paths.AppData);
        File.WriteAllText(paths.SettingsFile, "{\"futureSection\":{\"x\":1},\"library\":{\"maxTrayItems\":5000},\"capture\":{\"videoFps\":7}}");
        var svc = new SettingsService(paths);
        Assert.Equal(200, svc.Current.Library.MaxTrayItems);
        Assert.Equal(30, svc.Current.Capture.VideoFps);
        svc.Update(s => s.General.ShowOnboarding = false);
        Assert.Contains("futureSection", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public async Task Reset_RestoresDefaults_AndRaisesAllSections()
    {
        using var tmp = new TempDir();
        var svc = new SettingsService(new AppPaths(tmp.Path));
        svc.Update(s => s.Capture.ShowMagnifier = false);
        SettingsChangedEventArgs? args = null;
        svc.Changed += (_, e) => args = e;
        await svc.ResetAsync();
        Assert.True(svc.Current.Capture.ShowMagnifier);
        Assert.Equal(AppSettings.SectionNames.Count, args!.ChangedSections.Count);
    }
}

public class PresetRepositoryTests
{
    [Fact]
    public void SeedsBuiltIns_OnFirstRun()
    {
        using var tmp = new TempDir();
        var repo = new PresetRepository(new AppPaths(tmp.Path));
        var names = repo.All.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "All-in-One", "Image Region", "Video", "Grab Text", "Panoramic", "Step Capture", "Fullscreen", "Window" }, names);
        Assert.All(repo.All, p => Assert.True(p.IsBuiltIn));
        Assert.Equal(PresetDefaults.AllInOneId, repo.Active.Id);
        Assert.Equal(SelectionType.GrabText, repo.Find(PresetDefaults.GrabTextId)!.Selection);
        Assert.Equal(CaptureMode.Video, repo.Find(PresetDefaults.VideoId)!.Mode);
    }

    [Fact]
    public async Task SaveDeleteReorder_AndActive_Persist()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        var repo = new PresetRepository(paths);
        var changed = 0;
        repo.Changed += (_, _) => changed++;
        var p = new CapturePreset
        {
            Name = "Bordered window",
            Mode = CaptureMode.Image,
            Selection = SelectionType.Window,
            Effects = { EffectDefaults.Border, new ColorReplaceEffect(new[] { (Argb.White, Argb.Black) }, 0.2) },
            Outputs = { new OutputBinding(KnownDestinations.Clipboard) },
            Hotkey = Hotkey.Parse("Ctrl+Shift+W"),
            Timing = new TimingOptions { DelaySeconds = 5 },
        };
        await repo.SaveAsync(p);
        repo.Active = repo.Find(p.Id)!;
        await repo.DeleteAsync(PresetDefaults.WindowId);
        await repo.ReorderAsync(new[] { p.Id, PresetDefaults.VideoId });
        Assert.Equal(4, changed);

        var again = new PresetRepository(paths);
        Assert.Equal(8, again.All.Count);
        Assert.Equal(p.Id, again.All[0].Id);
        Assert.Equal(PresetDefaults.VideoId, again.All[1].Id);
        Assert.Equal(p.Id, again.Active.Id);
        var loaded = again.Find(p.Id)!;
        Assert.Equal(2, loaded.Effects.Count);
        var cr = Assert.IsType<ColorReplaceEffect>(loaded.Effects[1]);
        Assert.Equal((Argb.White, Argb.Black), Assert.Single(cr.Pairs));
        Assert.Equal(Hotkey.Parse("Ctrl+Shift+W"), loaded.Hotkey);
        Assert.Equal(5, loaded.Timing.DelaySeconds);
        Assert.Null(again.Find(PresetDefaults.WindowId));
    }

    [Fact]
    public async Task ExportImport_AssignsNewIds_AndClearsDuplicateHotkeys()
    {
        using var tmp = new TempDir();
        var repo = new PresetRepository(new AppPaths(tmp.File("a")));
        var p = new CapturePreset { Name = "Mine", Hotkey = Hotkey.Parse("Ctrl+F1"), GroupId = "g1" };
        await repo.SaveGroupAsync(new PresetGroup("g1", "Work"));
        await repo.SaveAsync(p);
        var file = tmp.File("x.shotpresets");
        await repo.ExportAsync(new[] { p.Id }, file);
        var exported = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(file), ShotwiseJson.Indented)!;
        Assert.Equal("shotpresets", exported.Format);
        Assert.Single(exported.Presets);
        Assert.Single(exported.Groups);

        // import into the same repo: id + hotkey conflict
        await repo.ImportAsync(file);
        var mine = repo.All.Where(x => x.Name == "Mine").ToList();
        Assert.Equal(2, mine.Count);
        Assert.NotEqual(mine[0].Id, mine[1].Id);
        Assert.Single(mine, x => x.Hotkey is not null);
        Assert.All(mine, x => Assert.Equal("g1", x.GroupId));
        Assert.All(mine, x => Assert.False(x.IsBuiltIn));

        // import into a fresh repo
        var other = new PresetRepository(new AppPaths(tmp.File("b")));
        await other.ImportAsync(file);
        Assert.Equal(9, other.All.Count);
        Assert.Contains(other.Groups, g => g.Name == "Work");
    }

    [Fact]
    public async Task Import_RejectsForeignFiles()
    {
        using var tmp = new TempDir();
        var repo = new PresetRepository(new AppPaths(tmp.Path));
        var f = tmp.File("bad.shotpresets");
        File.WriteAllText(f, "{\"format\":\"other\",\"version\":1}");
        Assert.Equal(ErrorCode.PresetImportFailed, (await Assert.ThrowsAsync<ShotwiseException>(() => repo.ImportAsync(f))).Code);
        File.WriteAllText(f, "{\"format\":\"shotpresets\",\"version\":42}");
        Assert.Equal(ErrorCode.NewerFormatVersion, (await Assert.ThrowsAsync<ShotwiseException>(() => repo.ImportAsync(f))).Code);
    }

    [Fact]
    public async Task ResetDefaults_RestoresBuiltIns_KeepsUserPresets()
    {
        using var tmp = new TempDir();
        var repo = new PresetRepository(new AppPaths(tmp.Path));
        var builtIn = repo.Find(PresetDefaults.ImageRegionId)!;
        builtIn.Name = "Renamed";
        await repo.SaveAsync(builtIn);
        await repo.DeleteAsync(PresetDefaults.FullscreenId);
        await repo.SaveAsync(new CapturePreset { Name = "User" });
        await repo.ResetDefaultsAsync();
        Assert.Equal("Image Region", repo.Find(PresetDefaults.ImageRegionId)!.Name);
        Assert.NotNull(repo.Find(PresetDefaults.FullscreenId));
        Assert.Contains(repo.All, p => p.Name == "User");
    }

    [Fact]
    public void CorruptFile_RestoresBuiltIns()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        Directory.CreateDirectory(paths.AppData);
        File.WriteAllText(paths.PresetsFile, "[[[");
        var repo = new PresetRepository(paths);
        Assert.Equal(8, repo.All.Count);
    }

    [Fact]
    public void MarkUsed_StoresSnapshot()
    {
        using var tmp = new TempDir();
        var repo = new PresetRepository(new AppPaths(tmp.Path));
        var adHoc = new CapturePreset { Name = "Ad hoc", Selection = SelectionType.FixedRegion };
        repo.MarkUsed(adHoc);
        adHoc.Name = "changed";
        Assert.Equal("Ad hoc", repo.LastUsed!.Name);
    }
}
