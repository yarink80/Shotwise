using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;
using Shotwise.UI.Capture.Coordinator;
using Shotwise.UI.Capture.Hud;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;
using CaptureMode = Shotwise.Core.Capture.CaptureMode;

namespace Shotwise.UI.Capture.Views;

public enum DockEdge { Left, Right, Top, Bottom }

/// <summary>
/// The Capture Widget (OneClick): a pill-shaped floating bar docked to a screen edge. Drag to move it along (or to another) edge, it
/// snaps back to the nearest edge on release, collapses to a thin handle when the pointer leaves and expands on hover. The coral dot
/// runs the selected preset, Image / Video run it in that mode; the menu button lists all presets.
/// </summary>
public sealed class CaptureWidget : HudWindow
{
    private sealed record Placement(string Monitor, DockEdge Edge, double Offset);

    private readonly CaptureCoordinator _coordinator;
    private readonly IServiceProvider _sp;
    private readonly IPresetRepository _repo;
    private readonly ILogger? _log;
    private readonly StackPanel _buttons = new();
    private readonly Border _handle = new() { CornerRadius = new CornerRadius(3) };
    private readonly Grid _root = new();
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private DockEdge _edge = DockEdge.Right;
    private double _offset = 0.4; // 0..1 along the edge
    private string _monitorName = "";
    private bool _collapsed;
    private bool _dragging;
    private CapturePreset _selected;
    private readonly string _stateFile;

