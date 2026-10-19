using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Shotwise.UI.Common.Controls;

/// <summary>Shared helper: keeps an automatic "Label (Shortcut)" tooltip and automation name unless the app set its own.</summary>
internal static class LabelSync
{
    private static readonly DependencyProperty AutoToolTipProperty = DependencyProperty.RegisterAttached("AutoToolTip", typeof(bool), typeof(LabelSync));
    private static readonly DependencyProperty AutoNameProperty = DependencyProperty.RegisterAttached("AutoName", typeof(bool), typeof(LabelSync));

    public static void Update(FrameworkElement element, object? label, string? shortcut)
    {
        var text = label as string ?? (label as TextBlock)?.Text;
        if (string.IsNullOrEmpty(text)) return;
        var tip = string.IsNullOrEmpty(shortcut) ? text : $"{text} ({shortcut})";
        if (element.ToolTip is null || (bool)element.GetValue(AutoToolTipProperty))
        {
            element.ToolTip = tip;
            element.SetValue(AutoToolTipProperty, true);
        }
        if (element.ReadLocalValue(AutomationProperties.NameProperty) == DependencyProperty.UnsetValue || (bool)element.GetValue(AutoNameProperty))
        {
            AutomationProperties.SetName(element, text);
            element.SetValue(AutoNameProperty, true);
        }
        if (!string.IsNullOrEmpty(shortcut)) AutomationProperties.SetAcceleratorKey(element, shortcut);
    }
}

/// <summary>
/// Editor tool button (DESIGN-SYSTEM §3, item 3.1): borderless, 24 px colour icon above an 11 px caption, 56x52 hit area,
/// hover Surface2, checked = accent-tinted pill + accent caption, focus ring. A RadioButton, so tools in one panel are exclusive;
/// bind <see cref="ToggleButton.IsChecked"/> to the active tool. Tooltip/automation name = "Content (Shortcut)".
/// </summary>
public class ToolButton : RadioButton
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(object), typeof(ToolButton));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(ToolButton), new FrameworkPropertyMetadata(24.0));
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(nameof(Shortcut), typeof(string), typeof(ToolButton),
        new FrameworkPropertyMetadata(null, (d, _) => ((ToolButton)d).Sync()));

    static ToolButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ToolButton), new FrameworkPropertyMetadata(typeof(ToolButton)));

    /// <summary>Usually an <c>Icon.Tool.X.Color</c> DrawingImage.</summary>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public string? Shortcut { get => (string?)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }

    protected override void OnContentChanged(object oldContent, object newContent) { base.OnContentChanged(oldContent, newContent); Sync(); }
    private void Sync() => LabelSync.Update(this, Content, Shortcut);
}

/// <summary>
/// The hero capture button (DESIGN-SYSTEM §3, item 3.2): circular coral gradient (<see cref="Diameter"/>, default 104), soft glow on hover,
/// label inside and the shortcut caption below.
/// </summary>
public class CaptureButton : Button
{
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(nameof(Shortcut), typeof(string), typeof(CaptureButton),
        new FrameworkPropertyMetadata(null, (d, _) => ((CaptureButton)d).Sync()));
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(CaptureButton), new FrameworkPropertyMetadata(104.0));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(object), typeof(CaptureButton));

    static CaptureButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(CaptureButton), new FrameworkPropertyMetadata(typeof(CaptureButton)));

    /// <summary>Shortcut caption under the button ("Print Screen").</summary>
    public string? Shortcut { get => (string?)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }
    /// <summary>Optional icon above the label (e.g. Icon.Record.Filled in video mode).</summary>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    private static readonly DependencyPropertyKey ShowFocusRingKey = DependencyProperty.RegisterReadOnly(nameof(ShowFocusRing), typeof(bool), typeof(CaptureButton), new FrameworkPropertyMetadata(false));
    /// <summary>
    /// True only while the button has keyboard focus that came from keyboard navigation (Tab / arrows), so the accent ring is not
    /// shown permanently when the window focuses the button on activation or after a mouse click (POLISH-1).
    /// </summary>
    public static readonly DependencyProperty ShowFocusRingProperty = ShowFocusRingKey.DependencyProperty;
    public bool ShowFocusRing => (bool)GetValue(ShowFocusRingProperty);

    protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SetValue(ShowFocusRingKey, IsKeyboardNavigation());
    }

    protected override void OnLostKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetValue(ShowFocusRingKey, false);
    }

    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        SetValue(ShowFocusRingKey, false);
    }

    private static bool IsKeyboardNavigation()
    {
        if (System.Windows.Input.InputManager.Current.MostRecentInputDevice is not System.Windows.Input.KeyboardDevice) return false;
        foreach (var k in new[] { System.Windows.Input.Key.Tab, System.Windows.Input.Key.Left, System.Windows.Input.Key.Right, System.Windows.Input.Key.Up, System.Windows.Input.Key.Down })
            if (System.Windows.Input.Keyboard.IsKeyDown(k)) return true;
        return false;
    }

    protected override void OnContentChanged(object oldContent, object newContent) { base.OnContentChanged(oldContent, newContent); Sync(); }
    private void Sync() => LabelSync.Update(this, Content, Shortcut);
}

