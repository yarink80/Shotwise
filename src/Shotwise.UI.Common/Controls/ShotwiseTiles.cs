using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Shotwise.UI.Common.Controls;

/// <summary>
/// Quick Style tile (DESIGN-SYSTEM §3, item 3.9): rounded tile showing a rendered <see cref="Thumbnail"/> of the style, a star when
/// <see cref="IsFavorite"/>, hover lift and an accent ring when <see cref="IsSelected"/>. Inside a ListBox using
/// <c>Shotwise.TileList</c>, IsSelected follows the ListBoxItem automatically.
/// </summary>
public class QuickStyleTile : Control
{
    public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(nameof(Thumbnail), typeof(ImageSource), typeof(QuickStyleTile));
    public static readonly DependencyProperty IsFavoriteProperty = DependencyProperty.Register(nameof(IsFavorite), typeof(bool), typeof(QuickStyleTile));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(QuickStyleTile));
    public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(QuickStyleTile), new FrameworkPropertyMetadata(60.0));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(QuickStyleTile),
        new FrameworkPropertyMetadata(null, (d, e) => { if (e.NewValue is string s) { AutomationProperties.SetName(d, s); ((FrameworkElement)d).ToolTip ??= s; } }));

    static QuickStyleTile()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(QuickStyleTile), new FrameworkPropertyMetadata(typeof(QuickStyleTile)));
        FocusableProperty.OverrideMetadata(typeof(QuickStyleTile), new FrameworkPropertyMetadata(false));
    }

    public ImageSource? Thumbnail { get => (ImageSource?)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
    public bool IsFavorite { get => (bool)GetValue(IsFavoriteProperty); set => SetValue(IsFavoriteProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public double TileSize { get => (double)GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }
    /// <summary>Style name (tooltip + automation name).</summary>
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
}

/// <summary>
/// Recent-captures tray card (DESIGN-SYSTEM §3, item 3.10): rounded thumbnail (96x72 by default), accent ring when selected, pin badge,
/// video duration badge (<see cref="Duration"/>, e.g. "0:42"), optional unsaved dot. Use inside a ListBox with <c>Shotwise.TrayList</c>.
/// </summary>
public class TrayItem : Control
{
    public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(nameof(Thumbnail), typeof(ImageSource), typeof(TrayItem));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TrayItem));
    public static readonly DependencyProperty IsPinnedProperty = DependencyProperty.Register(nameof(IsPinned), typeof(bool), typeof(TrayItem));
    public static readonly DependencyProperty IsModifiedProperty = DependencyProperty.Register(nameof(IsModified), typeof(bool), typeof(TrayItem));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(string), typeof(TrayItem));
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(TrayItem), new FrameworkPropertyMetadata(96.0));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(TrayItem), new FrameworkPropertyMetadata(72.0));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(TrayItem),
        new FrameworkPropertyMetadata(null, (d, e) => { if (e.NewValue is string s) { AutomationProperties.SetName(d, s); ((FrameworkElement)d).ToolTip ??= s; } }));

    static TrayItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TrayItem), new FrameworkPropertyMetadata(typeof(TrayItem)));
        FocusableProperty.OverrideMetadata(typeof(TrayItem), new FrameworkPropertyMetadata(false));
    }

    public ImageSource? Thumbnail { get => (ImageSource?)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsPinned { get => (bool)GetValue(IsPinnedProperty); set => SetValue(IsPinnedProperty, value); }
    public bool IsModified { get => (bool)GetValue(IsModifiedProperty); set => SetValue(IsModifiedProperty, value); }
    /// <summary>Video/GIF duration text; null hides the badge.</summary>
    public string? Duration { get => (string?)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
}

public enum InfoSeverity { Informational, Success, Warning, Error }

/// <summary>
/// Inline message bar / toast (DESIGN-SYSTEM §3, item 3.11). Severity picks icon + tint. Style <c>Shotwise.Toast</c> turns it into a floating
/// elevated toast. <see cref="IsOpen"/> = visible; the close button (when <see cref="IsClosable"/>) sets it false and raises <see cref="Closed"/>.
/// </summary>
public class InfoBar : ContentControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(nameof(Severity), typeof(InfoSeverity), typeof(InfoBar));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(InfoBar));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(InfoBar));
    public static readonly DependencyProperty IsClosableProperty = DependencyProperty.Register(nameof(IsClosable), typeof(bool), typeof(InfoBar), new FrameworkPropertyMetadata(true));
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(InfoBar),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(nameof(Action), typeof(object), typeof(InfoBar));
    public static readonly RoutedEvent ClosedEvent = EventManager.RegisterRoutedEvent(nameof(Closed), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(InfoBar));
    public static readonly RoutedCommand CloseCommand = new(nameof(CloseCommand), typeof(InfoBar));

    static InfoBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(InfoBar), new FrameworkPropertyMetadata(typeof(InfoBar)));
        FocusableProperty.OverrideMetadata(typeof(InfoBar), new FrameworkPropertyMetadata(false));
        CommandManager.RegisterClassCommandBinding(typeof(InfoBar), new CommandBinding(CloseCommand, (s, _) =>
        {
            var bar = (InfoBar)s;
            bar.SetCurrentValue(IsOpenProperty, false);
            bar.RaiseEvent(new RoutedEventArgs(ClosedEvent, bar));
        }));
    }

    public InfoBar() => AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

    public InfoSeverity Severity { get => (InfoSeverity)GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public bool IsClosable { get => (bool)GetValue(IsClosableProperty); set => SetValue(IsClosableProperty, value); }
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    /// <summary>Optional action (e.g. a GhostButton "Undo").</summary>
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }

    public event RoutedEventHandler Closed { add => AddHandler(ClosedEvent, value); remove => RemoveHandler(ClosedEvent, value); }
}

/// <summary>
/// Empty state (DESIGN-SYSTEM §3, item 3.11): illustration (colour icon in a soft accent disc), title, description and an action
/// (e.g. a PrimaryButton "New capture").
/// </summary>
public class EmptyState : Control
{
    public static readonly DependencyProperty IllustrationProperty = DependencyProperty.Register(nameof(Illustration), typeof(object), typeof(EmptyState));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(nameof(Action), typeof(object), typeof(EmptyState));

    static EmptyState()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));
        FocusableProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Geometry / ImageSource / resource key, drawn 44 px inside a 96 px disc.</summary>
    public object? Illustration { get => GetValue(IllustrationProperty); set => SetValue(IllustrationProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }
}
