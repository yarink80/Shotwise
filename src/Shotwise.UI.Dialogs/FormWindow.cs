using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using Shotwise.UI.Common.Controls;
using Button = System.Windows.Controls.Button;

namespace Shotwise.UI.Dialogs;

internal static class Texts
{
    private static readonly ResourceManager Resources = new("Shotwise.UI.Dialogs.Resources.DialogStrings", typeof(Texts).Assembly);

    /// <summary>Localized text for a resource key. Unknown identifier-like keys are humanized ("FadeToBlack" -> "Fade To Black"); free text is returned unchanged.</summary>
    public static string Get(string key)
    {
        var found = Resources.GetString(key, CultureInfo.CurrentUICulture);
        if (found is not null) return found;
        return key.Contains(' ') ? key : Regex.Replace(key, "(?<=[a-z])([A-Z])", " $1");
    }

    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Localized display name of an enum value: resource "Enum_{Type}_{Value}", then the bare value name, then a humanized name.</summary>
    public static string Enum(object value)
    {
        var name = value.ToString() ?? "";
        return Resources.GetString("Enum_" + value.GetType().Name + "_" + name, CultureInfo.CurrentUICulture) ?? Get(name);
    }
}

public sealed class DialogViewModel : ObservableObject
{
    private string? error;
    public string? Error { get => error; set => SetProperty(ref error, value); }
}

