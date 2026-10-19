using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.UI.Capture.Coordinator;

/// <summary>A hotkey that could not be registered (shown in preferences and as a notification).</summary>
public sealed record HotkeyProblem(string Label, Hotkey Key, HotkeyRegistrationResult Result);

public sealed partial class CaptureCoordinator : IHotkeyStatusSource
{
    private readonly HashSet<string> _registeredIds = new();
    private IReadOnlyList<HotkeyProblem> _problems = Array.Empty<HotkeyProblem>();
    private IReadOnlyList<HotkeyStatus> _statuses = Array.Empty<HotkeyStatus>();
    private string _lastProblemSignature = "";
    private string _configSignature = "";

    /// <summary>Hotkeys that failed to register in the last pass.</summary>
    public IReadOnlyList<HotkeyProblem> HotkeyProblems => _problems;

    /// <summary>Raised on the UI thread after each registration pass.</summary>
    public event EventHandler? HotkeysRegistered;

    /// <summary>Per-hotkey outcome of the last registration pass (global hotkeys first, then presets in display order).</summary>
    public IReadOnlyList<HotkeyStatus> Current => _statuses;

    public HotkeyStatus? Find(string id) => _statuses.FirstOrDefault(s => s.Id == id);

    event EventHandler? IHotkeyStatusSource.Changed { add => HotkeysRegistered += value; remove => HotkeysRegistered -= value; }

    /// <summary>The key that currently triggers the global (All-in-One / active preset) capture, or null when none is registered.</summary>
    public Hotkey? EffectiveCaptureHotkey => Find(HotkeyIds.GlobalCapture)?.Effective;

    /// <summary>Global-capture fallbacks: the configured fallback when the preferred key is PrintScreen-based or explicitly set.</summary>
    private static IEnumerable<Hotkey> CaptureFallbacks(Shotwise.Core.Settings.HotkeySettings hk)
    {
        if (hk.GlobalCaptureFallback is { IsEmpty: false } f) yield return f;
    }

    /// <summary>Signature of everything the user configures (runtime fields excluded) - unchanged = no re-registration needed.</summary>
    private static string ConfigSignature(Shotwise.Core.Settings.HotkeySettings hk)
        => string.Join('|', hk.GlobalCapture, hk.GlobalCaptureFallback, hk.RepeatLastCapture, hk.ShowHideApp, hk.VideoStartPauseResume, hk.VideoStop,
            hk.ScreenDrawToggle, hk.WebcamFullscreenToggle, hk.AllowOverrideOtherAssignments);

    /// <summary>Re-registers only when a configured hotkey changed (settings updates of runtime fields must not loop).</summary>
    private void RegisterHotkeysIfConfigChanged()
    {
        if (ConfigSignature(_settings.Current.Hotkeys) != _configSignature) RegisterHotkeys();
    }

    private int _suspended;

    /// <summary>
    /// Releases every global hotkey until the returned handle is disposed (UI thread), so a hotkey recorder in Preferences receives
    /// combinations such as Ctrl+Shift+PrintScreen instead of starting a capture. Nested calls are counted.
    /// </summary>
    public IDisposable SuspendHotkeys()
    {
        if (_suspended++ == 0 && Opt<IHotkeyService>() is { } svc)
        {
            foreach (var id in _registeredIds) svc.Unregister(id);
            _registeredIds.Clear();
            _log.LogDebug("Global hotkeys suspended");
        }
        return new Resume(this);
    }

