using Shotwise.Core.Capture;
using Shotwise.Core.Settings;

namespace Shotwise.Core.Platform;

public enum HotkeyAvailabilityStatus
{
    /// <summary>Free (or already held by the binding being edited).</summary>
    Available,
    /// <summary>Another Shotwise hotkey uses it (see <see cref="HotkeyAvailability.ShotwiseBindingId"/>).</summary>
    UsedByShotwise,
    /// <summary>Windows refused to register it: another app (or Windows) holds it. See <see cref="HotkeyAvailability.LikelyOwners"/>.</summary>
    InUseByOtherApp,
    /// <summary>Windows reserves it, or it is a bad global hotkey (see <see cref="HotkeyAvailability.Reservation"/>).</summary>
    Reserved,
    /// <summary>Cannot be a hotkey at all (empty, modifier only).</summary>
    Unsupported,
}

/// <summary>Result of <see cref="IHotkeyAdvisor.CheckAvailability(Hotkey, string?)"/>.</summary>
public sealed record HotkeyAvailability(Hotkey Key, HotkeyAvailabilityStatus Status)
{
    public string? ShotwiseBindingId { get; init; }
    public string? ShotwiseBindingName { get; init; }
    /// <summary>Apps that probably hold the key (best-effort guess; Windows does not say which process owns a hotkey).</summary>
    public IReadOnlyList<string> LikelyOwners { get; init; } = Array.Empty<string>();
    public HotkeyReservation Reservation { get; init; }
    /// <summary>English explanation for <see cref="HotkeyAvailabilityStatus.Reserved"/> (the UI shows a localized one).</summary>
    public string? Reason { get; init; }

    public bool IsAvailable => Status == HotkeyAvailabilityStatus.Available;
    /// <summary>True when Windows will not deliver the key to Shotwise (another app holds it, or Windows reserves it).</summary>
    public bool WillNotWork => Status is HotkeyAvailabilityStatus.InUseByOtherApp or HotkeyAvailabilityStatus.Reserved or HotkeyAvailabilityStatus.Unsupported;
}

/// <summary>A suggested hotkey and its current availability.</summary>
public sealed record HotkeySuggestion(Hotkey Key, HotkeyAvailability Availability)
{
    public bool IsAvailable => Availability.IsAvailable;
    /// <summary>Held by another Shotwise hotkey: the UI can offer "Move here" (clear that binding, assign this one).</summary>
    public bool CanMoveHere => Availability.Status == HotkeyAvailabilityStatus.UsedByShotwise && Availability.ShotwiseBindingId is not null;
}

/// <summary>One configured Shotwise hotkey (settings hotkey or preset hotkey).</summary>
/// <param name="Id">Registration id (<see cref="HotkeyIds"/>, incl. <see cref="HotkeyIds.GlobalCaptureFallback"/>).</param>
public sealed record HotkeyBinding(string Id, string Name, Hotkey? Key, HotkeyPurpose Purpose = HotkeyPurpose.Generic);

/// <summary>The configured Shotwise hotkeys (implemented by the capture coordinator from settings + presets).</summary>
public interface IHotkeyBindingSource
{
    IReadOnlyList<HotkeyBinding> Bindings { get; }
}

public enum HotkeyProbeResult
{
    /// <summary>RegisterHotKey succeeded (and was undone at once).</summary>
    Free,
    /// <summary>This Shotwise process already registered it.</summary>
    HeldByShotwise,
    /// <summary>RegisterHotKey failed: Windows or another app holds it.</summary>
    HeldByOtherApp,
    /// <summary>Not a registrable combination.</summary>
    Invalid,
}

/// <summary>Non-destructive registration test (implemented by the hotkey service on its own thread).</summary>
public interface IHotkeyProbe
{
    HotkeyProbeResult Probe(Hotkey key);
    /// <summary>What this process has registered (id → key).</summary>
    IReadOnlyDictionary<string, Hotkey> Registered { get; }
}

/// <summary>Availability check, likely owner detection and ranked suggestions for global hotkeys.</summary>
public interface IHotkeyAdvisor
{
    /// <summary>Whether <paramref name="key"/> can be used for <paramref name="forBindingId"/> (null = a new binding).</summary>
    HotkeyAvailability CheckAvailability(Hotkey key, string? forBindingId = null);

