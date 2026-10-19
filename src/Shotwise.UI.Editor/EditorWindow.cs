using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Settings;
using Shotwise.Core.Styles;
using Shotwise.Editor;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;
using Shotwise.UI.Editor.Library;
using Shotwise.UI.Editor.Panels;
using Shotwise.UI.Editor.Share;
using Shotwise.UI.Editor.Surface;
using Shotwise.UI.Editor.Toolbar;
using Shotwise.UI.Editor.Tray;
using Shotwise.UI.Editor.Video;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using ProgressBar = System.Windows.Controls.ProgressBar;
using SwSplitButton = Shotwise.UI.Common.Controls.SplitButton;

namespace Shotwise.UI.Editor;

/// <summary>
/// The editor main window (DESIGN-SYSTEM §4): custom title bar, a 56 px top bar (menu button + Editor/Library/
/// Capture/Create navigation, centred colour tools with a "More" flyout, Undo/Redo, Copy All and the accent Share split button),
/// the canvas / video / library / start views, a right panel with modes (Quick Styles + Tool Properties, Effects, Smart Redact,
/// Simplify) and a bottom bar (Recent / Tag, status, zoom, Effects | Properties) above the tray.
/// Acts as the controller of the open documents (see the partial files for commands, documents, tray and export).
/// </summary>
internal sealed partial class EditorWindow : FluentWindow
{
    private enum View { Document, Library, Learning }

    /// <summary>Right-panel modes (replace the former text tabs).</summary>
    private enum SideMode { Properties, Effects, Redact, Simplify }

    private readonly EditorContext _ctx;
    private readonly ILogger _log;
    private readonly List<EditorDocument> _docs = new();
    private readonly PreviewRenderer _previews;
    private EditorDocument? _active;
    private View _view = View.Document;

    private readonly TitleBar _titleBar = new() { ShowMaximize = true, ShowMinimize = true };
    private ContextMenu _mainMenu = null!;
    private Button _menuButton = null!;
    private readonly ToolbarView _toolbar;
    private readonly Grid _stage = new();
    private readonly CanvasView _canvas;
    private readonly VideoView _video;
    private readonly LibraryView _library;
    private LearningCenterView _learn = null!;
    private readonly PropertiesPanel _props;
    private readonly StylesPanel _styles;
    private readonly EffectsPanel _effects;
    private readonly SmartRedactPane _redact;
    private readonly SimplifyPane _simplify;
    private readonly Grid _propsMode = new(), _effectsMode = new(), _redactMode = new(), _simplifyMode = new();
    private SideMode _sideMode = SideMode.Properties;
    private readonly Border _sideHost = new();
    private readonly GridSplitter _splitter = new() { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false };
    private readonly TrayView _tray;
    private readonly ProgressOverlay _overlay = new();
    private readonly ToastHost _toasts = new();
    private readonly ShareController _share;
    private readonly VideoFromImagesController _vfi;
    private readonly QuickStylePopup _quickStyles;
    private readonly SwSplitButton _shareButton;
    private readonly Button _copyAllButton;
    private NavItem _navEditor = null!, _navLibrary = null!, _navCapture = null!, _navCreate = null!;
    private UndoStackWindow? _undoWindow;
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _thumbTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Grid _body = new();
    private readonly ColumnDefinition _sideColumn = new() { Width = new GridLength(312) };
    private readonly ColumnDefinition _splitColumn = new() { Width = GridLength.Auto };

