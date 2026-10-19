using System.Text.Json;
using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Xunit;

namespace Shotwise.Core.Tests;

/// <summary>POLISH-1: hotkey defaults, the PrintScreen fallback and the preset hotkey migration.</summary>
public class HotkeyDefaultsTests
{
    private static readonly Hotkey PrintScreen = new(VirtualKey.PrintScreen, ModifierKeys.None);
    private static readonly Hotkey ShiftPrintScreen = new(VirtualKey.PrintScreen, ModifierKeys.Shift);
    private static readonly Hotkey CtrlShiftPrintScreen = new(VirtualKey.PrintScreen, ModifierKeys.Control | ModifierKeys.Shift);
    private static readonly Hotkey AltShiftPrintScreen = new(VirtualKey.PrintScreen, ModifierKeys.Alt | ModifierKeys.Shift);
    private static readonly Hotkey CtrlAltPrintScreen = new(VirtualKey.PrintScreen, ModifierKeys.Control | ModifierKeys.Alt);

    [Fact]
    public void SettingsDefaults_PrintScreenWithShiftFallback_AndStartMenuOn()
    {
        using var tmp = new TempDir();
        var s = new SettingsService(new AppPaths(tmp.Path)).Current;
        Assert.Equal(PrintScreen, s.Hotkeys.GlobalCapture);
        Assert.Equal(ShiftPrintScreen, s.Hotkeys.GlobalCaptureFallback);
        Assert.Equal(Hotkey.Parse("Ctrl+Shift+F11"), s.Hotkeys.RepeatLastCapture);
        Assert.Equal(Hotkey.Parse("Ctrl+Shift+F9"), s.Hotkeys.VideoStartPauseResume);
        Assert.Equal(Hotkey.Parse("Ctrl+Shift+F10"), s.Hotkeys.VideoStop);
        Assert.Equal(Hotkey.Parse("Ctrl+Shift+F8"), s.Hotkeys.ScreenDrawToggle);
        Assert.Null(s.Hotkeys.EffectiveGlobalCapture);
        Assert.False(s.Hotkeys.FallbackNoticeShown);
        Assert.True(s.Advanced.ShowInStartMenu);
    }

    [Fact]
    public void BuiltInPresets_ShipDefaultHotkeys()
    {
        using var tmp = new TempDir();
        var repo = new PresetRepository(new AppPaths(tmp.Path));
        Assert.Null(repo.Find(PresetDefaults.AllInOneId)!.Hotkey);
        Assert.Equal(CtrlShiftPrintScreen, repo.Find(PresetDefaults.ImageRegionId)!.Hotkey);
        Assert.Equal(AltShiftPrintScreen, repo.Find(PresetDefaults.VideoId)!.Hotkey);
        Assert.Equal(CtrlAltPrintScreen, repo.Find(PresetDefaults.GrabTextId)!.Hotkey);
        // all distinct and none clashes with the global defaults
        var keys = repo.All.Where(p => p.Hotkey is not null).Select(p => p.Hotkey!.Value).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain(PrintScreen, keys);
        Assert.DoesNotContain(ShiftPrintScreen, keys);
        Assert.All(keys, k => Assert.True(k.IsValidGlobalHotkey));
    }

    /// <summary>A presets.json written before POLISH-1: built-ins without hotkeys and no HotkeyDefaultsVersion.</summary>
    private static string WriteLegacyFile(TempDir tmp, Action<List<CapturePreset>>? edit = null)
    {
        var presets = PresetDefaults.CreateBuiltIns().ToList();
        foreach (var p in presets) p.Hotkey = null;
        edit?.Invoke(presets);
        var json = JsonSerializer.Serialize(new PresetFile { ActivePresetId = PresetDefaults.AllInOneId, Presets = presets }, ShotwiseJson.Indented);
        // emulate the old format exactly: no "hotkeyDefaultsVersion" member at all
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        foreach (var name in node.Select(kv => kv.Key).Where(k => k.Equals("hotkeyDefaultsVersion", StringComparison.OrdinalIgnoreCase)).ToList()) node.Remove(name);
        var paths = new AppPaths(tmp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.PresetsFile(), node.ToJsonString());
        return paths.PresetsFile();
    }