    /// <summary>As <see cref="CheckAvailability(Hotkey, string?)"/>, against <paramref name="staged"/> bindings (unsaved edits) instead of the saved ones.</summary>
    HotkeyAvailability CheckAvailability(Hotkey key, string? forBindingId, IReadOnlyList<HotkeyBinding>? staged);

    /// <summary>
    /// Ranked common options for a binding id (<see cref="HotkeyIds"/>) or a preset kind ("all-in-one", "video"...), filtered to available
    /// keys. For All-in-One, PrintScreen is always listed first, with its availability, so the UI can explain why it is not offered.
    /// </summary>
    IReadOnlyList<HotkeySuggestion> Suggest(string bindingIdOrPresetKind, int max = 6);

    /// <summary>As <see cref="Suggest(string, int)"/> with an explicit purpose / binding and staged bindings.</summary>
    IReadOnlyList<HotkeySuggestion> Suggest(HotkeyPurpose purpose, string? forBindingId, int max, IReadOnlyList<HotkeyBinding>? staged);

    /// <summary>Forgets cached probe results (the "Check" buttons call this).</summary>
    void Invalidate();
}

/// <summary>
/// <see cref="IHotkeyAdvisor"/> over a probe, the likely-owner environment and the configured bindings. Probe results are cached for
/// <see cref="CacheDuration"/> (suggestion rows probe a dozen keys at a time).
/// </summary>
public sealed class HotkeyAdvisor : IHotkeyAdvisor
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private readonly IHotkeyProbe _probe;
    private readonly IHotkeyEnvironment? _env;
    private readonly Func<IHotkeyBindingSource?> _bindings;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<Hotkey, (HotkeyProbeResult Result, DateTime At)> _cache = new();
    private readonly object _gate = new();

    public HotkeyAdvisor(IHotkeyProbe probe, IHotkeyEnvironment? environment, Func<IHotkeyBindingSource?> bindings, Func<DateTime>? clock = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _env = environment;
        _bindings = bindings ?? (() => null);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Number of real probes made (tests).</summary>
    public int ProbeCount { get; private set; }

    public void Invalidate() { lock (_gate) _cache.Clear(); }

    private IReadOnlyList<HotkeyBinding> SavedBindings()
    {
        try { return _bindings()?.Bindings ?? Array.Empty<HotkeyBinding>(); }
        catch (Exception) { return Array.Empty<HotkeyBinding>(); }
    }

    private IReadOnlyDictionary<string, Hotkey> Registered()
    {
        try { return _probe.Registered; }
        catch (Exception) { return new Dictionary<string, Hotkey>(); }
    }

    private HotkeyProbeResult CachedProbe(Hotkey key)
    {
        var now = _clock();
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit) && now - hit.At < CacheDuration) return hit.Result;
        }
        HotkeyProbeResult result;
        try { result = _probe.Probe(key); }
        catch (Exception) { result = HotkeyProbeResult.Free; }   // cannot tell: do not claim a conflict
        lock (_gate) { _cache[key] = (result, now); ProbeCount++; }
        return result;
    }

    public HotkeyAvailability CheckAvailability(Hotkey key, string? forBindingId = null) => CheckAvailability(key, forBindingId, null);

    public HotkeyAvailability CheckAvailability(Hotkey key, string? forBindingId, IReadOnlyList<HotkeyBinding>? staged)
    {
        if (HotkeyRules.IsUnsupported(key)) return new(key, HotkeyAvailabilityStatus.Unsupported);
        var reservation = HotkeyRules.Reservation(key);
        if (reservation != HotkeyReservation.None)
            return new(key, HotkeyAvailabilityStatus.Reserved) { Reservation = reservation, Reason = HotkeyRules.Describe(reservation) };

        var bindings = staged ?? SavedBindings();
        string NameOf(string id) => bindings.FirstOrDefault(b => b.Id == id)?.Name ?? id;

        // 1. registered by this Shotwise process (we hold it, so Windows cannot tell us about other apps)
        bool heldByUs = false;
        foreach (var (id, registeredKey) in Registered())
        {
            if (registeredKey != key) continue;
            heldByUs = true;
            if (HotkeyIds.SameBinding(id, forBindingId)) break;
            // an unsaved edit moved that binding off the key: it is released when the edit is applied
            if (staged is not null && !staged.Any(b => HotkeyIds.SameBinding(b.Id, id) && b.Key == key)) break;
            return new(key, HotkeyAvailabilityStatus.UsedByShotwise) { ShotwiseBindingId = id, ShotwiseBindingName = NameOf(id) };
        }

        // 2. ask Windows (RegisterHotKey + UnregisterHotKey)
        if (!heldByUs)
        {
            switch (CachedProbe(key))
            {
                case HotkeyProbeResult.HeldByOtherApp:
                    return new(key, HotkeyAvailabilityStatus.InUseByOtherApp) { LikelyOwners = _env is null ? Array.Empty<string>() : KnownHotkeyOwners.Detect(key, _env) };
                case HotkeyProbeResult.Invalid:
                    return new(key, HotkeyAvailabilityStatus.Unsupported);
                case HotkeyProbeResult.HeldByShotwise:
                    // registered between the snapshot and the probe
                    var owner = Registered().FirstOrDefault(p => p.Value == key).Key;
                    if (owner is not null && !HotkeyIds.SameBinding(owner, forBindingId))
                        return new(key, HotkeyAvailabilityStatus.UsedByShotwise) { ShotwiseBindingId = owner, ShotwiseBindingName = NameOf(owner) };
                    break;
            }
        }

        // 3. configured (not registered: recording-only, suspended, or an unsaved edit) for another Shotwise hotkey
        var other = bindings.FirstOrDefault(b => b.Key == key && !string.Equals(b.Id, forBindingId, StringComparison.Ordinal));
        if (other is not null) return new(key, HotkeyAvailabilityStatus.UsedByShotwise) { ShotwiseBindingId = other.Id, ShotwiseBindingName = other.Name };
        return new(key, HotkeyAvailabilityStatus.Available);
    }

    public IReadOnlyList<HotkeySuggestion> Suggest(string bindingIdOrPresetKind, int max = 6)
    {
        var binding = SavedBindings().FirstOrDefault(b => b.Id == bindingIdOrPresetKind);
        var purpose = binding?.Purpose is { } p && p != HotkeyPurpose.Generic ? p : HotkeyRules.ParsePurpose(bindingIdOrPresetKind);
        string? forId = binding?.Id ?? (bindingIdOrPresetKind.StartsWith("capture.", StringComparison.Ordinal) ? bindingIdOrPresetKind : null);
        return Suggest(purpose, forId, max, null);
    }

    public IReadOnlyList<HotkeySuggestion> Suggest(HotkeyPurpose purpose, string? forBindingId, int max, IReadOnlyList<HotkeyBinding>? staged)
    {
        var bindings = staged ?? SavedBindings();
        Hotkey? current = forBindingId is null ? null : bindings.FirstOrDefault(b => b.Id == forBindingId)?.Key;
        var result = new List<HotkeySuggestion>();
        if (max <= 0) return result;
        foreach (var (key, anchor) in HotkeySuggestionCatalog.For(purpose))
        {
            if (result.Count >= max) break;
            if (current == key) continue;   // already this binding's key
            var a = CheckAvailability(key, forBindingId, staged);
            if (a.Status is HotkeyAvailabilityStatus.Reserved or HotkeyAvailabilityStatus.Unsupported) continue;
            if (a.IsAvailable || (anchor && a.Status is HotkeyAvailabilityStatus.InUseByOtherApp or HotkeyAvailabilityStatus.UsedByShotwise))
                result.Add(new HotkeySuggestion(key, a));
        }
        return result;
    }
}