    public CaptureWidget(CaptureCoordinator coordinator, IServiceProvider sp) : base(noActivate: true, HudKind.Card)
    {
        _coordinator = coordinator;
        _sp = sp;
        _repo = sp.GetRequiredService<IPresetRepository>();
        _log = sp.GetService<ILogger<CaptureWidget>>();
        _selected = _repo.Active;
        _stateFile = Path.Combine(sp.GetService<IAppPaths>()?.AppData ?? Path.GetTempPath(), "widget.json");
        ChromePadding = new Thickness(6);
        ChromeRadius = new CornerRadius(26);
        Title = CS.Get("Widget_Title");
        LoadPlacement();

        _handle.SetResourceReference(Border.BackgroundProperty, "Shotwise.AccentBrush");
        _root.Children.Add(_buttons);
        _root.Children.Add(_handle);
        Content = _root;
        BuildButtons();

        MouseEnter += (_, _) => { _collapseTimer.Stop(); SetCollapsed(false); };
        MouseLeave += (_, _) => { if (!_dragging && _settingsAutoCollapse) _collapseTimer.Start(); };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!IsMouseOver && !_dragging) SetCollapsed(true); };
        SizeChanged += (_, _) => Dock();
        MouseLeftButtonUp += (_, _) => { };
        LocationChanged += (_, _) => { };
        _repo.Changed += OnRepoChanged;
        Closed += (_, _) => { _repo.Changed -= OnRepoChanged; _collapseTimer.Stop(); SavePlacement(); };
    }

    private bool _settingsAutoCollapse = true;

    public void Open()
    {
        Show();
        Dock();
        _collapseTimer.Start();
    }

    private void OnRepoChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        _selected = _repo.Find(_selected.Id) ?? _repo.Active;
        BuildButtons();
    }));

    // ------------------------------------------------------------------ content

    private bool Vertical => _edge is DockEdge.Left or DockEdge.Right;

    private void BuildButtons()
    {
        _buttons.Children.Clear();
        _buttons.Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal;

        // Transparent margin (shadow room) only on the sides that do not touch the docked screen edge.
        ShadowMargin = _edge switch
        {
            DockEdge.Left => new Thickness(0, 10, 10, 10),
            DockEdge.Right => new Thickness(10, 10, 0, 10),
            DockEdge.Top => new Thickness(10, 0, 10, 10),
            _ => new Thickness(10, 10, 10, 0),
        };

        var grip = new Border
        {
            Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = CS.Get("Widget_Drag"),
            Padding = Vertical ? new Thickness(8, 4, 8, 4) : new Thickness(4, 8, 4, 8),
            Child = Ui.Icon(Vertical ? "Icon.More.Regular" : "Icon.MoreVertical.Regular", 16, "Shotwise.TextTertiaryBrush"),
        };
        grip.MouseLeftButtonDown += (_, e) => { BeginDrag(); e.Handled = true; };

        // The coral dot runs the selected preset as configured; Image / Video run it in that mode.
        var dot = new Button { ToolTip = CS.Get("Capture_Button") + " - " + _selected.Name }.WithStyle("Capture.HudStopButton");
        dot.Width = dot.Height = 40;
        SwUi.SetIcon(dot, "Icon.Capture.Filled");
        SwUi.SetIconSize(dot, 20);
        Ui.AutomationProperties(dot, CS.Get("Capture_Button"));
        dot.Click += (_, _) => { if (!_coordinator.IsCapturing) _ = _coordinator.RunAsync(_selected.Clone()); };
        var image = Ui.IconButton("Icon.Image.Color", CS.Get("Widget_Image"), () => Run(wantVideo: false), 24);
        var video = Ui.IconButton("Icon.Video.Color", CS.Get("Widget_Video"), () => Run(wantVideo: true), 24);
        var preset = Ui.IconButton("Icon.ChevronDown.Regular", CS.Format("Widget_Preset", _selected.Name), () => { }, 16);
        preset.Click += (_, _) => ShowPresetMenu(preset);
        var window = Ui.IconButton("Icon.Open.Regular", CS.Get("Widget_OpenWindow"), () => _coordinator.ShowCaptureWindow(), 18);
        foreach (var b in new FrameworkElement[] { dot, image, video, preset, window }) b.Margin = new Thickness(Vertical ? 0 : 3, Vertical ? 3 : 0, 0, 0);
        var divider = Vertical
            ? new Border { Height = 1, Margin = new Thickness(6, 5, 6, 2) }
            : new Border { Width = 1, Margin = new Thickness(5, 6, 2, 6) };
        divider.SetResourceReference(Border.BackgroundProperty, "Shotwise.BorderSubtleBrush");
        _buttons.Children.Add(grip);
        _buttons.Children.Add(dot);
        _buttons.Children.Add(image);
        _buttons.Children.Add(video);
        _buttons.Children.Add(divider);
        _buttons.Children.Add(preset);
        _buttons.Children.Add(window);
        _handle.Width = Vertical ? 5 : 40;
        _handle.Height = Vertical ? 40 : 5;
        _handle.HorizontalAlignment = HorizontalAlignment.Center;
        _handle.VerticalAlignment = VerticalAlignment.Center;
        SetCollapsed(_collapsed);
    }

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _buttons.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _handle.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        ChromePadding = collapsed ? new Thickness(4) : new Thickness(6);
        ChromeRadius = collapsed ? new CornerRadius(8) : new CornerRadius(26);
        UpdateLayout();
        Dock();
    }

    private void Run(bool wantVideo)
    {
        if (_coordinator.IsCapturing) return;
        var p = _selected;
        if (wantVideo && p.Mode != CaptureMode.Video) p = _repo.Find(PresetDefaults.VideoId) ?? p;
        else if (!wantVideo && p.Mode == CaptureMode.Video) p = _repo.Find(PresetDefaults.ImageRegionId) ?? p;
        _ = _coordinator.RunAsync(p.Clone());
    }

    private void ShowPresetMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = Vertical ? PlacementMode.Left : PlacementMode.Bottom };
        if (_edge == DockEdge.Left) menu.Placement = PlacementMode.Right;
        if (_edge == DockEdge.Bottom) menu.Placement = PlacementMode.Top;
        foreach (var p in _repo.All.OrderBy(p => p.Order))
        {
            var preset = p;
            var item = new MenuItem { Header = p.Name, InputGestureText = p.Hotkey is { } h ? h.ToDisplayString() : "", IsCheckable = true, IsChecked = p.Id == _selected.Id };
            item.Click += (_, _) => { _selected = preset; _repo.Active = preset; BuildButtons(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = CS.Get("Widget_Hide") };
        hide.Click += (_, _) => _coordinator.ToggleWidget(false);
        menu.Items.Add(hide);
        _collapseTimer.Stop();
        menu.Closed += (_, _) => { if (!IsMouseOver) _collapseTimer.Start(); };
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ docking

    private IReadOnlyList<MonitorInfo> Monitors => _sp.GetService<IMonitorService>()?.GetMonitors() ?? Array.Empty<MonitorInfo>();

    private MonitorInfo? CurrentMonitor()
    {
        var list = Monitors;
        return list.FirstOrDefault(m => m.DeviceName == _monitorName) ?? list.FirstOrDefault(m => m.IsPrimary) ?? list.FirstOrDefault();
    }

    /// <summary>Positions the window on its edge (physical pixels) according to the stored edge/offset.</summary>
    private void Dock()
    {
        if (_dragging) return;
        var mon = CurrentMonitor();
        if (mon is null || !IsLoaded && !IsVisible) return;
        var area = mon.WorkAreaPx;
        double scale = NativeUi.ScaleOf(this);
        int w = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale)), h = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
        int x, y;
        switch (_edge)
        {
            case DockEdge.Left: x = area.X; y = area.Y + (int)((area.Height - h) * _offset); break;
            case DockEdge.Right: x = area.Right - w; y = area.Y + (int)((area.Height - h) * _offset); break;
            case DockEdge.Top: y = area.Y; x = area.X + (int)((area.Width - w) * _offset); break;
            default: y = area.Bottom - h; x = area.X + (int)((area.Width - w) * _offset); break;
        }
        NativeUi.MoveTo(this, x, y);
    }

    protected override void OnDragCompleted()
    {
        _dragging = false;
        SnapToNearestEdge();
    }

    private void BeginDrag()
    {
        _dragging = true;
        _collapseTimer.Stop();
        try { DragMove(); } catch (InvalidOperationException) { }
        _dragging = false;
        SnapToNearestEdge();
    }

    private void SnapToNearestEdge()
    {
        var rect = NativeUi.WindowRectPx(this);
        var center = new PointI(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var mon = Monitors.FirstOrDefault(m => m.BoundsPx.Contains(center)) ?? CurrentMonitor();
        if (mon is null) return;
        var a = mon.WorkAreaPx;
        int dl = center.X - a.Left, dr = a.Right - center.X, dt = center.Y - a.Top, db = a.Bottom - center.Y;
        int min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        var edge = min == dl ? DockEdge.Left : min == dr ? DockEdge.Right : min == dt ? DockEdge.Top : DockEdge.Bottom;
        bool orientationChanged = (edge is DockEdge.Left or DockEdge.Right) != Vertical;
        bool edgeChanged = edge != _edge;
        _edge = edge;
        _monitorName = mon.DeviceName;
        _offset = edge is DockEdge.Left or DockEdge.Right
            ? Math.Clamp((rect.Y - a.Y) / Math.Max(1.0, a.Height - rect.Height), 0, 1)
            : Math.Clamp((rect.X - a.X) / Math.Max(1.0, a.Width - rect.Width), 0, 1);
        if (orientationChanged || edgeChanged) BuildButtons();
        UpdateLayout();
        Dock();
        SavePlacement();
        if (!IsMouseOver) _collapseTimer.Start();
    }

    // ------------------------------------------------------------------ persistence

    private void LoadPlacement()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var p = JsonSerializer.Deserialize<Placement>(File.ReadAllText(_stateFile));
            if (p is null) return;
            _edge = p.Edge; _offset = Math.Clamp(p.Offset, 0, 1); _monitorName = p.Monitor;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.LogDebug(ex, "Widget placement could not be loaded");
        }
    }

    private void SavePlacement()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            Shotwise.Core.Serialization.AtomicFile.WriteAllText(_stateFile, JsonSerializer.Serialize(new Placement(_monitorName, _edge, _offset)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogDebug(ex, "Widget placement could not be saved");
        }
    }
}
