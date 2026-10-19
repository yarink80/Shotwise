using System.Windows.Input;
using WpfKeys = System.Windows.Input.ModifierKeys;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Routed commands of the editor menus with their keyboard shortcuts (SPEC EDT-*).</summary>
internal static class EditorCommands
{
    private static RoutedUICommand Make(string name, string text, Key key = Key.None, WpfKeys mods = WpfKeys.None, string? gestureText = null)
    {
        var gestures = new InputGestureCollection();
        if (key != Key.None) gestures.Add(new KeyGesture(key, mods, gestureText ?? ""));
        return new RoutedUICommand(text, name, typeof(EditorCommands), gestures);
    }

    // File
    public static readonly RoutedUICommand NewCapture = Make(nameof(NewCapture), "New Capture");
    public static readonly RoutedUICommand NewImage = Make(nameof(NewImage), "New Image…", Key.N, WpfKeys.Control);
    public static readonly RoutedUICommand NewFromClipboard = Make(nameof(NewFromClipboard), "New from Clipboard", Key.N, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand Open = Make(nameof(Open), "Open…", Key.O, WpfKeys.Control);
    public static readonly RoutedUICommand CreateFromTemplate = Make(nameof(CreateFromTemplate), "Create Image from Template…", Key.T, WpfKeys.Control | WpfKeys.Alt);
    public static readonly RoutedUICommand ImportToLibrary = Make(nameof(ImportToLibrary), "Import to Library…");
    public static readonly RoutedUICommand ImportStamps = Make(nameof(ImportStamps), "Import Stamps…");
    public static readonly RoutedUICommand ImportTemplates = Make(nameof(ImportTemplates), "Import Templates…");
    public static readonly RoutedUICommand Save = Make(nameof(Save), "Save", Key.S, WpfKeys.Control);
    public static readonly RoutedUICommand SaveAs = Make(nameof(SaveAs), "Save As…", Key.S, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand BatchExport = Make(nameof(BatchExport), "Export / Batch Convert…");
    public static readonly RoutedUICommand Print = Make(nameof(Print), "Print…", Key.P, WpfKeys.Control);
    public static readonly RoutedUICommand PrintPreview = Make(nameof(PrintPreview), "Print Preview");
    public static readonly RoutedUICommand PageSetup = Make(nameof(PageSetup), "Page Setup…");
    public static readonly RoutedUICommand QuickPrint = Make(nameof(QuickPrint), "Quick Print");
    public static readonly RoutedUICommand Duplicate = Make(nameof(Duplicate), "Duplicate", Key.D, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand Rename = Make(nameof(Rename), "Rename", Key.F2);
    public static readonly RoutedUICommand Pin = Make(nameof(Pin), "Pin / Unpin", Key.P, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand CloseDocument = Make(nameof(CloseDocument), "Close", Key.F4, WpfKeys.Control);
    public static readonly RoutedUICommand MoveToRecycleBin = Make(nameof(MoveToRecycleBin), "Move to Recycle Bin", Key.Delete, WpfKeys.Control);
    public static readonly RoutedUICommand EditorPreferences = Make(nameof(EditorPreferences), "Editor Preferences…");
    public static readonly RoutedUICommand CapturePreferences = Make(nameof(CapturePreferences), "Capture Preferences…");
    public static readonly RoutedUICommand ConnectMobile = Make(nameof(ConnectMobile), "Connect Mobile Device…");
    public static readonly RoutedUICommand ExitEditor = Make(nameof(ExitEditor), "Close Editor", Key.F4, WpfKeys.Alt, "Alt+F4");
    public static readonly RoutedUICommand VideoFromImages = Make(nameof(VideoFromImages), "Video from Images…");
    public static readonly RoutedUICommand OpenLibrary = Make(nameof(OpenLibrary), "Library…");
    public static readonly RoutedUICommand OpenCaptureWindow = Make(nameof(OpenCaptureWindow), "Capture Window");
    public static readonly RoutedUICommand ExitApp = Make(nameof(ExitApp), "Exit Shotwise");
    public static readonly RoutedUICommand ManageModels = Make(nameof(ManageModels), "Download AI Models…");

    // Edit
    /// <summary>EDT-03 "Duplicate" for objects (Ctrl+J; Ctrl+D is Deselect, Ctrl+Shift+D duplicates the document).</summary>
    public static readonly RoutedUICommand DuplicateObjects = Make(nameof(DuplicateObjects), "Duplicate Objects", Key.J, WpfKeys.Control);
    public static readonly RoutedUICommand CopyAll = Make(nameof(CopyAll), "Copy All", Key.C, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand Deselect = Make(nameof(Deselect), "Deselect All", Key.D, WpfKeys.Control);
    public static readonly RoutedUICommand SelectAllObjects = Make(nameof(SelectAllObjects), "Select All Objects");
    public static readonly RoutedUICommand InvertSelection = Make(nameof(InvertSelection), "Invert Selection", Key.I, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand RevertToOriginal = Make(nameof(RevertToOriginal), "Revert to Original");
    public static readonly RoutedUICommand UndoStack = Make(nameof(UndoStack), "Undo Stack…");
    public static readonly RoutedUICommand Group = Make(nameof(Group), "Group", Key.G, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand Ungroup = Make(nameof(Ungroup), "Ungroup", Key.U, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand BringForward = Make(nameof(BringForward), "Bring Forward", Key.F, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand SendBackward = Make(nameof(SendBackward), "Send Backward", Key.B, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand BringToFront = Make(nameof(BringToFront), "Bring to Front");
    public static readonly RoutedUICommand SendToBack = Make(nameof(SendToBack), "Send to Back");
    public static readonly RoutedUICommand AlignLeft = Make(nameof(AlignLeft), "Left");
    public static readonly RoutedUICommand AlignCenter = Make(nameof(AlignCenter), "Center");
    public static readonly RoutedUICommand AlignRight = Make(nameof(AlignRight), "Right");
    public static readonly RoutedUICommand AlignTop = Make(nameof(AlignTop), "Top");
    public static readonly RoutedUICommand AlignMiddle = Make(nameof(AlignMiddle), "Middle");
    public static readonly RoutedUICommand AlignBottom = Make(nameof(AlignBottom), "Bottom");
    public static readonly RoutedUICommand DistributeH = Make(nameof(DistributeH), "Distribute Horizontally");
    public static readonly RoutedUICommand DistributeV = Make(nameof(DistributeV), "Distribute Vertically");
    public static readonly RoutedUICommand Lock = Make(nameof(Lock), "Lock");
    public static readonly RoutedUICommand Unlock = Make(nameof(Unlock), "Unlock");
    public static readonly RoutedUICommand FlipObjectsH = Make(nameof(FlipObjectsH), "Flip Objects Horizontally");
    public static readonly RoutedUICommand FlipObjectsV = Make(nameof(FlipObjectsV), "Flip Objects Vertically");
    public static readonly RoutedUICommand Flatten = Make(nameof(Flatten), "Flatten", Key.T, WpfKeys.Control);
    public static readonly RoutedUICommand FlattenAll = Make(nameof(FlattenAll), "Flatten All", Key.T, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand GrabText = Make(nameof(GrabText), "Grab Text…");
    public static readonly RoutedUICommand EditText = Make(nameof(EditText), "Edit Text");
    public static readonly RoutedUICommand AddToQuickStyles = Make(nameof(AddToQuickStyles), "Add to Quick Styles");

    // Image
    public static readonly RoutedUICommand Trim = Make(nameof(Trim), "Trim");
    public static readonly RoutedUICommand SmartRedact = Make(nameof(SmartRedact), "Smart Redact");
    public static readonly RoutedUICommand AutoSimplify = Make(nameof(AutoSimplify), "Auto Simplify");
    public static readonly RoutedUICommand CanvasColor = Make(nameof(CanvasColor), "Canvas Color…");
    public static readonly RoutedUICommand CombineImages = Make(nameof(CombineImages), "Combine Images…", Key.C, WpfKeys.Control | WpfKeys.Alt);
    public static readonly RoutedUICommand CombineInTemplate = Make(nameof(CombineInTemplate), "Combine in Template…");
    public static readonly RoutedUICommand ApplyTemplate = Make(nameof(ApplyTemplate), "Apply Template…");
    public static readonly RoutedUICommand RotateCw = Make(nameof(RotateCw), "Clockwise");
    public static readonly RoutedUICommand RotateCcw = Make(nameof(RotateCcw), "Counter Clockwise");
    public static readonly RoutedUICommand Rotate180 = Make(nameof(Rotate180), "180 Degrees");
    public static readonly RoutedUICommand CustomAngle = Make(nameof(CustomAngle), "Custom Angle…");
    public static readonly RoutedUICommand FlipImageH = Make(nameof(FlipImageH), "Flip Horizontal");
    public static readonly RoutedUICommand FlipImageV = Make(nameof(FlipImageV), "Flip Vertical");
    public static readonly RoutedUICommand ResizeCanvas = Make(nameof(ResizeCanvas), "Resize Canvas…");
    public static readonly RoutedUICommand ResizeImage = Make(nameof(ResizeImage), "Resize Image…");
    public static readonly RoutedUICommand CanvasSnapping = Make(nameof(CanvasSnapping), "Canvas Snapping", Key.OemSemicolon, WpfKeys.Control, "Ctrl+;");
    public static readonly RoutedUICommand TagImage = Make(nameof(TagImage), "Tag Image…");

    // View
    public static readonly RoutedUICommand ZoomIn = Make(nameof(ZoomIn), "Zoom In", Key.OemPlus, WpfKeys.Control, "Ctrl++");
    public static readonly RoutedUICommand ZoomOut = Make(nameof(ZoomOut), "Zoom Out", Key.OemMinus, WpfKeys.Control, "Ctrl+-");
    public static readonly RoutedUICommand ActualSize = Make(nameof(ActualSize), "Actual Size", Key.D0, WpfKeys.Control);
    public static readonly RoutedUICommand ZoomToFit = Make(nameof(ZoomToFit), "Zoom to Fit");
    public static readonly RoutedUICommand ShrinkToFit = Make(nameof(ShrinkToFit), "Shrink to Fit");
    public static readonly RoutedUICommand PixelGrid = Make(nameof(PixelGrid), "Pixel Grid", Key.G, WpfKeys.Control);
    public static readonly RoutedUICommand ToggleProperties = Make(nameof(ToggleProperties), "Tool Properties");
    public static readonly RoutedUICommand ToggleEffects = Make(nameof(ToggleEffects), "Effects");
    public static readonly RoutedUICommand ToggleTray = Make(nameof(ToggleTray), "Recent Images Tray");
    public static readonly RoutedUICommand SwitchToLibrary = Make(nameof(SwitchToLibrary), "Switch to Library");
    public static readonly RoutedUICommand SwitchToEditor = Make(nameof(SwitchToEditor), "Switch to Editor");
    public static readonly RoutedUICommand ToggleDetails = Make(nameof(ToggleDetails), "Details (Status Bar)");

    // Library
    public static readonly RoutedUICommand LibraryOpenSelected = Make(nameof(LibraryOpenSelected), "Open Selected");
    public static readonly RoutedUICommand LibraryReveal = Make(nameof(LibraryReveal), "Show in Explorer");
    public static readonly RoutedUICommand LibraryRefresh = Make(nameof(LibraryRefresh), "Refresh", Key.F5);
    public static readonly RoutedUICommand LibrarySearch = Make(nameof(LibrarySearch), "Search Library", Key.F, WpfKeys.Control);
    public static readonly RoutedUICommand LibraryBackup = Make(nameof(LibraryBackup), "Back Up Library…");
    public static readonly RoutedUICommand LibraryRestore = Make(nameof(LibraryRestore), "Restore Backup…");
    public static readonly RoutedUICommand LibraryLocation = Make(nameof(LibraryLocation), "Library Location…");

    // Share
    public static readonly RoutedUICommand ShareLink = Make(nameof(ShareLink), "Share Link");
    public static readonly RoutedUICommand ShareHistory = Make(nameof(ShareHistory), "Share History…");
    public static readonly RoutedUICommand ShareAccounts = Make(nameof(ShareAccounts), "Share Preferences…");

    // Video
    public static readonly RoutedUICommand VideoPlayPause = Make(nameof(VideoPlayPause), "Play / Pause");
    public static readonly RoutedUICommand VideoPrevFrame = Make(nameof(VideoPrevFrame), "Previous Frame");
    public static readonly RoutedUICommand VideoNextFrame = Make(nameof(VideoNextFrame), "Next Frame");
    public static readonly RoutedUICommand VideoCutOut = Make(nameof(VideoCutOut), "Cut Out");
    public static readonly RoutedUICommand VideoMute = Make(nameof(VideoMute), "Mute / Unmute");
    public static readonly RoutedUICommand VideoVolumeUp = Make(nameof(VideoVolumeUp), "Volume Up");
    public static readonly RoutedUICommand VideoVolumeDown = Make(nameof(VideoVolumeDown), "Volume Down");
    public static readonly RoutedUICommand VideoSaveFrame = Make(nameof(VideoSaveFrame), "Save Frame as Image");
    public static readonly RoutedUICommand VideoSaveSelection = Make(nameof(VideoSaveSelection), "Save Selection as New Video…");
    public static readonly RoutedUICommand VideoSaveGif = Make(nameof(VideoSaveGif), "Save Video as GIF…");
    public static readonly RoutedUICommand CombineVideos = Make(nameof(CombineVideos), "Combine Videos…", Key.V, WpfKeys.Control | WpfKeys.Shift);
    public static readonly RoutedUICommand VideoProperties = Make(nameof(VideoProperties), "Video Properties…");

    // Help
    public static readonly RoutedUICommand GettingStarted = Make(nameof(GettingStarted), "Getting Started", Key.F1);
    public static readonly RoutedUICommand Tutorials = Make(nameof(Tutorials), "Tutorials");
    public static readonly RoutedUICommand WhatsNew = Make(nameof(WhatsNew), "What's New");
    public static readonly RoutedUICommand CheckForUpdates = Make(nameof(CheckForUpdates), "Check for Updates");
    public static readonly RoutedUICommand LearningCenter = Make(nameof(LearningCenter), "Learn");
    public static readonly RoutedUICommand OpenLogFolder = Make(nameof(OpenLogFolder), "Technical Support: Open Log Folder");
    public static readonly RoutedUICommand About = Make(nameof(About), "About Shotwise");
    public static readonly RoutedUICommand KeyboardShortcuts = Make(nameof(KeyboardShortcuts), "Keyboard Shortcuts", Key.OemQuestion, WpfKeys.Control, "Ctrl+/");

    /// <summary>First command of each menu section (declaration order) → section key used by the shortcut sheet.</summary>
    private static readonly Dictionary<string, (string Key, string Fallback)> SectionStarts = new()
    {
        [nameof(NewCapture)] = ("Menu_File", "File"),
        [nameof(DuplicateObjects)] = ("Menu_Edit", "Edit"),
        [nameof(Trim)] = ("Menu_Image", "Image"),
        [nameof(ZoomIn)] = ("Menu_View", "View"),
        [nameof(LibraryOpenSelected)] = ("Menu_Library", "Library"),
        [nameof(ShareLink)] = ("Menu_Share", "Share"),
        [nameof(VideoPlayPause)] = ("Menu_Video", "Video"),
        [nameof(GettingStarted)] = ("Menu_Help", "Help"),
    };

    /// <summary>
    /// Every editor command that has a keyboard shortcut, grouped by menu (Help → Keyboard shortcuts). Built from the command
    /// definitions above, so the sheet always shows the gestures the window actually binds.
    /// </summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<(string Name, string Gesture)> Items)> ShortcutGroups()
    {
        var groups = new List<(string, IReadOnlyList<(string, string)>)>();
        string group = ES.T("Menu_File", "File");
        var items = new List<(string, string)>();
        foreach (var f in typeof(EditorCommands).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).OrderBy(f => f.MetadataToken))
        {
            if (f.GetValue(null) is not RoutedUICommand cmd) continue;
            if (SectionStarts.TryGetValue(cmd.Name, out var section))
            {
                if (items.Count > 0) groups.Add((group, items));
                group = ES.T(section.Key, section.Fallback);
                items = new List<(string, string)>();
            }
            foreach (var g in cmd.InputGestures.OfType<KeyGesture>())
                items.Add((ES.T("Cmd_" + cmd.Name, cmd.Text).TrimEnd('…'), Gesture(g)));
        }
        if (items.Count > 0) groups.Add((group, items));
        return groups;
    }

    /// <summary>"Ctrl+Shift+S" style text for a gesture (uses the explicit display string when one was given).</summary>
    public static string Gesture(KeyGesture g)
    {
        if (!string.IsNullOrEmpty(g.DisplayString)) return g.DisplayString;
        var parts = new List<string>();
        if ((g.Modifiers & WpfKeys.Control) != 0) parts.Add("Ctrl");
        if ((g.Modifiers & WpfKeys.Alt) != 0) parts.Add("Alt");
        if ((g.Modifiers & WpfKeys.Shift) != 0) parts.Add("Shift");
        if ((g.Modifiers & WpfKeys.Windows) != 0) parts.Add("Win");
        parts.Add(g.Key switch { Key.D0 => "0", Key.Delete => "Del", Key.OemPlus => "+", Key.OemMinus => "-", _ => g.Key.ToString() });
        return string.Join("+", parts);
    }
}
