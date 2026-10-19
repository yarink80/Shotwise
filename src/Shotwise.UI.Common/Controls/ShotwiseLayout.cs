using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Shotwise.UI.Common.Controls;

/// <summary>
/// Surface1 card with radius 10, subtle border and a soft shadow drawn on a separate layer behind the content (so text stays crisp).
/// <see cref="Elevation"/>: 0 = flat, 1 = card (default), 2 = flyout/popup.
/// </summary>
public class Card : ContentControl
{
    public static readonly DependencyProperty ElevationProperty = DependencyProperty.Register(nameof(Elevation), typeof(int), typeof(Card), new FrameworkPropertyMetadata(1));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(Card), new FrameworkPropertyMetadata(new CornerRadius(10)));

    static Card()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(typeof(Card)));
        FocusableProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(false));
    }

    public int Elevation { get => (int)GetValue(ElevationProperty); set => SetValue(ElevationProperty, value); }
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
}

/// <summary>
/// Panel section header (DESIGN-SYSTEM §3, item 3.5): 13 px semibold title (Content), optional right-aligned <see cref="Actions"/>,
/// optional help button (shown when <see cref="HelpText"/> is set; raises <see cref="HelpRequested"/>) and optional collapse chevron.
/// </summary>
public class PanelHeader : ContentControl
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PanelHeader));
    public static readonly DependencyProperty HelpTextProperty = DependencyProperty.Register(nameof(HelpText), typeof(string), typeof(PanelHeader));
    public static readonly DependencyProperty CanCollapseProperty = DependencyProperty.Register(nameof(CanCollapse), typeof(bool), typeof(PanelHeader));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(PanelHeader),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly RoutedEvent HelpRequestedEvent = EventManager.RegisterRoutedEvent(nameof(HelpRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PanelHeader));
    public static readonly RoutedCommand HelpCommand = new(nameof(HelpCommand), typeof(PanelHeader));

    static PanelHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PanelHeader), new FrameworkPropertyMetadata(typeof(PanelHeader)));
        FocusableProperty.OverrideMetadata(typeof(PanelHeader), new FrameworkPropertyMetadata(false));
        CommandManager.RegisterClassCommandBinding(typeof(PanelHeader), new CommandBinding(HelpCommand, (s, _) => ((PanelHeader)s).RaiseEvent(new RoutedEventArgs(HelpRequestedEvent, s))));
    }

    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public string? HelpText { get => (string?)GetValue(HelpTextProperty); set => SetValue(HelpTextProperty, value); }
    public bool CanCollapse { get => (bool)GetValue(CanCollapseProperty); set => SetValue(CanCollapseProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    public event RoutedEventHandler HelpRequested { add => AddHandler(HelpRequestedEvent, value); remove => RemoveHandler(HelpRequestedEvent, value); }
}

/// <summary>
/// Property row (DESIGN-SYSTEM §3, item 3.5): <see cref="Label"/> | control (Content) | optional <see cref="Trailing"/> (numeric box, unit, button).
/// Label column width = <see cref="LabelWidth"/> (default 84) so stacked rows align.
/// </summary>
public class PropertyRow : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(PropertyRow),
        new FrameworkPropertyMetadata(null, (d, e) => { if (e.NewValue is string s) AutomationProperties.SetName(d, s); }));
    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(PropertyRow), new FrameworkPropertyMetadata(84.0));
    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(nameof(Trailing), typeof(object), typeof(PropertyRow));

    static PropertyRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PropertyRow), new FrameworkPropertyMetadata(typeof(PropertyRow)));
        FocusableProperty.OverrideMetadata(typeof(PropertyRow), new FrameworkPropertyMetadata(false));
    }

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double LabelWidth { get => (double)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public object? Trailing { get => GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }
}