    // bottom bar / status
    private readonly TextBlock _statusMessage = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Margin = new Thickness(12, 0, 0, 0) };
    private readonly StatusText _statusPosition = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly StatusText _statusSelection = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly StatusText _statusImage = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly StackPanel _details = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _zoomText = new() { Width = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly Slider _zoomSlider = new() { Minimum = Math.Log2(0.05), Maximum = Math.Log2(64), Width = 120, VerticalAlignment = VerticalAlignment.Center, SmallChange = 0.1, LargeChange = 0.5 };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Width = 70, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private ToggleButton _recentToggle = null!, _effectsToggle = null!, _propsToggle = null!;
    private FrameworkElement _zoomGroup = null!, _panelToggles = null!;
    private Button _tagButton = null!;
    private bool _settingZoomSlider;
    private bool _closingForReal;
    private EditorLayout _layout;

    public EditorWindow(EditorContext ctx)
    {
        _ctx = ctx;
        _log = ctx.Log<EditorWindow>();
        _layout = ctx.Layout.Load();
        ctx.ActiveWindow = this;
        _previews = new PreviewRenderer(ctx);

        Title = ES.T("Window_Title", "Shotwise Editor");
        MinWidth = 900;
        MinHeight = 560;
        WindowBackdropType = WindowBackdropType.None;
        ExtendsContentIntoTitleBar = true;
        AllowDrop = true;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        SetResourceReference(FontFamilyProperty, Tok.FontFamily);
        RestoreWindowState();

        _canvas = new CanvasView(ctx);
        _video = new VideoView(ctx) { Progress = _overlay, OpenFrameAsImage = OpenFrameAsImageAsync, OpenMedia = OpenFileAsync };
        _library = new LibraryView(ctx);
        _props = new PropertiesPanel(ctx, _previews, _layout);
        _styles = new StylesPanel(ctx, _previews);
        _effects = new EffectsPanel(ctx);
        _redact = new SmartRedactPane(ctx);
        _simplify = new SimplifyPane(ctx);
        _toolbar = new ToolbarView(ctx);
        _tray = new TrayView(ctx, () => _docs);
        _share = new ShareController(ctx, _overlay, _toasts);
        _vfi = new VideoFromImagesController(ctx);
        _quickStyles = new QuickStylePopup(ctx, _previews);

        _shareButton = new SwSplitButton
        {
            Content = ES.T("Share_Button", "Share"),
            Icon = UiKit.IconSource(Icons.Glyph.Share),
            DropDownToolTip = ES.T("Share_Destinations", "Share destinations"),
            DropDownMenu = new ContextMenu(),
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 108,
        };
        AutomationProperties.SetName(_shareButton, ES.T("Share_Button", "Share"));
        _shareButton.Click += (_, _) => ShareDefault();
        _shareButton.DropDownOpening += (_, _) => FillShareDropDown();
        _copyAllButton = UiKit.GhostButton(ES.T("Cmd_CopyAll", "Copy All"), Icons.Glyph.Copy, () => { }, ES.T("Tip_CopyAll", "Copy the whole image with its annotations (Ctrl+Shift+C)"));
        _copyAllButton.Command = EditorCommands.CopyAll;

        _propsMode.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _propsMode.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _propsMode.Children.Add(_styles);
        Grid.SetRow(_props, 1);
        _propsMode.Children.Add(_props);
        BuildMode(_effectsMode, "Icon.Effects.Regular", ES.T("Tab_Effects", "Effects"), ES.T("Effects_Help", "Borders, shadows, edges and colour changes for the whole image."), _effects);
        BuildMode(_redactMode, "Icon.Tool.Blur.Color", ES.T("Tab_Redact", "Smart Redact"), ES.T("Redact_HelpShort", "Find and cover sensitive information."), _redact);
        BuildMode(_simplifyMode, "Icon.Tool.Simplify.Color", ES.T("Tab_Simplify", "Simplify"), ES.T("Simplify_HelpShort", "Replace detail with simple shapes."), _simplify);

        BuildLayout();
        WireEvents();
        BindCommands();
        UpdateAll();
        _ = _tray.RefreshAsync();
    }

    public IReadOnlyList<EditorDocument> Documents => _docs;
    public EditorDocument? ActiveDocument => _active;

    private ImageEditorDocument? ImageDoc => _active as ImageEditorDocument;
    private VideoEditorDocument? VideoDoc => _active as VideoEditorDocument;
    private EditorSession? Session => ImageDoc?.Session;

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var root = UiKit.XamlRoot<Grid>();
        root.SetResourceReference(Panel.BackgroundProperty, Tok.Surface0);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // title bar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // top bar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // update InfoBar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // body
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // bottom bar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // tray

        // ---- title bar: brand mark · document name · help / settings
        _titleBar.Title = Title;
        // App icon (same artwork as the exe / taskbar); the pixel-hinted frame for the current DPI keeps it crisp at 16 DIP.
        ImageSource? titleIcon = null;
        void SetTitleIcon(double scale)
        {
            var brand = Shotwise.UI.Common.Theming.AppIcon.ForSize(16, scale) ?? TryFindResource("Icon.Brand.Color") as ImageSource;
            if (brand is null || ReferenceEquals(brand, titleIcon)) return;   // replacing the icon element after load is not re-rendered
            titleIcon = brand;
            _titleBar.Icon = new ImageIcon { Source = brand, Width = 16, Height = 16 };
        }
        SetTitleIcon(VisualTreeHelper.GetDpi(this).DpiScaleX);
        DpiChanged += (_, e) => SetTitleIcon(e.NewDpi.DpiScaleX);
        Loaded += (_, _) => SetTitleIcon(VisualTreeHelper.GetDpi(this).DpiScaleX);
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (glyph, tip, cmd) in new[] { (Icons.Glyph.Lightbulb, ES.T("Cmd_LearningCenter", "Learning Center"), (ICommand)EditorCommands.LearningCenter), (Icons.Glyph.Settings, ES.T("Cmd_EditorPreferences", "Editor Preferences…"), EditorCommands.EditorPreferences) })
        {
            var b = UiKit.IconButton(glyph, tip, () => { }, 15);
            b.Command = cmd;
            b.Width = 30; b.Height = 30;
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            trailing.Children.Add(b);
        }
        _titleBar.TrailingContent = trailing;
        var titleHost = new Border { Child = _titleBar }.Themed(Border.BackgroundProperty, Tok.Surface1);
        root.Children.Add(titleHost);

        // ---- top bar
        var top = BuildTopBar();
        Grid.SetRow(top, 1);
        root.Children.Add(top);

        // ---- "Shotwise X.Y.Z is ready - Restart now / Later" (only while a downloaded update waits)
        if (_ctx.Updates is { } updates)
        {
            var updateBar = new UpdateBar(updates, (text, kind) => _toasts.Show(text, kind));
            Grid.SetRow(updateBar, 2);
            root.Children.Add(updateBar);
        }

        // ---- body: stage + splitter + side panel
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(_splitColumn);
        _body.ColumnDefinitions.Add(_sideColumn);
        _stage.SetResourceReference(Panel.BackgroundProperty, Tok.Canvas);
        _stage.Children.Add(_canvas);
        _stage.Children.Add(_video);
        _stage.Children.Add(_library);
        _learn = new LearningCenterView(() => RunUi(NewCaptureAsync), () => RunUi(NewImageAsync), () => RunUi(OpenDialogAsync), () => SetView(View.Library),
            show => _ctx.Settings.Update(s => s.General.ShowOnboarding = show), _ctx.Settings.Current.General.ShowOnboarding);
        _stage.Children.Add(_learn);
        _stage.Children.Add(_vfi);
        _stage.Children.Add(_overlay);
        _stage.Children.Add(_toasts);
        Grid.SetColumn(_stage, 0);
        _body.Children.Add(_stage);
        Grid.SetColumn(_splitter, 1);
        _body.Children.Add(_splitter);
        _sideHost.Styled("Shotwise.SidePanel");
        _sideHost.Width = double.NaN;
        var modes = new Grid();
        foreach (var m in new[] { _propsMode, _effectsMode, _redactMode, _simplifyMode }) modes.Children.Add(m);
        _sideHost.Child = modes;
        Grid.SetColumn(_sideHost, 2);
        _body.Children.Add(_sideHost);
        Grid.SetRow(_body, 3);
        root.Children.Add(_body);

        // ---- bottom bar + tray
        var bottom = BuildBottomBar();
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);
        Grid.SetRow(_tray, 5);
        root.Children.Add(_tray);
        Content = root;

        _sideMode = _layout.SideTab switch { "Effects" => SideMode.Effects, _ => SideMode.Properties };
        ApplySideMode();
        _sideColumn.Width = new GridLength(Math.Clamp(_layout.SideWidth, 280, 640));
        _sideColumn.MinWidth = 280;
        if (!_layout.ShowSidePanel) SetSideVisible(false);
        if (!_layout.ShowTray) _tray.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement BuildTopBar()
    {
        var bar = new Border().Styled("Shotwise.TopBar");
        var g = new Grid { MinHeight = 60 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // left: menu button + segmented navigation
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _mainMenu = BuildMenu();
        _menuButton = UiKit.IconButton(Icons.Glyph.Menu, ES.T("Menu_Main", "Menu (F10)"), OpenMainMenu, 19);
        _menuButton.Width = 36; _menuButton.Height = 36;
        _menuButton.Margin = new Thickness(0, 0, 8, 0);
        left.Children.Add(_menuButton);
        var navPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _navEditor = Nav("Icon.Editor.Regular", ES.T("Nav_Editor", "Editor"), () => SetView(View.Document));
        _navLibrary = Nav("Icon.Library.Regular", ES.T("Nav_Library", "Library"), () => SetView(View.Library));
        _navCapture = Nav("Icon.Capture.Regular", ES.T("Nav_Capture", "Capture"), () => { ExecuteCommand(_ctx.Capture is not null ? EditorCommands.OpenCaptureWindow : EditorCommands.NewCapture); SyncNav(); });
        _navCapture.ShowDot = true;
        _navCreate = Nav("Icon.Create.Regular", ES.T("Nav_Create", "Create"), () => { OpenCreateMenu(); SyncNav(); });
        foreach (var n in new[] { _navEditor, _navLibrary, _navCapture, _navCreate }) navPanel.Children.Add(n);
        var nav = new Border { Child = navPanel, VerticalAlignment = VerticalAlignment.Center }.Styled("Shotwise.SegmentedNav");
        left.Children.Add(nav);
        g.Children.Add(left);

        // centre: tools + More, then Undo / Redo
        _toolbar.Margin = new Thickness(12, 0, 12, 0);
        _toolbar.AddTrailing(UiKit.VDivider());
        _toolbar.AddTrailing(CommandToolButton(ApplicationCommands.Undo, Icons.Glyph.Undo, ES.T("Toolbar_Undo", "Undo"), "Ctrl+Z"));
        _toolbar.AddTrailing(CommandToolButton(ApplicationCommands.Redo, Icons.Glyph.Redo, ES.T("Toolbar_Redo", "Redo"), "Ctrl+Y"));
        Grid.SetColumn(_toolbar, 1);
        g.Children.Add(_toolbar);

        // right: Copy All + Share
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_copyAllButton);
        right.Children.Add(_shareButton);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        bar.Child = g;
        return bar;
    }

    private NavItem Nav(string icon, string text, Action click)
    {
        var n = new NavItem { Icon = icon, Content = text, GroupName = "EditorNav" };
        n.Click += (_, _) => click();
        return n;
    }

    private Button CommandToolButton(ICommand command, string glyph, string text, string shortcut)
    {
        var b = new Button { Content = text, Command = command, ToolTip = $"{text} ({shortcut})" }.Styled("Shotwise.ToolCommandButton");
        SwUi.SetIcon(b, glyph);
        AutomationProperties.SetName(b, text);
        AutomationProperties.SetAcceleratorKey(b, shortcut);
        return b;
    }

    private void BuildMode(Grid host, string icon, string title, string help, FrameworkElement content)
    {
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new SwIcon { Source = icon, Size = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
        var close = UiKit.IconButton(Icons.Glyph.Close, ES.T("Side_BackToProperties", "Back to Tool Properties"), () => ShowPane(SideMode.Properties), 14);
        close.Width = 28; close.Height = 28;
        var header = new PanelHeader { Content = head, HelpText = help, Actions = close, Padding = new Thickness(16, 12, 10, 8) };
        host.Children.Add(header);
        Grid.SetRow(content, 1);
        host.Children.Add(content);
    }

    private FrameworkElement BuildBottomBar()
    {
        var bar = new Border { Padding = new Thickness(8, 4, 8, 4) }.Styled("Shotwise.BottomBar");
        var g = new Grid { MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _recentToggle = UiKit.GhostToggle(ES.T("Cmd_RecentCaptures", "Recent Captures"), Icons.Glyph.History, ES.T("Tip_Recent", "Show or hide recent captures"));
        _recentToggle.Command = EditorCommands.ToggleTray;
        _recentToggle.MinHeight = 30;
        left.Children.Add(_recentToggle);
        _tagButton = UiKit.GhostButton(ES.T("Bottom_Tag", "Tag"), Icons.Glyph.Tag, () => { }, ES.T("Cmd_TagImage", "Tag Image…"));
        _tagButton.Command = EditorCommands.TagImage;
        _tagButton.MinHeight = 30;
        left.Children.Add(_tagButton);
        left.Children.Add(_busy);
        g.Children.Add(left);

        // status details (Details toggle hides them)
        foreach (var t in new[] { _statusMessage, _statusPosition, _statusSelection, _statusImage }) t.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        _details.Children.Add(StatusItem("Icon.Image.Regular", _statusImage));
        _details.Children.Add(StatusItem("Icon.Tool.Selection.Regular", _statusSelection));
        _details.Children.Add(StatusItem("Icon.Tool.Cursor.Regular", _statusPosition));
        _details.Children.Add(_statusMessage);
        var detailsScroll = new Grid { ClipToBounds = true, Children = { _details } };
        Grid.SetColumn(detailsScroll, 1);
        g.Children.Add(detailsScroll);

        // zoom
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var zoomOut = UiKit.IconButton(Icons.Glyph.ZoomOut, ES.T("Cmd_ZoomOut", "Zoom out (Ctrl+-)"), () => _canvas.ZoomOut(), 15);
        var zoomIn = UiKit.IconButton(Icons.Glyph.ZoomIn, ES.T("Cmd_ZoomIn", "Zoom in (Ctrl++)"), () => _canvas.ZoomIn(), 15);
        foreach (var b in new[] { zoomOut, zoomIn }) { b.Width = 28; b.Height = 28; }
        _zoomSlider.SetResourceReference(StyleProperty, "Shotwise.Slider");
        AutomationProperties.SetName(_zoomSlider, ES.T("Status_Zoom", "Zoom"));
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text);
        var zoomChip = new Border { Child = _zoomText, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(6) }.Themed(Border.BackgroundProperty, Tok.Surface2);
        var fit = UiKit.IconButton(Icons.Glyph.Fit, ES.T("Cmd_ZoomToFit", "Zoom to fit"), () => _canvas.ZoomToFit(), 15);
        fit.Width = 28; fit.Height = 28;
        var actual = UiKit.GhostButton("1:1", null, () => _canvas.ActualSize(), ES.T("Cmd_ActualSize", "Actual size (Ctrl+0)"));
        actual.MinHeight = 28; actual.Padding = new Thickness(8, 0, 8, 0); actual.FontSize = 12;
        foreach (UIElement e in new UIElement[] { zoomOut, _zoomSlider, zoomIn, zoomChip, fit, actual }) zoom.Children.Add(e);
        var grip = UiKit.IconButton(Icons.Glyph.Page, ES.T("Status_Drag", "Drag this out to copy the image as a file"), () => { }, 15);
        grip.Width = 28; grip.Height = 28;
        grip.PreviewMouseMove += OnDragGrip;
        zoom.Children.Add(grip);
        _zoomGroup = zoom;
        Grid.SetColumn(zoom, 2);
        g.Children.Add(zoom);

        // panel toggles
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        toggles.Children.Add(UiKit.VDivider());
        _effectsToggle = UiKit.GhostToggle(ES.T("Tab_Effects", "Effects"), Icons.Glyph.Effects, ES.T("Tip_Effects", "Show the Effects panel"));
        _effectsToggle.Command = EditorCommands.ToggleEffects;
        _propsToggle = UiKit.GhostToggle(ES.T("Tab_Properties", "Properties"), Icons.Glyph.Properties, ES.T("Tip_Properties", "Show Quick Styles and Tool Properties"));
        _propsToggle.Command = EditorCommands.ToggleProperties;
        foreach (var t in new[] { _effectsToggle, _propsToggle }) { t.MinHeight = 30; t.Margin = new Thickness(2, 0, 0, 0); toggles.Children.Add(t); }
        _panelToggles = toggles;
        Grid.SetColumn(toggles, 3);
        g.Children.Add(toggles);
        bar.Child = g;

        _zoomSlider.ValueChanged += (_, _) =>
        {
            if (!_settingZoomSlider && _view == View.Document && ImageDoc is not null) _canvas.SetZoom(Math.Pow(2, _zoomSlider.Value), null, true);
        };
        return bar;
    }

    private static FrameworkElement StatusItem(string icon, TextBlock text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var i = new SwIcon { Source = icon, Size = 14, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        i.SetResourceReference(SwIcon.ForegroundProperty, Tok.Text3);
        sp.Children.Add(i);
        sp.Children.Add(text);
        sp.Visibility = string.IsNullOrEmpty(text.Text) ? Visibility.Collapsed : Visibility.Visible;
        return sp;
    }

    // ------------------------------------------------------------------ menus from the top bar

    private void OpenMainMenu()
    {
        _mainMenu.PlacementTarget = _menuButton;
        _mainMenu.Placement = PlacementMode.Bottom;
        _mainMenu.VerticalOffset = 4;
        _mainMenu.IsOpen = true;
    }

    private void OpenCreateMenu()
    {
        var m = new ContextMenu { PlacementTarget = _navCreate, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
        m.Items.Add(Mi(EditorCommands.NewImage, "Icon.Add.Regular"));
        m.Items.Add(Mi(EditorCommands.NewFromClipboard, Icons.Glyph.Paste));
        m.Items.Add(Mi(EditorCommands.CreateFromTemplate, "Icon.Grid.Regular"));
        m.Items.Add(new Separator());
        m.Items.Add(Mi(EditorCommands.CombineImages, "Icon.Collections.Regular"));
        m.Items.Add(Mi(EditorCommands.CombineInTemplate));
        m.Items.Add(Mi(EditorCommands.CombineVideos, "Icon.VideoClip.Regular"));
        m.Items.Add(Mi(EditorCommands.VideoFromImages, "Icon.Video.Regular"));
        m.Items.Add(Mi(EditorCommands.VideoSaveGif, "Icon.Gif.Regular"));
        m.IsOpen = true;
    }

    private void ExecuteCommand(RoutedUICommand cmd)
    {
        if (cmd.CanExecute(null, this)) cmd.Execute(null, this);
    }

    private void SyncNav()
    {
        _navEditor.IsChecked = _view != View.Library;
        _navLibrary.IsChecked = _view == View.Library;
        _navCapture.IsChecked = false;
        _navCreate.IsChecked = false;
    }

    // ------------------------------------------------------------------ share

    private IReadOnlyList<Shotwise.Core.Sharing.IShareDestination> ShareTargets() => _active is null ? Array.Empty<Shotwise.Core.Sharing.IShareDestination>() : _share.DestinationsFor(_active);

    /// <summary>Main segment of the Share split button: the last used destination, otherwise the destination list.</summary>
    private void ShareDefault()
    {
        if (_active is null) return;
        var last = _layout.LastShareDestination;
        var dest = last is null ? null : ShareTargets().FirstOrDefault(d => d.Id == last);
        if (dest is null) { _shareButton.IsDropDownOpen = true; return; }
        var doc = _active;
        RunUi(() => _share.ShareAsync(doc, dest));
    }

    private void FillShareDropDown()
    {
        var menu = _shareButton.DropDownMenu!;
        menu.Items.Clear();
        if (_active is null)
        {
            menu.Items.Add(new MenuItem { Header = ES.T("Share_NothingOpen", "Open an image or video to share it"), IsEnabled = false });
            return;
        }
        var built = _share.BuildMenu(_active, false, () => RunUi(() => _share.ShowHistoryAsync(_active)), () => RunUi(() => _ctx.Dialogs.ShowShareAccounts(new ShareAccountsRequest() { Owner = this })),
            id => { _layout.LastShareDestination = id; UpdateShareTip(); });
        foreach (var i in built.Items.Cast<object>().ToList()) { built.Items.Remove(i); menu.Items.Add(i); }
    }

    private void UpdateShareTip()
    {
        var last = _layout.LastShareDestination;
        var name = last is null ? null : _ctx.Share?.Destinations.FirstOrDefault(d => d.Id == last)?.DisplayName;
        _shareButton.ToolTip = name is null ? ES.T("Share_ButtonTip", "Share this item") : ES.F("Share_ButtonTipTo", "Share to {0}", name);
    }

    // ------------------------------------------------------------------ events

    private void WireEvents()
    {
        _toolbar.ToolSelected += k => { if (Session is { } s) { _canvas.CommitTextEditing(); s.ActiveTool = k; _quickStyles.ShowFor(s, k, _toolbar); } };
        _props.PaneRequested += OnPaneRequested;
        _canvas.ZoomChanged += z =>
        {
            _zoomText.Text = $"{Math.Round(z * 100)}%";
            _settingZoomSlider = true;
            _zoomSlider.Value = Math.Clamp(Math.Log2(Math.Max(0.05, z)), _zoomSlider.Minimum, _zoomSlider.Maximum);
            _settingZoomSlider = false;
        };
        _canvas.PointerMoved += p => _statusPosition.Text = p is { } pt ? $"{Math.Round(pt.X)}, {Math.Round(pt.Y)}" : "";
        _canvas.ContextMenuRequested += ShowCanvasContextMenu;
        _canvas.NonImageFilesDropped += (files, _) => RunUi(() => OpenFilesAsync(files));
        _canvas.EscapeUnhandled += () => { if (_ctx.Prefs.EscapeClosesEditor) Close(); };
        _canvas.SessionMessage += ShowEditorMessage;
        _tray.ItemActivated += item => RunUi(() => ActivateTrayItemAsync(item));
        _tray.CommandRequested += (items, cmd) => RunUi(() => HandleTrayCommandAsync(items, cmd));
        _tray.FilesDropped += files => RunUi(() => OpenFilesAsync(files));
        _library.OpenRequested += item => RunUi(() => OpenLibraryItemAsync(item));
        _library.CommandRequested += (items, cmd) => RunUi(() => HandleLibraryCommandAsync(items, cmd));
        _library.NewCaptureRequested += () => ExecuteCommand(EditorCommands.NewCapture);
        _video.EditsChanged += () => { CommandManager.InvalidateRequerySuggested(); _active?.RaiseChanged(); };
        _autosave.Tick += (_, _) => RunUi(AutosaveTickAsync);
        _autosave.Start();
        _thumbTimer.Tick += (_, _) => _tray.RefreshOpenThumbnails();
        _thumbTimer.Start();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Drop += OnWindowDrop;
        DragOver += (_, e) => { if (!e.Handled) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; } };
        Closing += OnWindowClosing;
        LocationChanged += (_, _) => UpdateRestoreBounds();
        _ctx.Settings.Changed += OnSettingsChanged;
        _knownLibraryRoot = _ctx.Library?.LibraryRoot;
        if (_ctx.Library is { } lib) lib.Changed += OnLibraryChanged;
        _ctx.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        SourceInitialized += (_, _) => { if (_layout.Maximized) WindowState = WindowState.Maximized; };
        Shotwise.UI.Common.Theming.ShotwiseTheme.Changed += OnThemeChanged;
        UpdateShareTip();
    }

    /// <summary>
    /// Live Light/Dark switch: ShotwiseTheme swaps a token dictionary nested inside SharedResources, which does not notify existing
    /// DynamicResource references. Touching the application's top-level merged dictionaries makes WPF re-resolve every reference.
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (Application.Current?.Resources is { } res)
        {
            // WPF only re-resolves references to keys contained in the added/removed dictionary: use the current token set.
            var nudge = Shotwise.UI.Common.Theming.ShotwiseTheme.CreateTokens(Shotwise.UI.Common.Theming.ShotwiseTheme.Current);
            res.MergedDictionaries.Add(nudge);
            res.MergedDictionaries.Remove(nudge);
        }
        _canvas.RefreshWorkspace();
        _previews.Clear();
        _styles.Refresh();
        _tray.QueueRefresh();
    });

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!e.Affects(nameof(AppSettings.Editor)) && !e.Affects(nameof(AppSettings.Library))) return;
        Dispatcher.BeginInvoke(() =>
        {
            _canvas.RefreshWorkspace();
            _tray.QueueRefresh();
            CommandManager.InvalidateRequerySuggested();
        });
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F10 opens the main menu (the classic menu bar now lives behind the menu button).
        if (e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            OpenMainMenu();
            e.Handled = true;
            return;
        }
        if (_view != View.Document) return;
        if (Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9)
        {
            var index = e.Key - Key.D1;
            var tools = _toolbar.VisibleTools;
            if (index < tools.Count && Session is { } s)
            {
                _canvas.CommitTextEditing();
                s.ActiveTool = tools[index];
                _quickStyles.ShowFor(s, tools[index], _canvas);
                e.Handled = true;
            }
        }
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            RunUi(() => OpenFilesAsync(files));
            e.Handled = true;
        }
    }

    private void OnPaneRequested(string pane)
    {
        switch (pane)
        {
            case "redact": ShowPane(SideMode.Redact); break;
            case "simplify": ShowPane(SideMode.Simplify); break;
            case "styles": ShowPane(SideMode.Properties); break;
            case "effects": ShowPane(SideMode.Effects); break;
        }
    }

    private void ShowPane(SideMode mode)
    {
        SetSideVisible(true);
        _sideMode = mode;
        ApplySideMode();
    }

    private void ApplySideMode()
    {
        _propsMode.Visibility = _sideMode == SideMode.Properties ? Visibility.Visible : Visibility.Collapsed;
        _effectsMode.Visibility = _sideMode == SideMode.Effects ? Visibility.Visible : Visibility.Collapsed;
        _redactMode.Visibility = _sideMode == SideMode.Redact ? Visibility.Visible : Visibility.Collapsed;
        _simplifyMode.Visibility = _sideMode == SideMode.Simplify ? Visibility.Visible : Visibility.Collapsed;
        _layout.SideTab = _sideMode == SideMode.Effects ? "Effects" : "Properties";
        SyncPanelToggles();
    }

    private void SyncPanelToggles()
    {
        if (_effectsToggle is null) return;
        var visible = _layout.ShowSidePanel;
        _effectsToggle.IsChecked = visible && _sideMode == SideMode.Effects;
        _propsToggle.IsChecked = visible && _sideMode == SideMode.Properties;
        _recentToggle.IsChecked = _layout.ShowTray;
    }

    private void SetSideVisible(bool visible)
    {
        _layout.ShowSidePanel = visible;
        ApplySideColumn();
        SyncPanelToggles();
    }

    /// <summary>The side panel shows for image documents only (library / video / start page use the full width).</summary>
    private void ApplySideColumn()
    {
        var show = _layout.ShowSidePanel && _view == View.Document && ImageDoc is not null;
        _sideHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _sideColumn.Width = show ? new GridLength(Math.Clamp(_layout.SideWidth, 280, 640)) : new GridLength(0);
        _sideColumn.MinWidth = show ? 280 : 0;
        _splitColumn.Width = show ? GridLength.Auto : new GridLength(0);
    }

    private void RunUi(Func<Task> action) => UiKit.Fire(action, ex =>
    {
        _log.LogWarning(ex, "Editor operation failed");
        _ = _ctx.ShowErrorAsync(ex);
    });

    private void RunUi(Action action) => RunUi(() => { action(); return Task.CompletedTask; });

    // ------------------------------------------------------------------ view switching / state

    private void SetView(View view)
    {
        if (view == _view && view != View.Library) { UpdateAll(); return; }
        if (_view == View.Document && _sideHost.Visibility == Visibility.Visible && _sideColumn.ActualWidth > 100) _layout.SideWidth = _sideColumn.ActualWidth;
        _view = view;
        if (view == View.Library) RunUi(_library.ActivateAsync);
        UpdateAll();
    }

    /// <summary>Shows the correct stage view, side panels and toolbar for the current document / view.</summary>
    private void UpdateAll()
    {
        var doc = ImageDoc;
        var video = VideoDoc;
        var showCanvas = _view == View.Document && doc is not null;
        var showVideo = _view == View.Document && video is not null;
        var showLibrary = _view == View.Library;
        var showLearn = _view == View.Learning || _view == View.Document && _active is null;
        _canvas.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
        _video.Visibility = showVideo ? Visibility.Visible : Visibility.Collapsed;
        _library.Visibility = showLibrary ? Visibility.Visible : Visibility.Collapsed;
        _learn.Visibility = showLearn ? Visibility.Visible : Visibility.Collapsed;
        _toolbar.SetToolsVisible(showCanvas);
        _toolbar.Visibility = showLibrary || showLearn ? Visibility.Hidden : Visibility.Visible;
        _copyAllButton.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
        _shareButton.Visibility = showCanvas || showVideo ? Visibility.Visible : Visibility.Collapsed;
        _zoomGroup.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
        _panelToggles.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
        _tagButton.Visibility = showCanvas || showVideo ? Visibility.Visible : Visibility.Collapsed;
        ApplySideColumn();
        _tray.Visibility = _layout.ShowTray ? Visibility.Visible : Visibility.Collapsed;
        _canvas.SetDocument(showCanvas ? doc : null);
        _props.Bind(doc);
        _styles.Bind(doc);
        _effects.Bind(doc);
        _redact.Bind(doc);
        _simplify.Bind(doc);
        if (doc is not null) _toolbar.SetActive(doc.Session.ActiveTool);
        _undoWindow?.Bind(doc?.Session.Undo ?? VideoDoc?.Undo, _active?.Title ?? "");
        _tray.SetActive(_active);
        _shareButton.IsEnabled = _active is not null && _share.IsAvailable;
        SyncNav();
        SyncPanelToggles();
        UpdateTitle();
        UpdateStatus();
        RebuildDynamicMenus();
        CommandManager.InvalidateRequerySuggested();
        if (showCanvas) _canvas.Focus();
    }

    private void UpdateTitle()
    {
        var name = _view switch
        {
            View.Library => ES.T("Window_Library", "Library"),
            View.Learning => ES.T("Window_Learn", "Getting started"),
            _ => _active is null ? ES.T("Window_NoDoc", "No image open") : _active.Title + (_active.IsDirty ? " *" : ""),
        };
        var title = $"{name} – {ES.T("Window_Title", "Shotwise Editor")}";
        Title = title;
        _titleBar.Title = title;
    }

    private void UpdateStatus()
    {
        var doc = ImageDoc;
        if (_view == View.Document && doc is not null)
        {
            var page = doc.Session.ActivePage;
            _statusImage.Text = $"{page.CanvasSize.Width:0} × {page.CanvasSize.Height:0} px";
            var sel = doc.Session.SelectedAnnotations();
            if (doc.Session.Selection.PixelBounds is { } pb) _statusSelection.Text = $"{pb.Width:0} × {pb.Height:0}";
            else if (sel.Count > 0)
            {
                var union = sel.Select(a => _ctx.Renderer.MeasureBounds(a)).Aggregate((a, b) => a.Union(b));
                _statusSelection.Text = sel.Count == 1 ? $"{union.Width:0} × {union.Height:0}" : $"{sel.Count} · {union.Width:0} × {union.Height:0}";
            }
            else _statusSelection.Text = "";
        }
        else if (VideoDoc is { } v && _view == View.Document)
        {
            _statusImage.Text = $"{v.Info.Width} × {v.Info.Height} · {UiKit.FormatDuration(v.Info.Duration)}";
            _statusSelection.Text = "";
            _statusPosition.Text = "";
        }
        else
        {
            _statusImage.Text = "";
            _statusSelection.Text = "";
            _statusPosition.Text = "";
        }
    }

    // ------------------------------------------------------------------ window state

    private void RestoreWindowState()
    {
        var l = _layout;
        Width = Math.Clamp(l.Width, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(l.Height, MinHeight, SystemParameters.VirtualScreenHeight);
        if (!double.IsNaN(l.Left) && !double.IsNaN(l.Top) && IsOnScreen(l.Left, l.Top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l.Left;
            Top = l.Top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var vx = SystemParameters.VirtualScreenLeft;
        var vy = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        return left + width > vx + 80 && left < vx + vw - 80 && top + 40 > vy && top < vy + vh - 80;
    }

    private void UpdateRestoreBounds() { }

    private void SaveWindowState()
    {
        try
        {
            _layout.Maximized = WindowState == WindowState.Maximized;
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (!b.IsEmpty)
            {
                _layout.Left = b.Left;
                _layout.Top = b.Top;
                _layout.Width = b.Width;
                _layout.Height = b.Height;
            }
            if (_sideHost.Visibility == Visibility.Visible && _sideColumn.ActualWidth > 100) _layout.SideWidth = _sideColumn.ActualWidth;
            if (_tray.Visibility == Visibility.Visible && _tray.ActualHeight > 40) _layout.TrayHeight = _tray.ActualHeight;
            var keepHidden = _ctx.Layout.Load().HiddenTools;
            _layout.HiddenTools = keepHidden;
            _ctx.Layout.Save(_layout);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Saving window state failed"); }
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _layout.Maximized ? WindowState.Maximized : WindowState.Normal;
        // Minimized from outside WPF (shell, another app): WindowState can still read Normal, so ask Win32 directly.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    /// <summary>Really closes (called by the host on shutdown).</summary>
    public async Task<bool> CloseForRealAsync()
    {
        _closingForReal = true;
        foreach (var d in _docs.ToList())
            if (!await CloseDocumentAsync(d, true)) { _closingForReal = false; return false; }
        Close();
        return true;
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingForReal) { Cleanup(); return; }
        SaveWindowState();
        if (_ctx.Prefs.KeepEditorRunningInBackground)
        {
            e.Cancel = true;
            _canvas.CommitTextEditing();
            await AutosaveAllAsync();
            Hide();
            return;
        }
        // not keeping the editor alive: close every document (prompting for unsaved ones)
        e.Cancel = true;
        if (await CloseAllAsync()) { _closingForReal = true; Close(); }
    }

    /// <summary>
    /// Real closure (not "hide to background"): releases every subscription on the shared singleton services (settings,
    /// library, styles, quick effects) and native canvas caches so the closed window and its visual tree can be collected
    /// before the host builds a fresh one (R5-#5).
    /// </summary>
    private void Cleanup()
    {
        _autosave.Stop();
        _thumbTimer.Stop();
        if (_vfi.IsRecording) _ = _vfi.CancelAsync();
        _video.Dispose();
        _undoWindow?.Close();
        SaveWindowState();
        _ctx.Settings.Changed -= OnSettingsChanged;
        Shotwise.UI.Common.Theming.ShotwiseTheme.Changed -= OnThemeChanged;
        if (_ctx.Library is { } lib) lib.Changed -= OnLibraryChanged;
        _canvas.ReleaseResources();
        _tray.ReleaseServices();
        _library.ReleaseServices();
        _styles.ReleaseServices();
        _effects.ReleaseServices();
        _toolbar.ReleaseServices();
        _props.Bind(null);
        _redact.Bind(null);
        _simplify.Bind(null);
        foreach (var d in _docs.ToList()) { d.Changed -= OnDocumentChanged; d.Dispose(); }
        _docs.Clear();
        _active = null;
        _previews.Clear();
        if (ReferenceEquals(_ctx.ActiveWindow, this)) _ctx.ActiveWindow = null;
    }

    public void SetStatus(string message) => _statusMessage.Text = message;

    public Task ReportAsync(Exception ex) => _ctx.ShowErrorAsync(ex);

    public void ShowLibraryView() => SetView(View.Library);

    private void BusyChanged(EditorSession s) => _busy.Visibility = s.IsBusy ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Status-bar value: hides its icon+text group while empty.</summary>
internal sealed class StatusText : TextBlock
{
    static StatusText() => TextProperty.OverrideMetadata(typeof(StatusText), new FrameworkPropertyMetadata(string.Empty, (d, _) =>
    {
        var t = (StatusText)d;
        if (t.Parent is StackPanel group) group.Visibility = string.IsNullOrEmpty(t.Text) ? Visibility.Collapsed : Visibility.Visible;
    }));
}
