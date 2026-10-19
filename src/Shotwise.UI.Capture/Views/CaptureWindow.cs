using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.UI.Capture.Coordinator;
using Shotwise.UI.Capture.Hud;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using CaptureMode = Shotwise.Core.Capture.CaptureMode;

namespace Shotwise.UI.Capture.Views;

/// <summary>
/// The Capture Window, a compact card: mode rail (All-in-One / Image / Video), option switches (Preview in Editor, Copy to Clipboard,
/// Cursor, Delay; Video adds microphone / system audio / webcam with device chips), the big coral Capture button with the configured
/// hotkey, a "More options" expander (selection type, aspect, timing, effects, outputs, video options) and a footer with the Presets
/// flyout, "+" (save as new preset) and "Open Editor".
/// </summary>
public sealed partial class CaptureWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly CaptureCoordinator _coordinator;
    private readonly IServiceProvider _sp;
    private readonly ISettingsService _settings;
    private readonly IPresetRepository _repo;
    private readonly CaptureWindowModel _m;
    private readonly ILogger? _log;

    private readonly StackPanel _selectionHost = new();
    private readonly StackPanel _rows = new();
    private readonly StackPanel _moreHost = new();
    private readonly Border _moreWrap = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<CaptureMode, NavItem> _modeItems = new();
    private readonly CaptureButton _captureButton;
    private readonly Button _moreToggle;
    private readonly TextBlock _moreText = Ui.Label("", 12, weight: FontWeights.SemiBold, brush: "Shotwise.TextSecondaryBrush");
    private readonly SwIcon _moreChevron = Ui.Icon("Icon.ChevronDown.Regular", 14, "Shotwise.TextSecondaryBrush");
    private readonly Border _modifiedBar;
    private readonly TextBlock _presetName = Ui.Label("", 11.5, weight: FontWeights.SemiBold);
    private bool _more;
    private bool _suppress;
    private int _lastDelay = 5;

    public CaptureWindow(CaptureCoordinator coordinator, IServiceProvider sp)
    {
        Ui.EnsureStyles();
        _coordinator = coordinator;
        _sp = sp;
        _settings = sp.GetRequiredService<ISettingsService>();
        _repo = sp.GetRequiredService<IPresetRepository>();
        _log = sp.GetService<ILogger<CaptureWindow>>();
        _m = new CaptureWindowModel(_repo);

        Title = CS.Get("CaptureWindow_Title");
        // Compact card (POLISH-1): ~512 x 275 DIP for All-in-One; "More options" grows the window downwards.
        Width = 512; MinWidth = 512;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 40);
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
        ExtendsContentIntoTitleBar = true;
        UseLayoutRounding = true;
        this.SetResourceReference(BackgroundProperty, "Shotwise.Surface0Brush");
        this.SetResourceReference(FontFamilyProperty, "Shotwise.FontFamily");

        _captureButton = new CaptureButton { Diameter = 92, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
        _captureButton.Click += (_, _) => RunCapture();
        _modifiedBar = BuildModifiedBar();

        // ---- card: mode rail | option rows (+ "More options" under them) | capture button
        var rail = BuildModeRail();
        var inset = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Child = _rows };
        inset.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface0Brush");

        _moreToggle = new Button { Content = Ui.Row(_moreText, new Border { Width = 4 }, _moreChevron), Padding = new Thickness(8, 0, 6, 0), MinHeight = 26, HorizontalAlignment = HorizontalAlignment.Left }.WithStyle("Shotwise.GhostButton");
        _moreToggle.Click += (_, _) => { _more = !_more; RefreshMore(); };
        // Two columns so the "modified preset" bar never overlaps the More options toggle.
        var moreRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        moreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        moreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        moreRow.Children.Add(_moreToggle);
        _modifiedBar.HorizontalAlignment = HorizontalAlignment.Right;
        _modifiedBar.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_modifiedBar, 1);
        moreRow.Children.Add(_modifiedBar);
        var middle = new StackPanel { Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(inset);
        middle.Children.Add(moreRow);

        var reset = Ui.IconButton("Icon.Reset.Regular", CS.Get("Preset_ResetCurrent"), () => { _m.ResetCurrent(); RefreshAll(); }, 15);
        reset.Width = reset.Height = 28;
        reset.HorizontalAlignment = HorizontalAlignment.Right; reset.VerticalAlignment = VerticalAlignment.Top; reset.Margin = new Thickness(0, -6, -6, 0);
        var captureSide = new Grid { Width = 124 };
        captureSide.Children.Add(_captureButton);
        captureSide.Children.Add(reset);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(rail);
        Grid.SetColumn(middle, 1); top.Children.Add(middle);
        Grid.SetColumn(captureSide, 2); top.Children.Add(captureSide);

        _moreWrap.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330, Content = _moreHost, Padding = new Thickness(0, 0, 6, 0) };
        _moreWrap.Margin = new Thickness(0, 2, 0, 0);

        var cardBody = new StackPanel();
        cardBody.Children.Add(top);
        cardBody.Children.Add(_moreWrap);
        var card = Ui.Card(cardBody, new Thickness(8, 8, 8, 6), elevation: 1, radius: 14);

        var body = new StackPanel { Margin = new Thickness(10, 0, 10, 8) };
        body.Children.Add(card);
        body.Children.Add(BuildFooter());

        // Tight title bar: app icon + small caption + minimise / close (32 DIP instead of WPF-UI's 48).
        var titleBar = new Wpf.Ui.Controls.TitleBar { Title = Title, Height = 32, ShowMaximize = false, ShowHelp = false, CanMaximize = false, Padding = new Thickness(10, 0, 0, 0) };
        if (Shotwise.UI.Common.Theming.AppIcon.ForSize(16, VisualTreeHelper.GetDpi(this).DpiScaleX) is { } appIcon)
            titleBar.Icon = new Wpf.Ui.Controls.ImageIcon { Source = appIcon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) };
        // The root paints Surface0 itself (WPF-UI replaces Window.Background on theme changes) and sizes the window to its content:
        // SizeToContent adds the hidden native caption to the height when the content extends into the title bar (~60 DIP of
        // empty space under the footer), so the window height follows the root's natural height instead.
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Top };
        grid.SetResourceReference(Panel.BackgroundProperty, "Shotwise.Surface0Brush");
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(titleBar);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        Content = grid;
        SizeToContent = SizeToContent.Manual;
        MinHeight = 0;
        Height = 290;
        grid.SizeChanged += (_, e) =>
        {
            var h = Math.Min(MaxHeight, Math.Ceiling(e.NewSize.Height));
            if (Math.Abs(h - Height) > 0.5) Height = h;
        };

        _repo.Changed += OnRepoChanged;
        _coordinator.IsCapturingChanged += OnCapturingChanged;
        _coordinator.HotkeysRegistered += OnCapturingChanged;
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _repo.Changed -= OnRepoChanged;
            _coordinator.IsCapturingChanged -= OnCapturingChanged;
            _coordinator.HotkeysRegistered -= OnCapturingChanged;
            _settings.Changed -= OnSettingsChanged;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && !_coordinator.IsCapturing) { e.Handled = true; RunCapture(); }
        };
        RefreshAll();
    }

    private static T Spaced<T>(T e, double top) where T : FrameworkElement { e.Margin = new Thickness(0, top, 0, 0); return e; }

    // ------------------------------------------------------------------ events

    private void OnRepoChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_suppress) return;
        var saved = _repo.Find(_m.Working.Id);
        if (saved is null && _repo.All.Count > 0) _m.Load(_repo.Active);
        RefreshAll();
    }));

    private void OnSettingsChanged(object? s, SettingsChangedEventArgs e)
    {
        if (e.Affects(nameof(AppSettings.Hotkeys))) Dispatcher.BeginInvoke(new Action(UpdateCaptureButton));
    }

    private void OnCapturingChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateCaptureButton));

    private async void RunCapture()
    {
        if (_coordinator.IsCapturing) return;
        try { await _coordinator.RunAsync(_m.Working); }
        catch (Exception ex) { _log?.LogError(ex, "Capture failed"); }
    }

    /// <summary>Rebuilds the dynamic parts of the window.</summary>
    private void RefreshAll()
    {
        SyncMode();
        BuildRows();
        BuildMore();
        BuildPresets();
        UpdateCaptureButton();
        UpdateModifiedBar();
    }

    private void Changed(bool rebuildSelection = false, bool rebuildRows = false)
    {
        if (rebuildSelection) BuildSelection();
        if (rebuildRows) { BuildRows(); BuildMore(); }
        UpdateCaptureButton();
        UpdateModifiedBar();
        BuildPresetHeaderOnly();
    }

    // ------------------------------------------------------------------ mode rail

    private UIElement BuildModeRail()
    {
        var rail = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (mode, icon, text) in new[]
        {
            (CaptureMode.AllInOne, "Icon.AllInOne.Color", CS.Get("Mode_AllInOne")),
            (CaptureMode.Image, "Icon.Image.Color", CS.Get("Mode_Image")),
            (CaptureMode.Video, "Icon.Video.Color", CS.Get("Mode_Video")),
        })
        {
            var n = new NavItem { Icon = icon, Content = text, GroupName = "capture-mode" }.WithStyle("Shotwise.ModeRailItem");
            n.Height = 54; n.Width = 72; n.IconSize = 24; n.Margin = new Thickness(0, 0, 0, 3);
            var captured = mode;
            n.Click += (_, _) => { _m.SetMode(captured); RefreshAll(); };
            _modeItems[mode] = n;
            rail.Children.Add(n);
        }
        return rail;
    }

    private void SyncMode()
    {
        foreach (var (mode, item) in _modeItems) item.IsChecked = _m.Working.Mode == mode;
    }

    // ------------------------------------------------------------------ option rows (the switches next to the capture button)

    private void BuildRows()
    {
        _rows.Children.Clear();
        var p = _m.Working;
        bool video = p.Mode == CaptureMode.Video;
        var ro = p.Recording;
        void Rec(RecordingOptions n) { p.Recording = n; Changed(); }

        if (!video)
        {
            _rows.Children.Add(Ui.Switch(CS.Get("Chip_PreviewEditor"), p.PreviewInEditor, v => { p.PreviewInEditor = v; Changed(); }, "Icon.Editor.Color"));
            _rows.Children.Add(Ui.Switch(CS.Get("Chip_Clipboard"), p.CopyToClipboard, v => { p.CopyToClipboard = v; Changed(); }, "Icon.Copy.Regular"));
            _rows.Children.Add(Ui.Switch(CS.Get("Chip_Cursor"), p.IncludeCursor, v => { p.IncludeCursor = v; Changed(); }, "Icon.Tool.Cursor.Color"));
        }
        else
        {
            _rows.Children.Add(Ui.Switch(CS.Get("Chip_Cursor"), ro.IncludeCursor, v => Rec(ro with { IncludeCursor = v }), "Icon.Tool.Cursor.Color"));
            _rows.Children.Add(MicRow(p, ro, Rec));
            _rows.Children.Add(Ui.Switch(CS.Get("Chip_SystemAudio"), ro.RecordSystemAudio, v => Rec(ro with { RecordSystemAudio = v }), "Icon.Speaker.Regular"));
            _rows.Children.Add(WebcamRow(p, ro, Rec));
            _rows.Children.Add(Ui.Switch(CS.Get("Chip_PreviewEditor"), p.PreviewInEditor, v => { p.PreviewInEditor = v; Changed(); }, "Icon.Editor.Color"));
        }
        _rows.Children.Add(DelayRow());
        foreach (var r in _rows.Children.OfType<ToggleSwitchRow>()) { r.MinHeight = 34; r.Padding = new Thickness(8, 4, 8, 4); }
    }

    private ToggleSwitchRow MicRow(CapturePreset p, RecordingOptions ro, Action<RecordingOptions> rec)
    {
        var row = Ui.Switch(CS.Get("Chip_Microphone"), ro.RecordMicrophone, v => rec(ro with { RecordMicrophone = v }), "Icon.Mic.Color");
        var mics = _sp.GetService<IAudioDeviceService>()?.Microphones() ?? Array.Empty<DeviceInfo>();
        if (mics.Count > 0)
            row.Trailing = DeviceChips.Create(mics, ro.MicrophoneId, CS.Get("Ready_SystemDefault"), id => { p.Recording = p.Recording with { MicrophoneId = id }; Changed(); }, includeSystemDefault: true, maxWidth: 84);
        return row;
    }

    private ToggleSwitchRow WebcamRow(CapturePreset p, RecordingOptions ro, Action<RecordingOptions> rec)
    {
        var row = Ui.Switch(CS.Get("Chip_Webcam"), ro.Webcam, v => rec(ro with { Webcam = v }), "Icon.Webcam.Regular");
        var cams = _sp.GetService<IAudioDeviceService>()?.Webcams() ?? Array.Empty<DeviceInfo>();
        if (cams.Count > 0)
            row.Trailing = DeviceChips.Create(cams, ro.WebcamId, CS.Get("Ready_SystemDefault"), id => { p.Recording = p.Recording with { WebcamId = id }; Changed(); }, includeSystemDefault: true, maxWidth: 84);
        return row;
    }

    private ToggleSwitchRow DelayRow()
    {
        var p = _m.Working;
        bool on = p.Timing.DelaySeconds is > 0;
        if (on) _lastDelay = p.Timing.DelaySeconds!.Value;
        var row = Ui.Switch(CS.Get("CW_Delay"), on, v =>
        {
            p.Timing = p.Timing with { DelaySeconds = v ? Math.Max(1, _lastDelay) : null };
            Changed(rebuildRows: true);
        }, "Icon.Timer.Regular");
        Button? chip = null;
        chip = Ui.ChipButton(TimingChipText(p.Timing), () => ShowFlyout(chip!, BuildTimingPanel(), () => Changed(rebuildRows: true)), CS.Get("Timing_Title"), 110);
        row.Trailing = chip;
        return row;
    }

    private string TimingChipText(TimingOptions t)
    {
        if (t.ScheduleAt is { } at) return at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        if (t.IntervalSeconds is > 0) return CS.Format("Chip_Interval", t.IntervalSeconds.Value);
        return (t.DelaySeconds is > 0 ? t.DelaySeconds.Value : _lastDelay) + " s";
    }

    // ------------------------------------------------------------------ "More options"

    private void RefreshMore()
    {
        _moreWrap.Visibility = _more ? Visibility.Visible : Visibility.Collapsed;
        _moreText.Text = _more ? CS.Get("CW_FewerOptions") : CS.Get("CW_MoreOptions");
        _moreChevron.RenderTransformOrigin = new Point(0.5, 0.5);
        _moreChevron.RenderTransform = new RotateTransform(_more ? 180 : 0);
        if (_more) BuildMore();
    }

    private void BuildMore()
    {
        _moreText.Text = _more ? CS.Get("CW_FewerOptions") : CS.Get("CW_MoreOptions");
        if (!_more) return;
        _moreHost.Children.Clear();
        var p = _m.Working;
        bool video = p.Mode == CaptureMode.Video;
        bool all = p.Mode == CaptureMode.AllInOne;

        var divider = Ui.Separator();
        divider.Margin = new Thickness(0, 4, 0, 10);
        _moreHost.Children.Add(divider);

        // selection
        _moreHost.Children.Add(Ui.Header(CS.Get("Selection_Header")));
        _selectionHost.Margin = new Thickness(0, 6, 0, 0);
        if (_selectionHost.Parent is Panel old) old.Children.Remove(_selectionHost);
        _moreHost.Children.Add(_selectionHost);
        BuildSelection();

        // after capture
        var after = Ui.Header(CS.Get("CW_OutputSection"));
        after.Margin = new Thickness(0, 14, 0, 4);
        _moreHost.Children.Add(after);
        _moreHost.Children.Add(PopupRow("Icon.Timer.Regular", CS.Get("Chip_Delay"), TimingText(p.Timing), p.Timing.IsTimed, BuildTimingPanel));
        if (!video) _moreHost.Children.Add(PopupRow("Icon.Effects.Regular", CS.Get("Effects_Title"), EffectsText(), p.Effects.Count > 0, BuildEffectsPanel));
        _moreHost.Children.Add(PopupRow("Icon.Share.Regular", CS.Get("Share_Title"), ShareText(), p.Outputs.Count > 0, BuildSharePanel));
        _moreHost.Children.Add(OpenInRow());

        if (video || all)
        {
            var vh = Ui.Header(CS.Get("CW_VideoSection"));
            vh.Margin = new Thickness(0, 14, 0, 4);
            _moreHost.Children.Add(vh);
            var ro = p.Recording;
            void Rec(RecordingOptions n) { p.Recording = n; Changed(); }
            if (all)
            {
                // video capture toggles for All-in-One apply when "Record Video" is chosen in the overlay toolbar
                _moreHost.Children.Add(MicRow(p, ro, Rec));
                _moreHost.Children.Add(Ui.Switch(CS.Get("Chip_SystemAudio"), ro.RecordSystemAudio, v => Rec(ro with { RecordSystemAudio = v }), "Icon.Speaker.Regular"));
                _moreHost.Children.Add(WebcamRow(p, ro, Rec));
            }
            _moreHost.Children.Add(PopupRow("Icon.Record.Regular", CS.Get("Chip_VideoOptions"), ro.Fps + " fps", false, BuildVideoOptionsPanel));
        }
    }

    // ------------------------------------------------------------------ selection (inside "More options")

    private void BuildSelection()
    {
        _selectionHost.Children.Clear();
        var p = _m.Working;

        if (p.Mode == CaptureMode.AllInOne)
        {
            var note = Ui.Label(CS.Get("Selection_AllInOneNote"), 12, secondary: true, wrap: true);
            note.Margin = new Thickness(2, 0, 0, 6);
            _selectionHost.Children.Add(note);
        }

        var types = CaptureWindowModel.SelectionsFor(p.Mode).Select(t => (t, CS.Get("Sel_" + t))).ToList();
        var combo = Ui.Combo(types, p.Selection, t => { _m.SetSelection(t); Changed(rebuildSelection: true, rebuildRows: true); }, 200);
        combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _selectionHost.Children.Add(combo);

        var o = p.SelectionOptions;
        void SetOptions(SelectionOptions n, bool rebuild = false) { p.SelectionOptions = n; Changed(rebuildSelection: rebuild); }

        switch (p.Selection)
        {
            case SelectionType.Region or SelectionType.Freehand or SelectionType.MultipleArea when p.Mode != CaptureMode.AllInOne || p.Selection == SelectionType.Region:
                AddRow(CS.Get("Opt_Aspect"), Ui.Combo(new[]
                {
                    (AspectLock.None, CS.Get("Aspect_None")), (AspectLock.Square, "1:1"), (AspectLock.FourThree, "4:3"), (AspectLock.SixteenNine, "16:9"),
                }, o.AspectLock, a => SetOptions(o with { AspectLock = a }), 140));
                break;
            case SelectionType.FixedRegion:
                AddRow(CS.Get("Opt_Size"), Row(
                    NumberBox(o.FixedWidth, 8, 20000, v => SetOptions(o with { FixedWidth = v })), Ui.Label(" × ", 13),
                    NumberBox(o.FixedHeight, 8, 20000, v => SetOptions(o with { FixedHeight = v }))));
                AddCheck(CS.Get("Opt_FixedPosition"), o.UseFixedPosition, v => SetOptions(o with { UseFixedPosition = v, FixedX = v ? (o.FixedX ?? 0) : o.FixedX, FixedY = v ? (o.FixedY ?? 0) : o.FixedY }, rebuild: true));
                if (o.UseFixedPosition)
                    AddRow(CS.Get("Opt_Position"), Row(
                        NumberBox(o.FixedX ?? 0, -32000, 32000, v => SetOptions(o with { FixedX = v })), Ui.Label("  ,  ", 13),
                        NumberBox(o.FixedY ?? 0, -32000, 32000, v => SetOptions(o with { FixedY = v }))));
                break;
            case SelectionType.Window:
                AddCheck(CS.Get("Opt_ActiveWindow"), o.WindowCaptureActive, v => SetOptions(o with { WindowCaptureActive = v }));
                AddCheck(CS.Get("Opt_ClientOnly"), o.WindowClientOnly, v => SetOptions(o with { WindowClientOnly = v }));
                break;
            case SelectionType.Fullscreen:
                AddCheck(CS.Get("Opt_AllMonitors"), o.FullscreenAllMonitors, v => SetOptions(o with { FullscreenAllMonitors = v }, rebuild: true));
                if (!o.FullscreenAllMonitors)
                {
                    var monitors = _sp.GetService<IMonitorService>()?.GetMonitors() ?? Array.Empty<MonitorInfo>();
                    var items = new List<(int, string)> { (-1, CS.Get("Opt_MonitorUnderCursor")) };
                    for (int i = 0; i < monitors.Count; i++) items.Add((i, CS.Format("Opt_MonitorN", i + 1) + (monitors[i].IsPrimary ? " (" + CS.Get("Opt_Primary") + ")" : "")));
                    AddRow(CS.Get("Opt_Monitor"), Ui.Combo(items, o.MonitorIndex ?? -1, v => SetOptions(o with { MonitorIndex = v < 0 ? null : v }), 200));
                }
                break;
            case SelectionType.Menu:
                AddCheck(CS.Get("Opt_MenuBar"), o.MenuIncludeMenuBar, v => SetOptions(o with { MenuIncludeMenuBar = v }));
                AddCheck(CS.Get("Opt_MenuCascaded"), o.MenuCascaded, v => SetOptions(o with { MenuCascaded = v }));
                break;
            case SelectionType.ScrollingWindow:
                AddRow(CS.Get("Opt_ScrollDirection"), Ui.Combo(new[]
                {
                    (ScrollDirection.Vertical, CS.Get("Scroll_Vertical")), (ScrollDirection.Horizontal, CS.Get("Scroll_Horizontal")), (ScrollDirection.Both, CS.Get("Scroll_Both")),
                }, o.ScrollDirection, d => SetOptions(o with { ScrollDirection = d }), 160));
                AddCheck(CS.Get("Opt_ScrollTopLeft"), o.ScrollStartAtTopLeft, v => SetOptions(o with { ScrollStartAtTopLeft = v }));
                AddRow(CS.Get("Opt_ScrollDelay"), Row(NumberBox(o.ScrollStepDelayMs, 0, 5000, v => SetOptions(o with { ScrollStepDelayMs = v })), Ui.Label(" ms", 12, secondary: true)));
                break;
            case SelectionType.StepCapture:
            {
                var s = o.Steps;
                AddRow(CS.Get("Opt_StepArea"), Ui.Combo(new[]
                {
                    (StepCaptureArea.Window, CS.Get("Step_Window")), (StepCaptureArea.Region, CS.Get("Step_Region")), (StepCaptureArea.Monitor, CS.Get("Step_Monitor")),
                }, s.Area, a => SetOptions(o with { Steps = s with { Area = a } }), 160));
                AddCheck(CS.Get("Opt_StepCursor"), s.IncludeCursor, v => SetOptions(o with { Steps = s with { IncludeCursor = v } }));
                AddCheck(CS.Get("Opt_StepHighlight"), s.HighlightClick, v => SetOptions(o with { Steps = s with { HighlightClick = v } }));
                AddRow(CS.Get("Opt_StepMax"), NumberBox(s.MaxSteps, 1, 1000, v => SetOptions(o with { Steps = s with { MaxSteps = v } })));
                break;
            }
        }
    }

    private void AddRow(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(2, 8, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Ui.Label(label, 13));
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        _selectionHost.Children.Add(g);
    }

    private void AddCheck(string text, bool value, Action<bool> set)
    {
        var c = Ui.Switch(text, value, set);
        c.MinHeight = 34;
        c.Margin = new Thickness(0, 4, 0, 0);
        _selectionHost.Children.Add(c);
    }

    private static StackPanel Row(params UIElement[] children) => Ui.Row(children);

    private static TextBox NumberBox(int value, int min, int max, Action<int> set, double width = 80)
    {
        var t = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Width = width, MinHeight = 30 }.WithStyle("Shotwise.NumberBox");
        t.TextChanged += (_, _) =>
        {
            if (int.TryParse(t.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) && v >= min && v <= max)
            {
                t.ClearValue(Control.BorderBrushProperty);
                set(v);
            }
            else t.SetResourceReference(Control.BorderBrushProperty, "Shotwise.DangerBrush");
        };
        return t;
    }

    // ------------------------------------------------------------------ flyouts + rows that open them

    /// <summary>Row-style button (icon, title, summary chip) that opens a flyout panel.</summary>
    private Button PopupRow(string iconKey, string title, string summary, bool active, Func<UIElement> content)
    {
        var b = PopupRowRaw(iconKey, title, summary, active);
        b.Click += (_, _) => ShowFlyout(b, content(), () => { Changed(rebuildRows: true); });
        return b;
    }

    /// <summary>Opens a themed floating card below <paramref name="anchor"/>; <paramref name="closed"/> runs after it closes.</summary>
    private void ShowFlyout(FrameworkElement anchor, UIElement content, Action? closed = null, double minWidth = 300)
    {
        var card = Ui.FloatingCard(content, 12, new Thickness(14));
        card.MinWidth = minWidth; card.MaxWidth = 440;
        card.Margin = new Thickness(10, 2, 10, 16);
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = card,
        };
        popup.Closed += (_, _) => closed?.Invoke();
        popup.IsOpen = true;
    }

    private string TimingText(TimingOptions t)
    {
        if (t.ScheduleAt is { } at) return CS.Format("Chip_ScheduleAt", at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        if (t.IntervalSeconds is > 0) return CS.Format("Chip_Interval", t.IntervalSeconds.Value);
        if (t.DelaySeconds is > 0) return CS.Format("Chip_DelayN", t.DelaySeconds.Value);
        return CS.Get("Timing_Off");
    }

    private string ShareText() => _m.Working.Outputs.Count == 0 ? CS.Get("Timing_Off") : CS.Format("Chip_ShareToN", _m.Working.Outputs.Count);
    private string EffectsText() => _m.Working.Effects.Count == 0 ? CS.Get("Timing_Off") : CS.Format("Chip_EffectsN", _m.Working.Effects.Count);

    private UIElement OpenInRow()
    {
        var p = _m.Working;
        string summary = string.IsNullOrWhiteSpace(p.OpenInProgramId) ? CS.Get("Timing_Off") : System.IO.Path.GetFileNameWithoutExtension(p.OpenInProgramId);
        var row = PopupRowRaw("Icon.Open.Regular", CS.Get("Chip_OpenIn"), summary, !string.IsNullOrWhiteSpace(p.OpenInProgramId));
        row.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = row, Placement = PlacementMode.Bottom };
            var choose = new MenuItem { Header = CS.Get("OpenIn_Choose") };
            choose.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = CS.Get("OpenIn_Choose"), Filter = CS.Get("OpenIn_Filter") };
                if (dlg.ShowDialog(this) == true) { p.OpenInProgramId = dlg.FileName; Changed(rebuildRows: true); }
            };
            menu.Items.Add(choose);
            if (!string.IsNullOrWhiteSpace(p.OpenInProgramId))
            {
                var clear = new MenuItem { Header = CS.Get("OpenIn_Clear") };
                clear.Click += (_, _) => { p.OpenInProgramId = null; Changed(rebuildRows: true); };
                menu.Items.Add(clear);
            }
            menu.IsOpen = true;
        };
        return row;
    }

    /// <summary>Row button without a flyout (the caller wires Click).</summary>
    private Button PopupRowRaw(string iconKey, string title, string summary, bool active)
    {
        var chip = new Border { Child = new TextBlock { Text = summary, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 }, Margin = new Thickness(8, 0, 10, 0) };
        chip.WithChipStyle(active);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);
        var b = new Button { Content = grid, MinHeight = 38 }.WithStyle("Capture.RowButton");
        SwUi.SetIcon(b, iconKey);
        Ui.AutomationProperties(b, title);
        return b;
    }

    // ------------------------------------------------------------------ popup panels

    private static TextBlock PanelTitle(string text) => Ui.Label(text, 14, weight: FontWeights.SemiBold);

    private UIElement BuildTimingPanel()
    {
        var p = _m.Working;
        var panel = new StackPanel();
        panel.Children.Add(PanelTitle(CS.Get("Timing_Title")));

        // delay
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        delayRow.Children.Add(Ui.Label(CS.Get("Timing_Delay"), 13));
        var delayBox = NumberBox(p.Timing.DelaySeconds ?? 0, 0, 3600, v => { p.Timing = p.Timing with { DelaySeconds = v == 0 ? null : v }; if (v > 0) _lastDelay = v; }, 70);
        delayBox.Margin = new Thickness(10, 0, 6, 0);
        delayRow.Children.Add(delayBox);
        delayRow.Children.Add(Ui.Label(CS.Get("Timing_Seconds"), 12, secondary: true));
        foreach (var (label, secs) in new[] { (CS.Get("Timing_Off"), 0), ("5 s", 5), ("10 s", 10) })
        {
            var b = Ui.TextButton(label, () => delayBox.Text = secs.ToString(), ghost: true);
            b.Margin = new Thickness(6, 0, 0, 0); b.Padding = new Thickness(8, 0, 8, 0); b.MinHeight = 28;
            delayRow.Children.Add(b);
        }
        panel.Children.Add(delayRow);
        panel.Children.Add(Spaced(Ui.Switch(CS.Get("Timing_Countdown"), p.Timing.ShowCountdown, v => p.Timing = p.Timing with { ShowCountdown = v }), 6));

        // interval
        panel.Children.Add(Ui.Separator());
        var (iv, unit) = p.Timing.IntervalSeconds is { } s ? CaptureWindowModel.SplitInterval(s) : (30.0, 0);
        bool intervalOn = p.Timing.IntervalSeconds is > 0;
        var intervalBox = new TextBox { Text = iv.ToString("0.##", CultureInfo.CurrentCulture), Width = 70, MinHeight = 30, Margin = new Thickness(8, 0, 6, 0), IsEnabled = intervalOn }.WithStyle("Shotwise.NumberBox");
        var unitBox = new ComboBox { ItemsSource = new[] { CS.Get("Unit_Seconds"), CS.Get("Unit_Minutes"), CS.Get("Unit_Hours") }, SelectedIndex = unit, MinWidth = 100, MinHeight = 32, IsEnabled = intervalOn };
        void ApplyInterval()
        {
            if (double.TryParse(intervalBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && v > 0)
                p.Timing = p.Timing with { IntervalSeconds = CaptureWindowModel.IntervalSeconds(v, unitBox.SelectedIndex) };
        }
        var intervalCheck = Ui.Switch(CS.Get("Timing_Interval"), intervalOn, on =>
        {
            intervalBox.IsEnabled = unitBox.IsEnabled = on;
            if (on) ApplyInterval(); else p.Timing = p.Timing with { IntervalSeconds = null };
        }, "Icon.History.Color");
        panel.Children.Add(intervalCheck);
        var ivRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(40, 4, 0, 0) };
        ivRow.Children.Add(Ui.Label(CS.Get("Timing_Every"), 12, secondary: true));
        ivRow.Children.Add(intervalBox); ivRow.Children.Add(unitBox);
        intervalBox.TextChanged += (_, _) => { if (intervalCheck.IsChecked == true) ApplyInterval(); };
        unitBox.SelectionChanged += (_, _) => { if (intervalCheck.IsChecked == true) ApplyInterval(); };
        panel.Children.Add(ivRow);

        // schedule
        panel.Children.Add(Ui.Separator());
        bool schedOn = p.Timing.ScheduleAt is not null;
        var at = p.Timing.ScheduleAt?.ToLocalTime() ?? DateTimeOffset.Now.AddHours(1);
        var date = new DatePicker { SelectedDate = at.Date, IsEnabled = schedOn, MinWidth = 140 };
        var time = new TextBox { Text = at.ToString("HH:mm", CultureInfo.CurrentCulture), Width = 70, MinHeight = 30, Margin = new Thickness(8, 0, 0, 0), IsEnabled = schedOn }.WithStyle("Shotwise.NumberBox");
        void ApplySchedule()
        {
            if (date.SelectedDate is { } d && TimeSpan.TryParseExact(time.Text.Trim(), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var t))
            {
                var local = new DateTimeOffset(d.Date + t, TimeZoneInfo.Local.GetUtcOffset(d.Date + t));
                p.Timing = p.Timing with { ScheduleAt = local };
                time.ClearValue(Control.BorderBrushProperty);
            }
            else time.SetResourceReference(Control.BorderBrushProperty, "Shotwise.DangerBrush");
        }
        var schedCheck = Ui.Switch(CS.Get("Timing_Schedule"), schedOn, on =>
        {
            date.IsEnabled = time.IsEnabled = on;
            if (on) ApplySchedule(); else p.Timing = p.Timing with { ScheduleAt = null };
        }, "Icon.Calendar.Regular");
        panel.Children.Add(schedCheck);
        var schedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(40, 4, 0, 0) };
        schedRow.Children.Add(date); schedRow.Children.Add(time);
        date.SelectedDateChanged += (_, _) => { if (schedCheck.IsChecked == true) ApplySchedule(); };
        time.TextChanged += (_, _) => { if (schedCheck.IsChecked == true) ApplySchedule(); };
        panel.Children.Add(schedRow);
        return panel;
    }

    private UIElement BuildVideoOptionsPanel()
    {
        var p = _m.Working;
        var ro = p.Recording;
        void Rec(RecordingOptions n) { p.Recording = n; ro = n; }
        var devices = _sp.GetService<IAudioDeviceService>();
        var mics = devices?.Microphones() ?? Array.Empty<DeviceInfo>();
        var cams = devices?.Webcams() ?? Array.Empty<DeviceInfo>();
        var noise = _sp.GetService<INoiseSuppressor>();
        var panel = new StackPanel();
        panel.Children.Add(PanelTitle(CS.Get("VideoOptions_Title")));

        panel.Children.Add(Section(CS.Get("Ready_Microphone")));
        if (mics.Count == 0) panel.Children.Add(Ui.Label(CS.Get("Ready_NoMicrophones"), 12, secondary: true));
        else panel.Children.Add(Ui.Combo(mics.Select(m => ((string?)m.Id, m.Name + (m.IsDefault ? " " + CS.Get("Ready_Default") : ""))).Prepend(((string?)null, CS.Get("Ready_SystemDefault"))), ro.MicrophoneId, id => Rec(ro with { MicrophoneId = id }), 320));
        var nr = Ui.Switch(CS.Get("Ready_RemoveNoise"), ro.RemoveNoise, v => Rec(ro with { RemoveNoise = v }));
        nr.IsEnabled = noise?.IsAvailable ?? false;
        nr.ToolTip = nr.IsEnabled ? CS.Get("Ready_RemoveNoiseTip") : CS.Get("Ready_RemoveNoiseUnavailable");
        panel.Children.Add(nr);

        panel.Children.Add(Section(CS.Get("Ready_Webcam")));
        if (cams.Count == 0) panel.Children.Add(Ui.Label(CS.Get("Ready_NoWebcams"), 12, secondary: true));
        else panel.Children.Add(Ui.Combo(cams.Select(c => ((string?)c.Id, c.Name)).Prepend(((string?)null, CS.Get("Ready_SystemDefault"))), ro.WebcamId, id => Rec(ro with { WebcamId = id }), 320));
        var shapeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        shapeRow.Children.Add(Ui.Combo(new[] { (WebcamShape.Circle, CS.Get("Shape_Circle")), (WebcamShape.RoundedSquare, CS.Get("Shape_RoundedSquare")), (WebcamShape.RoundedRectangle, CS.Get("Shape_RoundedRectangle")) }, ro.PipShape, s => Rec(ro with { PipShape = s }), 150));
        shapeRow.Children.Add(new Border { Width = 8 });
        shapeRow.Children.Add(Ui.Combo(new[] { (Corner.TopLeft, CS.Get("Corner_TopLeft")), (Corner.TopRight, CS.Get("Corner_TopRight")), (Corner.BottomLeft, CS.Get("Corner_BottomLeft")), (Corner.BottomRight, CS.Get("Corner_BottomRight")) }, ro.PipCorner, c => Rec(ro with { PipCorner = c }), 150));
        panel.Children.Add(shapeRow);

        var previewBtn = Ui.TextButton(CS.Get("Webcam_PreviewButton"), () => ToggleWebcamPreview(ro), "Icon.Webcam.Regular");
        previewBtn.Margin = new Thickness(0, 8, 0, 0);
        previewBtn.HorizontalAlignment = HorizontalAlignment.Left;
        previewBtn.IsEnabled = cams.Count > 0;
        panel.Children.Add(previewBtn);

        panel.Children.Add(Section(CS.Get("Ready_Fps")));
        panel.Children.Add(Ui.Combo(RecordingOptions.SupportedFps.Select(f => (f, f + " fps")), ro.Fps, f => Rec(ro with { Fps = f }), 100));

        panel.Children.Add(Section(CS.Get("VideoOptions_CursorFx")));
        panel.Children.Add(Ui.Switch(CS.Get("Ready_CursorHighlight"), ro.CursorHighlight, v => Rec(ro with { CursorHighlight = v })));
        panel.Children.Add(Ui.Switch(CS.Get("Ready_ClickAnimation"), ro.ClickAnimation, v => Rec(ro with { ClickAnimation = v })));
        panel.Children.Add(Ui.Switch(CS.Get("Ready_ShowReady"), ro.ShowReadyWindow, v => Rec(ro with { ShowReadyWindow = v })));
        panel.Children.Add(Ui.Switch(CS.Get("Ready_CountdownOpt"), ro.Countdown, v => Rec(ro with { Countdown = v })));
        panel.Children.Add(Ui.Switch(CS.Get("Ready_AutoStart"), ro.AutoStart, v => Rec(ro with { AutoStart = v })));

        // REC-11: save the recording as an animated GIF (the MP4 is kept as well).
        panel.Children.Add(Section(CS.Get("Chip_RecordAsGif")));
        var gif = Ui.Switch(CS.Get("Chip_RecordAsGif"), _coordinator.IsRecordAsGif(p.Id), v => _coordinator.SetRecordAsGif(p.Id, v), "Icon.Gif.Regular");
        gif.IsEnabled = _sp.GetService<IGifService>() is not null;
        panel.Children.Add(gif);
        return panel;
    }

    private static TextBlock Section(string text)
    {
        var t = Ui.Header(text);
        t.Margin = new Thickness(0, 14, 0, 6);
        return t;
    }

    private WebcamPreviewWindow? _previewWindow;

    private async void ToggleWebcamPreview(RecordingOptions ro)
    {
        if (_previewWindow is not null) { _previewWindow.StopPreview(); _previewWindow.Close(); _previewWindow = null; return; }
        var factory = _sp.GetService<IWebcamPreviewFactory>();
        var cams = _sp.GetService<IAudioDeviceService>()?.Webcams();
        var id = ro.WebcamId ?? cams?.FirstOrDefault(c => c.IsDefault)?.Id ?? cams?.FirstOrDefault()?.Id;
        if (factory is null || id is null) return;
        var w = new WebcamPreviewWindow(ro.PipShape);
        _previewWindow = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_previewWindow, w)) _previewWindow = null; };
        w.Show();
        try { await w.StartAsync(factory, id, _log, CancellationToken.None); }
        catch (Exception ex)
        {
            w.Close();
            if (_sp.GetService<IDialogService>() is { } d) await d.ShowMessage(new MessageRequest(CS.Get("Webcam_PreviewButton"), ex.Message, MessageKind.Warning) { Owner = this });
        }
    }

    private UIElement BuildSharePanel()
    {
        var p = _m.Working;
        var panel = new StackPanel();
        panel.Children.Add(PanelTitle(CS.Get("Share_Title")));
        var share = _sp.GetService<IShareService>();
        if (share is null)
        {
            panel.Children.Add(Spaced(Ui.Label(CS.Get("Share_Unavailable"), 12, secondary: true, wrap: true), 8));
            return panel;
        }
        string fmt = p.Mode == CaptureMode.Video ? "mp4" : "png";
        var dests = share.Destinations.Where(d => share.IsEnabled(d.Id) && d.Id != KnownDestinations.Clipboard && d.SupportedFormats.Contains(fmt)).ToList();
        if (dests.Count == 0) panel.Children.Add(Spaced(Ui.Label(CS.Get("Share_None"), 12, secondary: true, wrap: true), 8));
        foreach (var d in dests)
        {
            var id = d.Id;
            var row = Ui.Switch(d.DisplayName + (d.RequiresAccount && !d.IsConfigured ? " " + CS.Get("Share_NeedsAccount") : ""), _m.HasOutput(id), v => _m.SetOutput(id, v), "Icon.Share.Regular");
            row.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(row);
        }
        return panel;
    }

    private UIElement BuildEffectsPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(PanelTitle(CS.Get("Effects_Title")));
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(list);

        void Rebuild()
        {
            list.Children.Clear();
            var effects = _m.Working.Effects;
            if (effects.Count == 0) list.Children.Add(Ui.Label(CS.Get("Effects_None"), 12, secondary: true));
            for (int i = 0; i < effects.Count; i++)
            {
                int index = i;
                var e = effects[i];
                var enabled = new CheckBox { IsChecked = e.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = CS.Get("Effects_Enabled") }.WithStyle("Shotwise.Switch");
                enabled.Click += (_, _) => { _m.ReplaceEffect(index, e with { Enabled = enabled.IsChecked == true }); Rebuild(); Changed(); };
                var name = Ui.Label(CaptureWindowModel.EffectName(e), 13);
                name.Width = 130; name.TextTrimming = TextTrimming.CharacterEllipsis; name.Opacity = e.Enabled ? 1 : 0.5;
                var up = Ui.IconButton("Icon.ChevronDown.Regular", CS.Get("Effects_Up"), () => { _m.MoveEffect(index, -1); Rebuild(); Changed(); }, 14);
                up.RenderTransformOrigin = new Point(0.5, 0.5); up.RenderTransform = new RotateTransform(180);
                var down = Ui.IconButton("Icon.ChevronDown.Regular", CS.Get("Effects_Down"), () => { _m.MoveEffect(index, 1); Rebuild(); Changed(); }, 14);
                var edit = Ui.IconButton("Icon.Editor.Regular", CS.Get("Effects_Edit"), async () => await EditEffectAsync(index, Rebuild), 16);
                var remove = Ui.IconButton("Icon.Delete.Regular", CS.Get("Effects_Remove"), () => { _m.RemoveEffect(index); Rebuild(); Changed(); }, 16);
                up.IsEnabled = index > 0; down.IsEnabled = index < effects.Count - 1;
                var row = Ui.Row(enabled, new Border { Width = 8 }, name, up, down, edit, remove);
                row.Margin = new Thickness(0, 2, 0, 2);
                list.Children.Add(row);
            }
        }
        Rebuild();

        var add = Ui.TextButton(CS.Get("Effects_Add"), () => { }, "Icon.Add.Regular");
        add.Margin = new Thickness(0, 10, 0, 0);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
            foreach (var (key, create) in CaptureWindowModel.EffectChoices)
            {
                var item = new MenuItem { Header = CS.Get(key) };
                item.Click += async (_, _) =>
                {
                    var spec = create();
                    var edited = await EditSpecAsync(spec);
                    if (edited is null) return;
                    _m.AddEffect(edited);
                    Rebuild();
                    Changed();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        panel.Children.Add(add);
        return panel;
    }

    private async Task<EffectSpec?> EditSpecAsync(EffectSpec spec)
    {
        if (_sp.GetService<IDialogService>() is not { } dialogs) return spec;
        var result = await dialogs.ShowEffectEditor(new EffectEditorRequest(spec) { Owner = this });
        return result?.Effect;
    }

    private async Task EditEffectAsync(int index, Action rebuild)
    {
        if (index < 0 || index >= _m.Working.Effects.Count) return;
        var edited = await EditSpecAsync(_m.Working.Effects[index]);
        if (edited is null) return;
        _m.ReplaceEffect(index, edited);
        rebuild();
        Changed();
    }

    // ------------------------------------------------------------------ capture button / modified bar / footer

    private void UpdateCaptureButton()
    {
        var p = _m.Working;
        bool video = p.Mode == CaptureMode.Video;
        // The key that really triggers the capture (e.g. Shift+PrintScreen when PrintScreen is held by another app).
        var key = _coordinator.EffectiveCaptureHotkey ?? _settings.Current.Hotkeys.GlobalCapture;
        _captureButton.Content = video ? CS.Get("Capture_Record") : CS.Get("Capture_Button");
        _captureButton.Icon = video ? "Icon.Record.Filled" : "Icon.Capture.Filled";
        _captureButton.Shortcut = key is { IsEmpty: false } k ? k.ToDisplayString() : null;
        _captureButton.ToolTip = p.Name + (key is { IsEmpty: false } k2 ? " (" + k2.ToDisplayString() + ")" : "");
        _captureButton.IsEnabled = !_coordinator.IsCapturing;
        _presetName.Text = p.Name;
    }

    private Border BuildModifiedBar()
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Shotwise.WarningBrush");
        // Compact: dot + short label (trimmed if space is tight) + Save. "Reset current settings" lives on the
        // reset icon in the card's top-right corner, so it isn't repeated here (it didn't fit and was cut off).
        var text = Ui.Label(CS.Get("Preset_ModifiedShort"), 12, secondary: true);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.ToolTip = CS.Get("Preset_Modified");
        var save = Ui.TextButton(CS.Get("Preset_Save"), async () => { _suppress = true; try { await _m.SaveCurrentAsync(); } finally { _suppress = false; } RefreshAll(); }, ghost: true);
        save.MinHeight = 26; save.Padding = new Thickness(8, 0, 8, 0); save.FontSize = 12; save.Margin = new Thickness(4, 0, 0, 0);
        save.ToolTip = CS.Get("Preset_SaveTooltip");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(dot);
        Grid.SetColumn(text, 1); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text);
        Grid.SetColumn(save, 2); row.Children.Add(save);
        return new Border { Child = row, Visibility = Visibility.Collapsed };
    }

    private void UpdateModifiedBar() => _modifiedBar.Visibility = _m.IsModified ? Visibility.Visible : Visibility.Collapsed;

    private UIElement BuildFooter()
    {
        var presets = new Button { Content = Ui.Row(Ui.Icon("Icon.Collections.Regular", 16), new Border { Width = 6 }, Ui.Label(CS.Get("Presets_Header"), 12.5), new Border { Width = 6 }, _presetName, new Border { Width = 4 }, Ui.Icon("Icon.ChevronDown.Regular", 12)), Padding = new Thickness(8, 0, 6, 0), MinHeight = 30 }.WithStyle("Shotwise.GhostButton");
        _presetName.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.AccentTextBrush");
        Ui.AutomationProperties(presets, CS.Get("Presets_Header"));
        presets.Click += (_, _) => ShowPresetsFlyout(presets);
        var add = Ui.IconButton("Icon.Add.Regular", CS.Get("Preset_SaveAs"), async () => await SaveAsNewAsync(), 16);
        add.Width = add.Height = 30;
        var edit = Ui.TextButton(CS.Get("CW_OpenEditor"), () => _sp.GetService<IEditorHost>()?.ShowEditor(), "Icon.Editor.Color", ghost: true);
        edit.MinHeight = 30; edit.Padding = new Thickness(8, 0, 8, 0);
        edit.HorizontalAlignment = HorizontalAlignment.Right;

        var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(presets);
        Grid.SetColumn(add, 1); g.Children.Add(add);
        Grid.SetColumn(edit, 2); g.Children.Add(edit);
        return g;
    }
}

internal static class CaptureWindowExtensions
{
    /// <summary>Gives a border the design-system chip look (accent-tinted when <paramref name="active"/>).</summary>
    public static void WithChipStyle(this Border chip, bool active)
    {
        chip.SetResourceReference(FrameworkElement.StyleProperty, active ? "Shotwise.AccentChip" : "Shotwise.Chip");
    }
}