/// <summary>
/// Slider + numeric box (DESIGN-SYSTEM §3, item 3.5). A <see cref="RangeBase"/>: bind <see cref="RangeBase.Value"/> (two-way by default),
/// Minimum/Maximum/SmallChange/LargeChange. The box accepts typed values (Enter / focus loss commits, Esc reverts, Up/Down step).
/// Optional <see cref="Label"/> on the left (same column width as <see cref="PropertyRow"/>) and <see cref="Unit"/> suffix.
/// </summary>
[TemplatePart(Name = "PART_Box", Type = typeof(TextBox))]
public class SliderWithValue : RangeBase
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SliderWithValue),
        new FrameworkPropertyMetadata(null, (d, e) => { if (e.NewValue is string s) AutomationProperties.SetName(d, s); }));
    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(SliderWithValue), new FrameworkPropertyMetadata(84.0));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(SliderWithValue));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(SliderWithValue),
        new FrameworkPropertyMetadata(0, (d, _) => ((SliderWithValue)d).SyncText()));
    public static readonly DependencyProperty TickFrequencyProperty = DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(SliderWithValue), new FrameworkPropertyMetadata(1.0));
    public static readonly DependencyProperty IsSnapToTickEnabledProperty = DependencyProperty.Register(nameof(IsSnapToTickEnabled), typeof(bool), typeof(SliderWithValue));

    private TextBox? _box;

    static SliderWithValue()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SliderWithValue), new FrameworkPropertyMetadata(typeof(SliderWithValue)));
        ValueProperty.OverrideMetadata(typeof(SliderWithValue), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
        MaximumProperty.OverrideMetadata(typeof(SliderWithValue), new FrameworkPropertyMetadata(100.0));
        FocusableProperty.OverrideMetadata(typeof(SliderWithValue), new FrameworkPropertyMetadata(false));
    }

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double LabelWidth { get => (double)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public string? Unit { get => (string?)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public double TickFrequency { get => (double)GetValue(TickFrequencyProperty); set => SetValue(TickFrequencyProperty, value); }
    public bool IsSnapToTickEnabled { get => (bool)GetValue(IsSnapToTickEnabledProperty); set => SetValue(IsSnapToTickEnabledProperty, value); }

    public override void OnApplyTemplate()
    {
        if (_box is not null)
        {
            _box.LostKeyboardFocus -= OnBoxLostFocus;
            _box.PreviewKeyDown -= OnBoxKeyDown;
        }
        base.OnApplyTemplate();
        _box = GetTemplateChild("PART_Box") as TextBox;
        if (_box is not null)
        {
            _box.LostKeyboardFocus += OnBoxLostFocus;
            _box.PreviewKeyDown += OnBoxKeyDown;
            AutomationProperties.SetName(_box, (Label ?? AutomationProperties.GetName(this)) + " value");
        }
        SyncText();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        SyncText();
    }

    private void SyncText()
    {
        if (_box is not null && !_box.IsKeyboardFocusWithin) _box.Text = Format(Value);
    }

    private string Format(double v) => Math.Round(v, Math.Clamp(Decimals, 0, 6)).ToString("F" + Math.Clamp(Decimals, 0, 6), CultureInfo.CurrentCulture);

    private void Commit()
    {
        if (_box is null) return;
        var text = _box.Text.Trim();
        if (Unit is { Length: > 0 } u && text.EndsWith(u, StringComparison.OrdinalIgnoreCase)) text = text[..^u.Length].Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            SetCurrentValue(ValueProperty, Math.Clamp(Math.Round(v, Math.Clamp(Decimals, 0, 6)), Minimum, Maximum));
        _box.Text = Format(Value);
    }

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Commit(); _box!.SelectAll(); e.Handled = true; break;
            case Key.Escape: _box!.Text = Format(Value); _box.SelectAll(); e.Handled = true; break;
            case Key.Up: Commit(); SetCurrentValue(ValueProperty, Math.Min(Maximum, Value + SmallChange)); _box!.Text = Format(Value); e.Handled = true; break;
            case Key.Down: Commit(); SetCurrentValue(ValueProperty, Math.Max(Minimum, Value - SmallChange)); _box!.Text = Format(Value); e.Handled = true; break;
        }
    }
}
