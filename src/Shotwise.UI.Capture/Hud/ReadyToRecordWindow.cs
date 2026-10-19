using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Geometry;
using Shotwise.Core.Media;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Hud;

public enum ReadyAction { Record, Cancel, Redo }

public sealed record ReadyResult(ReadyAction Action, RecordingOptions Options);

public sealed record ReadyContext(
    RecordingOptions Options,
    RectI RegionPx,
    RectI WorkAreaPx,
    string TargetDescription,
    IReadOnlyList<DeviceInfo> Microphones,
    IReadOnlyList<DeviceInfo> Webcams,
    bool MicrophoneMuted,
    bool NoiseRemovalAvailable,
    bool MicrophoneAllowed,
    bool SystemAudioAllowed,
    bool WebcamAllowed,
    IWebcamPreviewFactory? PreviewFactory,
    ILogger? Log);

/// <summary>The optional "Ready to record" window: device toggles, frame rate, webcam shape/corner, cursor effects; Record / Redo selection / Cancel.</summary>
public sealed class ReadyToRecordWindow : HudWindow
{
    private readonly ReadyContext _ctx;
    private RecordingOptions _o;
    private readonly TaskCompletionSource<ReadyResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebcamPreviewWindow? _preview;
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;
    private StackPanel? _camPanel;
    private StackPanel? _noisePanel;

