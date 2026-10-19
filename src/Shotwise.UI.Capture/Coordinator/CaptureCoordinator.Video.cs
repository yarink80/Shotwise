using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.UI.Capture.Selection;

namespace Shotwise.UI.Capture.Coordinator;

public sealed partial class CaptureCoordinator
{
    private RecordingSession? _recording;

    /// <summary>True while a video recording session (ready window, countdown or recording) is active.</summary>
    public bool IsRecording => _recording is not null;

    /// <summary>Raised on the UI thread when a recording session starts or ends (tray recording icon).</summary>
    public event EventHandler? RecordingChanged;

    private async Task<(CaptureOutcome? Outcome, bool Redo)> RecordVideoAsync(CapturePreset preset, SelectionResult sel, CancellationToken ct)
    {
        var session = new RecordingSession(_sp, _settings, _log, preset, sel);
        _recording = session;
        RecordingChanged?.Invoke(this, EventArgs.Empty);
        RegisterHotkeys(); // adds the recording-only hotkeys (ScreenDraw, webcam fullscreen)
        try
        {
            var outcome = await session.RunAsync(ct);
            switch (outcome.End)
            {
                case RecordingEnd.Redo: return (null, true);
                case RecordingEnd.Cancelled: return (null, false);
            }

            // Always the path the recorder returned: it can be a unique sibling ("name (2).mp4") or the finalized temp file (FX-1).
            var video = outcome.Result!;
            _log.LogInformation("Recording finished: {Path} ({Duration})", video.Mp4Path, video.Duration);
            foreach (var key in session.Warnings.Where(k => k is "Recording_SourceLost" or "Recording_SavedToAlternatePath"))
                Toast(CS.Get("Recording_DoneTitle"), CS.Warning(key) + (key == "Recording_SavedToAlternatePath" ? "\n" + video.Mp4Path : ""), NotificationKind.Warning);
            if (IsRecordAsGif(preset.Id) && await ConvertToGifAsync(video, ct) is { } gif) video = gif;
            try { _events.Publish(video); }
            catch (Exception ex) { _log.LogWarning(ex, "A video event subscriber failed"); }

            if (preset.PreviewInEditor && Opt<Shotwise.Core.Documents.IEditorHost>() is { } editor)
            {
                try
                {
                    if (video.IsGif) await editor.OpenFileAsync(video.Mp4Path);
                    else await editor.OpenVideoAsync(video.Mp4Path);
                }
                catch (Exception ex) { _log.LogError(ex, "Opening the recording in the editor failed"); }
            }
            else
            {
                Toast(CS.Get("Recording_DoneTitle"), video.IsGif ? CS.Get("Gif_DoneBody") : CS.Format("Recording_DoneBody", RecordingHudFormat(video.Duration)), NotificationKind.Success);
            }
            _ = RunVideoOutputsAsync(video, preset);
            return (new CaptureOutcome(CaptureMode.Video, Video: video), false);
        }
        finally
        {
            _recording = null;
            RecordingChanged?.Invoke(this, EventArgs.Empty);
            RegisterHotkeys(); // releases the recording-only hotkeys
        }
    }

    private static string RecordingHudFormat(TimeSpan t) => Hud.RecordingHudWindow.Format(t);

    // ------------------------------------------------------------------ hotkey actions (video)

    /// <summary>Video start/pause/resume hotkey: pauses/resumes an active recording, otherwise starts the built-in video preset.</summary>
    public void VideoStartPauseResume()
    {
        if (_recording is { } s) { _ = s.PauseResumeAsync(); return; }
        var preset = _presets.Active.Mode == CaptureMode.Video ? _presets.Active : _presets.Find(PresetDefaults.VideoId) ?? _presets.Active;
        _ = RunAsync(preset);
    }

    /// <summary>Video stop hotkey.</summary>
    public void VideoStop() => _ = _recording?.StopAsync();

    /// <summary>ScreenDraw enter/exit hotkey (recording only).</summary>
    public void ToggleScreenDraw() => _recording?.ToggleScreenDraw();

    /// <summary>Webcam fullscreen/PiP swap hotkey (recording only).</summary>
    public void ToggleWebcamFullscreen() => _recording?.ToggleWebcamFullscreen();
}
