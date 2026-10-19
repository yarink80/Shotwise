using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Imaging;
using Button = System.Windows.Controls.Button;

namespace Shotwise.UI.Dialogs;

/// <summary>
/// Hand-laid-out field set for one dialog section: every control is captioned once, numeric ranges are validated and the
/// getters (returned by the factory methods) read the live control state. <see cref="Changed"/> fires after any edit.
/// </summary>
internal sealed class FieldSet
{
    private readonly Panel host;
    public FieldSet(Panel host) => this.host = host;
    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public void Heading(string key) => host.Children.Add(new TextBlock { Text = Texts.Get(key), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });

    public Func<double> Number(string key, double value, double min, double max, string? suffixKey = null)
    {
        var box = new TextBox { Text = value.ToString("0.####", CultureInfo.CurrentCulture), MinHeight = 28 };
        box.TextChanged += (_, _) => Raise();
        Add(key, box, suffixKey);
        return () => FormWindow.Number(box, min, max);
    }

    /// <summary>Optional number: a blank box means "not set".</summary>
    public Func<double?> OptionalNumber(string key, double? value, double min, double max, string? suffixKey = null)
    {
        var box = new TextBox { Text = value?.ToString("0.####", CultureInfo.CurrentCulture) ?? "", MinHeight = 28 };
        box.TextChanged += (_, _) => Raise();
        Add(key, box, suffixKey);
        return () => string.IsNullOrWhiteSpace(box.Text) ? null : FormWindow.Number(box, min, max);
    }

    public Func<int> Integer(string key, int value, int min, int max, string? suffixKey = null)
    {
        var get = Number(key, value, min, max, suffixKey);
        return () => { var n = get(); if (n != Math.Truncate(n)) throw new ArgumentException(Texts.Get("WholeNumberRequired")); return (int)n; };
    }

    public Func<int?> OptionalInteger(string key, int? value, int min, int max, string? suffixKey = null)
    {
        var get = OptionalNumber(key, value, min, max, suffixKey);
        return () => { var n = get(); if (n is null) return null; if (n != Math.Truncate(n.Value)) throw new ArgumentException(Texts.Get("WholeNumberRequired")); return (int)n; };
    }

    public Func<string> Text(string key, string? value, bool multiline = false)
    {
        var box = new TextBox { Text = value ?? "", MinHeight = multiline ? 70 : 28, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        box.TextChanged += (_, _) => Raise();
        Add(key, box);
        return () => box.Text;
    }

    public Func<bool> Check(string key, bool value)
    {
        var caption = Texts.Get(key);
        var box = new ToggleSwitchRow { Content = caption, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        box.Checked += (_, _) => Raise(); box.Unchecked += (_, _) => Raise();
        System.Windows.Automation.AutomationProperties.SetName(box, caption);
        host.Children.Add(box);
        return () => box.IsChecked == true;
    }

    public Func<T> Choice<T>(string key, T value, IEnumerable<T>? items = null) where T : struct, Enum
    {
        var combo = FormWindow.MakeCombo(items ?? Enum.GetValues<T>(), value);
        combo.SelectionChanged += (_, _) => Raise();
        Add(key, combo);
        return () => combo.SelectedItem is T t ? t : value;
    }

    /// <summary>Combo over arbitrary values shown by <paramref name="text"/> (default ToString).</summary>
    public Func<T> Pick<T>(string key, T value, IEnumerable<T> items, Func<T, string>? text = null)
    {
        var combo = new ComboBox { ItemsSource = items.ToList(), MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch };
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FuncConverter(v => v is null ? "" : text is not null ? text((T)v) : v.ToString() ?? "") });
        template.VisualTree = factory; combo.ItemTemplate = template; combo.SelectedItem = value;
        combo.SelectionChanged += (_, _) => Raise();
        Add(key, combo);
        return () => combo.SelectedItem is T t ? t : value;
    }

    /// <summary>A [Flags] enum as a column of check boxes (one per single-bit member).</summary>
    public Func<T> Flags<T>(string key, T value) where T : struct, Enum
    {
        var panel = new WrapPanel(); var checks = new List<(long Bit, CheckBox Box)>(); long current = Convert.ToInt64(value);
        foreach (var item in Enum.GetValues<T>())
        {
            long bit = Convert.ToInt64(item); if (bit == 0 || (bit & (bit - 1)) != 0) continue;
            var box = new CheckBox { Content = Texts.Enum(item), IsChecked = (current & bit) == bit, Margin = new Thickness(0, 0, 14, 4) };
            box.Checked += (_, _) => Raise(); box.Unchecked += (_, _) => Raise(); panel.Children.Add(box); checks.Add((bit, box));
        }
        Add(key, panel);
        return () => (T)Enum.ToObject(typeof(T), checks.Where(c => c.Box.IsChecked == true).Aggregate(0L, (n, c) => n | c.Bit));
    }