    private ReadyToRecordWindow(ReadyContext ctx) : base(noActivate: false, HudKind.Card)
    {
        _ctx = ctx;
        _o = ctx.Options;
        ChromePadding = new Thickness(18);
        Content = Build();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Finish(ReadyAction.Cancel); }
            else if (e.Key == Key.Enter) { e.Handled = true; Finish(ReadyAction.Record); }
        };
        Closed += (_, _) => { _cts.Cancel(); ClosePreview(); _done.TrySetResult(new ReadyResult(ReadyAction.Cancel, _o)); };
    }

    public static async Task<ReadyResult> ShowAsync(ReadyContext ctx)
    {
        var w = new ReadyToRecordWindow(ctx);
        w.PlaceNear(ctx.RegionPx, ctx.WorkAreaPx);
        w.Activate();
        return await w._done.Task;
    }

    private UIElement Build()
    {
        var root = new StackPanel { Width = 392 };

        // header: coral record badge + title / target
        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 0, 12, 0), Child = Ui.Icon("Icon.Record.Filled", 20, "Shotwise.OnAccentBrush") };
        badge.SetResourceReference(Border.BackgroundProperty, "Shotwise.CaptureBrush");
        var desc = Ui.Label(_ctx.TargetDescription, 12, secondary: true);
        desc.TextTrimming = TextTrimming.CharacterEllipsis;
        desc.MaxWidth = 320;
        var head = Ui.Row(badge, Ui.Column(Ui.Label(CS.Get("Ready_Title"), 17, weight: FontWeights.SemiBold), desc));
        head.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(head);

        var inset = new StackPanel();
        var insetBorder = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(4), Child = inset };
        insetBorder.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface0Brush");
        root.Children.Add(insetBorder);

        // --- microphone
        var micOn = Ui.Switch(CS.Get("Ready_Microphone"), _o.RecordMicrophone && _ctx.MicrophoneAllowed, v => { _o = _o with { RecordMicrophone = v }; Sync(); }, "Icon.Mic.Color");
        micOn.IsEnabled = _ctx.MicrophoneAllowed && _ctx.Microphones.Count > 0;
        if (_ctx.Microphones.Count == 0) { micOn.IsChecked = false; _o = _o with { RecordMicrophone = false }; }
        if (_ctx.Microphones.Count > 0)
            micOn.Trailing = DeviceChips.Create(_ctx.Microphones, _o.MicrophoneId ?? _ctx.Microphones.FirstOrDefault(m => m.IsDefault)?.Id ?? _ctx.Microphones[0].Id,
                CS.Get("Ready_SystemDefault"), id => _o = _o with { MicrophoneId = id }, includeSystemDefault: false, maxWidth: 110);
        inset.Children.Add(micOn);
        if (_ctx.Microphones.Count == 0) inset.Children.Add(Note(CS.Get("Ready_NoMicrophones")));
        if (_ctx.MicrophoneMuted && _o.RecordMicrophone) inset.Children.Add(Note(CS.Get("Ready_MicMuted"), warn: true));
        var noise = Ui.Switch(CS.Get("Ready_RemoveNoise"), _o.RemoveNoise, v => _o = _o with { RemoveNoise = v });
        noise.IsEnabled = _ctx.NoiseRemovalAvailable && _ctx.MicrophoneAllowed;
        noise.ToolTip = _ctx.NoiseRemovalAvailable ? CS.Get("Ready_RemoveNoiseTip") : CS.Get("Ready_RemoveNoiseUnavailable");
        noise.MinHeight = 34;
        _noisePanel = new StackPanel { Margin = new Thickness(30, 0, 0, 0) };
        _noisePanel.Children.Add(noise);
        inset.Children.Add(_noisePanel);

        // --- system audio
        var sys = Ui.Switch(CS.Get("Ready_SystemAudio"), _o.RecordSystemAudio && _ctx.SystemAudioAllowed, v => _o = _o with { RecordSystemAudio = v }, "Icon.Speaker.Regular");
        sys.IsEnabled = _ctx.SystemAudioAllowed;
        inset.Children.Add(sys);

        // --- webcam
        var cam = Ui.Switch(CS.Get("Ready_Webcam"), _o.Webcam && _ctx.WebcamAllowed && _ctx.Webcams.Count > 0, v => { _o = _o with { Webcam = v }; Sync(); _ = UpdatePreviewAsync(); }, "Icon.Webcam.Regular");
        cam.IsEnabled = _ctx.WebcamAllowed && _ctx.Webcams.Count > 0;
        if (_ctx.Webcams.Count == 0) { _o = _o with { Webcam = false }; }
        if (_ctx.Webcams.Count > 0)
            cam.Trailing = DeviceChips.Create(_ctx.Webcams, _o.WebcamId ?? _ctx.Webcams.FirstOrDefault(c => c.IsDefault)?.Id ?? _ctx.Webcams[0].Id,
                CS.Get("Ready_SystemDefault"), id => { _o = _o with { WebcamId = id }; _ = UpdatePreviewAsync(); }, includeSystemDefault: false, maxWidth: 110);
        inset.Children.Add(cam);
        if (_ctx.Webcams.Count == 0) inset.Children.Add(Note(CS.Get("Ready_NoWebcams")));
        var camPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(40, 2, 10, 8), Name = "camPanel" };
        camPanel.Children.Add(Ui.Combo(new[] { (WebcamShape.Circle, CS.Get("Shape_Circle")), (WebcamShape.RoundedSquare, CS.Get("Shape_RoundedSquare")), (WebcamShape.RoundedRectangle, CS.Get("Shape_RoundedRectangle")) },
            _o.PipShape, s => { _o = _o with { PipShape = s }; _preview?.SetShape(s); }, 150));
        camPanel.Children.Add(new Border { Width = 8 });
        camPanel.Children.Add(Ui.Combo(new[] { (Corner.TopLeft, CS.Get("Corner_TopLeft")), (Corner.TopRight, CS.Get("Corner_TopRight")), (Corner.BottomLeft, CS.Get("Corner_BottomLeft")), (Corner.BottomRight, CS.Get("Corner_BottomRight")) },
            _o.PipCorner, c => _o = _o with { PipCorner = c }, 150));
        inset.Children.Add(camPanel);
        _camPanel = camPanel;

        // --- cursor + frame rate
        inset.Children.Add(Ui.Switch(CS.Get("Ready_Cursor"), _o.IncludeCursor, v => _o = _o with { IncludeCursor = v }, "Icon.Tool.Cursor.Color"));
        inset.Children.Add(Ui.Switch(CS.Get("Ready_CursorHighlight"), _o.CursorHighlight, v => _o = _o with { CursorHighlight = v }, "Icon.Effects.Regular"));
        inset.Children.Add(Ui.Switch(CS.Get("Ready_ClickAnimation"), _o.ClickAnimation, v => _o = _o with { ClickAnimation = v }, "Icon.Tool.Select.Color"));
        var fps = new Grid { Margin = new Thickness(10, 4, 10, 6) };
        fps.ColumnDefinitions.Add(new ColumnDefinition());
        fps.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fpsLabel = Ui.Row(Ui.Icon("Icon.Timer.Regular", 20, "Shotwise.TextSecondaryBrush"), new Border { Width = 10 }, Ui.Label(CS.Get("Ready_Fps"), 13));
        fps.Children.Add(fpsLabel);
        var fpsCombo = Ui.Combo(RecordingOptions.SupportedFps.Select(f => (f, f + " fps")), _o.Fps, f => _o = _o with { Fps = f }, 96);
        Grid.SetColumn(fpsCombo, 1);
        fps.Children.Add(fpsCombo);
        inset.Children.Add(fps);

        // --- buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = Ui.TextButton(CS.Get("Toolbar_Cancel"), () => Finish(ReadyAction.Cancel), ghost: true);
        cancel.IsCancel = true;
        var redo = Ui.TextButton(CS.Get("Ready_Redo"), () => Finish(ReadyAction.Redo), "Icon.Reset.Regular");
        redo.Margin = new Thickness(8, 0, 8, 0);
        var record = Ui.RecordButton(CS.Get("Ready_Record"), () => Finish(ReadyAction.Record));
        record.IsDefault = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(redo);
        buttons.Children.Add(record);
        root.Children.Add(buttons);

        Sync();
        Loaded += (_, _) => _ = UpdatePreviewAsync();
        return root;
    }

    private static TextBlock Note(string text, bool warn = false)
    {
        var t = Ui.Label(text, 11.5, secondary: !warn, wrap: true, brush: warn ? "Shotwise.WarningTextBrush" : null);
        t.Margin = new Thickness(40, 0, 10, 6);
        return t;
    }

    private void Sync()
    {
        if (_camPanel is not null) _camPanel.Visibility = _o.Webcam ? Visibility.Visible : Visibility.Collapsed;
        if (_noisePanel is not null) _noisePanel.Visibility = _o.RecordMicrophone ? Visibility.Visible : Visibility.Collapsed;
        if (!_o.Webcam) ClosePreview();
    }

    private async Task UpdatePreviewAsync()
    {
        if (_finished) return;
        if (!_o.Webcam || _ctx.PreviewFactory is null || _ctx.Webcams.Count == 0) { ClosePreview(); return; }
        var id = _o.WebcamId ?? _ctx.Webcams.FirstOrDefault(c => c.IsDefault)?.Id ?? _ctx.Webcams[0].Id;
        try
        {
            if (_preview is null)
            {
                _preview = new WebcamPreviewWindow(_o.PipShape);
                _preview.Show();
                PlacePreviewInCorner();
            }
            await _preview.StartAsync(_ctx.PreviewFactory, id, _ctx.Log, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            ClosePreview();
            _o = _o with { Webcam = false };
            Sync();
        }
    }

    private void PlacePreviewInCorner()
    {
        if (_preview is null) return;
        _preview.UpdateLayout();
        double s = NativeUi.ScaleOf(_preview);
        int w = (int)(_preview.ActualWidth * s), h = (int)(_preview.ActualHeight * s);
        var r = _ctx.RegionPx;
        int m = 16;
        int x = _o.PipCorner is Corner.TopLeft or Corner.BottomLeft ? r.X + m : r.Right - w - m;
        int y = _o.PipCorner is Corner.TopLeft or Corner.TopRight ? r.Y + m : r.Bottom - h - m;
        NativeUi.MoveTo(_preview, Math.Max(_ctx.WorkAreaPx.X, x), Math.Max(_ctx.WorkAreaPx.Y, y));
    }

    private void ClosePreview()
    {
        var p = _preview;
        _preview = null;
        if (p is null) return;
        p.StopPreview();
        try { p.Close(); } catch (InvalidOperationException) { }
    }

    private void Finish(ReadyAction action)
    {
        if (_finished) return;
        _finished = true;
        ClosePreview();
        _done.TrySetResult(new ReadyResult(action, _o));
        try { Close(); } catch (InvalidOperationException) { }
    }
}
