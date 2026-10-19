using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.Core.Sharing;
using Shotwise.UI.Capture.Hud;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Capture.Views;

namespace Shotwise.UI.Capture.Coordinator;

public sealed partial class CaptureCoordinator
{
    /// <summary>
    /// Post-capture pipeline for a raster: preset effects -> clipboard -> publish <see cref="ICaptureEvents"/> -> editor -> open-in and
    /// share outputs (background). Returns the processed result.
    /// </summary>
    internal async Task<CaptureResult> ProcessImageAsync(CaptureResult cr, CapturePreset preset, CancellationToken ct)
    {
        var image = cr.Image;
        var annotations = cr.InitialAnnotations;

        var effects = preset.Effects.Where(e => e.Enabled).ToList();
        if (effects.Count > 0 && Opt<IEffectProcessor>() is { } fx)
        {
            effects = await PromptCaptionsAsync(effects);
            var md = cr.Metadata;
            var applied = await Task.Run(() => fx.ApplyAll(image, effects, md), ct);
            image = applied.Image;
            if (annotations is { Count: > 0 } && (applied.ContentOffset.X != 0 || applied.ContentOffset.Y != 0))
            {
                var moved = new List<Shotwise.Core.Annotations.Annotation>(annotations.Count);
                foreach (var a in annotations)
                {
                    var c = a.DeepClone();
                    c.Translate(applied.ContentOffset.X, applied.ContentOffset.Y);
                    moved.Add(c);
                }
                annotations = moved;
            }
        }

        var result = new CaptureResult(image, cr.Metadata, annotations);
        var doc = BuildDocument(result);
        bool isSteps = preset.Selection == SelectionType.StepCapture;

        if (preset.CopyToClipboard) CopyToClipboard(image);

        try { _events.Publish(result); }
        catch (Exception ex) { _log.LogWarning(ex, "A capture event subscriber failed"); }

        if (preset.PreviewInEditor)
        {
            if (Opt<IEditorHost>() is { } editor)
            {
                try { await editor.OpenAsync(doc); }
                catch (Exception ex) { _log.LogError(ex, "Opening the capture in the editor failed"); }
            }
            else
            {
                Toast(CS.Get("Capture_Done"), CS.Get("Capture_DoneNoEditor"), NotificationKind.Success);
            }
        }
        else if (preset.Selection != SelectionType.GrabText)
        {
            Toast(CS.Get("Capture_Done"), CS.Format("Capture_DoneSize", image.Width, image.Height), NotificationKind.Success);
        }

        if (!string.IsNullOrWhiteSpace(preset.OpenInProgramId) || preset.Outputs.Count > 0)
        {
            var reviewed = await ConfirmImportedOutputsAsync(preset);
            _ = RunOutputsAsync(doc, reviewed, isSteps);
        }
        return result;
    }

    /// <summary>
    /// R2-P1 (UI side): a preset that came from an imported file (<see cref="CapturePreset.RequiresReview"/>) may name a program to run
    /// ("Open in") and share destinations. Before its first use the user sees exactly which program and destinations will receive the
    /// capture and must allow them; the decision to allow is stored on the preset (RequiresReview cleared). Declining, or having no
    /// dialog service, runs this capture without those outputs (clipboard copies stay).
    /// </summary>
    internal async Task<CapturePreset> ConfirmImportedOutputsAsync(CapturePreset preset)
    {
        var lines = DescribeReviewedOutputs(preset, Opt<IShareService>());
        if (!preset.RequiresReview || lines.Count == 0) return preset;
        bool allowed = false;
        if (Opt<Shotwise.UI.Common.Dialogs.IDialogService>() is { } dialogs)
        {
            try
            {
                var answer = await dialogs.Confirm(new Shotwise.UI.Common.Dialogs.ConfirmRequest(CS.Get("Review_Title"),
                    CS.Format("Review_Body", preset.Name, string.Join("\n", lines)), CS.Get("Review_Allow"), CS.Get("Review_Skip")));
                allowed = answer.Confirmed;
            }
            catch (Exception ex) { _log.LogWarning(ex, "Imported preset review could not be shown"); }
        }
        if (allowed)
        {
            preset.RequiresReview = false;
            if (_presets.Find(preset.Id) is { } stored)
            {
                stored.RequiresReview = false;
                try { await _presets.SaveAsync(stored); } catch (Exception ex) { _log.LogWarning(ex, "Saving the reviewed preset failed"); }
            }
            _log.LogInformation("Outputs of imported preset {Preset} allowed by the user", preset.Name);
            return preset;
        }
        _log.LogInformation("Outputs of imported preset {Preset} skipped (not allowed)", preset.Name);
        Toast(CS.Get("Review_Title"), CS.Get("Review_Skipped"), NotificationKind.Warning);
        return WithoutReviewedOutputs(preset);
    }

