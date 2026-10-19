using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shotwise.Core.Settings;
using Shotwise.Core.Styles;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Toolbar;

/// <summary>
/// Centre of the editor top bar (DESIGN-SYSTEM §4): the user's pinned tools as colour <see cref="ToolButton"/>s, a "More" button
/// whose flyout grid holds every other tool (hidden ones + those that do not fit), then trailing commands (Undo / Redo).
/// Order and visibility stay customizable (prefs <see cref="EditorSettings.ToolbarTools"/> + layout hidden list): drag a tool
/// from the flyout onto the bar to pin it, drag a bar tool onto "More" to unpin it, or use "Customize toolbar…".
/// Labels follow <see cref="EditorSettings.ShowToolbarLabels"/>; tooltips show the Ctrl+digit hotkey.
/// </summary>
internal sealed class ToolbarView : Grid
{
    private const string DragFormat = "Shotwise.ToolKind";

    private readonly EditorContext _ctx;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, AllowDrop = true, Background = Brushes.Transparent };
    private readonly ContentControl _transientHost = new() { Focusable = false };
    private readonly ToggleButton _more;
    private readonly StackPanel _trailing = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };
    private readonly WrapPanel _flyoutGrid = new() { Width = 4 * 76, Margin = new Thickness(4, 0, 4, 0) };
    private readonly Dictionary<ToolKind, ToolButton> _buttons = new();
    private readonly List<ToolButton> _flyoutButtons = new();
    private List<ToolKind> _order = new();
    private List<ToolKind> _overflow = new();
    private ToolKind _active = ToolKind.Select;
    private int _fitting = int.MaxValue;
    private Point _dragStart;
    private ToolKind? _dragKind;
    private bool _relayoutQueued;

    public ToolbarView(EditorContext ctx)
    {
        _ctx = ctx;
        _more = new ToggleButton { Content = ES.T("Toolbar_MoreLabel", "More"), ToolTip = ES.T("Toolbar_More", "More tools"), AllowDrop = true }.Styled("Shotwise.ToolCommandButton");
        SwUi.SetIcon(_more, "Icon.More.Regular");
        AutomationProperties.SetName(_more, ES.T("Toolbar_More", "More tools"));
        _more.Checked += (_, _) => OpenFlyout();
        _more.Unchecked += (_, _) => _popup.IsOpen = false;
        _more.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        _more.Drop += (_, e) => { if (e.Data.GetData(DragFormat) is string s && Enum.TryParse<ToolKind>(s, out var k)) Unpin(k); e.Handled = true; };
        _popup.PlacementTarget = _more;
        _popup.Closed += (_, _) => _more.IsChecked = false;
        _popup.Child = BuildFlyout();

        _tools.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        _tools.Drop += OnToolsDrop;

        _row.Children.Add(_tools);
        _row.Children.Add(_transientHost);
        _row.Children.Add(_more);
        _row.Children.Add(_trailing);
        Children.Add(_row);
        Children.Add(_popup); // in the tree so the flyout inherits resources and the XAML namespace context
        SizeChanged += (_, _) => QueueRelayout();
        ctx.Settings.Changed += OnSettingsChanged;
        Rebuild();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Affects(nameof(AppSettings.Editor)) || e.Affects(nameof(AppSettings.General))) Dispatcher.BeginInvoke(Rebuild);
    }

    /// <summary>Real window closure (R5-#5): unsubscribes from the shared settings service.</summary>
    public void ReleaseServices() => _ctx.Settings.Changed -= OnSettingsChanged;

    public event Action<ToolKind>? ToolSelected;

    /// <summary>Pinned tools in toolbar order (Ctrl+1..9 map to the first nine).</summary>
    public IReadOnlyList<ToolKind> VisibleTools => _order;

    /// <summary>Elements shown after the tools (Undo / Redo); counted when deciding how many tools fit.</summary>
    public void AddTrailing(UIElement element) => _trailing.Children.Add(element);

    /// <summary>Hides the tools and "More" (video view) while keeping the trailing commands (Undo / Redo).</summary>
    public void SetToolsVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        _tools.Visibility = v;
        _transientHost.Visibility = v;
        _more.Visibility = v;
        if (_trailing.Children.Count > 0 && _trailing.Children[0] is FrameworkElement divider) divider.Visibility = v;
    }

    public void SetActive(ToolKind kind)
    {
        _active = kind;
        foreach (var (k, b) in _buttons) b.IsChecked = k == kind;
        foreach (var b in _flyoutButtons) b.IsChecked = b.Tag is ToolKind k && k == kind;
        UpdateTransient();
    }

    // ------------------------------------------------------------------ build

    private bool Labels => _ctx.Prefs.ShowToolbarLabels;

    private ToolButton MakeButton(ToolKind kind, int slot, bool inFlyout)
    {
        var labels = Labels || inFlyout;
        var b = new ToolButton
        {
            Tag = kind,
            ToolTip = ToolInfo.Tooltip(kind, slot, _ctx.Settings.Current.General.Tooltips),
            Icon = Icons.ToolKey(kind),
            Content = labels ? ToolInfo.Name(kind) : null,
            IsChecked = kind == _active,
            Focusable = true,
        };
        if (!labels) { b.MinWidth = 40; b.Height = 44; }
        if (inFlyout) { b.Width = 72; b.Height = 60; b.Margin = new Thickness(2); }
        AutomationProperties.SetName(b, ToolInfo.Name(kind));
        b.Click += (_, _) =>
        {
            if (inFlyout) { _popup.IsOpen = false; }
            ToolSelected?.Invoke(kind);
            SetActive(kind);
        };
        b.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragKind = kind; };
        b.PreviewMouseMove += (_, e) => OnButtonMouseMove(b, kind, e);
        return b;
    }

    public void Rebuild()
    {
        var hidden = _ctx.Layout.Load().HiddenTools.ToHashSet();
        _order = ToolInfo.Resolve(_ctx.Prefs.ToolbarTools).Where(t => !hidden.Contains(t.ToString())).ToList();
        _tools.Children.Clear();
        _buttons.Clear();
        for (var i = 0; i < _order.Count; i++)
        {
            var b = MakeButton(_order[i], i, false);
            _buttons[_order[i]] = b;
            _tools.Children.Add(b);
        }
        _more.Content = Labels ? ES.T("Toolbar_MoreLabel", "More") : null;
        _more.MinWidth = Labels ? 52 : 40;
        _more.Height = Labels ? 52 : 44;
        Relayout();
    }

    private void QueueRelayout()
    {
        if (_relayoutQueued) return;
        _relayoutQueued = true;
        Dispatcher.BeginInvoke(() => { _relayoutQueued = false; Relayout(); }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void Relayout()
    {
        if (ActualWidth <= 0) return;
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _more.Measure(inf);
        _trailing.Measure(inf);
        var available = ActualWidth - _more.DesiredSize.Width - _trailing.DesiredSize.Width - 70; // keep room for a transient tool
        double used = 0;
        var fit = 0;
        foreach (UIElement child in _tools.Children)
        {
            child.Visibility = Visibility.Visible;
            child.Measure(inf);
            used += child.DesiredSize.Width;
            if (used > available) break;
            fit++;
        }
        _fitting = Math.Max(1, fit);
        for (var i = 0; i < _tools.Children.Count; i++) _tools.Children[i].Visibility = i < _fitting ? Visibility.Visible : Visibility.Collapsed;
        UpdateTransient();
    }

    /// <summary>Tools reachable only through the flyout: unpinned ones plus pinned ones that did not fit.</summary>
    private List<ToolKind> FlyoutTools()
    {
        var all = ToolInfo.Resolve(_ctx.Prefs.ToolbarTools);
        var shown = _order.Take(_fitting).ToHashSet();
        return all.Where(t => !shown.Contains(t)).ToList();
    }

    /// <summary>When the active tool lives in the flyout, show it temporarily before "More" so the selection stays visible.</summary>
    private void UpdateTransient()
    {
        var shown = _order.Take(_fitting).ToHashSet();
        if (shown.Contains(_active)) { _transientHost.Content = null; return; }
        if (_transientHost.Content is ToolButton { Tag: ToolKind k } && k == _active) return;
        var b = MakeButton(_active, -1, false);
        b.IsChecked = true;
        _transientHost.Content = b;
    }

    // ------------------------------------------------------------------ flyout

    private FrameworkElement BuildFlyout()
    {
        var stack = new StackPanel { Margin = new Thickness(4) };
        var title = new TextBlock { Text = ES.T("Toolbar_MoreTitle", "More tools"), Margin = new Thickness(8, 4, 8, 2) }.Styled("Shotwise.Text.Section");
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock { Text = ES.T("Toolbar_DragHint", "Drag a tool onto the toolbar to pin it; drag a toolbar tool onto More to unpin it."), Margin = new Thickness(8, 0, 8, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 }.Styled("Shotwise.Text.Caption"));
        stack.Children.Add(_flyoutGrid);
        stack.Children.Add(UiKit.Divider(6));
        var labels = UiKit.Check(ES.T("Toolbar_Labels", "Show labels"), _ctx.Prefs.ShowToolbarLabels, on => _ctx.Settings.Update(s => s.Editor.ShowToolbarLabels = on));
        labels.Margin = new Thickness(0, 0, 0, 0);
        labels.Tag = "labels";
        stack.Children.Add(labels);
        var customize = UiKit.GhostButton(ES.T("Toolbar_Customize", "Customize toolbar…"), "Icon.Settings.Regular", () => { _popup.IsOpen = false; Customize(); });
        customize.HorizontalAlignment = HorizontalAlignment.Left;
        customize.Margin = new Thickness(0, 2, 0, 2);
        stack.Children.Add(customize);
        var card = UiKit.Card(stack, new Thickness(8), 2);
        card.Margin = new Thickness(14, 4, 14, 20); // room for the flyout shadow
        card.AllowDrop = true;
        card.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        card.Drop += (_, e) => { if (e.Data.GetData(DragFormat) is string s && Enum.TryParse<ToolKind>(s, out var k)) Unpin(k); e.Handled = true; };
        return card;
    }

    private void OpenFlyout()
    {
        _flyoutGrid.Children.Clear();
        _flyoutButtons.Clear();
        foreach (var kind in FlyoutTools())
        {
            var b = MakeButton(kind, _order.IndexOf(kind), true);
            _flyoutButtons.Add(b);
            _flyoutGrid.Children.Add(b);
        }
        if (_popup.Child is Card { Content: StackPanel sp })
            foreach (var row in sp.Children.OfType<ToggleSwitchRow>()) row.IsChecked = _ctx.Prefs.ShowToolbarLabels;
        _popup.HorizontalOffset = -(4 * 76 + 24) / 2.0 + 26;
        _popup.IsOpen = true;
    }

    // ------------------------------------------------------------------ drag to customise

    private void OnButtonMouseMove(ToolButton b, ToolKind kind, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragKind != kind) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        _dragKind = null;
        DragDrop.DoDragDrop(b, new DataObject(DragFormat, kind.ToString()), DragDropEffects.Move);
    }

    private void OnToolsDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string s || !Enum.TryParse<ToolKind>(s, out var kind)) return;
        e.Handled = true;
        var x = e.GetPosition(_tools).X;
        var index = 0;
        double acc = 0;
        foreach (FrameworkElement child in _tools.Children)
        {
            if (child.Visibility != Visibility.Visible) break;
            if (x < acc + child.ActualWidth / 2) break;
            acc += child.ActualWidth;
            index++;
        }
        Pin(kind, index);
    }

    /// <summary>Shows <paramref name="kind"/> on the bar before the visible tool at <paramref name="index"/>.</summary>
    private void Pin(ToolKind kind, int index)
    {
        _popup.IsOpen = false;
        var all = ToolInfo.Resolve(_ctx.Prefs.ToolbarTools);
        var visible = _order.Take(_fitting).Where(t => t != kind).ToList();
        all.Remove(kind);
        var insertAt = index < visible.Count ? all.IndexOf(visible[index]) : (visible.Count > 0 ? all.IndexOf(visible[^1]) + 1 : 0);
        all.Insert(Math.Clamp(insertAt, 0, all.Count), kind);
        var layout = _ctx.Layout.Load();
        layout.HiddenTools.Remove(kind.ToString());
        _ctx.Layout.Save(layout);
        _ctx.Settings.Update(st => st.Editor.ToolbarTools = all.Select(t => t.ToString()).ToList());
        Rebuild();
    }

    /// <summary>Moves a pinned tool into the "More" flyout.</summary>
    private void Unpin(ToolKind kind)
    {
        if (_order.Count <= 1 || !_order.Contains(kind)) return;
        var layout = _ctx.Layout.Load();
        if (!layout.HiddenTools.Contains(kind.ToString())) layout.HiddenTools.Add(kind.ToString());
        _ctx.Layout.Save(layout);
        Rebuild();
    }

    private void Customize()
    {
        var dlg = new ToolbarCustomizeDialog(_ctx);
        dlg.SetOwner(_ctx.ActiveWindow);
        if (dlg.ShowDialog() == true) Rebuild();
    }
}