    private sealed class Resume(CaptureCoordinator owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--owner._suspended == 0) owner.RegisterHotkeys();
        }
    }

    /// <summary>(Re)registers the global hotkeys from settings and presets. Safe to call repeatedly.</summary>
    public void RegisterHotkeys()
    {
        if (!Dsp.CheckAccess()) { Dsp.BeginInvoke(new Action(RegisterHotkeys)); return; }
        if (_suspended > 0) return;
        var svc = Opt<IHotkeyService>();
        if (svc is null) return;

        foreach (var id in _registeredIds) svc.Unregister(id);
        _registeredIds.Clear();

        var hk = _settings.Current.Hotkeys;
        _configSignature = ConfigSignature(hk);
        bool allowOverride = hk.AllowOverrideOtherAssignments;
        var problems = new List<HotkeyProblem>();
        var statuses = new List<HotkeyStatus>();
        Hotkey? effectiveCapture = null;
        bool captureFallback = false;

        void Reg(string id, string label, Hotkey? key, Action action, IEnumerable<Hotkey>? fallbacks = null, bool isPreset = false)
        {
            if (key is not { } k || k.IsEmpty) { statuses.Add(new HotkeyStatus(id, label, null, null, HotkeyState.Off, isPreset)); return; }
            if (!k.IsValidGlobalHotkey)
            {
                problems.Add(new HotkeyProblem(label, k, HotkeyRegistrationResult.Unsupported));
                statuses.Add(new HotkeyStatus(id, label, k, null, HotkeyState.Unsupported, isPreset));
                return;
            }
            try
            {
                var reg = svc.RegisterWithFallback(id, k, fallbacks ?? Array.Empty<Hotkey>(), () => Dsp.BeginInvoke(new Action(() => SafeInvoke(action, label))), allowOverride);
                statuses.Add(new HotkeyStatus(id, label, k, reg.Effective, HotkeyRegistration.StateOf(reg.Result, reg.UsedFallback), isPreset));
                if (reg.Result == HotkeyRegistrationResult.Ok)
                {
                    _registeredIds.Add(id);
                    if (reg.UsedFallback) _log.LogInformation("Hotkey {Id} ({Label}): {Preferred} is {Result}; using fallback {Fallback}", id, label, k.ToDisplayString(), reg.PreferredResult, reg.Effective?.ToDisplayString());
                    else _log.LogInformation("Hotkey {Id} ({Label}) registered as {Key}", id, label, k.ToDisplayString());
                    if (id == HotkeyIds.GlobalCapture) { effectiveCapture = reg.Effective; captureFallback = reg.UsedFallback; }
                }
                else
                {
                    problems.Add(new HotkeyProblem(label, k, reg.Result));
                    _log.LogWarning("Hotkey {Id} ({Label}) {Key} not registered: {Result}", id, label, k.ToDisplayString(), reg.Result);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Registering hotkey {Label} failed", label);
                problems.Add(new HotkeyProblem(label, k, HotkeyRegistrationResult.Unsupported));
                statuses.Add(new HotkeyStatus(id, label, k, null, HotkeyState.Unsupported, isPreset));
            }
        }

        Reg(HotkeyIds.GlobalCapture, CS.Get("Hotkey_Capture"), hk.GlobalCapture, () => _ = RunAsync(_presets.Active), CaptureFallbacks(hk));
        Reg(HotkeyIds.Repeat, CS.Get("Hotkey_Repeat"), hk.RepeatLastCapture, () => _ = RepeatLastAsync());
        Reg(HotkeyIds.ShowHide, CS.Get("Hotkey_ShowHide"), hk.ShowHideApp, ToggleAppVisibility);
        Reg(HotkeyIds.VideoPauseResume, CS.Get("Hotkey_VideoPause"), hk.VideoStartPauseResume, VideoStartPauseResume);
        Reg(HotkeyIds.VideoStop, CS.Get("Hotkey_VideoStop"), hk.VideoStop, VideoStop);
        // ScreenDraw and webcam-fullscreen only act during a recording, and their defaults (Ctrl+Shift+F8 / Ctrl+Shift+F)
        // would otherwise be stolen system-wide (Ctrl+Shift+F is "find in files" in many apps and Bring Forward in our
        // editor), so they are registered only while recording (P3). RecordVideoAsync re-runs this method on start/end.
        if (_recording is not null)
        {
            Reg(HotkeyIds.ScreenDraw, CS.Get("Hotkey_Draw"), hk.ScreenDrawToggle, ToggleScreenDraw);
            Reg(HotkeyIds.WebcamFullscreen, CS.Get("Hotkey_Webcam"), hk.WebcamFullscreenToggle, ToggleWebcamFullscreen);
        }
        else
        {
            statuses.Add(new HotkeyStatus(HotkeyIds.ScreenDraw, CS.Get("Hotkey_Draw"), hk.ScreenDrawToggle, null, hk.ScreenDrawToggle is null ? HotkeyState.Off : HotkeyState.RecordingOnly));
            statuses.Add(new HotkeyStatus(HotkeyIds.WebcamFullscreen, CS.Get("Hotkey_Webcam"), hk.WebcamFullscreenToggle, null, hk.WebcamFullscreenToggle is null ? HotkeyState.Off : HotkeyState.RecordingOnly));
        }
        foreach (var p in _presets.All)
        {
            var preset = p;
            Reg(HotkeyIds.Preset(preset.Id), preset.Name, preset.Hotkey, () => _ = RunAsync(_presets.Find(preset.Id) ?? preset), isPreset: true);
        }

        _problems = problems;
        _statuses = statuses;
        RememberEffectiveCapture(hk, effectiveCapture, captureFallback);
        HotkeysRegistered?.Invoke(this, EventArgs.Empty);

        var signature = string.Join('|', problems.Select(x => x.Label + x.Key + x.Result));
        if (problems.Count > 0 && signature != _lastProblemSignature)
        {
            var body = string.Join("\n", problems.Take(5).Select(x => $"{x.Label}: {x.Key.ToDisplayString()} ({DescribeResult(x.Result)})"));
            Toast(CS.Get("Hotkey_ProblemTitle"), body, NotificationKind.Warning);
        }
        _lastProblemSignature = signature;

        if (!captureFallback) _ = MaybePromptPrintScreenAsync(svc);
    }

    /// <summary>
    /// Persists the key the global capture runs on and, the first time the fallback is used, tells the user once
    /// ("PrintScreen is used by another app - Shotwise is using Shift+PrintScreen..."). The preferred key is retried on every start.
    /// </summary>
    private void RememberEffectiveCapture(Shotwise.Core.Settings.HotkeySettings hk, Hotkey? effective, bool usedFallback)
    {
        bool notify = usedFallback && !hk.FallbackNoticeShown;
        if (hk.EffectiveGlobalCapture != effective || notify)
            _settings.Update(s => { s.Hotkeys.EffectiveGlobalCapture = effective; if (notify) s.Hotkeys.FallbackNoticeShown = true; });
        if (notify && hk.GlobalCapture is { } preferred && effective is { } fb)
            Toast(CS.Get("Hotkey_FallbackTitle"), CS.Format("Hotkey_FallbackBody", preferred.ToDisplayString(), fb.ToDisplayString()), NotificationKind.Info);
    }

    private static string DescribeResult(HotkeyRegistrationResult r) => r switch
    {
        HotkeyRegistrationResult.Duplicate => CS.Get("Hotkey_Duplicate"),
        HotkeyRegistrationResult.InUseBySystemOrOtherApp => CS.Get("Hotkey_InUse"),
        _ => CS.Get("Hotkey_Unsupported"),
    };

    private void SafeInvoke(Action action, string label)
    {
        try { action(); }
        catch (Exception ex) { _log.LogError(ex, "Hotkey action '{Label}' failed", label); }
    }

    /// <summary>
    /// Windows can reserve PrintScreen for its own snipping tool. When the capture hotkey is PrintScreen and that setting is on, ask once
    /// whether to open the Windows setting that releases the key.
    /// </summary>
    private async Task MaybePromptPrintScreenAsync(IHotkeyService svc)
    {
        var hk = _settings.Current.Hotkeys;
        if (hk.PrintScreenConflictPrompted) return;
        if (hk.GlobalCapture is not { Key: VirtualKey.PrintScreen, Modifiers: ModifierKeys.None }) return;
        bool taken;
        try { taken = svc.IsPrintScreenTakenBySnipping(); } catch (Exception ex) { _log.LogDebug(ex, "PrintScreen check failed"); return; }
        if (!taken) return;
        _settings.Update(s => s.Hotkeys.PrintScreenConflictPrompted = true);
        if (Opt<IDialogService>() is not { } dialogs) return;
        var answer = await dialogs.Confirm(new ConfirmRequest(CS.Get("PrintScreen_Title"), CS.Get("PrintScreen_Body"), CS.Get("PrintScreen_Open"), CS.Get("PrintScreen_Keep")));
        if (answer.Confirmed)
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not open the Windows keyboard settings"); }
        }
    }

    // ------------------------------------------------------------------ implicit inputs: clipboard, printer, mobile

    private CapturePreset? _listenPreset;
    private IClipboardImageWatcher? _watcher;
    private IPrinterCaptureService? _printer;
    private IMobileConnectServer? _mobile;

    private void WireInputs()
    {
        _watcher = Opt<IClipboardImageWatcher>();
        if (_watcher is not null) { _watcher.ImageCopied += OnClipboardImage; UpdateClipboardWatcher(); }
        _printer = Opt<IPrinterCaptureService>();
        if (_printer is not null) _printer.PagesCaptured += OnPrinterPages;
        _mobile = Opt<IMobileConnectServer>();
        if (_mobile is not null) _mobile.FileReceived += OnMobileFile;
    }

    private void UnwireInputs()
    {
        if (_watcher is not null) _watcher.ImageCopied -= OnClipboardImage;
        if (_printer is not null) _printer.PagesCaptured -= OnPrinterPages;
        if (_mobile is not null) _mobile.FileReceived -= OnMobileFile;
    }

    private void UpdateClipboardWatcher()
    {
        if (_watcher is null) return;
        _watcher.IsEnabled = Prefs.OpenScreenshotsFromSystemTools || _listenPreset is not null;
    }

    /// <summary>"Listen to clipboard" selection type: toggles auto-import of images copied by other applications.</summary>
    private void StartListening(CapturePreset preset)
    {
        if (_listenPreset is not null)
        {
            _listenPreset = null;
            UpdateClipboardWatcher();
            Toast(CS.Get("Listen_Title"), CS.Get("Listen_Stopped"), NotificationKind.Info);
            return;
        }
        _listenPreset = preset;
        UpdateClipboardWatcher();
        Toast(CS.Get("Listen_Title"), CS.Get("Listen_Started"), NotificationKind.Info);
    }

    private CapturePreset ImplicitPreset(string name) => new()
    {
        Name = name,
        Mode = CaptureMode.Image,
        Selection = SelectionType.Clipboard,
        PreviewInEditor = true,
        IsBuiltIn = false,
    };

    private void OnClipboardImage(object? sender, BgraImage image)
        => Dsp.BeginInvoke(new Action(() => _ = ImportImageAsync(image, _listenPreset ?? ImplicitPreset(CS.Get("Input_Clipboard")), CS.Get("Input_Clipboard"))));

    private void OnPrinterPages(object? sender, IReadOnlyList<BgraImage> pages)
        => Dsp.BeginInvoke(new Action(async () =>
        {
            foreach (var page in pages) await ImportImageAsync(page, ImplicitPreset(CS.Get("Input_Printer")), CS.Get("Input_Printer"));
        }));

    private void OnMobileFile(object? sender, ReceivedFile file)
        => Dsp.BeginInvoke(new Action(async () => await ImportMobileFileAsync(file)));

    private async Task ImportImageAsync(BgraImage image, CapturePreset preset, string source)
    {
        try
        {
            var md = DocumentMetadata.Create() with { SourcePresetId = preset.Id, WindowTitle = source };
            await ProcessImageAsync(new CaptureResult(image, md), preset, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importing an image from {Source} failed", source);
            Toast(CS.Get("Error_CaptureTitle"), ex.Message, NotificationKind.Error);
        }
    }

    private async Task ImportMobileFileAsync(ReceivedFile file)
    {
        try
        {
            string ext = System.IO.Path.GetExtension(file.OriginalName).ToLowerInvariant();
            bool isVideo = file.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || ext is ".mp4" or ".mov" or ".m4v" or ".webm";
            var source = CS.Format("Input_Mobile", file.DeviceName ?? "");
            if (isVideo)
            {
                VideoInfo? info = null;
                try { if (Opt<IVideoService>() is { } vs) info = await vs.ProbeAsync(file.Path); }
                catch (Exception ex) { _log.LogDebug(ex, "Probing the received video failed"); }
                var md = DocumentMetadata.Create() with { WindowTitle = source };
                var result = new VideoCaptureResult(file.Path, info?.Duration ?? TimeSpan.Zero, new Shotwise.Core.Geometry.Size2(info?.Width ?? 0, info?.Height ?? 0), md, false);
                _events.Publish(result);
                if (Opt<IEditorHost>() is { } editor) await editor.OpenVideoAsync(file.Path);
                return;
            }
            var codec = Need<IImageCodec>();
            var image = await Task.Run(() => codec.Load(file.Path));
            await ImportImageAsync(image, ImplicitPreset(source), source);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importing the received mobile file failed");
            Toast(CS.Get("Error_CaptureTitle"), ex.Message, NotificationKind.Error);
        }
    }
}