    /// <summary>The program and destinations an imported preset would send a capture to (empty = nothing to review).</summary>
    internal static List<string> DescribeReviewedOutputs(CapturePreset preset, IShareService? share)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(preset.OpenInProgramId)) lines.Add(CS.Format("Review_OpenIn", preset.OpenInProgramId));
        foreach (var o in preset.Outputs.Where(o => o.DestinationId != KnownDestinations.Clipboard))
        {
            var name = share?.Destinations.FirstOrDefault(d => d.Id == o.DestinationId)?.DisplayName ?? o.DestinationId;
            lines.Add(CS.Format("Review_ShareTo", name) + (o.Options is not null ? " " + CS.Get("Review_WithOptions") : ""));
        }
        return lines;
    }

    /// <summary>Copy of the preset without "Open in" and share outputs (the clipboard output is kept).</summary>
    internal static CapturePreset WithoutReviewedOutputs(CapturePreset preset)
    {
        var c = preset.Clone();
        c.OpenInProgramId = null;
        c.Outputs = preset.Outputs.Where(o => o.DestinationId == KnownDestinations.Clipboard).ToList();
        return c;
    }

    /// <summary>
    /// "Open in" may only start a local executable: fully qualified, not UNC / network drive / device path, extension .exe
    /// (scripts such as .bat/.cmd/.ps1/.vbs/.js and shortcuts are refused) — R2-P1.
    /// </summary>
    internal static bool IsAllowedOpenInProgram(string path, Func<string, bool>? isNetworkDrive = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (!Path.IsPathFullyQualified(path)) return false;
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        isNetworkDrive ??= r => { try { return new DriveInfo(r).DriveType is DriveType.Network or DriveType.NoRootDirectory; } catch (ArgumentException) { return true; } };
        return !isNetworkDrive(root);
    }

    internal ShotDocument BuildDocument(CaptureResult result)
    {
        var doc = ShotDocument.FromImage(result.Image, result.Metadata, Opt<IImageCodec>());
        if (result.InitialAnnotations is { Count: > 0 } list && doc.Pages.Count > 0)
            foreach (var a in list) doc.Pages[0].Annotations.Add(a.DeepClone());
        doc.IsDirty = true;
        return doc;
    }

    private void CopyToClipboard(BgraImage image)
    {
        try
        {
            Need<IClipboardWriter>().SetImage(image, _settings.Current.Editor.PreventClipboardTransparency);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Copy to clipboard failed");
            Toast(CS.Get("Error_CaptureTitle"), Localizer_ErrorMessage(ErrorCode.ClipboardUnavailable), NotificationKind.Warning);
        }
    }

    private static string Localizer_ErrorMessage(ErrorCode code) => Shotwise.UI.Common.Resources.Localizer.ErrorMessage(code);

    /// <summary>Asks for the caption text of effects flagged "prompt at capture".</summary>
    private async Task<List<EffectSpec>> PromptCaptionsAsync(List<EffectSpec> effects)
    {
        var result = new List<EffectSpec>(effects.Count);
        foreach (var e in effects)
        {
            if (e is CaptionEffect { PromptAtCapture: true } cap)
            {
                var text = await InputPromptWindow.AskAsync(CS.Get("Caption_PromptTitle"), CS.Get("Caption_PromptLabel"), cap.CustomText ?? "", null);
                result.Add(text is null ? cap : cap with { CustomText = text, Fields = cap.Fields | CaptionFields.CustomText });
            }
            else result.Add(e);
        }
        return result;
    }

    // ------------------------------------------------------------------ outputs (open in / share)

    private async Task RunOutputsAsync(ShotDocument doc, CapturePreset preset, bool isSteps)
    {
        try
        {
            var share = Opt<IShareService>();
            if (share is null) { _log.LogInformation("No share service registered; outputs skipped"); return; }
            if (Opt<IPolicyService>() is { } policy && !policy.IsAllowed(PolicyFeature.Outputs)) return;
            var files = await share.MaterializeAsync(doc, "png", null, CancellationToken.None);
            if (isSteps) files = files.Select(f => f with { IsStepCapture = true }).ToList();
            await DeliverAsync(files, preset, share);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Capture outputs failed");
            Toast(CS.Get("Output_FailedTitle"), ex.Message, NotificationKind.Error);
        }
    }

    private async Task RunVideoOutputsAsync(VideoCaptureResult video, CapturePreset preset)
    {
        try
        {
            if (preset.CopyToClipboard && File.Exists(video.Mp4Path))
            {
                try { Need<IClipboardWriter>().SetFileDropList(new[] { video.Mp4Path }); }
                catch (Exception ex) { _log.LogWarning(ex, "Copying the video file to the clipboard failed"); }
            }
            var share = Opt<IShareService>();
            if (share is null) return;
            if (string.IsNullOrWhiteSpace(preset.OpenInProgramId) && preset.Outputs.Count == 0) return;
            if (Opt<IPolicyService>() is { } policy && !policy.IsAllowed(PolicyFeature.Outputs)) return;
            preset = await ConfirmImportedOutputsAsync(preset);
            var file = new ShareFile(video.Mp4Path, video.IsGif ? MediaKind.Gif : MediaKind.Video, video.IsGif ? "gif" : "mp4",
                Path.GetFileNameWithoutExtension(video.Mp4Path), (int)video.FrameSize.Width, (int)video.FrameSize.Height, video.Duration, false);
            await DeliverAsync(new[] { file }, preset, share);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Video outputs failed");
            Toast(CS.Get("Output_FailedTitle"), ex.Message, NotificationKind.Error);
        }
    }

    private async Task DeliverAsync(IReadOnlyList<ShareFile> files, CapturePreset preset, IShareService share)
    {
        if (!string.IsNullOrWhiteSpace(preset.OpenInProgramId))
            await OpenInAsync(preset.OpenInProgramId!, files, share);

        if (preset.Outputs.Count == 0) return;
        var ui = Opt<IShareUi>();
        if (ui is null) { _log.LogWarning("IShareUi is not registered; share outputs skipped"); return; }
        var compatible = share.For(files).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var output in preset.Outputs)
        {
            if (output.DestinationId == KnownDestinations.Clipboard) continue; // handled by CopyToClipboard
            if (!compatible.Contains(output.DestinationId))
            {
                _log.LogInformation("Destination {Id} does not accept this capture; skipped", output.DestinationId);
                continue;
            }
            var name = share.Destinations.FirstOrDefault(d => d.Id == output.DestinationId)?.DisplayName ?? output.DestinationId;
            try
            {
                var progress = new Progress<double>();
                // Per-binding destination options from the preset (CONTRACT A24); null = the destination's saved settings.
                var r = await OnUi(() => share.ShareAsync(output.DestinationId, files, ui, output.Options, progress, CancellationToken.None));
                switch (r.Outcome)
                {
                    case ShareOutcome.Succeeded:
                        var actions = new List<NotificationAction>();
                        if (r.OpenDestination is { } open) actions.Add(new NotificationAction(CS.Get("Output_Open"), open));
                        Toast(CS.Format("Output_SharedTo", name), r.Link ?? r.Message ?? "", NotificationKind.Success, actions);
                        break;
                    case ShareOutcome.Failed:
                        Toast(CS.Format("Output_FailedTo", name), r.Message ?? "", NotificationKind.Error);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sharing to {Destination} failed", output.DestinationId);
                Toast(CS.Format("Output_FailedTo", name), ex.Message, NotificationKind.Error);
            }
        }
    }

    private async Task OpenInAsync(string target, IReadOnlyList<ShareFile> files, IShareService share)
    {
        try
        {
            if (File.Exists(target))
            {
                if (!IsAllowedOpenInProgram(target))
                {
                    _log.LogWarning("Open in '{Target}' refused: only local .exe programs can be started", target);
                    Toast(CS.Get("Output_FailedTitle"), CS.Format("Output_ProgramRefused", Path.GetFileName(target)), NotificationKind.Warning);
                    return;
                }
                foreach (var f in files)
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, ArgumentList = { f.Path } });
                return;
            }
            var ui = Opt<IShareUi>();
            if (ui is not null && share.Destinations.Any(d => d.Id == target))
                await OnUi(() => share.ShareAsync(target, files, ui, null, CancellationToken.None));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Open in '{Target}' failed", target);
            Toast(CS.Get("Output_FailedTitle"), ex.Message, NotificationKind.Warning);
        }
    }

    // ------------------------------------------------------------------ step capture

    private enum StepCommand { Stop, Cancel, Restart, Pause }

    private async Task<CaptureOutcome?> StepCaptureAsync(CapturePreset preset, CancellationToken ct)
    {
        var service = Need<IStepCaptureService>();
        var folder = preset.SelectionOptions.Steps.OutputFolder
            ?? Path.Combine(Opt<IAppPaths>()?.Temp ?? Path.GetTempPath(), "Steps", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var options = preset.SelectionOptions.Steps with { OutputFolder = folder };

        var hud = new StepCaptureHudWindow();
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StepCommand>();
        hud.StopRequested += () => channel.Writer.TryWrite(StepCommand.Stop);
        hud.CancelRequested += () => channel.Writer.TryWrite(StepCommand.Cancel);
        hud.RestartRequested += () => channel.Writer.TryWrite(StepCommand.Restart);
        hud.PauseToggled += () => channel.Writer.TryWrite(StepCommand.Pause);
        using var reg = ct.Register(() => channel.Writer.TryWrite(StepCommand.Cancel));

        IStepCaptureSession? session = null;
        async Task StartSessionAsync()
        {
            session = service.CreateSession(options);
            session.Captured += (_, s) => hud.Dispatcher.BeginInvoke(new Action(() => hud.SetCount(session?.Count ?? s.Index, s.Image)));
            await session.StartAsync(ct);
            hud.SetCount(0, null);
            hud.SetPaused(false);
        }

        var work = MonitorOfPoint(NativeUi.CursorPx())?.WorkAreaPx ?? PrimaryWorkArea();
        hud.PlaceTopCenter(work);
        try
        {
            await StartSessionAsync();
            while (true)
            {
                var cmd = await channel.Reader.ReadAsync(CancellationToken.None);
                switch (cmd)
                {
                    case StepCommand.Pause:
                        if (hud.IsPaused) { session!.Resume(); hud.SetPaused(false); }
                        else { session!.Pause(); hud.SetPaused(true); }
                        break;
                    case StepCommand.Restart:
                        await session!.DisposeAsync();
                        await StartSessionAsync();
                        break;
                    case StepCommand.Cancel:
                        return null;
                    case StepCommand.Stop:
                    {
                        hud.CloseSafe();
                        var steps = await session!.StopAsync();
                        if (steps.Steps.Count == 0)
                        {
                            await ShowMessageAsync(CS.Get("Steps_Title"), CS.Get("Steps_None"), Shotwise.UI.Common.Dialogs.MessageKind.Info);
                            return null;
                        }
                        var md = (Opt<ICaptureMetadataProvider>() is { } mp ? await SafeMetadata(mp, ct) : DocumentMetadata.Create()) with { SourcePresetId = preset.Id };
                        var guide = StepGuideBuilder.Build(steps.Steps, md, CS.Get("Steps_GuideTitle"), DescribeStep);
                        var processed = await ProcessImageAsync(guide, preset, ct);
                        return new CaptureOutcome(CaptureMode.Image, Image: processed);
                    }
                }
            }
        }
        finally
        {
            hud.CloseSafe();
            if (session is not null) { try { await session.DisposeAsync(); } catch (Exception ex) { _log.LogDebug(ex, "Step session dispose failed"); } }
        }
    }

    private static string DescribeStep(StepCaptured s)
    {
        var action = string.IsNullOrWhiteSpace(s.Action) ? CS.Get("Steps_Click") : s.Action;
        return string.IsNullOrWhiteSpace(s.ElementName) ? CS.Format("Steps_DescNoElement", action) : CS.Format("Steps_Desc", action, s.ElementName);
    }
}