    public Func<Argb> Color(string key, Argb value, bool allowAlpha = false, IReadOnlyList<Argb>? myColors = null)
    {
        var picker = new ColorPicker(value, allowAlpha, Texts.Get(key)) { MyColors = myColors ?? Array.Empty<Argb>() };
        picker.ColorChanged += _ => Raise();
        Add(key, picker);
        return () => picker.Color;
    }

    public void Add(string key, FrameworkElement editor, string? suffixKey = null)
    {
        FrameworkElement shown = editor;
        if (suffixKey is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal }; editor.MinWidth = 100; row.Children.Add(editor);
            row.Children.Add(new TextBlock { Text = " " + Texts.Get(suffixKey), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 }); shown = row;
        }
        var stack = new PropertyRow { Label = Texts.Get(key), LabelWidth = 190, Content = shown, Margin = new Thickness(0, 4, 0, 8) };
        if (!ReferenceEquals(shown, editor)) System.Windows.Automation.AutomationProperties.SetName(editor, Texts.Get(key));
        host.Children.Add(stack);
    }

    public void AddPlain(UIElement element) => host.Children.Add(element);
    public void NotifyChanged() => Raise();
}

public sealed partial class DialogService
{
    /// <summary>Builds the labelled parameter form for one effect; returns the factory that reads the form back into an effect.</summary>
    internal Func<EffectSpec> BuildEffectForm(FieldSet f, EffectSpec initial, FormWindow w)
    {
        var mine = settings.Current.Editor.MyColors;
        switch (initial)
        {
            case BorderEffect e:
            {
                var color = f.Color("Color", e.Color, false, mine); var width = f.Number("Width", e.Width, 0, 500, "P_Pixels"); var outside = f.Check("Outside", e.Outside);
                return () => e with { Color = color(), Width = width(), Outside = outside() };
            }
            case EdgeEffect e:
            {
                var style = f.Choice("Style", e.Style); var sides = f.Flags("Sides", e.Sides); var size = f.Number("Size", e.Size, 1, 500, "P_Pixels");
                var shadow = f.Check("WithShadow", e.WithShadow); var border = f.Check("WithBorder", e.WithBorder);
                return () => e with { Style = style(), Sides = sides(), Size = size(), WithShadow = shadow(), WithBorder = border() };
            }
            case ShadowEffect e:
            {
                var color = f.Color("Color", e.Color, false, mine); var direction = f.Choice("Direction", e.Direction); var size = f.Number("Size", e.Size, 0, 500, "P_Pixels");
                var blur = f.Number("Blur", e.Blur, 0, 200, "P_Pixels"); var opacity = f.Number("Opacity", e.Opacity, 0, 1);
                return () => e with { Color = color(), Direction = direction(), Size = size(), Blur = blur(), Opacity = opacity() };
            }
            case PerspectiveEffect e:
            {
                var shear = f.Check("Shear", e.Shear); var h = f.Number("Horizontal", e.Horizontal, -100, 100); var v = f.Number("Vertical", e.Vertical, -100, 100);
                var border = f.Check("Border", e.Border); var shadow = f.Check("Shadow", e.Shadow);
                return () => e with { Shear = shear(), Horizontal = h(), Vertical = v(), Border = border(), Shadow = shadow() };
            }
            case PageCurlEffect e:
            {
                var corner = f.Choice("Corner", e.Corner); var size = f.Number("Size", e.Size, 1, 1000, "P_Pixels");
                return () => e with { Corner = corner(), Size = size() };
            }
            case ColorAdjustEffect e:
            {
                var br = f.Number("Brightness", e.Brightness, -100, 100); var co = f.Number("Contrast", e.Contrast, -100, 100); var hue = f.Number("Hue", e.Hue, -180, 180, "P_Degrees");
                var sat = f.Number("Saturation", e.Saturation, -100, 100); var gamma = f.Number("Gamma", e.Gamma, 0.1, 10);
                return () => e with { Brightness = br(), Contrast = co(), Hue = hue(), Saturation = sat(), Gamma = gamma() };
            }
            case ColorReplaceEffect e:
            {
                var rows = new List<(ColorPicker From, ColorPicker To, FrameworkElement Row)>(); var list = new StackPanel();
                void AddRow(Argb from, Argb to)
                {
                    var from1 = new ColorPicker(from, false, Texts.Get("FromColor")) { MyColors = mine }; var to1 = new ColorPicker(to, false, Texts.Get("ToColor")) { MyColors = mine };
                    var remove = new Button { Content = Texts.Get("Remove"), Margin = new Thickness(8, 0, 0, 0) };
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    row.Children.Add(from1); row.Children.Add(new TextBlock { Text = "→", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(to1); row.Children.Add(remove);
                    var item = (from1, to1, (FrameworkElement)row); rows.Add(item); list.Children.Add(row);
                    from1.ColorChanged += _ => f.NotifyChanged(); to1.ColorChanged += _ => f.NotifyChanged();
                    remove.Click += (_, _) => { rows.Remove(item); list.Children.Remove(row); f.NotifyChanged(); };
                }
                foreach (var (a, b) in e.Pairs) AddRow(a, b);
                var add = new Button { Content = Texts.Get("AddColorPair"), Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
                add.Click += (_, _) => { AddRow(Argb.White, Argb.Black); f.NotifyChanged(); };
                f.Add("P_ColorPairs", list); f.AddPlain(add);
                var tolerance = f.Number("Tolerance", e.Tolerance, 0, 1);
                return () => e with { Pairs = rows.Select(r => (r.From.Color, r.To.Color)).ToArray(), Tolerance = tolerance() };
            }
            case FilterEffect e:
            {
                var kind = f.Choice("Kind", e.Kind); var intensity = f.Number("Intensity", e.Intensity, 0, 2); var opacity = f.Number("Opacity", e.Opacity, 0, 1);
                return () => e with { Kind = kind(), Intensity = intensity(), Opacity = opacity() };
            }
            case WatermarkEffect e:
            {
                var path = new TextBox { Text = e.ImagePath, MinHeight = 28 }; path.TextChanged += (_, _) => f.NotifyChanged();
                var browse = new Button { Content = Texts.Get("Browse"), Margin = new Thickness(8, 0, 0, 0) };
                browse.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*" }; if (d.ShowDialog(w) == true) path.Text = d.FileName; };
                var row = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(path);
                f.Add("ImagePath", row);
                var scale = f.Number("Scale", e.Scale, 0.01, 20); var position = f.Choice("Position", e.Position); var underlay = f.Check("Underlay", e.Underlay);
                var transparency = f.Number("Transparency", e.Transparency, 0, 1); var emboss = f.Check("Emboss", e.Emboss);
                var depth = f.Number("EmbossDepth", e.EmbossDepth, 0, 50); var angle = f.Number("EmbossAngle", e.EmbossAngle, 0, 360, "P_Degrees");
                return () => e with { ImagePath = path.Text.Trim(), Scale = scale(), Position = position(), Underlay = underlay(), Transparency = transparency(), Emboss = emboss(), EmbossDepth = depth(), EmbossAngle = angle() };
            }
            case CaptionEffect e:
            {
                var fields = f.Flags("Fields", e.Fields); var custom = f.Text("CustomText", e.CustomText); var prompt = f.Check("PromptAtCapture", e.PromptAtCapture);
                var font = f.Text("FontFamily", e.FontFamily); var size = f.Number("FontSize", e.FontSize, 4, 200, "P_Points");
                var text = f.Color("TextColor", e.TextColor, true, mine); var back = f.Color("BackColor", e.BackColor, true, mine);
                var outside = f.Check("Outside", e.Outside); var position = f.Choice("Position", e.Position);
                return () => e with { Fields = fields(), CustomText = string.IsNullOrEmpty(custom()) ? null : custom(), PromptAtCapture = prompt(), FontFamily = string.IsNullOrWhiteSpace(font()) ? "Segoe UI" : font().Trim(), FontSize = size(), TextColor = text(), BackColor = back(), Outside = outside(), Position = position() };
            }
            case ResizeEffect e:
            {
                var percent = f.OptionalNumber("Percent", e.Percent, 0.1, 10000, "P_Percent"); var width = f.OptionalInteger("Width", e.Width, 1, 100000, "P_Pixels"); var height = f.OptionalInteger("Height", e.Height, 1, 100000, "P_Pixels");
                var aspect = f.Check("KeepAspect", e.KeepAspect); var dpi = f.OptionalNumber("Dpi", e.Dpi, 1, 9600); var smooth = f.Check("Smooth", e.Smooth);
                f.AddPlain(new TextBlock { Text = Texts.Get("P_ResizeHint"), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                return () =>
                {
                    var p = percent(); var wd = width(); var ht = height();
                    if (p is null && wd is null && ht is null) throw new ArgumentException(Texts.Get("P_ResizeNeedsSize"));
                    return e with { Percent = p, Width = wd, Height = ht, KeepAspect = aspect(), Dpi = dpi(), Smooth = smooth() };
                };
            }
            case RotateEffect e:
            {
                var degrees = f.Number("Degrees", e.Degrees, -360, 360, "P_Degrees"); var resize = f.Check("ResizeCanvas", e.ResizeCanvas); var flipH = f.Check("FlipH", e.FlipH); var flipV = f.Check("FlipV", e.FlipV);
                return () => e with { Degrees = degrees(), ResizeCanvas = resize(), FlipH = flipH(), FlipV = flipV() };
            }
            case TrimEffect e:
            {
                var auto = f.Check("Auto", e.Auto); var tolerance = f.Number("Tolerance", e.Tolerance, 0, 1);
                var left = f.Integer("Left", e.Left, 0, 100000, "P_Pixels"); var top = f.Integer("Top", e.Top, 0, 100000, "P_Pixels"); var right = f.Integer("Right", e.Right, 0, 100000, "P_Pixels"); var bottom = f.Integer("Bottom", e.Bottom, 0, 100000, "P_Pixels");
                return () => e with { Auto = auto(), Tolerance = tolerance(), Left = left(), Top = top(), Right = right(), Bottom = bottom() };
            }
            case SpotlightMagnifyEffect e:
            {
                var x = f.Number("X", e.Region.X, 0, 1_000_000, "P_Pixels"); var y = f.Number("Y", e.Region.Y, 0, 1_000_000, "P_Pixels");
                var width = f.Number("Width", e.Region.Width, 1, 1_000_000, "P_Pixels"); var height = f.Number("Height", e.Region.Height, 1, 1_000_000, "P_Pixels");
                var dim = f.Number("DimOpacity", e.DimOpacity, 0, 1); var blur = f.Number("Blur", e.Blur, 0, 200); var magnify = f.Number("Magnify", e.Magnify, 1, 10);
                return () => e with { Region = new Rect2(x(), y(), width(), height()), DimOpacity = dim(), Blur = blur(), Magnify = magnify() };
            }
            case TransparencyEffect e:
            {
                var color = f.Color("Color", e.Color, false, mine); var tolerance = f.Number("Tolerance", e.Tolerance, 0, 1);
                return () => e with { Color = color(), Tolerance = tolerance() };
            }
            default:
                f.AddPlain(new TextBlock { Text = Texts.Get("P_EffectNotEditable"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
                return () => initial;
        }
    }

    public async Task<EffectEditorResult?> ShowEffectEditor(EffectEditorRequest r)
    {
        var w = new FormWindow("EffectEditor");
        w.Body.Children.Add(new TextBlock { Text = Texts.Get(EffectTitleKey(r.Initial)), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        var form = new StackPanel(); w.Body.Children.Add(form);
        var fields = new FieldSet(form); var build = BuildEffectForm(fields, r.Initial, w);
        var image = new Image { MaxHeight = 240, MaxWidth = 480, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (r.PreviewSource is not null) { var preview = new StackPanel(); preview.Children.Add(new PanelHeader { Content = Texts.Get("Preview") }); preview.Children.Add(image); w.Body.Children.Add(new Card { Content = preview, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16) }); }
        int generation = 0; var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        async Task Preview()
        {
            if (r.PreviewSource is null || services.GetService<IEffectProcessor>() is not { } processor) return;
            int current = ++generation;
            try
            {
                var effect = build(); w.Model.Error = null;
                var bitmap = await Task.Run(() => processor.Preview(r.PreviewSource, effect, 480), w.Lifetime.Token);
                if (current == generation && w.IsVisible) image.Source = BitmapInterop.ToBitmapSource(bitmap);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { w.Model.Error = ex.Message; log.LogDebug(ex, "Effect preview unavailable"); }
        }
        timer.Tick += async (_, _) => { timer.Stop(); await Preview(); };
        fields.Changed += () => { timer.Stop(); timer.Start(); };
        w.Loaded += async (_, _) => await Preview(); w.Closed += (_, _) => { timer.Stop(); generation++; };
        return await Show(w, r, () => new EffectEditorResult(build()));
    }

    internal static string EffectTitleKey(EffectSpec effect) => effect switch
    {
        BorderEffect => "Border", EdgeEffect => "P_EffectEdge", ShadowEffect => "Shadow", PerspectiveEffect => "P_EffectPerspective", PageCurlEffect => "P_EffectPageCurl",
        ColorAdjustEffect => "P_EffectColorAdjust", ColorReplaceEffect => "P_EffectColorReplace", FilterEffect => "P_EffectFilter", WatermarkEffect => "P_EffectWatermark",
        CaptionEffect => "Caption", ResizeEffect => "P_EffectResize", RotateEffect => "P_EffectRotate", TrimEffect => "P_EffectTrim", SpotlightMagnifyEffect => "P_EffectSpotlight",
        TransparencyEffect => "Transparency", _ => "EffectEditor",
    };
}
