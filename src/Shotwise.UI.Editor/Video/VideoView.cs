using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Video;

/// <summary>
/// Video / animated GIF view (MED-01/02/05): playback, frame stepping with frame-accurate previews, volume, a timeline with
/// trim handles and selection, Cut Out (undoable), Save Selection as New Video, Save Frame as Image, Create GIF, save.
/// </summary>
internal sealed class VideoView : Grid
{
    private readonly EditorContext _ctx;
    private readonly ILogger _log;
    private readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform, ScrubbingEnabled = true };
    private readonly Image _frame = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly VideoTimeline _timeline = new();
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _timeTotal = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 14, Margin = new Thickness(4, 0, 0, 0) };
    private readonly Border _editedChip = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _error = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Visibility = Visibility.Collapsed, TextAlignment = TextAlignment.Center };
    private readonly Button _play;
    private readonly ToggleButton _mute;
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Value = 0.8, Width = 96, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Button _cutOut, _trimSel, _saveSel, _saveFrame, _gif, _clearSel;
    private VideoEditorDocument? _doc;
    private bool _playing;
    private TimeSpan _pos;
    private CancellationTokenSource? _frameCts;
    private DateTime _gifStarted;
    private TimeSpan _gifStartPos;
    private bool _mediaFailed;

    public VideoView(EditorContext ctx)
    {
        _ctx = ctx;
        _log = ctx.Log<VideoView>();
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // dark stage (HUD token in both themes) with rounded corners
        var stage = new Border { CornerRadius = new CornerRadius(12), Margin = new Thickness(20, 20, 20, 12), ClipToBounds = true, BorderThickness = new Thickness(1) };
        stage.SetResourceReference(Border.BackgroundProperty, Tok.Hud);
        stage.SetResourceReference(Border.BorderBrushProperty, Tok.BorderSubtle);
        var viewer = new Grid { Margin = new Thickness(12) };
        viewer.Children.Add(_media);
        viewer.Children.Add(_frame);
        _error.SetResourceReference(TextBlock.ForegroundProperty, Tok.HudText);
        viewer.Children.Add(_error);
        stage.Child = viewer;
        stage.SizeChanged += (_, e) => stage.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
        Children.Add(stage);

        // transport + timeline card
        var card = new Shotwise.UI.Common.Controls.Card { Padding = new Thickness(16, 10, 16, 12), Margin = new Thickness(20, 0, 20, 12) };
        var cardBody = new StackPanel();
        var transport = new Grid();
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var timeBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _time.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text);
        _timeTotal.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text3);
        timeBox.Children.Add(_time);
        timeBox.Children.Add(_timeTotal);
        _editedChip.SetResourceReference(StyleProperty, "Shotwise.AccentChip");
        timeBox.Children.Add(_editedChip);
        transport.Children.Add(timeBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var prev = UiKit.IconButton(Icons.Glyph.Previous, ES.T("Video_PrevFrame", "Previous frame (,)"), () => Step(-1), 18);
        var next = UiKit.IconButton(Icons.Glyph.Next, ES.T("Video_NextFrame", "Next frame (.)"), () => Step(+1), 18);
        var stop = UiKit.IconButton(Icons.Glyph.Stop, ES.T("Video_Stop", "Stop"), Stop, 15);
        _play = UiKit.IconButton(Icons.Glyph.Play, ES.T("Video_Play", "Play (Space)"), TogglePlay, 20);
        _play.Width = 44; _play.Height = 44;
        _play.Margin = new Thickness(10, 0, 10, 0);
        _play.SetResourceReference(Control.BackgroundProperty, Tok.Accent);
        _play.SetResourceReference(Control.ForegroundProperty, Tok.OnAccent);
        foreach (var b in new[] { prev, next, stop }) { b.Width = 36; b.Height = 36; }
        buttons.Children.Add(stop);
        buttons.Children.Add(prev);
        buttons.Children.Add(_play);
        buttons.Children.Add(next);
        Grid.SetColumn(buttons, 1);
        transport.Children.Add(buttons);
        var volume = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _mute = UiKit.Toggle(Icons.Glyph.Volume, ES.T("Video_Mute", "Mute"), false, on => { _media.IsMuted = on; SetMuteGlyph(on); }, true);
        volume.Children.Add(_mute);
        _volume.SetResourceReference(StyleProperty, "Shotwise.Slider");
        System.Windows.Automation.AutomationProperties.SetName(_volume, ES.T("Video_Volume", "Volume"));
        volume.Children.Add(_volume);
        Grid.SetColumn(volume, 2);
        transport.Children.Add(volume);
        cardBody.Children.Add(transport);
        _timeline.Margin = new Thickness(-6, 8, -6, 0);
        cardBody.Children.Add(_timeline);
        card.Content = cardBody;
        SetRow(card, 1);
        Children.Add(card);

        // actions: ghost edit commands left, primary outputs right
        var actionsRow = new Grid { Margin = new Thickness(20, 0, 20, 16) };
        actionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        _cutOut = UiKit.GhostButton(ES.T("Video_CutOut", "Cut Out"), "Icon.Tool.CutOut.Regular", () => UiKit.Fire(CutOutAsync, Report), ES.T("Video_CutOutTip", "Remove the selected range (select a range on the timeline first)"));
        _trimSel = UiKit.GhostButton(ES.T("Video_TrimToSelection", "Trim to selection"), "Icon.Tool.Crop.Regular", TrimToSelection);
        _saveSel = UiKit.GhostButton(ES.T("Video_SaveSelection", "Save Selection as New Video…"), Icons.Glyph.VideoClip, () => UiKit.Fire(SaveSelectionAsync, Report));
        _saveFrame = UiKit.GhostButton(ES.T("Video_SaveFrame", "Save Frame as Image"), Icons.Glyph.Image, () => UiKit.Fire(SaveFrameAsync, Report));
        _clearSel = UiKit.GhostButton(ES.T("Video_ClearSelection", "Clear selection"), Icons.Glyph.Close, () => { _timeline.Selection = null; UpdateButtons(); });
        foreach (var b in new[] { _cutOut, _trimSel, _saveSel, _saveFrame, _clearSel }) actions.Children.Add(b);
        actionsRow.Children.Add(actions);
        var outputs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _gif = UiKit.TextButton(ES.T("Video_SaveGif", "Create GIF…"), () => UiKit.Fire(CreateGifAsync, Report));
        Shotwise.UI.Common.Controls.SwUi.SetIcon(_gif, UiKit.IconSource(Icons.Glyph.Gif));
        _gif.Margin = new Thickness(0, 0, 8, 0);
        outputs.Children.Add(_gif);
        var save = UiKit.PrimaryButton(ES.T("Video_Save", "Save"), Icons.Glyph.Save, () => { });
        save.Command = EditorCommands.Save;
        save.ToolTip = ES.T("Video_SaveTip", "Save the edited video (Ctrl+S)");
        save.Margin = new Thickness(0);
        outputs.Children.Add(save);
        Grid.SetColumn(outputs, 1);
        actionsRow.Children.Add(outputs);
        SetRow(actionsRow, 2);
        Children.Add(actionsRow);

        _timer.Tick += OnTick;
        _volume.ValueChanged += (_, _) => _media.Volume = _volume.Value;
        _media.MediaOpened += (_, _) => { _mediaFailed = false; _error.Visibility = Visibility.Collapsed; };
        _media.MediaFailed += (_, e) =>
        {
            _mediaFailed = true;
            _log.LogWarning(e.ErrorException, "Media playback failed");
            _error.Text = ES.T("Video_PlayFailed", "This video cannot be played here. You can still step through frames, edit and save it.");
            _error.Visibility = Visibility.Visible;
            _ = ShowFrameAsync(_pos);
        };
        _media.MediaEnded += (_, _) => Stop();
        _timeline.Scrubbed += t => SeekTo(t);
        _timeline.SelectionChanged += _ => UpdateButtons();
        _timeline.TrimCommitted += range => UiKit.Fire(() => { Execute(new SetTrimCommand(_doc!.Edl, range)); return Task.CompletedTask; }, Report);
        _timeline.CutRestoreRequested += cut => Execute(new RemoveCutCommand(_doc!.Edl, cut));
        Focusable = true;
        PreviewKeyDown += OnKey;
        UpdateButtons();
    }

    /// <summary>Host callbacks.</summary>
    public Func<BgraImage, string, Task>? OpenFrameAsImage { get; set; }
    public Func<string, Task>? OpenMedia { get; set; }
    public ProgressOverlay? Progress { get; set; }
    public VideoEditorDocument? Document => _doc;
    public event Action? EditsChanged;

    private void Report(Exception ex) => _ = _ctx.ShowErrorAsync(ex);

    // ------------------------------------------------------------------ load

    public async Task LoadAsync(VideoEditorDocument? doc)
    {
        Stop();
        if (_doc is not null) _doc.Edl.Changed -= OnEdlChanged;
        _doc = doc;
        _timeline.Selection = null;
        _frame.Source = null;
        _error.Visibility = Visibility.Collapsed;
        _mediaFailed = false;
        if (doc is null)
        {
            _media.Source = null;
            _timeline.SetMedia(TimeSpan.FromSeconds(1), Array.Empty<ImageSource>(), new VideoEditList());
            UpdateButtons();
            return;
        }
        doc.Edl.Changed += OnEdlChanged;
        _timeline.SetMedia(doc.Info.Duration, Array.Empty<ImageSource>(), doc.Edl);
        _pos = doc.Edl.Trim.Start;
        _timeline.Position = _pos;
        if (doc.IsGif) _media.Source = null;
        else _media.Source = new Uri(doc.FilePath!);
        _media.Volume = _volume.Value;
        _media.IsMuted = _mute.IsChecked == true;
        UpdateButtons();
        UpdateTime();
        await ShowFrameAsync(_pos);
        await LoadThumbnailsAsync(doc);
    }

    private async Task LoadThumbnailsAsync(VideoEditorDocument doc)
    {
        try
        {
            var count = 24;
            var thumbs = new List<ImageSource>();
            if (doc.IsGif && doc.Gif is { } gif && gif.Frames.Count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var idx = Math.Min(gif.Frames.Count - 1, (int)((double)i / count * gif.Frames.Count));
                    thumbs.Add(Scaled(gif.Frames[idx]));
                }
            }
            else if (_ctx.Video is { } vs && doc.FilePath is { } path)
            {
                var frames = await vs.GetThumbnailsAsync(path, count, 64, CancellationToken.None);
                thumbs.AddRange(frames.Select(f => f.ToBitmapSource()));
            }
            if (!ReferenceEquals(doc, _doc)) return;
            _timeline.SetThumbs(thumbs);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Timeline thumbnails failed"); }
    }

    private static ImageSource Scaled(BgraImage img)
    {
        var scaled = Math.Max(img.Width, img.Height) > 160 ? ImageOps.Downscale(img, 160) : img;
        return scaled.ToBitmapSource();
    }

    private void OnEdlChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _timeline.InvalidateVisual();
            UpdateTime();
            EditsChanged?.Invoke();
            UpdateButtons();
        });
    }

    // ------------------------------------------------------------------ playback

    private TimeSpan Duration => _doc?.Info.Duration ?? TimeSpan.Zero;
    private double Fps => _doc is { Info.Fps: > 0 } d ? d.Info.Fps : 25;

    public void TogglePlay()
    {
        if (_doc is null) return;
        if (_playing) Pause(); else Play();
    }

    public void Play()
    {
        if (_doc is null) return;
        var edl = _doc.Edl;
        if (_pos >= edl.Trim.End - TimeSpan.FromMilliseconds(40)) _pos = edl.Trim.Start;
        _pos = SkipCuts(_pos);
        _playing = true;
        _frameCts?.Cancel();
        _frame.Visibility = _doc.IsGif ? Visibility.Visible : Visibility.Collapsed;
        if (!_doc.IsGif && !_mediaFailed)
        {
            _media.Position = _pos;
            _media.Play();
        }
        _gifStarted = DateTime.UtcNow;
        _gifStartPos = _pos;
        _timer.Start();
        SetPlayGlyph(true);
    }

    public void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _timer.Stop();
        if (!_doc?.IsGif ?? false) _media.Pause();
        SetPlayGlyph(false);
        _ = ShowFrameAsync(_pos);
    }

    public void Stop()
    {
        _playing = false;
        _timer.Stop();
        try { _media.Pause(); } catch (Exception) { }
        SetPlayGlyph(false);
        if (_doc is not null)
        {
            _pos = _doc.Edl.Trim.Start;
            _timeline.Position = _pos;
            UpdateTime();
            _ = ShowFrameAsync(_pos);
        }
    }

    private void SetPlayGlyph(bool playing)
    {
        Shotwise.UI.Common.Controls.SwUi.SetIcon(_play, UiKit.IconSource(playing ? Icons.Glyph.Pause : Icons.Glyph.Play));
        _play.ToolTip = playing ? ES.T("Video_Pause", "Pause (Space)") : ES.T("Video_Play", "Play (Space)");
        System.Windows.Automation.AutomationProperties.SetName(_play, playing ? ES.T("Video_PauseName", "Pause") : ES.T("Video_PlayName", "Play"));
    }

    private void SetMuteGlyph(bool muted) => Shotwise.UI.Common.Controls.SwUi.SetIcon(_mute, UiKit.IconSource(muted ? Icons.Glyph.Mute : Icons.Glyph.Volume));

    private TimeSpan SkipCuts(TimeSpan t)
    {
        if (_doc is null) return t;
        foreach (var r in _doc.Edl.Cuts.OrderBy(c => c.Start))
            if (r.Contains(t)) t = r.End;
        return t;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_doc is null) return;
        var edl = _doc.Edl;
        if (_doc.IsGif)
        {
            _pos = _gifStartPos + (DateTime.UtcNow - _gifStarted);
        }
        else _pos = _media.Position;

        var skipped = SkipCuts(_pos);
        if (skipped != _pos)
        {
            _pos = skipped;
            if (_doc.IsGif) { _gifStartPos = _pos; _gifStarted = DateTime.UtcNow; }
            else _media.Position = _pos;
        }
        if (_pos >= edl.Trim.End)
        {
            Pause();
            _pos = edl.Trim.End;
            UpdateTime();
            _timeline.Position = _pos;
            return;
        }
        if (_doc.IsGif && _doc.Gif is { } gif) _frame.Source = GifFrameAt(gif, _pos);
        _timeline.Position = _pos;
        UpdateTime();
    }

    private ImageSource? _lastGifSource;
    private int _lastGifIndex = -1;

    private ImageSource? GifFrameAt(AnimatedImage gif, TimeSpan t)
    {
        var acc = TimeSpan.Zero;
        var index = gif.Frames.Count - 1;
        for (var i = 0; i < gif.Frames.Count; i++)
        {
            acc += gif.Delays[i];
            if (t < acc) { index = i; break; }
        }
        if (index != _lastGifIndex)
        {
            _lastGifIndex = index;
            _lastGifSource = gif.Frames[index].ToBitmapSource();
        }
        return _lastGifSource;
    }

    public void SeekTo(TimeSpan t)
    {
        if (_doc is null) return;
        t = TimeSpan.FromTicks(Math.Clamp(t.Ticks, 0, Duration.Ticks));
        _pos = t;
        _timeline.Position = t;
        UpdateTime();
        if (_playing)
        {
            if (_doc.IsGif) { _gifStartPos = t; _gifStarted = DateTime.UtcNow; }
            else _media.Position = t;
        }
        else
        {
            try { _media.Position = t; } catch (Exception) { }
            _ = ShowFrameAsync(t);
        }
    }

    public void Step(int frames)
    {
        if (_doc is null) return;
        if (_playing) Pause();
        var step = TimeSpan.FromSeconds(1.0 / Fps);
        var t = _pos + TimeSpan.FromTicks(step.Ticks * frames);
        t = SkipCutsDirected(t, frames);
        t = TimeSpan.FromTicks(Math.Clamp(t.Ticks, _doc.Edl.Trim.Start.Ticks, Math.Max(_doc.Edl.Trim.Start.Ticks, _doc.Edl.Trim.End.Ticks - 1)));
        SeekTo(t);
    }

    private TimeSpan SkipCutsDirected(TimeSpan t, int dir)
    {
        if (_doc is null) return t;
        foreach (var c in _doc.Edl.Cuts)
            if (c.Contains(t)) return dir > 0 ? c.End : c.Start - TimeSpan.FromMilliseconds(1);
        return t;
    }

    private async Task ShowFrameAsync(TimeSpan t)
    {
        if (_doc is null) return;
        _frameCts?.Cancel();
        var cts = _frameCts = new CancellationTokenSource();
        try
        {
            BgraImage? img = null;
            if (_doc.IsGif && _doc.Gif is { } gif)
            {
                _frame.Source = GifFrameAt(gif, t);
                _frame.Visibility = Visibility.Visible;
                return;
            }
            if (_ctx.Video is null || _doc.FilePath is null) return;
            await Task.Delay(30, cts.Token);
            img = await _ctx.Video.GetFrameAsync(_doc.FilePath, t, cts.Token);
            if (cts.IsCancellationRequested || _playing) return;
            _frame.Source = img.ToBitmapSource();
            _frame.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug(ex, "Frame preview failed"); }
    }

    private void UpdateTime()
    {
        if (_doc is null) { _time.Text = ""; _timeTotal.Text = ""; _editedChip.Visibility = Visibility.Collapsed; return; }
        var edl = _doc.Edl;
        _time.Text = UiKit.FormatDuration(_pos);
        _timeTotal.Text = "/ " + UiKit.FormatDuration(Duration);
        _editedChip.Child = new TextBlock { Text = $"{UiKit.FormatDuration(edl.KeptDuration)} {ES.T("Video_Edited", "after edits")}" };
        _editedChip.Visibility = edl.IsIdentity ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_doc is null) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.OemComma or Key.Left when Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None: Step(-1); e.Handled = true; break;
            case Key.OemPeriod or Key.Right when Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None: Step(+1); e.Handled = true; break;
            case Key.Home: SeekTo(_doc.Edl.Trim.Start); e.Handled = true; break;
            case Key.End: SeekTo(_doc.Edl.Trim.End); e.Handled = true; break;
        }
    }

    public void ToggleMute() { _mute.IsChecked = _mute.IsChecked != true; _media.IsMuted = _mute.IsChecked == true; SetMuteGlyph(_media.IsMuted); }

    public void AdjustVolume(double delta) => _volume.Value = Math.Clamp(_volume.Value + delta, 0, 1);

    // ------------------------------------------------------------------ editing

    private void UpdateButtons()
    {
        var has = _doc is not null;
        var sel = _timeline.Selection is { IsEmpty: false };
        _cutOut.IsEnabled = has && sel;
        _trimSel.IsEnabled = has && sel;
        _saveSel.IsEnabled = has && sel && _ctx.Video is not null && !(_doc?.IsGif ?? false);
        _saveFrame.IsEnabled = has;
        _gif.IsEnabled = has && _ctx.Gif is not null && !(_doc?.IsGif ?? false);
    }

    private void Execute(Shotwise.Core.Commands.IEditCommand cmd)
    {
        if (_doc is null) return;
        try { _doc.Execute(cmd); }
        catch (ShotwiseException ex) when (ex.Code == ErrorCode.CannotCutEntireMedia)
        {
            _ = _ctx.InfoAsync(ES.T("Video_CutOut", "Cut Out"), Shotwise.UI.Common.Resources.Localizer.ErrorMessage(ex.Code), MessageKind.Warning);
        }
        _timeline.InvalidateVisual();
    }

    public Task CutOutAsync()
    {
        if (_doc is null || _timeline.Selection is not { IsEmpty: false } sel) return Task.CompletedTask;
        var pos = sel.Start;
        Execute(new AddCutCommand(_doc.Edl, sel));
        _timeline.Selection = null;
        SeekTo(pos);
        UpdateButtons();
        return Task.CompletedTask;
    }

    public void TrimToSelection()
    {
        if (_doc is null || _timeline.Selection is not { IsEmpty: false } sel) return;
        Execute(new SetTrimCommand(_doc.Edl, sel));
        _timeline.Selection = null;
        SeekTo(sel.Start);
        UpdateButtons();
    }

    public async Task SaveFrameAsync()
    {
        if (_doc is null || OpenFrameAsImage is null) return;
        BgraImage? img = null;
        if (_doc.IsGif && _doc.Gif is { } gif)
        {
            var acc = TimeSpan.Zero;
            var idx = gif.Frames.Count - 1;
            for (var i = 0; i < gif.Frames.Count; i++) { acc += gif.Delays[i]; if (_pos < acc) { idx = i; break; } }
            img = gif.Frames[idx];
        }
        else if (_ctx.Video is { } vs && _doc.FilePath is { } path) img = await vs.GetFrameAsync(path, _pos, CancellationToken.None);
        if (img is null) return;
        await OpenFrameAsImage(img, $"{_doc.Title} {UiKit.FormatDuration(_pos).Replace(':', '.')}");
    }

    public async Task SaveSelectionAsync()
    {
        if (_doc is null || _ctx.Video is null || _timeline.Selection is not { IsEmpty: false } sel) return;
        var path = await AskVideoPathAsync(_doc.Title + " (selection)");
        if (path is null) return;
        var edl = _doc.Edl.Clone();
        edl.Trim = sel.Clamp(edl.Trim);
        if (edl.Trim.IsEmpty) return;
        await RenderToAsync(edl, path);
        if (OpenMedia is not null) await OpenMedia(path);
    }

    /// <summary>Renders the document's edit list. <paramref name="overwrite"/> writes over the source file.</summary>
    public async Task<string?> SaveAsync(bool overwrite)
    {
        if (_doc is null || _ctx.Video is null && !_doc.IsGif) return null;
        string? target = overwrite ? _doc.FilePath : await AskVideoPathAsync(_doc.Title);
        if (target is null) return null;
        if (_doc.IsGif) return await SaveGifAsync(target) ? target : null;
        return await RenderToAsync(_doc.Edl.Clone(), target) ? target : null;
    }

    private async Task<string?> AskVideoPathAsync(string suggested)
    {
        var folder = Path.GetDirectoryName(_doc!.FilePath) ?? _ctx.Paths.Temp;
        var formats = _ctx.Codec.SupportedFormats;
        var r = await _ctx.Dialogs.ShowSaveAs(new SaveAsRequest(suggested, folder, true, formats, _doc.IsGif ? "gif" : "mp4") { Owner = _ctx.ActiveWindow });
        return r?.Path;
    }

    private async Task<bool> RenderToAsync(VideoEditList edl, string target)
    {
        var video = _ctx.Video!;
        var overlay = Progress;
        var metadata = MetadataFor(target);
        async Task Work(IProgress<double> p, CancellationToken ct)
        {
            await video.RenderAsync(edl, target, new VideoExportOptions(), p, ct);
            // MED-06: saved videos carry title / date / source application / tags in their MP4 metadata.
            try { await video.WriteMetadataAsync(target, metadata, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { _log.LogWarning(ex, "Writing video metadata failed for {Path}", target); }
        }
        if (overlay is null) { await Work(new Progress<double>(), CancellationToken.None); return true; }
        return await overlay.RunAsync(ES.T("Video_Rendering", "Saving video…"), Path.GetFileName(target), true, Work);
    }

    private VideoMetadata MetadataFor(string target)
    {
        var li = _doc?.LibraryItem;
        return new VideoMetadata(Path.GetFileNameWithoutExtension(target), li?.Url, li?.Tags ?? Array.Empty<string>(), li?.Created ?? DateTimeOffset.Now, li?.AppName);
    }

    private async Task<bool> SaveGifAsync(string target)
    {
        if (_doc?.Gif is not { } gif || _ctx.Gif is null) return false;
        var edl = _doc.Edl.Clone();
        var kept = edl.KeptRanges().ToList();
        async IAsyncEnumerable<(BgraImage Frame, TimeSpan Delay)> Frames()
        {
            var t = TimeSpan.Zero;
            for (var i = 0; i < gif.Frames.Count; i++)
            {
                var start = t;
                t += gif.Delays[i];
                if (kept.Any(k => start >= k.Start && start < k.End)) yield return (gif.Frames[i], gif.Delays[i]);
                await Task.Yield();
            }
        }
        var opt = _ctx.Gif.DefaultsFor(GifPreset.Custom, gif.Width, gif.Height) with { Width = gif.Width, Height = gif.Height, Loop = gif.LoopCount == 0, Fps = Math.Max(1, gif.Frames.Count / Math.Max(0.1, gif.Duration.TotalSeconds)) };
        var temp = target + ".tmp.gif";
        var ok = Progress is null
            ? await RunNoProgress(() => _ctx.Gif.CreateFromFramesAsync(Frames(), temp, opt, CancellationToken.None))
            : await Progress.RunAsync(ES.T("Video_Rendering", "Saving video…"), Path.GetFileName(target), true, (p, ct) => _ctx.Gif.CreateFromFramesAsync(Frames(), temp, opt, ct));
        if (ok) File.Move(temp, target, true); else TryDelete(temp);
        return ok;
    }

    private static async Task<bool> RunNoProgress(Func<Task> work) { await work(); return true; }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }

    public async Task CreateGifAsync()
    {
        if (_doc is null || _ctx.Gif is null || _doc.FilePath is null) return;
        var sel = _timeline.Selection is { IsEmpty: false } s ? s : null;
        var suggested = Path.Combine(Path.GetDirectoryName(_doc.FilePath) ?? _ctx.Paths.Temp, _doc.Title + ".gif");
        var r = await _ctx.Dialogs.ShowCreateGif(new CreateGifRequest(_doc.FilePath, _doc.Info, sel, suggested) { Owner = _ctx.ActiveWindow });
        if (r is null) return;
        var source = _doc.FilePath;
        string? temp = null;
        var options = r.Options;
        var gifService = _ctx.Gif;
        var edl = _doc.Edl;
        var progress = Progress;
        async Task Work(IProgress<double> p, CancellationToken ct)
        {
            if (!edl.IsIdentity && _ctx.Video is { } vs)
            {
                temp = Path.Combine(_ctx.Paths.Temp, "gif-src-" + Guid.NewGuid().ToString("N") + ".mp4");
                Directory.CreateDirectory(_ctx.Paths.Temp);
                await vs.RenderAsync(edl.Clone(), temp, new VideoExportOptions(), new Progress<double>(v => p.Report(v * 0.4)), ct);
                source = temp;
                if (options.Range is not null) options = options with { Range = null };
            }
            var inner = new Progress<double>(v => p.Report(temp is null ? v : 0.4 + v * 0.6));
            await gifService.CreateFromVideoAsync(source, r.OutputPath, options, inner, ct);
        }
        try
        {
            var ok = progress is null
                ? await RunNoProgress(() => Work(new Progress<double>(), CancellationToken.None))
                : await progress.RunAsync(ES.T("Video_CreatingGif", "Creating GIF…"), Path.GetFileName(r.OutputPath), true, Work);
            if (ok && OpenMedia is not null)
            {
                if (_ctx.Library is { } lib) { try { await lib.ImportFileAsync(r.OutputPath); } catch (Exception ex) { _log.LogDebug(ex, "Importing GIF into the library failed"); } }
                await OpenMedia(r.OutputPath);
            }
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _frameCts?.Cancel();
        try { _media.Close(); } catch (Exception) { }
    }
}
