using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Editor;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;
using Shotwise.UI.Editor.Share;

namespace Shotwise.UI.Editor.Video;

/// <summary>
/// "Video from Images" (SPEC REC-12): records the active image document — including every annotation the user adds,
/// moves or deletes while recording — plus optional microphone / system-audio narration into an MP4, through Core
/// <see cref="IVideoFromImagesSessionFactory"/>. A control bar over the canvas offers pause/resume, stop and cancel.
/// </summary>
internal sealed class VideoFromImagesController : Border
{
    private readonly EditorContext _ctx;
    private readonly ILogger _log;
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0), MinWidth = 48 };
    private readonly Button _pause;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IVideoFromImagesSession? _session;
    private CanvasFrameSource? _source;
    private TaskCompletionSource<VideoCaptureResult?>? _done;

    public VideoFromImagesController(EditorContext ctx)
    {
        _ctx = ctx;
        _log = ctx.Log<VideoFromImagesController>();
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 12, 0, 0);
        // Recording HUD pill (DESIGN-SYSTEM §4): dark translucent HUD token, coral record dot, live timer, icon buttons.
        Padding = new Thickness(16, 6, 6, 6);
        CornerRadius = new CornerRadius(22);
        this.Themed(BackgroundProperty, Tok.Hud);
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Tok.Capture);
        var label = new TextBlock { Text = ES.T("Vfi_Recording", "Recording the canvas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontWeight = FontWeights.SemiBold };
        label.Themed(TextBlock.ForegroundProperty, Tok.HudText);
        _time.Themed(TextBlock.ForegroundProperty, Tok.HudText);
        _time.Margin = new Thickness(12, 0, 8, 0);
        _time.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        _pause = UiKit.GhostButton(ES.T("Vfi_Pause", "Pause"), Icons.Glyph.Pause, () => UiKit.Fire(TogglePauseAsync, Report));
        var stop = UiKit.GhostButton(ES.T("Vfi_Stop", "Stop"), "Icon.RecordStop.Filled", () => UiKit.Fire(StopAsync, Report));
        var cancel = UiKit.GhostButton(ES.T("Common_Cancel", "Cancel"), Icons.Glyph.Close, () => UiKit.Fire(CancelAsync, Report));
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement e in new UIElement[] { dot, label, _time, _pause, stop, cancel }) bar.Children.Add(e);
        foreach (var b in new[] { _pause, stop, cancel }) { b.Margin = new Thickness(2, 0, 0, 0); b.SetResourceReference(Control.ForegroundProperty, Tok.HudText); b.MinHeight = 30; }
        Child = bar;
        System.Windows.Automation.AutomationProperties.SetName(this, ES.T("Vfi_Bar", "Video from Images recording controls"));
        _clock.Tick += (_, _) => _time.Text = UiKit.FormatDuration(_session?.Elapsed ?? TimeSpan.Zero);
    }

    public bool IsRecording => _session is not null;

    /// <summary>Asks for the recording options, records until Stop/Cancel and returns the published MP4 (null when cancelled).</summary>
    public async Task<VideoCaptureResult?> RecordAsync(ImageEditorDocument doc, Window owner)
    {
        if (_session is not null) return null;
        if (_ctx.VideoFromImages is not { } factory)
        {
            await _ctx.InfoAsync(ES.T("Cmd_VideoFromImages", "Video from Images…"), ES.T("Msg_NoVideoFromImages", "Recording video from images is not available."), MessageKind.Warning);
            return null;
        }
        var options = await VideoFromImagesDialog.AskAsync(owner);
        if (options is null) return null;
        doc.Session.CommitTextEditSafe();
        var output = Path.Combine(_ctx.Paths.Temp, "video-from-images", $"{Sanitize(doc.Title)} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        _source = new CanvasFrameSource(_ctx, doc, Math.Max(1, options.Fps));
        _session = factory.Create();
        _done = new TaskCompletionSource<VideoCaptureResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _session.StartAsync(_source, options, output, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ShotwiseException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Video from Images could not start");
            await TearDownAsync();
            throw;
        }
        _pause.Content = ES.T("Vfi_Pause", "Pause");
        _time.Text = UiKit.FormatDuration(TimeSpan.Zero);
        Visibility = Visibility.Visible;
        _clock.Start();
        return await _done.Task;
    }

    private async Task TogglePauseAsync()
    {
        if (_session is not { } s) return;
        if (s.State == RecorderState.Paused) { await s.ResumeAsync(); _pause.Content = ES.T("Vfi_Pause", "Pause"); }
        else { await s.PauseAsync(); _pause.Content = ES.T("Vfi_Resume", "Resume"); }
    }

    public async Task StopAsync()
    {
        if (_session is not { } s) return;
        VideoCaptureResult? result = null;
        try { result = await s.StopAsync(); }
        finally
        {
            await TearDownAsync();
            _done?.TrySetResult(result);
        }
    }

    public async Task CancelAsync()
    {
        if (_session is not { } s) return;
        try { await s.CancelAsync(); }
        finally
        {
            await TearDownAsync();
            _done?.TrySetResult(null);
        }
    }

    private async Task TearDownAsync()
    {
        _clock.Stop();
        Visibility = Visibility.Collapsed;
        _source?.Dispose();
        _source = null;
        if (_session is { } s)
        {
            _session = null;
            try { await s.DisposeAsync(); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ShotwiseException) { _log.LogDebug(ex, "Session dispose failed"); }
        }
    }

    private void Report(Exception ex) => _ = _ctx.ShowErrorAsync(ex);

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Video" : name.Trim();
    }
}