/// <summary>Reads, writes and collects the Shotwise hotkey bindings (settings hotkeys + preset hotkeys) by registration id.</summary>
public static class HotkeyBindings
{
    /// <summary>Settings-backed binding ids in display order.</summary>
    public static readonly IReadOnlyList<string> SettingIds = new[]
    {
        HotkeyIds.GlobalCapture, HotkeyIds.GlobalCaptureFallback, HotkeyIds.Repeat, HotkeyIds.ShowHide,
        HotkeyIds.VideoPauseResume, HotkeyIds.VideoStop, HotkeyIds.ScreenDraw, HotkeyIds.WebcamFullscreen,
    };

    public static bool IsSettingsBinding(string id) => SettingIds.Contains(id);

    public static Hotkey? Get(HotkeySettings hk, string id) => id switch
    {
        HotkeyIds.GlobalCapture => hk.GlobalCapture,
        HotkeyIds.GlobalCaptureFallback => hk.GlobalCaptureFallback,
        HotkeyIds.Repeat => hk.RepeatLastCapture,
        HotkeyIds.ShowHide => hk.ShowHideApp,
        HotkeyIds.VideoPauseResume => hk.VideoStartPauseResume,
        HotkeyIds.VideoStop => hk.VideoStop,
        HotkeyIds.ScreenDraw => hk.ScreenDrawToggle,
        HotkeyIds.WebcamFullscreen => hk.WebcamFullscreenToggle,
        _ => null,
    };