/// <summary>
/// Accent split button (DESIGN-SYSTEM §3, item 3.2, e.g. Share): the main segment raises <see cref="ButtonBase.Click"/>/Command, the chevron
/// segment opens <see cref="DropDownMenu"/> below the button.
/// </summary>
[TemplatePart(Name = "PART_DropDown", Type = typeof(ToggleButton))]
public class SplitButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(object), typeof(SplitButton));
    public static readonly DependencyProperty DropDownMenuProperty = DependencyProperty.Register(nameof(DropDownMenu), typeof(ContextMenu), typeof(SplitButton));
    public static readonly DependencyProperty IsDropDownOpenProperty = DependencyProperty.Register(nameof(IsDropDownOpen), typeof(bool), typeof(SplitButton),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SplitButton)d).OnIsDropDownOpenChanged((bool)e.NewValue)));
    public static readonly DependencyProperty DropDownToolTipProperty = DependencyProperty.Register(nameof(DropDownToolTip), typeof(string), typeof(SplitButton), new FrameworkPropertyMetadata("More options"));

    static SplitButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SplitButton), new FrameworkPropertyMetadata(typeof(SplitButton)));

    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public ContextMenu? DropDownMenu { get => (ContextMenu?)GetValue(DropDownMenuProperty); set => SetValue(DropDownMenuProperty, value); }
    public bool IsDropDownOpen { get => (bool)GetValue(IsDropDownOpenProperty); set => SetValue(IsDropDownOpenProperty, value); }
    /// <summary>Tooltip / automation name of the chevron segment.</summary>
    public string DropDownToolTip { get => (string)GetValue(DropDownToolTipProperty); set => SetValue(DropDownToolTipProperty, value); }

    /// <summary>Raised just before the menu opens (fill it lazily here).</summary>
    public event EventHandler? DropDownOpening;

    private void OnIsDropDownOpenChanged(bool open)
    {
        if (DropDownMenu is not { } menu) return;
        if (open)
        {
            DropDownOpening?.Invoke(this, EventArgs.Empty);
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
            menu.Closed -= OnMenuClosed;
            menu.Closed += OnMenuClosed;
            menu.IsOpen = true;
        }
        else menu.IsOpen = false;
    }

    private void OnMenuClosed(object sender, RoutedEventArgs e) => SetCurrentValue(IsDropDownOpenProperty, false);
}

/// <summary>
/// Navigation item for the SegmentedNav (top bar: Editor / Library / Capture / Create), the capture ModeRail and left nav lists.
/// A RadioButton: items sharing a parent are exclusive. Pick the look with a style: default = segmented (icon above label),
/// <c>Shotwise.ModeRailItem</c> (large colour icon + label, accent pill), <c>Shotwise.NavListItem</c> (row with count badge).
/// </summary>
public class NavItem : RadioButton
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(object), typeof(NavItem));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(NavItem), new FrameworkPropertyMetadata(20.0));
    public static readonly DependencyProperty ShowDotProperty = DependencyProperty.Register(nameof(ShowDot), typeof(bool), typeof(NavItem));
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(nameof(Badge), typeof(object), typeof(NavItem));
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(nameof(Shortcut), typeof(string), typeof(NavItem),
        new FrameworkPropertyMetadata(null, (d, _) => ((NavItem)d).Sync()));

    static NavItem() => DefaultStyleKeyProperty.OverrideMetadata(typeof(NavItem), new FrameworkPropertyMetadata(typeof(NavItem)));

    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    /// <summary>Coral status dot on the icon (Capture item).</summary>
    public bool ShowDot { get => (bool)GetValue(ShowDotProperty); set => SetValue(ShowDotProperty, value); }
    /// <summary>Count badge (NavListItem style), e.g. 128.</summary>
    public object? Badge { get => GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public string? Shortcut { get => (string?)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }

    protected override void OnContentChanged(object oldContent, object newContent) { base.OnContentChanged(oldContent, newContent); Sync(); }
    private void Sync() => LabelSync.Update(this, Content, Shortcut);
}

/// <summary>
/// Boolean option row (DESIGN-SYSTEM §3, item 3.3): label (+ optional description and icon) on the left, a switch on the right.
/// The whole row toggles; bind <see cref="ToggleButton.IsChecked"/>. <see cref="Trailing"/> shows e.g. a value chip before the switch.
/// </summary>
public class ToggleSwitchRow : ToggleButton
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(ToggleSwitchRow));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(object), typeof(ToggleSwitchRow));
    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(nameof(Trailing), typeof(object), typeof(ToggleSwitchRow));

    static ToggleSwitchRow() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ToggleSwitchRow), new FrameworkPropertyMetadata(typeof(ToggleSwitchRow)));

    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public object? Trailing { get => GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is string s && ReadLocalValue(AutomationProperties.NameProperty) == DependencyProperty.UnsetValue) AutomationProperties.SetName(this, s);
    }
}