/// <summary>
/// Frame source for the recorder thread: the document is rendered on the UI thread (the editing session is not
/// thread-safe) whenever it changed, at most at the recording frame rate; the recorder reads the latest immutable frame.
/// </summary>
internal sealed class CanvasFrameSource : IFrameSource, IDisposable
{
    private const int MaxWidth = 1920, MaxHeight = 1080;
    private readonly EditorContext _ctx;
    private readonly ImageEditorDocument _doc;
    private readonly DispatcherTimer _timer;
    private readonly object _gate = new();
    private BgraImage _frame;
    private bool _dirty = true;

    public CanvasFrameSource(EditorContext ctx, ImageEditorDocument doc, int fps)
    {
        _ctx = ctx;
        _doc = doc;
        var page = doc.Session.ActivePage;
        FrameSize = FitEven(page.CanvasSize, MaxWidth, MaxHeight);
        _frame = BgraImage.CreateFilled(FrameSize.Width, FrameSize.Height, Argb.White);
        doc.Session.Invalidated += OnInvalidated;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 1, 60)) };
        _timer.Tick += (_, _) => Refresh();
        Refresh();
        _timer.Start();
    }

    public SizeI FrameSize { get; }

    public BgraImage GetFrame(TimeSpan timestamp)
    {
        lock (_gate) return _frame;
    }

    /// <summary>Largest size with the canvas aspect that fits the bounds, with even dimensions (H.264 requirement).</summary>
    internal static SizeI FitEven(Size2 canvas, int maxW, int maxH)
    {
        var w = Math.Max(2, canvas.Width);
        var h = Math.Max(2, canvas.Height);
        var s = Math.Min(1, Math.Min(maxW / w, maxH / h));
        var fw = Math.Max(2, (int)Math.Round(w * s) & ~1);
        var fh = Math.Max(2, (int)Math.Round(h * s) & ~1);
        return new SizeI(fw, fh);
    }

    private void OnInvalidated(object? sender, InvalidatedEventArgs e) => _dirty = true;

    private void Refresh()
    {
        if (!_dirty) return;
        _dirty = false;
        var page = _doc.Session.ActivePage;
        var scale = Math.Min(FrameSize.Width / Math.Max(1, page.CanvasSize.Width), FrameSize.Height / Math.Max(1, page.CanvasSize.Height));
        BgraImage rendered;
        try { rendered = _ctx.Renderer.RenderPage(_doc.Document, page, new RenderOptions(scale)); }
        catch (ShotwiseException) { return; }
        var frame = BgraImage.CreateFilled(FrameSize.Width, FrameSize.Height, Argb.White);
        frame.DrawOver(rendered, (FrameSize.Width - rendered.Width) / 2, (FrameSize.Height - rendered.Height) / 2);
        lock (_gate) _frame = frame;
    }

    public void Dispose()
    {
        _timer.Stop();
        _doc.Session.Invalidated -= OnInvalidated;
    }
}

/// <summary>Options for "Video from Images": narration sources and frame rate.</summary>
internal sealed class VideoFromImagesDialog : DialogWindow
{
    private readonly CheckBox _mic;
    private readonly CheckBox _system;
    private readonly ComboBox _fps;

    private VideoFromImagesDialog()
    {
        Title = ES.T("Cmd_VideoFromImages", "Video from Images…");
        MinWidth = 420;
        var panel = new StackPanel { Margin = new Thickness(20, 48, 20, 16) };
        panel.Children.Add(new TextBlock
        {
            Text = ES.T("Vfi_Intro", "Records the canvas while you draw, move and remove annotations, with optional narration. Click Stop in the bar above the canvas to finish; the video opens in the editor."),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 0, 0, 12),
        });
        _mic = new CheckBox { Content = ES.T("Vfi_Microphone", "Record microphone narration"), IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
        _system = new CheckBox { Content = ES.T("Vfi_SystemAudio", "Record system audio"), IsChecked = false, Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(_mic);
        panel.Children.Add(_system);
        _fps = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var f in new[] { 10, 15, 30 }) _fps.Items.Add(new ComboBoxItem { Content = ES.F("Vfi_Fps", "{0} fps", f), Tag = f });
        _fps.SelectedIndex = 1;
        panel.Children.Add(UiKit.Row(ES.T("Vfi_FrameRate", "Frame rate"), _fps, 110));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = ES.T("Vfi_Start", "Start recording"), IsDefault = true, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = ES.T("Common_Cancel", "Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => CancelDialog();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static async Task<VideoFromImagesOptions?> AskAsync(object? owner)
    {
        var d = new VideoFromImagesDialog();
        if (await d.ShowDialogAsync(owner) != true) return null;
        var fps = (d._fps.SelectedItem as ComboBoxItem)?.Tag is int f ? f : 15;
        return new VideoFromImagesOptions(Fps: fps, RecordMicrophone: d._mic.IsChecked == true, RecordSystemAudio: d._system.IsChecked == true, Background: Argb.White);
    }
}