    /// <summary>Sets a settings hotkey; false for unknown ids (preset ids are not settings).</summary>
    public static bool Set(HotkeySettings hk, string id, Hotkey? key)
    {
        switch (id)
        {
            case HotkeyIds.GlobalCapture: hk.GlobalCapture = key; return true;
            case HotkeyIds.GlobalCaptureFallback: hk.GlobalCaptureFallback = key; return true;
            case HotkeyIds.Repeat: hk.RepeatLastCapture = key; return true;
            case HotkeyIds.ShowHide: hk.ShowHideApp = key; return true;
            case HotkeyIds.VideoPauseResume: hk.VideoStartPauseResume = key; return true;
            case HotkeyIds.VideoStop: hk.VideoStop = key; return true;
            case HotkeyIds.ScreenDraw: hk.ScreenDrawToggle = key; return true;
            case HotkeyIds.WebcamFullscreen: hk.WebcamFullscreenToggle = key; return true;
            default: return false;
        }
    }

    /// <summary>All bindings: settings hotkeys (named by <paramref name="settingName"/>) then presets.</summary>
    public static IReadOnlyList<HotkeyBinding> Collect(HotkeySettings hk, IEnumerable<CapturePreset> presets, Func<string, string> settingName)
    {
        var list = SettingIds.Select(id => new HotkeyBinding(id, settingName(id), Get(hk, id), HotkeyRules.ParsePurpose(id))).ToList();
        foreach (var p in presets)
            list.Add(new HotkeyBinding(HotkeyIds.Preset(p.Id), p.Name, p.Hotkey, HotkeyRules.PurposeOf(p)));
        return list;
    }

    /// <summary>
    /// "Move here": clears <paramref name="key"/> from <paramref name="fromId"/> (only when that binding still has it) and assigns it to
    /// <paramref name="toId"/>. Works on settings and on the given presets (mutated in place). Returns the ids that changed.
    /// </summary>
    public static IReadOnlyList<string> Move(HotkeySettings hk, IList<CapturePreset> presets, Hotkey key, string fromId, string toId)
    {
        var changed = new List<string>();
        if (string.Equals(fromId, toId, StringComparison.Ordinal)) return changed;
        if (Read(hk, presets, fromId) == key && Write(hk, presets, fromId, null)) changed.Add(fromId);
        if (Read(hk, presets, toId) != key && Write(hk, presets, toId, key)) changed.Add(toId);
        return changed;
    }

    private static Hotkey? Read(HotkeySettings hk, IList<CapturePreset> presets, string id)
        => HotkeyIds.PresetIdOf(id) is { } pid ? presets.FirstOrDefault(p => p.Id == pid)?.Hotkey : Get(hk, id);

    private static bool Write(HotkeySettings hk, IList<CapturePreset> presets, string id, Hotkey? key)
    {
        if (HotkeyIds.PresetIdOf(id) is { } pid)
        {
            var p = presets.FirstOrDefault(x => x.Id == pid);
            if (p is null) return false;
            p.Hotkey = key;
            return true;
        }
        return Set(hk, id, key);
    }

    /// <summary>
    /// Persists the "clear the other binding" half of a move: a settings hotkey through <paramref name="settings"/>, a preset hotkey by
    /// saving a clone. No-op when <paramref name="fromId"/> no longer has <paramref name="key"/>.
    /// </summary>
    public static async Task ReleaseAsync(ISettingsService settings, IPresetRepository presets, string fromId, Hotkey key)
    {
        if (HotkeyIds.PresetIdOf(fromId) is { } pid)
        {
            if (presets.Find(pid) is not { } p || p.Hotkey != key) return;
            var copy = p.Clone();
            copy.Hotkey = null;
            await presets.SaveAsync(copy).ConfigureAwait(false);
            return;
        }
        if (Get(settings.Current.Hotkeys, fromId) != key) return;
        settings.Update(s => Set(s.Hotkeys, fromId, null));
    }
}