/// <summary>Reorder / show / hide tools.</summary>
internal sealed class ToolbarCustomizeDialog : DialogWindow
{
    private readonly EditorContext _ctx;
    private readonly ListBox _list = new() { MinHeight = 360, MinWidth = 280 };
    private readonly List<(ToolKind Tool, bool Visible)> _state = new();

    public ToolbarCustomizeDialog(EditorContext ctx)
    {
        _ctx = ctx;
        Title = ES.T("Toolbar_CustomizeTitle", "Customize toolbar");
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        var hidden = ctx.Layout.Load().HiddenTools.ToHashSet();
        foreach (var t in ToolInfo.Resolve(ctx.Prefs.ToolbarTools)) _state.Add((t, !hidden.Contains(t.ToString())));
        var panel = new DockPanel { Margin = new Thickness(20, 48, 20, 16) };
        var intro = UiKit.Hint(ES.T("Toolbar_CustomizeHint", "Switch tools on to show them on the toolbar; the others stay one click away under More. The first nine are Ctrl+1…9."));
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var reset = UiKit.GhostButton(ES.T("Toolbar_Reset", "Reset"), "Icon.Reset.Regular", () =>
        {
            _state.Clear();
            var secondary = EditorLayout.DefaultHiddenTools.ToHashSet();
            foreach (var t in ToolInfo.DefaultOrder) _state.Add((t, !secondary.Contains(t.ToString())));
            Fill(0);
        });
        reset.Margin = new Thickness(0, 0, 8, 0);
        var ok = new Button { Content = ES.T("Common_OK", "OK"), IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) }.Styled("Shotwise.PrimaryButton");
        ok.Click += (_, _) => { Save(); Accept(); };
        var cancel = new Button { Content = ES.T("Common_Cancel", "Cancel"), IsCancel = true, MinWidth = 88 }.Styled("Shotwise.SecondaryButton");
        cancel.Click += (_, _) => CancelDialog();
        buttons.Children.Add(reset);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        side.Children.Add(UiKit.IconButton(Icons.Glyph.Up, ES.T("Common_MoveUp", "Move up"), () => Move(-1)));
        side.Children.Add(UiKit.IconButton(Icons.Glyph.Down, ES.T("Common_MoveDown", "Move down"), () => Move(1)));
        DockPanel.SetDock(side, Dock.Right);
        panel.Children.Add(side);
        panel.Children.Add(_list);
        Content = panel;
        Fill(0);
    }

    private void Fill(int select)
    {
        _list.Items.Clear();
        for (var i = 0; i < _state.Count; i++)
        {
            var index = i;
            var row = new Grid { Height = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = Icons.ForTool(_state[i].Tool, 22);
            icon.Margin = new Thickness(4, 0, 12, 0);
            row.Children.Add(icon);
            var name = UiKit.Label(ToolInfo.Name(_state[i].Tool), false);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var sw = new CheckBox { IsChecked = _state[i].Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) }.Styled("Shotwise.Switch");
            AutomationProperties.SetName(sw, ES.F("Toolbar_ShowTool", "Show {0} on the toolbar", ToolInfo.Name(_state[i].Tool)));
            sw.Click += (_, _) => _state[index] = (_state[index].Tool, sw.IsChecked == true);
            Grid.SetColumn(sw, 2);
            row.Children.Add(sw);
            _list.Items.Add(new ListBoxItem { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (_state.Count > 0) _list.SelectedIndex = Math.Clamp(select, 0, _state.Count - 1);
    }

    private void Move(int dir)
    {
        var i = _list.SelectedIndex;
        var j = i + dir;
        if (i < 0 || j < 0 || j >= _state.Count) return;
        (_state[i], _state[j]) = (_state[j], _state[i]);
        Fill(j);
    }

    private void Save()
    {
        var order = _state.Select(s => s.Tool.ToString()).ToList();
        var layout = _ctx.Layout.Load();
        layout.HiddenTools = _state.Where(s => !s.Visible).Select(s => s.Tool.ToString()).ToList();
        _ctx.Layout.Save(layout);
        _ctx.Settings.Update(s => s.Editor.ToolbarTools = order);
    }
}