/// <summary>Shows enum values by their localized name (items stay the raw enum values, so SelectedItem is still the enum).</summary>
internal sealed class EnumTextConverter : System.Windows.Data.IValueConverter
{
    public static EnumTextConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Enum e ? Texts.Enum(e) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

internal sealed class FormWindow : DialogWindow
{
    public const double DefaultBodyWidth = 520;
    public StackPanel Body { get; } = new() { Margin = new Thickness(24, 8, 24, 8), Width = DefaultBodyWidth };
    public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
    public Func<Task>? Commit { get; set; }
    public DialogViewModel Model { get; } = new();
    public CancellationTokenSource Lifetime { get; } = new();
    public FormWindow(string title)
    {
        SetResourceReference(BackgroundProperty, "Shotwise.Surface0Brush");
        SetResourceReference(ForegroundProperty, "Shotwise.TextPrimaryBrush");
        SetResourceReference(FontFamilyProperty, "Shotwise.FontFamily"); FontSize = 13;
        Title = Texts.Get(title); DataContext = Model; MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60);
        MaxWidth = SystemParameters.WorkArea.Width - 60;
        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "Shotwise.Surface0Brush");
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 20, 24, 16) };
        var icon = new SwIcon { Source = "Icon." + (title switch { "Preferences" => "Settings", "ShareAccounts" => "Share", "CreateGif" => "Gif", "SaveAs" => "Save", "ColorPicker" => "Color", "EffectEditor" => "Effects", _ => "Info" }) + ".Regular", Size = 24, Margin = new Thickness(0, 0, 12, 0) };
        icon.SetResourceReference(SwIcon.ForegroundProperty, "Shotwise.AccentTextBrush"); heading.Children.Add(icon);
        heading.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        DockPanel.SetDock(Actions, Dock.Bottom); root.Children.Add(Actions);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 8, 24, 8), MaxWidth = DefaultBodyWidth };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.DangerTextBrush");
        error.SetBinding(TextBlock.TextProperty, nameof(DialogViewModel.Error));
        DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error);
        FrameworkElement surface = title == "Preferences" ? Body : new Card { Content = Body, Margin = new Thickness(16, 0, 16, 0), Padding = new Thickness(0) };
        root.Children.Add(new ScrollViewer { Content = surface, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = root;
        var cancel = Action("Cancel", () => { DialogResult = false; return Task.CompletedTask; }); cancel.IsCancel = true;
        var ok = Action("OK", async () => { if (Commit is not null) await Commit(); DialogResult = true; }); ok.IsDefault = true; ok.SetResourceReference(StyleProperty, "Shotwise.PrimaryButton");
        Closed += (_, _) => { Lifetime.Cancel(); Lifetime.Dispose(); };
    }
    public Button Action(string key, Func<Task> action)
    {
        var b = new Button { Content = Texts.Get(key), Margin = new Thickness(4), MinWidth = 80 };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { Model.Error = null; await action(); }
            catch (OperationCanceledException) { Model.Error = Texts.Get("Cancelled"); }
            catch (Exception ex) { Model.Error = ex.GetBaseException() is Shotwise.Core.ShotwiseException failure ? Shotwise.UI.Common.Resources.Localizer.ErrorMessage(failure.Code) : ex.GetBaseException().Message; }
            finally { b.IsEnabled = true; }
        };
        SwUi.SetCornerRadius(b, new CornerRadius(6));
        b.SetResourceReference(StyleProperty, "Shotwise.GhostButton");
        Actions.Children.Add(b); return b;
    }
    /// <summary>A secondary action placed in the body (next to the field it concerns) instead of the footer.</summary>
    public Button InlineAction(string key, Func<Task> action, Panel? host = null)
    {
        var b = new Button { Content = Texts.Get(key), Margin = new Thickness(0, 0, 8, 8), MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { Model.Error = null; await action(); }
            catch (OperationCanceledException) { Model.Error = Texts.Get("Cancelled"); }
            catch (Exception ex) { Model.Error = ex.GetBaseException() is Shotwise.Core.ShotwiseException failure ? Shotwise.UI.Common.Resources.Localizer.ErrorMessage(failure.Code) : ex.GetBaseException().Message; }
            finally { b.IsEnabled = true; }
        };
        SwUi.SetCornerRadius(b, new CornerRadius(6));
        b.SetResourceReference(StyleProperty, "Shotwise.SecondaryButton");
        (host ?? Body).Children.Add(b); return b;
    }
    public void Label(string text) => Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
    public void Heading(string key) => Body.Children.Add(new PanelHeader { Content = Texts.Get(key), Margin = new Thickness(0, 14, 0, 8) });
    public void Note(string text) { var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 6) }; Body.Children.Add(t); }

    /// <summary>Wraps <paramref name="control"/> with its caption above (not added to the body).</summary>
    public static StackPanel Labelled(string key, FrameworkElement control)
    {
        var caption = Texts.Get(key); var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 6, 0, 3) });
        AutomationProperties.SetName(control, caption); stack.Children.Add(control); return stack;
    }
    public T Field<T>(string key, T control) where T : FrameworkElement { Body.Children.Add(Labelled(key, control)); return control; }

    /// <summary>Places already-built captioned fields side by side (equal widths).</summary>
    public Grid Group(params FrameworkElement[] fields)
    {
        var grid = new Grid();
        for (int i = 0; i < fields.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fields[i].Margin = new Thickness(i == 0 ? 0 : 8, fields[i].Margin.Top, 0, fields[i].Margin.Bottom);
            Grid.SetColumn(fields[i], i); grid.Children.Add(fields[i]);
        }
        Body.Children.Add(grid); return grid;
    }
    public TextBox Text(string key, string? value, bool multiline = false) => Field(key, new TextBox { Text = value ?? "", AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 100 : 28 });
    /// <summary>A check box labelled by its own content only (no separate caption).</summary>
    public ToggleSwitchRow Check(string key, bool value)
    {
        var caption = Texts.Get(key); var box = new ToggleSwitchRow { IsChecked = value, Content = caption, Margin = new Thickness(0, 6, 0, 6) };
        AutomationProperties.SetName(box, caption); Body.Children.Add(box); return box;
    }
    public ComboBox Choice<T>(string key, IEnumerable<T> items, T value) => Field(key, MakeCombo(items, value));
    public ComboBox Enum<T>(string key, T value) where T : struct, Enum => Choice(key, System.Enum.GetValues<T>(), value);

    public static ComboBox MakeCombo<T>(IEnumerable<T> items, T value)
    {
        var combo = new ComboBox { ItemsSource = items, SelectedItem = value, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (typeof(T).IsEnum) ApplyEnumTemplate(combo);
        return combo;
    }
    public static void ApplyEnumTemplate(ComboBox combo)
    {
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = EnumTextConverter.Instance }); template.VisualTree = factory; combo.ItemTemplate = template;
    }
    public static double Number(TextBox box, double min = 0, double max = 1000000)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var n) || !double.IsFinite(n) || n < min || n > max)
            throw new ArgumentException($"{AutomationProperties.GetName(box)}: {min} – {max}");
        return n;
    }
    public static int Integer(TextBox box, int min = 0, int max = 1000000)
    {
        var n = Number(box, min, max); if (n != Math.Truncate(n)) throw new ArgumentException(Texts.Get("WholeNumberRequired")); return (int)n;
    }
}
