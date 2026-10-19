using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.UI.Capture.Hud;

namespace Shotwise.UI.Capture.Coordinator;

/// <summary>
/// REC-11 "record as GIF" from the capture window: a video preset flagged "Save as GIF" converts the finished MP4 with
/// <see cref="IGifService"/> (Screen Video preset defaults) and delivers the GIF (<see cref="VideoCaptureResult.IsGif"/>) to the editor,
/// library and outputs; the MP4 is kept next to it.
/// <para>
/// Core has no per-preset field for this yet (CONTRACT-REQUEST filed for <c>RecordingOptions.SaveAsGif</c>); until it lands the flag is
/// stored as a list of preset ids in the settings' extension data under <see cref="GifPresetsKey"/>.
/// </para>
/// </summary>
public sealed partial class CaptureCoordinator
{
    internal const string GifPresetsKey = "uiCapture.recordAsGifPresetIds";

    /// <summary>Whether recordings made with <paramref name="presetId"/> are saved as animated GIF.</summary>
    public bool IsRecordAsGif(string presetId) => GifPresetIds(_settings.Current.Extra).Contains(presetId);

    /// <summary>Turns "Save as GIF" on or off for a preset (persisted).</summary>
    public void SetRecordAsGif(string presetId, bool on)
    {
        _settings.Update(s =>
        {
            var ids = GifPresetIds(s.Extra);
            if (on) ids.Add(presetId); else ids.Remove(presetId);
            s.Extra ??= new();
            if (ids.Count == 0) s.Extra.Remove(GifPresetsKey);
            else s.Extra[GifPresetsKey] = JsonSerializer.SerializeToElement(ids.Order(StringComparer.Ordinal).ToArray());
        });
        // Extension data is not a settings section, so Update does not persist a change that only touches it.
        _ = SaveSettingsQuietlyAsync();
    }

    private async Task SaveSettingsQuietlyAsync()
    {
        try { await _settings.SaveAsync(); } catch (Exception ex) { _log.LogWarning(ex, "Saving the GIF preference failed"); }
    }

    internal static HashSet<string> GifPresetIds(IReadOnlyDictionary<string, JsonElement>? extra)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (extra is null || !extra.TryGetValue(GifPresetsKey, out var value) || value.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in value.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 128 } id) result.Add(id);
        return result;
    }

    /// <summary>MP4 → GIF next to it (unique name). Returns the GIF result, or null (MP4 kept) when conversion is unavailable or fails.</summary>
    private async Task<VideoCaptureResult?> ConvertToGifAsync(VideoCaptureResult video, CancellationToken ct)
    {
        if (Opt<IGifService>() is not { } gifs) { _log.LogWarning("Save as GIF requested but no GIF service is registered"); return null; }
        if (Opt<IPolicyService>() is { } policy && !policy.IsAllowed(PolicyFeature.Video)) return null;
        var gifPath = UniquePath(Path.ChangeExtension(video.Mp4Path, ".gif"));
        var hud = new WaitHud(CS.Get("Toolbar_Cancel"));
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hud.StopRequested += () => link.Cancel();
        hud.SetText(CS.Get("Gif_Creating"), "0 %");
        hud.Show();
        hud.PlaceTopCenter(PrimaryWorkArea());
        try
        {
            var options = gifs.DefaultsFor(GifPreset.ScreenVideo, (int)video.FrameSize.Width, (int)video.FrameSize.Height);
            var progress = new Progress<double>(p => hud.SetText(CS.Get("Gif_Creating"), $"{Math.Clamp(p, 0, 1) * 100:0} %"));
            await gifs.CreateFromVideoAsync(video.Mp4Path, gifPath, options, progress, link.Token);
            _log.LogInformation("Recording saved as GIF: {Path}", gifPath);
            return video with { Mp4Path = gifPath, IsGif = true, FrameSize = new(options.Width, options.Height) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(gifPath);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Creating the GIF failed");
            TryDelete(gifPath);
            Toast(CS.Get("Recording_DoneTitle"), CS.Get("Gif_Failed"), NotificationKind.Warning);
            return null;
        }
        finally { hud.CloseSafe(); }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogDebug(ex, "Could not remove {Path}", path); }
    }
}
