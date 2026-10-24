using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Xunit;

namespace Shotwise.Core.Tests;

/// <summary>Hotkey availability check, reserved combinations, likely-owner detection, suggestions and "Move here".</summary>
public class HotkeyAdvisorTests
{
    private static Hotkey K(string s) => Hotkey.Parse(s);
    private static readonly Hotkey PrintScreen = new(VirtualKey.PrintScreen, ModifierKeys.None);
    private static readonly string AllInOnePreset = HotkeyIds.Preset(PresetDefaults.AllInOneId);
    // two screenshot tools from the likely-owner table (app names live only in that table)
    private static readonly KnownHotkeyOwner[] Tools = KnownHotkeyOwners.Table.Where(o => o.Scope == HotkeyOwnerScope.PrintScreenFamily && o.Setting == HotkeyOwnerSetting.None && o.UncertainName is null).ToArray();
    private static KnownHotkeyOwner ToolA => Tools[0];
    private static KnownHotkeyOwner ToolB => Tools[1];

    private sealed class FakeProbe : IHotkeyProbe
    {
        public HashSet<Hotkey> TakenByOthers { get; } = new();
        public Dictionary<string, Hotkey> Map { get; } = new();
        public int Calls { get; private set; }
        public HotkeyProbeResult Probe(Hotkey key)
        {
            Calls++;
            if (!key.IsValidGlobalHotkey) return HotkeyProbeResult.Invalid;
            if (Map.ContainsValue(key)) return HotkeyProbeResult.HeldByShotwise;
            return TakenByOthers.Contains(key) ? HotkeyProbeResult.HeldByOtherApp : HotkeyProbeResult.Free;
        }
        public IReadOnlyDictionary<string, Hotkey> Registered => Map;
    }