    [Fact]
    public void Migration_AppliesDefaults_ToLegacyBuiltIns_Once()
    {
        using var tmp = new TempDir();
        var file = WriteLegacyFile(tmp);
        var repo = new PresetRepository(file);
        Assert.Equal(CtrlShiftPrintScreen, repo.Find(PresetDefaults.ImageRegionId)!.Hotkey);
        Assert.Equal(AltShiftPrintScreen, repo.Find(PresetDefaults.VideoId)!.Hotkey);
        Assert.Equal(CtrlAltPrintScreen, repo.Find(PresetDefaults.GrabTextId)!.Hotkey);
        Assert.Contains("\"hotkeyDefaultsVersion\": " + PresetDefaults.HotkeyDefaultsVersion, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migration_DoesNotFightTheUser()
    {
        using var tmp = new TempDir();
        var file = WriteLegacyFile(tmp, presets =>
        {
            presets.First(p => p.Id == PresetDefaults.VideoId).HotkeyCleared = true;                     // explicitly cleared
            presets.Add(new CapturePreset { Id = "mine", Name = "Mine", Hotkey = CtrlAltPrintScreen });  // user took a default key
            presets.First(p => p.Id == PresetDefaults.ImageRegionId).Hotkey = Hotkey.Parse("Ctrl+F7");  // user's own key
        });
        var repo = new PresetRepository(file);
        Assert.Null(repo.Find(PresetDefaults.VideoId)!.Hotkey);
        Assert.Null(repo.Find(PresetDefaults.GrabTextId)!.Hotkey);
        Assert.Equal(Hotkey.Parse("Ctrl+F7"), repo.Find(PresetDefaults.ImageRegionId)!.Hotkey);
        Assert.Equal(CtrlAltPrintScreen, repo.Find("mine")!.Hotkey);

        // clearing a hotkey later is remembered and survives a reload (the migration does not run again)
        var copy = repo.Find(PresetDefaults.ImageRegionId)!.Clone();
        copy.Hotkey = null;
        await repo.SaveAsync(copy);
        var reloaded = new PresetRepository(file);
        Assert.Null(reloaded.Find(PresetDefaults.ImageRegionId)!.Hotkey);
        Assert.True(reloaded.Find(PresetDefaults.ImageRegionId)!.HotkeyCleared);
    }

    [Fact]
    public async Task Save_SetsAndResetsHotkeyCleared()
    {
        using var tmp = new TempDir();
        var repo = new PresetRepository(new AppPaths(tmp.Path));
        var p = repo.Find(PresetDefaults.GrabTextId)!.Clone();
        p.Hotkey = null;
        await repo.SaveAsync(p);
        Assert.True(repo.Find(PresetDefaults.GrabTextId)!.HotkeyCleared);
        p = repo.Find(PresetDefaults.GrabTextId)!.Clone();
        p.Hotkey = Hotkey.Parse("Ctrl+Alt+G");
        await repo.SaveAsync(p);
        Assert.False(repo.Find(PresetDefaults.GrabTextId)!.HotkeyCleared);
        // a preset that never had a key is not "cleared"
        var fresh = new CapturePreset { Name = "No key" };
        await repo.SaveAsync(fresh);
        await repo.SaveAsync(repo.Find(fresh.Id)!.Clone());
        Assert.False(repo.Find(fresh.Id)!.HotkeyCleared);
    }

    [Fact]
    public async Task ResetDefaults_SkipsADefaultKeyAUserPresetHolds()
    {
        using var tmp = new TempDir();
        var repo = new PresetRepository(new AppPaths(tmp.Path));
        var video = repo.Find(PresetDefaults.VideoId)!.Clone();
        video.Hotkey = null;
        await repo.SaveAsync(video);
        await repo.SaveAsync(new CapturePreset { Id = "mine", Name = "Mine", Hotkey = AltShiftPrintScreen });
        await repo.ResetDefaultsAsync();
        Assert.Null(repo.Find(PresetDefaults.VideoId)!.Hotkey);
        Assert.Equal(AltShiftPrintScreen, repo.Find("mine")!.Hotkey);
        Assert.Equal(CtrlShiftPrintScreen, repo.Find(PresetDefaults.ImageRegionId)!.Hotkey);
    }

    // ------------------------------------------------------------------ fallback registration

    private sealed class FakeHotkeys : IHotkeyService
    {
        public HashSet<Hotkey> Taken { get; } = new();
        public Dictionary<string, Hotkey> Map { get; } = new();
        public List<Hotkey> Attempts { get; } = new();
        public HotkeyRegistrationResult Register(string id, Hotkey key, Action callback, bool allowOverride)
        {
            Attempts.Add(key);
            if (!key.IsValidGlobalHotkey) return HotkeyRegistrationResult.Unsupported;
            if (Taken.Contains(key)) return HotkeyRegistrationResult.InUseBySystemOrOtherApp;
            if (Map.Any(kv => kv.Key != id && kv.Value == key)) return HotkeyRegistrationResult.Duplicate;
            Map[id] = key;
            return HotkeyRegistrationResult.Ok;
        }
        public void Unregister(string id) => Map.Remove(id);
        public void UnregisterAll() => Map.Clear();
        public IReadOnlyDictionary<string, Hotkey> Registered => Map;
        public bool IsPrintScreenTakenBySnipping() => false;
    }

    [Fact]
    public void Fallback_UsedOnlyWhenThePreferredKeyIsTaken()
    {
        var svc = new FakeHotkeys();
        var free = svc.RegisterWithFallback(HotkeyIds.GlobalCapture, PrintScreen, new[] { ShiftPrintScreen }, () => { }, false);
        Assert.Equal(HotkeyRegistrationResult.Ok, free.Result);
        Assert.Equal(PrintScreen, free.Effective);
        Assert.False(free.UsedFallback);
        Assert.Equal(HotkeyState.Active, HotkeyRegistration.StateOf(free.Result, free.UsedFallback));

        svc.UnregisterAll();
        svc.Taken.Add(PrintScreen);   // another screenshot tool holds PrintScreen
        var fb = svc.RegisterWithFallback(HotkeyIds.GlobalCapture, PrintScreen, new[] { ShiftPrintScreen }, () => { }, false);
        Assert.Equal(HotkeyRegistrationResult.Ok, fb.Result);
        Assert.Equal(ShiftPrintScreen, fb.Effective);
        Assert.True(fb.UsedFallback);
        Assert.Equal(HotkeyRegistrationResult.InUseBySystemOrOtherApp, fb.PreferredResult);
        Assert.Equal(HotkeyState.Fallback, HotkeyRegistration.StateOf(fb.Result, fb.UsedFallback));
        Assert.Equal(ShiftPrintScreen, svc.Registered[HotkeyIds.GlobalCapture]);
    }

    [Fact]
    public void Fallback_NotUsedForDuplicates_AndReportsInUseWhenBothTaken()
    {
        var svc = new FakeHotkeys();
        svc.Register("other", PrintScreen, () => { }, false);
        var dup = svc.RegisterWithFallback(HotkeyIds.GlobalCapture, PrintScreen, new[] { ShiftPrintScreen }, () => { }, false);
        Assert.Equal(HotkeyRegistrationResult.Duplicate, dup.Result);
        Assert.Null(dup.Effective);
        Assert.DoesNotContain(ShiftPrintScreen, svc.Attempts);

        var svc2 = new FakeHotkeys();
        svc2.Taken.Add(PrintScreen); svc2.Taken.Add(ShiftPrintScreen);
        var both = svc2.RegisterWithFallback(HotkeyIds.GlobalCapture, PrintScreen, new[] { ShiftPrintScreen }, () => { }, false);
        Assert.Equal(HotkeyRegistrationResult.InUseBySystemOrOtherApp, both.Result);
        Assert.Null(both.Effective);
        Assert.Equal(HotkeyState.InUse, HotkeyRegistration.StateOf(both.Result));
    }

    [Fact]
    public void FindConflict_ReportsUnsupportedAndOwner()
    {
        var others = new (string, Hotkey?)[] { ("Repeat last capture", Hotkey.Parse("Ctrl+Shift+F11")), ("Off", null) };
        Assert.Null(HotkeyRegistration.FindConflict(ShiftPrintScreen, others, out var u1));
        Assert.False(u1);
        Assert.Equal("Repeat last capture", HotkeyRegistration.FindConflict(Hotkey.Parse("Ctrl+Shift+F11"), others, out _));
        HotkeyRegistration.FindConflict(Hotkey.Parse("A"), others, out var u2);
        Assert.True(u2);
        HotkeyRegistration.FindConflict(Hotkey.Parse("Ctrl+F12"), others, out var u3);
        Assert.True(u3);
    }
}