    private sealed class FakeEnv : IHotkeyEnvironment
    {
        public HashSet<string> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, object>> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<string> RunningProcessNames() => Processes;
        public object? ReadUserValue(string subKey, string name) => Registry.TryGetValue(subKey, out var v) && v.TryGetValue(name, out var o) ? o : null;
        public IReadOnlyList<string> UserSubKeys(string subKey)
            => Registry.Keys.Where(k => k.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase)).Select(k => k[(subKey.Length + 1)..]).Where(k => !k.Contains('\\')).ToList();
        public IReadOnlyList<string> UserValueNames(string subKey) => Registry.TryGetValue(subKey, out var v) ? v.Keys.ToList() : new List<string>();
        public void Set(string subKey, string name, object value)
        {
            if (!Registry.TryGetValue(subKey, out var v)) Registry[subKey] = v = new(StringComparer.OrdinalIgnoreCase);
            v[name] = value;
        }
    }

    private sealed class FakeBindings : IHotkeyBindingSource
    {
        public List<HotkeyBinding> List { get; } = new();
        public IReadOnlyList<HotkeyBinding> Bindings => List;
    }

    private sealed class Rig
    {
        public FakeProbe Probe { get; } = new();
        public FakeEnv Env { get; } = new();
        public FakeBindings Bindings { get; } = new();
        public DateTime Now { get; set; } = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        public HotkeyAdvisor Advisor { get; }
        public Rig()
        {
            Advisor = new HotkeyAdvisor(Probe, Env, () => Bindings, () => Now);
            // the shipped defaults: settings hotkeys + built-in presets
            Bindings.List.AddRange(HotkeyBindings.Collect(HotkeySettings.Defaults(), PresetDefaults.CreateBuiltIns(), id => id == HotkeyIds.GlobalCapture ? "Capture" : id));
        }
        /// <summary>Registers like the coordinator does on a clean PC (no fallback in use).</summary>
        public void RegisterDefaults()
        {
            foreach (var b in Bindings.List.Where(b => b.Key is not null && b.Id is not (HotkeyIds.GlobalCaptureFallback or HotkeyIds.ScreenDraw or HotkeyIds.WebcamFullscreen)))
                Probe.Map[b.Id] = b.Key!.Value;
        }
    }

    // ------------------------------------------------------------------ reserved combinations

    [Theory]
    [InlineData("Win+L", HotkeyReservation.LockScreen)]
    [InlineData("Ctrl+Alt+Del", HotkeyReservation.SecureAttention)]
    [InlineData("Ctrl+Shift+Esc", HotkeyReservation.TaskManager)]
    [InlineData("Alt+Tab", HotkeyReservation.AppSwitcher)]
    [InlineData("Alt+F4", HotkeyReservation.CloseWindow)]
    [InlineData("Win+Shift+S", HotkeyReservation.WindowsSnip)]
    [InlineData("F12", HotkeyReservation.Debugger)]
    [InlineData("Ctrl+Shift+F12", HotkeyReservation.Debugger)]
    [InlineData("Alt+PrintScreen", HotkeyReservation.ActiveWindowScreenshot)]
    [InlineData("Win+PrintScreen", HotkeyReservation.WindowsScreenshot)]
    [InlineData("Win+Alt+PrintScreen", HotkeyReservation.GameBarScreenshot)]
    [InlineData("A", HotkeyReservation.PlainKey)]
    [InlineData("7", HotkeyReservation.PlainKey)]
    [InlineData("Esc", HotkeyReservation.PlainKey)]
    [InlineData("Tab", HotkeyReservation.PlainKey)]
    [InlineData("Enter", HotkeyReservation.PlainKey)]
    [InlineData("Alt+Enter", HotkeyReservation.SystemKey)]
    [InlineData("Shift+A", HotkeyReservation.TypingKey)]
    [InlineData("Ctrl+C", HotkeyReservation.AppShortcut)]
    [InlineData("Alt+F", HotkeyReservation.MenuAccessKey)]
    [InlineData("Win+E", HotkeyReservation.WindowsShortcut)]
    [InlineData("Ctrl+Shift+Left", HotkeyReservation.TypingKey)]
    public void Reserved_Combinations(string key, HotkeyReservation expected)
    {
        Assert.Equal(expected, HotkeyRules.Reservation(K(key)));
        var a = new Rig().Advisor.CheckAvailability(K(key));
        Assert.Equal(HotkeyAvailabilityStatus.Reserved, a.Status);
        Assert.Equal(expected, a.Reservation);
        Assert.False(string.IsNullOrEmpty(a.Reason));
    }

    [Theory]
    [InlineData("PrintScreen")]
    [InlineData("Shift+PrintScreen")]
    [InlineData("Ctrl+PrintScreen")]
    [InlineData("Ctrl+Shift+PrintScreen")]
    [InlineData("Alt+Shift+PrintScreen")]
    [InlineData("Ctrl+Alt+PrintScreen")]
    [InlineData("Ctrl+Shift+X")]
    [InlineData("Ctrl+Alt+A")]
    [InlineData("Ctrl+Shift+F9")]
    [InlineData("Ctrl+Alt+5")]
    [InlineData("Win+Alt+R")]
    [InlineData("F9")]
    public void NotReserved_CommonGoodKeys(string key) => Assert.Equal(HotkeyReservation.None, HotkeyRules.Reservation(K(key)));

    [Fact]
    public void ShippedDefaults_AreNeverReserved_AndCatalogNeverListsReservedKeys()
    {
        foreach (var b in new Rig().Bindings.List.Where(b => b.Key is not null))
            Assert.Equal(HotkeyReservation.None, HotkeyRules.Reservation(b.Key!.Value));
        foreach (var purpose in Enum.GetValues<HotkeyPurpose>())
            foreach (var (key, _) in HotkeySuggestionCatalog.For(purpose))
            {
                Assert.True(key.IsValidGlobalHotkey, $"{purpose}: {key}");
                Assert.Equal(HotkeyReservation.None, HotkeyRules.Reservation(key));
            }
    }

    [Fact]
    public void ModifierOnly_IsUnsupported_WithoutProbing()
    {
        var rig = new Rig();
        Assert.Equal(HotkeyAvailabilityStatus.Unsupported, rig.Advisor.CheckAvailability(new Hotkey(VirtualKey.LeftShift, ModifierKeys.Control)).Status);
        Assert.Equal(HotkeyAvailabilityStatus.Unsupported, rig.Advisor.CheckAvailability(default).Status);
        Assert.Equal(0, rig.Probe.Calls);
    }

    // ------------------------------------------------------------------ Shotwise vs other apps

    [Fact]
    public void RegisteredByAnotherBinding_IsUsedByShotwise_SameBindingIsAvailable()
    {
        var rig = new Rig();
        rig.RegisterDefaults();
        var other = rig.Advisor.CheckAvailability(PrintScreen, AllInOnePreset);
        Assert.Equal(HotkeyAvailabilityStatus.UsedByShotwise, other.Status);
        Assert.Equal(HotkeyIds.GlobalCapture, other.ShotwiseBindingId);
        Assert.Equal("Capture", other.ShotwiseBindingName);

        Assert.True(rig.Advisor.CheckAvailability(PrintScreen, HotkeyIds.GlobalCapture).IsAvailable);
        Assert.Equal(0, rig.Probe.Calls);   // keys we hold are never probed
    }

    [Fact]
    public void FallbackKey_RegisteredUnderGlobalCapture_IsAvailableForTheFallbackRow()
    {
        var rig = new Rig();
        rig.Probe.Map[HotkeyIds.GlobalCapture] = K("Shift+PrintScreen");   // PrintScreen was taken: the fallback is in use
        Assert.True(rig.Advisor.CheckAvailability(K("Shift+PrintScreen"), HotkeyIds.GlobalCaptureFallback).IsAvailable);
        var forPreset = rig.Advisor.CheckAvailability(K("Shift+PrintScreen"), AllInOnePreset);
        Assert.Equal(HotkeyAvailabilityStatus.UsedByShotwise, forPreset.Status);
    }

    [Fact]
    public void ConfiguredButNotRegistered_IsUsedByShotwise()
    {
        var rig = new Rig();
        rig.RegisterDefaults();   // ScreenDraw (Ctrl+Shift+F8) is only registered while recording
        var a = rig.Advisor.CheckAvailability(K("Ctrl+Shift+F8"), AllInOnePreset);
        Assert.Equal(HotkeyAvailabilityStatus.UsedByShotwise, a.Status);
        Assert.Equal(HotkeyIds.ScreenDraw, a.ShotwiseBindingId);
        Assert.True(rig.Advisor.CheckAvailability(K("Ctrl+Shift+F8"), HotkeyIds.ScreenDraw).IsAvailable);
    }

    [Fact]
    public void StagedEdit_ReleasesARegisteredKey()
    {
        var rig = new Rig();
        rig.RegisterDefaults();
        var staged = rig.Bindings.List.Select(b => b.Id == HotkeyIds.Repeat ? b with { Key = K("Ctrl+Shift+X") } : b).ToList();
        Assert.True(rig.Advisor.CheckAvailability(K("Ctrl+Shift+F11"), HotkeyIds.VideoStop, staged).IsAvailable);
        var taken = rig.Advisor.CheckAvailability(K("Ctrl+Shift+X"), HotkeyIds.VideoStop, staged);
        Assert.Equal(HotkeyAvailabilityStatus.UsedByShotwise, taken.Status);
        Assert.Equal(HotkeyIds.Repeat, taken.ShotwiseBindingId);
    }

    [Fact]
    public void HeldByOtherApp_ReportsLikelyOwners()
    {
        var rig = new Rig();
        rig.Probe.TakenByOthers.Add(PrintScreen);
        rig.Probe.TakenByOthers.Add(K("Ctrl+Shift+X"));
        rig.Env.Processes.Add(ToolA.ProcessNames[0]);
        rig.Env.Processes.Add("explorer");
        var a = rig.Advisor.CheckAvailability(PrintScreen, HotkeyIds.GlobalCapture);
        Assert.Equal(HotkeyAvailabilityStatus.InUseByOtherApp, a.Status);
        Assert.Equal(new[] { ToolA.DisplayName }, a.LikelyOwners);
        Assert.True(a.WillNotWork);
        // screenshot tools are not blamed for unrelated keys
        Assert.Empty(rig.Advisor.CheckAvailability(K("Ctrl+Shift+X")).LikelyOwners);
        Assert.Equal(HotkeyAvailabilityStatus.InUseByOtherApp, rig.Advisor.CheckAvailability(K("Ctrl+Shift+X")).Status);
    }

    // ------------------------------------------------------------------ likely owners

    [Fact]
    public void LikelyOwners_WindowsPrintScreenSetting_OnlyForPlainPrintScreen()
    {
        var env = new FakeEnv();
        env.Set(KnownHotkeyOwners.WindowsKeyboardSubKey, KnownHotkeyOwners.WindowsSnippingValue, 1);
        env.Processes.Add(ToolB.ProcessNames[0]);
        Assert.Equal(new[] { KnownHotkeyOwners.WindowsSnippingName, ToolB.DisplayName }, KnownHotkeyOwners.Detect(PrintScreen, env));
        Assert.Equal(new[] { ToolB.DisplayName }, KnownHotkeyOwners.Detect(K("Shift+PrintScreen"), env));
        env.Set(KnownHotkeyOwners.WindowsKeyboardSubKey, KnownHotkeyOwners.WindowsSnippingValue, 0);
        Assert.Equal(new[] { ToolB.DisplayName }, KnownHotkeyOwners.Detect(PrintScreen, env));
    }

    [Fact]
    public void LikelyOwners_CloudScreenshotSetting_BestEffort()
    {
        var env = new FakeEnv();
        Assert.Empty(KnownHotkeyOwners.Detect(PrintScreen, env));
        env.Processes.Add("OneDrive");
        env.Set(KnownHotkeyOwners.OneDriveAccountsSubKey + "\\Personal", "UserEmail", "x");
        Assert.Equal(new[] { "OneDrive (if screenshot saving is on)" }, KnownHotkeyOwners.Detect(PrintScreen, env));   // setting not readable
        env.Set(KnownHotkeyOwners.OneDriveAccountsSubKey + "\\Personal", "ScreenshotsEnabled", 0);
        Assert.Empty(KnownHotkeyOwners.Detect(PrintScreen, env));
        env.Set(KnownHotkeyOwners.OneDriveAccountsSubKey + "\\Business1", "ScreenshotsEnabled", 1);
        Assert.Equal(new[] { "OneDrive (save screenshots to OneDrive)" }, KnownHotkeyOwners.Detect(PrintScreen, env));
    }

    [Fact]
    public void LikelyOwners_SpecificKeysBeforeAnyKeyApps()
    {
        var env = new FakeEnv();
        env.Processes.UnionWith(new[] { "Discord", "GameBar", "NVIDIA Share" });
        Assert.Equal(new[] { "Xbox Game Bar", "Discord" }, KnownHotkeyOwners.Detect(K("Win+Alt+R"), env));
        Assert.Equal(new[] { "NVIDIA overlay", "Discord" }, KnownHotkeyOwners.Detect(K("Alt+Z"), env));
        Assert.Equal(new[] { "Discord" }, KnownHotkeyOwners.Detect(K("Ctrl+Shift+X"), env));
        Assert.Empty(KnownHotkeyOwners.Detect(K("Ctrl+Shift+X"), new FakeEnv()));
    }

    [Fact]
    public void LikelyOwners_TableIsWellFormed()
    {
        Assert.Equal(KnownHotkeyOwners.Table.Count, KnownHotkeyOwners.Table.Select(o => o.DisplayName).Distinct().Count());
        foreach (var o in KnownHotkeyOwners.Table)
        {
            Assert.True(o.ProcessNames.Length > 0 || o.Setting != HotkeyOwnerSetting.None, o.DisplayName);
            Assert.All(o.ProcessNames, p => Assert.False(p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
            if (o.Scope == HotkeyOwnerScope.SpecificKeys) Assert.All(o.Keys!, k => Assert.True(Hotkey.TryParse(k, out _), k));
        }
    }

    // ------------------------------------------------------------------ suggestions

    [Fact]
    public void Suggest_AllInOne_PrintScreenFirst_OnAFreePc()
    {
        var rig = new Rig();
        // shipped defaults: the global Capture hotkey has PrintScreen, so the All-in-One preset is offered to take it over
        var withDefaults = rig.Advisor.Suggest(AllInOnePreset);
        Assert.Equal(PrintScreen, withDefaults[0].Key);
        Assert.True(withDefaults[0].CanMoveHere);
        Assert.Equal(K("Ctrl+PrintScreen"), withDefaults[1].Key);   // Shift+PrintScreen is the configured capture fallback

        rig.Bindings.List.Clear();   // nothing configured yet
        var list = rig.Advisor.Suggest(AllInOnePreset);
        Assert.Equal(PrintScreen, list[0].Key);
        Assert.True(list[0].IsAvailable);
        Assert.Equal(K("Shift+PrintScreen"), list[1].Key);
        Assert.InRange(list.Count, 1, 6);
        Assert.All(list, s => Assert.True(s.IsAvailable));
        Assert.Equal(list.Count, list.Select(s => s.Key).Distinct().Count());
        Assert.Equal(PrintScreen, rig.Advisor.Suggest("all-in-one", 3)[0].Key);
        Assert.Equal(3, rig.Advisor.Suggest("all-in-one", 3).Count);
    }

    [Fact]
    public void Suggest_AllInOne_ShowsPrintScreenHeldByAnotherApp_AsUnavailable_AndOffersFreeAlternatives()
    {
        var rig = new Rig();
        rig.Probe.TakenByOthers.UnionWith(new[] { PrintScreen, K("Shift+PrintScreen") });
        rig.Env.Processes.Add(ToolA.ProcessNames[^1]);
        var list = rig.Advisor.Suggest(AllInOnePreset);
        Assert.Equal(PrintScreen, list[0].Key);
        Assert.False(list[0].IsAvailable);
        Assert.False(list[0].CanMoveHere);
        Assert.Equal(HotkeyAvailabilityStatus.InUseByOtherApp, list[0].Availability.Status);
        Assert.Contains(ToolA.DisplayName, list[0].Availability.LikelyOwners);
        Assert.DoesNotContain(list, s => s.Key == K("Shift+PrintScreen"));   // taken non-anchor keys are filtered out
        Assert.All(list.Skip(1), s => Assert.True(s.IsAvailable));
        Assert.Equal(K("Ctrl+PrintScreen"), list[1].Key);
    }

    [Fact]
    public void Suggest_AllInOnePreset_OffersMovingPrintScreenFromTheCaptureHotkey()
    {
        var rig = new Rig();
        rig.RegisterDefaults();
        var list = rig.Advisor.Suggest(AllInOnePreset);
        Assert.Equal(PrintScreen, list[0].Key);
        Assert.True(list[0].CanMoveHere);
        Assert.Equal(HotkeyIds.GlobalCapture, list[0].Availability.ShotwiseBindingId);
        // Shift+PrintScreen is the configured fallback, Ctrl+Shift+PrintScreen etc. belong to presets: not offered
        Assert.DoesNotContain(list, s => s.Key == K("Shift+PrintScreen"));
        Assert.All(list.Skip(1), s => Assert.True(s.IsAvailable));
    }

    [Fact]
    public void Suggest_PerPurpose_SkipsTheBindingsOwnKey_AndOtherShotwiseKeys()
    {
        var rig = new Rig();
        rig.RegisterDefaults();
        var grab = rig.Advisor.Suggest(HotkeyIds.Preset(PresetDefaults.GrabTextId));
        Assert.Equal(K("Ctrl+Alt+F6"), grab[0].Key);   // its own Ctrl+Alt+PrintScreen is skipped
        var video = rig.Advisor.Suggest("video");
        Assert.Equal(K("Ctrl+Alt+F5"), video[0].Key);   // Alt+Shift+PrintScreen belongs to the Video preset
        var pano = rig.Advisor.Suggest(HotkeyIds.Preset(PresetDefaults.PanoramicId));
        Assert.Equal(K("Ctrl+Alt+F7"), pano[0].Key);
        var repeat = rig.Advisor.Suggest(HotkeyIds.Repeat);
        Assert.Equal(K("Ctrl+Alt+F11"), repeat[0].Key);
        // generic fallbacks fill the list
        Assert.Equal(6, rig.Advisor.Suggest(HotkeyIds.Preset(PresetDefaults.StepCaptureId)).Count);
    }

    [Fact]
    public void Suggest_NeverOffersReservedOrTakenKeys()
    {
        var rig = new Rig();
        foreach (var (key, _) in HotkeySuggestionCatalog.For(HotkeyPurpose.Image).Take(4)) rig.Probe.TakenByOthers.Add(key);
        var list = rig.Advisor.Suggest(HotkeyPurpose.Image, null, 6, null);
        Assert.Equal(6, list.Count);
        Assert.All(list, s => Assert.True(s.IsAvailable));
        Assert.All(list, s => Assert.Equal(HotkeyReservation.None, HotkeyRules.Reservation(s.Key)));
        Assert.Equal(K("Ctrl+Alt+F3"), list[0].Key);
    }

    [Fact]
    public void SuggestionCatalog_AvoidsInAppShortcutsAndAltGrCharacters()
    {
        // Ctrl+Shift+letter/digit are common in-app shortcuts; Ctrl+Alt+letter/digit is AltGr (typing) on many layouts.
        static bool Typing(VirtualKey k) => k is >= VirtualKey.A and <= VirtualKey.Z or >= VirtualKey.D0 and <= VirtualKey.D9;
        foreach (var purpose in Enum.GetValues<HotkeyPurpose>())
        foreach (var (key, _) in HotkeySuggestionCatalog.For(purpose))
        {
            if (purpose == HotkeyPurpose.Webcam) continue;   // recording-only binding, registered only while recording
            var m = key.Modifiers;
            Assert.False(m == (ModifierKeys.Control | ModifierKeys.Shift) && Typing(key.Key), $"{purpose}: {key.ToDisplayString()}");
            Assert.False(m == (ModifierKeys.Control | ModifierKeys.Alt) && Typing(key.Key), $"{purpose}: {key.ToDisplayString()}");
            Assert.Equal(HotkeyReservation.None, HotkeyRules.Reservation(key));
        }
    }

    [Fact]
    public void ProbeResults_AreCachedForTwoSeconds()
    {
        var rig = new Rig();
        rig.Advisor.CheckAvailability(K("Ctrl+Shift+X"));
        rig.Advisor.CheckAvailability(K("Ctrl+Shift+X"));
        Assert.Equal(1, rig.Probe.Calls);
        rig.Now = rig.Now.AddSeconds(1.5);
        rig.Advisor.CheckAvailability(K("Ctrl+Shift+X"));
        Assert.Equal(1, rig.Probe.Calls);
        rig.Now = rig.Now.AddSeconds(1);
        rig.Probe.TakenByOthers.Add(K("Ctrl+Shift+X"));
        Assert.Equal(HotkeyAvailabilityStatus.InUseByOtherApp, rig.Advisor.CheckAvailability(K("Ctrl+Shift+X")).Status);
        Assert.Equal(2, rig.Probe.Calls);
        rig.Advisor.Invalidate();
        rig.Advisor.CheckAvailability(K("Ctrl+Shift+X"));
        Assert.Equal(3, rig.Probe.Calls);
    }

    [Fact]
    public void Purposes_FromIdsKindsAndPresets()
    {
        Assert.Equal(HotkeyPurpose.AllInOne, HotkeyRules.ParsePurpose(HotkeyIds.GlobalCapture));
        Assert.Equal(HotkeyPurpose.CaptureFallback, HotkeyRules.ParsePurpose(HotkeyIds.GlobalCaptureFallback));
        Assert.Equal(HotkeyPurpose.AllInOne, HotkeyRules.ParsePurpose("All-in-One"));
        Assert.Equal(HotkeyPurpose.GrabText, HotkeyRules.ParsePurpose("grab-text"));
        Assert.Equal(HotkeyPurpose.Panoramic, HotkeyRules.ParsePurpose("scrolling"));
        Assert.Equal(HotkeyPurpose.VideoStop, HotkeyRules.ParsePurpose(HotkeyIds.VideoStop));
        Assert.Equal(HotkeyPurpose.Generic, HotkeyRules.ParsePurpose("capture.preset.mine"));
        Assert.Equal(HotkeyPurpose.Video, HotkeyRules.PurposeOf(new CapturePreset { Mode = CaptureMode.Video }));
        Assert.Equal(HotkeyPurpose.Panoramic, HotkeyRules.PurposeOf(new CapturePreset { Mode = CaptureMode.Image, Selection = SelectionType.ScrollingWindow }));
        Assert.Equal(HotkeyPurpose.AllInOne, HotkeyRules.PurposeOf(new CapturePreset { Mode = CaptureMode.AllInOne }));
        // a custom preset's binding carries its purpose, so its suggestions follow its mode
        var b = HotkeyBindings.Collect(new HotkeySettings(), new[] { new CapturePreset { Id = "mine", Name = "Mine", Mode = CaptureMode.Video } }, id => id).Last();
        Assert.Equal(HotkeyPurpose.Video, b.Purpose);
    }

    // ------------------------------------------------------------------ Move here

    [Fact]
    public void Move_ClearsTheOtherBinding_AndAssignsTheTarget()
    {
        var hk = HotkeySettings.Defaults();
        var presets = PresetDefaults.CreateBuiltIns().ToList();
        var changed = HotkeyBindings.Move(hk, presets, PrintScreen, HotkeyIds.GlobalCapture, AllInOnePreset);
        Assert.Equal(new[] { HotkeyIds.GlobalCapture, AllInOnePreset }, changed);
        Assert.Null(hk.GlobalCapture);
        Assert.Equal(PrintScreen, presets.First(p => p.Id == PresetDefaults.AllInOneId).Hotkey);
        Assert.Equal(K("Shift+PrintScreen"), hk.GlobalCaptureFallback);   // untouched

        // preset → settings, and a source that no longer has the key is left alone
        var back = HotkeyBindings.Move(hk, presets, PrintScreen, AllInOnePreset, HotkeyIds.GlobalCapture);
        Assert.Equal(PrintScreen, hk.GlobalCapture);
        Assert.Null(presets.First(p => p.Id == PresetDefaults.AllInOneId).Hotkey);
        Assert.Equal(2, back.Count);
        var stale = HotkeyBindings.Move(hk, presets, K("Ctrl+Shift+X"), HotkeyIds.Repeat, HotkeyIds.ShowHide);
        Assert.Equal(new[] { HotkeyIds.ShowHide }, stale);
        Assert.Equal(K("Ctrl+Shift+F11"), hk.RepeatLastCapture);
        Assert.Equal(K("Ctrl+Shift+X"), hk.ShowHideApp);
    }

    [Fact]
    public async Task Release_PersistsTheClearedBinding()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        var settings = new SettingsService(paths);
        var repo = new PresetRepository(paths);
        await HotkeyBindings.ReleaseAsync(settings, repo, HotkeyIds.GlobalCapture, PrintScreen);
        Assert.Null(settings.Current.Hotkeys.GlobalCapture);

        var video = HotkeyIds.Preset(PresetDefaults.VideoId);
        await HotkeyBindings.ReleaseAsync(settings, repo, video, K("Ctrl+Shift+X"));   // not its key: no-op
        Assert.Equal(K("Alt+Shift+PrintScreen"), repo.Find(PresetDefaults.VideoId)!.Hotkey);
        await HotkeyBindings.ReleaseAsync(settings, repo, video, K("Alt+Shift+PrintScreen"));
        Assert.Null(new PresetRepository(paths).Find(PresetDefaults.VideoId)!.Hotkey);
        Assert.Null(new SettingsService(paths).Current.Hotkeys.GlobalCapture);
    }

    [Fact]
    public void SettingsBindings_RoundTripEveryId()
    {
        var hk = new HotkeySettings();
        foreach (var id in HotkeyBindings.SettingIds)
        {
            Assert.True(HotkeyBindings.Set(hk, id, K("Ctrl+Alt+9")));
            Assert.Equal(K("Ctrl+Alt+9"), HotkeyBindings.Get(hk, id));
            HotkeyBindings.Set(hk, id, null);
            Assert.Null(HotkeyBindings.Get(hk, id));
        }
        Assert.False(HotkeyBindings.Set(hk, AllInOnePreset, PrintScreen));
        Assert.True(HotkeyIds.SameBinding(HotkeyIds.GlobalCapture, HotkeyIds.GlobalCaptureFallback));
        Assert.False(HotkeyIds.SameBinding(HotkeyIds.GlobalCapture, HotkeyIds.Repeat));
        Assert.Equal(PresetDefaults.AllInOneId, HotkeyIds.PresetIdOf(AllInOnePreset));
    }
}
