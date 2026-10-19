using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Core.Styles;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Dialogs;

public sealed partial class DialogService : IDialogService, IShareUi
{
    private readonly IServiceProvider services;
    private readonly ISettingsService settings;
    private readonly ILogger<DialogService> log;
    public DialogService(IServiceProvider services, ISettingsService settings, ILogger<DialogService> log)
    {
        this.services = services; this.settings = settings; this.log = log;
        // Every inline ColorPicker in the application gets the screen eyedropper and the full colour dialog from here.
        ColorPicker.DefaultEyedropper = async () => (await ShowEyedropper(new EyedropperRequest()))?.Color;
        ColorPicker.DefaultMoreColors = async (initial, allowAlpha, title) => (await ShowColorPicker(new ColorPickerRequest(initial, allowAlpha, title)))?.Color;
    }
    private T Need<T>() where T : notnull => services.GetRequiredService<T>();
    public IntPtr OwnerHwnd => Application.Current?.Dispatcher.Invoke(() =>
    { var w = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive); return w is null ? IntPtr.Zero : new WindowInteropHelper(w).Handle; }) ?? IntPtr.Zero;
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ShotwiseJson.Compact), ShotwiseJson.Compact)!;
    private async Task<T?> Show<T>(FormWindow w, DialogRequest request, Func<T> result) where T : class
    {
        T? value = null;
        w.Commit = () => { value = result(); return Task.CompletedTask; };
        await w.ShowDialogAsync(request.Owner); return value;
    }
    public async Task<NewImageResult?> ShowNewImage(NewImageRequest r)
    {
        var w = new FormWindow("NewImage");
        var width = new TextBox { Text = r.DefaultSize.Width.ToString(CultureInfo.CurrentCulture), MinHeight = 28 }; var height = new TextBox { Text = r.DefaultSize.Height.ToString(CultureInfo.CurrentCulture), MinHeight = 28 };
        var dpi = new TextBox { Text = r.DefaultDpi.ToString(CultureInfo.CurrentCulture), MinHeight = 28 };
        w.Group(FormWindow.Labelled("Width", width), FormWindow.Labelled("Height", height), FormWindow.Labelled("Dpi", dpi));
        var color = new ColorPicker(r.DefaultBackground, true, Texts.Get("Background")) { MyColors = settings.Current.Editor.MyColors };
        w.Body.Children.Add(FormWindow.Labelled("Background", color));
        return await Show(w, r, () => new NewImageResult(CheckedSize(FormWindow.Number(width, 1), FormWindow.Number(height, 1)), color.Color, FormWindow.Number(dpi, 1, 9600)));
    }
    private static Size2 CheckedSize(double width, double height)
    {
        if (width <= 0 || height <= 0 || width * height > 512L * 1024 * 1024 || !double.IsFinite(width * height)) throw new ArgumentException(Texts.Get("ImageTooLarge"));
        return new Size2(Math.Round(width), Math.Round(height));
    }
    private sealed class Dimensions
    {
        public TextBox Width { get; } = new() { MinHeight = 28 }; public TextBox Height { get; } = new() { MinHeight = 28 }; public TextBox Dpi { get; } = new() { MinHeight = 28 };
        public ToggleSwitchRow Aspect { get; } public ComboBox Unit { get; }
        private readonly Size2 original;
        public Dimensions(FormWindow w, Size2 size, double dpi, bool showDpi = true, string[]? units = null)
        {
            original = size; Unit = FormWindow.MakeCombo(units ?? new[] { "px", "%", "in", "cm" }, "px");
            Width.Text = size.Width.ToString(CultureInfo.CurrentCulture); Height.Text = size.Height.ToString(CultureInfo.CurrentCulture); Dpi.Text = dpi.ToString(CultureInfo.CurrentCulture);
            var fields = new List<FrameworkElement> { FormWindow.Labelled("Units", Unit), FormWindow.Labelled("Width", Width), FormWindow.Labelled("Height", Height) };
            if (showDpi) fields.Add(FormWindow.Labelled("Dpi", Dpi));
            w.Group(fields.ToArray());
            Aspect = w.Check("KeepAspect", true);
            bool updating = false;
            void Sync(bool fromWidth)
            {
                if (updating || Aspect.IsChecked != true) return;
                var source = fromWidth ? Width : Height; var dest = fromWidth ? Height : Width;
                if (!double.TryParse(source.Text, out var n) || !double.IsFinite(n)) return;
                updating = true;
                var ratio = (string)Unit.SelectedItem == "%" ? 1 : original.Width / original.Height;
                dest.Text = (fromWidth ? n / ratio : n * ratio).ToString("0.####", CultureInfo.CurrentCulture); updating = false;
            }
            Width.TextChanged += (_, _) => Sync(true); Height.TextChanged += (_, _) => Sync(false);
            string oldUnit = "px";
            Unit.SelectionChanged += (_, _) =>
            {
                try
                {
                    var px = ConvertSize(oldUnit); updating = true; var unit = (string)Unit.SelectedItem; var d = FormWindow.Number(Dpi, 1, 9600);
                    Width.Text = FromPx(px.Width, original.Width, unit, d).ToString("0.####"); Height.Text = FromPx(px.Height, original.Height, unit, d).ToString("0.####"); oldUnit = unit;
                }
                catch (ArgumentException ex) { w.Model.Error = ex.Message; }
                finally { updating = false; }
            };
        }
        private static double FromPx(double n, double source, string unit, double dpi) => unit switch { "%" => n / source * 100, "in" => n / dpi, "cm" => n / dpi * 2.54, _ => n };
        private Size2 ConvertSize(string unit)
        {
            double dpi = FormWindow.Number(Dpi, 1, 9600);
            double Px(double n, double original) => unit switch { "%" => n * original / 100, "in" => n * dpi, "cm" => n * dpi / 2.54, _ => n };
            return CheckedSize(Px(FormWindow.Number(Width, 0.001), original.Width), Px(FormWindow.Number(Height, 0.001), original.Height));
        }
        public Size2 Read() => ConvertSize((string)Unit.SelectedItem);
    }
    public Task<ResizeImageResult?> ShowResizeImage(ResizeImageRequest r)
    {
        var w = new FormWindow("ResizeImage"); var d = new Dimensions(w, r.CurrentSize, r.Dpi); var smooth = w.Check("Smooth", true);
        return Show(w, r, () => { var s = d.Read(); return new ResizeImageResult(new ResizeEffect(null, (int)s.Width, (int)s.Height, d.Aspect.IsChecked == true, FormWindow.Number(d.Dpi, 1, 9600), smooth.IsChecked == true)); });
    }
    public Task<ResizeCanvasResult?> ShowResizeCanvas(ResizeCanvasRequest r)
    {
        var w = new FormWindow("ResizeCanvas"); var d = new Dimensions(w, r.CurrentSize, r.Dpi); d.Aspect.IsChecked = false;
        var anchor = w.Enum("Anchor", Anchor9.Center);
        var color = new ColorPicker(r.CurrentColor, true, Texts.Get("Background")) { MyColors = settings.Current.Editor.MyColors }; w.Body.Children.Add(FormWindow.Labelled("Background", color));
        return Show(w, r, () => new ResizeCanvasResult(d.Read(), (Anchor9)anchor.SelectedItem, color.Color));
    }
    public Task<CreateGifResult?> ShowCreateGif(CreateGifRequest r)
    {
        var w = new FormWindow("CreateGif"); w.Body.Width = 520; var preset = w.Enum("Preset", GifPreset.ScreenVideo);
        w.Body.Children.Add(DialogVisuals.Choices(preset, "Icon.Gif.Regular", true));
        w.Heading("Dimensions");
        var d = new Dimensions(w, new Size2(r.Info.Width, r.Info.Height), 96, showDpi: false, units: new[] { "px", "%" }); d.Aspect.IsChecked = false; d.Width.Text = "1280"; d.Height.Text = "720";
        var fps = new TextBox { Text = "5", MinHeight = 28 }; var dither = FormWindow.MakeCombo(Enum.GetValues<GifDitherMode>(), GifDitherMode.FloydSteinberg);
        w.Group(FormWindow.Labelled("Fps", fps), FormWindow.Labelled("DitherMode", dither));
        var loop = w.Check("Loop", true); var fade = w.Check("FadeToBlack", false);
        var selection = w.Check("UseSelection", r.Selection is not null); selection.IsEnabled = r.Selection is not null;
        var path = w.Text("OutputPath", r.SuggestedOutputPath);
        w.InlineAction("Browse", () => { var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(path.Text), Filter = "GIF|*.gif", AddExtension = true, DefaultExt = ".gif" }; if (dialog.ShowDialog(w) == true) path.Text = dialog.FileName; return Task.CompletedTask; });
        var warning = new TextBlock { TextWrapping = TextWrapping.Wrap }; w.Body.Children.Add(warning);
        void Warn() => warning.Text = ((selection.IsChecked == true ? r.Selection?.Duration : r.Info.Duration)?.TotalSeconds ?? 0) > 30 ? Texts.Get("LongGifWarning") : "";
        selection.Checked += (_, _) => Warn(); selection.Unchecked += (_, _) => Warn(); Warn();
        preset.SelectionChanged += (_, _) =>
        {
            var p = (GifPreset)preset.SelectedItem;
            if (p == GifPreset.Custom) return;
            var size = p == GifPreset.ScreenVideo ? new Size2(1280, 720) : p == GifPreset.HighMotion ? new Size2(640, 360) : new Size2(400, Math.Max(1, Math.Round(400d * r.Info.Height / r.Info.Width)));
            d.Unit.SelectedItem = "px"; d.Aspect.IsChecked = false; d.Width.Text = size.Width.ToString(); d.Height.Text = size.Height.ToString();
            fps.Text = p == GifPreset.ScreenVideo ? "5" : p == GifPreset.HighMotion ? "15" : "3";
        };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        w.Body.Children.Add(DialogVisuals.Pill(summary));
        void Summary()
        {
            try { var size = d.Read(); var rate = FormWindow.Number(fps, 1, 60); var seconds = selection.IsChecked == true ? r.Selection!.Duration.TotalSeconds : r.Info.Duration.TotalSeconds;
                summary.Text = $"{size.Width:0} x {size.Height:0} px  /  {rate:0.#} fps  /  {seconds:0.#} s  /  {Math.Ceiling(seconds * rate):0} frames"; }
            catch (ArgumentException) { summary.Text = "?"; }
        }
        d.Width.TextChanged += (_, _) => Summary(); d.Height.TextChanged += (_, _) => Summary(); fps.TextChanged += (_, _) => Summary(); d.Unit.SelectionChanged += (_, _) => Summary();
        selection.Checked += (_, _) => Summary(); selection.Unchecked += (_, _) => Summary(); Summary();
        return Show(w, r, () =>
        {
            var size = d.Read();
            var options = new GifOptions((GifPreset)preset.SelectedItem, (int)size.Width, (int)size.Height, FormWindow.Number(fps, 1, 60), loop.IsChecked == true, true, fade.IsChecked == true, selection.IsChecked == true ? r.Selection : null)
                .WithDitherMode((GifDitherMode)dither.SelectedItem);
            return new CreateGifResult(options, OutputPath(path.Text));
        });
    }
    private static string OutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(Texts.Get("PathRequired"));
        var full = Path.GetFullPath(path); if (!Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException(Texts.Get("FolderMissing")); return full;
    }
    /// <summary>An ordered list with Move up / Move down; items are shown through <paramref name="display"/>.</summary>
    private static ListBox Reorder(FormWindow w, IEnumerable<object> items, Func<object, string> display)
    {
        var data = new System.Collections.ObjectModel.ObservableCollection<object>(items);
        var list = w.Field("Order", new ListBox { ItemsSource = data, MinHeight = 130 });
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FuncConverter(v => v is null ? "" : display(v)) }); template.VisualTree = factory; list.ItemTemplate = template;
        void Move(int delta) { int i = list.SelectedIndex; if (i >= 0 && i + delta >= 0 && i + delta < data.Count) { data.Move(i, i + delta); list.SelectedIndex = i + delta; } }
        var row = new StackPanel { Orientation = Orientation.Horizontal }; w.Body.Children.Add(row);
        w.InlineAction("MoveUp", () => { Move(-1); return Task.CompletedTask; }, row); w.InlineAction("MoveDown", () => { Move(1); return Task.CompletedTask; }, row);
        return list;
    }
    public async Task<TemplatePickerResult?> ShowTemplatePicker(TemplatePickerRequest r)
    {
        var items = (await Need<ITemplateCatalog>().GetTemplatesAsync()).Where(t => r.ImageCount is null || t.MaxImages >= r.ImageCount).ToList();
        var w = new FormWindow("TemplatePicker"); var list = w.Field("Template", new ListBox { ItemsSource = items, DisplayMemberPath = nameof(TemplateDefinition.Name), MinHeight = 160 });
        var preview = new Image { MaxHeight = 260 }; w.Body.Children.Add(preview);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is TemplateDefinition t && services.GetService<IDocumentRenderer>() is { } renderer)
            { try { var doc = t.Instantiate(); preview.Source = BitmapInterop.ToBitmapSource(renderer.RenderThumbnail(doc, 480)); } catch (Exception ex) { log.LogWarning(ex, "Template preview failed"); } }
        };
        list.SelectedIndex = items.Count > 0 ? 0 : -1;
        return await Show(w, r, () => new TemplatePickerResult(list.SelectedItem as TemplateDefinition ?? throw new ArgumentException(Texts.Get("ChooseTemplate"))));
    }
    public async Task<CombineImagesResult?> ShowCombineImages(CombineImagesRequest r)
    {
        if (r.Items.Count < 2) throw new ArgumentException(Texts.Get("TwoImagesRequired"));
        var templates = (await Need<ITemplateCatalog>().GetTemplatesAsync()).Where(t => t.MaxImages >= r.Items.Count).ToList();
        var w = new FormWindow("CombineImages"); var template = w.Choice("Layout", templates, templates.FirstOrDefault()); template.DisplayMemberPath = nameof(TemplateDefinition.Name);
        var list = Reorder(w, r.Items.Select(i => (object)i.Id), id => r.Items.FirstOrDefault(i => i.Id == (Guid)id)?.Name ?? id.ToString()!);
        var thumbs = new WrapPanel(); w.Body.Children.Add(thumbs);
        var zooms = r.Items.ToDictionary(i => i.Id, _ => 1d);
        var zoom = w.Field("Zoom", new Slider { Minimum = 0.25, Maximum = 4, Value = 1, TickFrequency = .25, IsSnapToTickEnabled = true });
        var preview = new Image { MaxWidth = 500, MaxHeight = 260 }; w.Body.Children.Add(preview);
        TemplateDefinition SelectedTemplate()
        {
            var result = Clone((TemplateDefinition?)template.SelectedItem ?? throw new ArgumentException(Texts.Get("ChooseTemplate")));
            var zones = result.Prototype.AllAnnotations().OfType<Shotwise.Core.Annotations.DropZoneAnnotation>().ToArray();
            for (int slot = 0; slot < list.Items.Count && slot < zones.Length; slot++) zones[slot].ContentZoom = zooms[(Guid)list.Items[slot]];
            return result;
        }
        void Refresh()
        {
            thumbs.Children.Clear();
            foreach (Guid id in list.Items)
            {
                var item = r.Items.First(i => i.Id == id);
                var button = new Wpf.Ui.Controls.Button { Content = new Image { Source = BitmapInterop.ToBitmapSource(item.Thumbnail), Width = 80, Height = 60 }, ToolTip = item.Name, Margin = new Thickness(3) };
                button.Click += (_, _) => list.SelectedItem = id; thumbs.Children.Add(button);
            }
            if (services.GetService<IDocumentRenderer>() is not { } renderer || template.SelectedItem is null) return;
            try
            {
                var doc = SelectedTemplate().Instantiate(); var zones = doc.Pages[0].AllAnnotations().OfType<Shotwise.Core.Annotations.DropZoneAnnotation>().ToArray();
                for (int slot = 0; slot < list.Items.Count && slot < zones.Length; slot++) zones[slot].Content = doc.Assets.AddImage(r.Items.First(i => i.Id == (Guid)list.Items[slot]).Thumbnail);
                preview.Source = BitmapInterop.ToBitmapSource(renderer.RenderThumbnail(doc, 480));
            }
            catch (Exception ex) { w.Model.Error = ex.Message; log.LogDebug(ex, "Combined image preview unavailable"); }
        }
        bool loadingZoom = false;
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is Guid id) { loadingZoom = true; zoom.Value = zooms[id]; loadingZoom = false; } };
        zoom.ValueChanged += (_, _) => { if (!loadingZoom && list.SelectedItem is Guid id) { zooms[id] = zoom.Value; Refresh(); } };
        ((System.Collections.Specialized.INotifyCollectionChanged)list.ItemsSource).CollectionChanged += (_, _) => Refresh();
        template.SelectionChanged += (_, _) => Refresh(); Refresh();
        var caption = w.Text("Caption", "");
        return await Show(w, r, () => new CombineImagesResult(SelectedTemplate(), list.Items.Cast<Guid>().ToArray(), caption.Text));
    }
    public Task<CombineVideosResult?> ShowCombineVideos(CombineVideosRequest r)
    {
        var w = new FormWindow("CombineVideos"); var list = Reorder(w, r.Paths.Cast<object>(), p => Path.GetFileName((string)p));
        var output = w.Text("OutputPath", r.SuggestedOutputPath);
        w.InlineAction("Browse", () => { var d = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(output.Text), Filter = "MP4|*.mp4", AddExtension = true, DefaultExt = ".mp4" }; if (d.ShowDialog(w) == true) output.Text = d.FileName; return Task.CompletedTask; });
        w.Heading("P_VideoOptions");
        var read = BuildVideoExportFields(new VideoExportOptions(), new FieldSet(w.Body));
        return Show(w, r, () => { if (list.Items.Count < 2) throw new ArgumentException(Texts.Get("TwoVideosRequired")); var options = read(); return new CombineVideosResult(list.Items.Cast<string>().ToArray(), OutputPath(output.Text), options); });
    }
    public async Task<SaveAsResult?> ShowSaveAs(SaveAsRequest r)
    {
        var formats = r.Formats.Where(f => f.CanWrite).Select(f => f.Extension.TrimStart('.')).ToList();
        formats.AddRange(r.IsVideo ? new[] { "mp4", "gif" } : new[] { "shotx" });
        var w = new FormWindow("SaveAs");
        var format = FormWindow.MakeCombo(formats.Distinct().ToList(), r.DefaultFormat.TrimStart('.'));
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FuncConverter(v => FormatLabel(v?.ToString() ?? "")) }); template.VisualTree = factory; format.ItemTemplate = template;
        w.Body.Children.Add(FormWindow.Labelled("Format", format));
        w.Body.Children.Add(DialogVisuals.Choices(format, "Icon.Document.Regular"));
        if (format.SelectedIndex < 0) format.SelectedIndex = 0;
        var file = w.Text("OutputPath", Path.Combine(r.InitialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), r.SuggestedFileName));
        w.InlineAction("Browse", () => { var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(file.Text), Filter = $"{format.SelectedItem}|*.{format.SelectedItem}", AddExtension = true, OverwritePrompt = true }; if (dialog.ShowDialog(w) == true) file.Text = dialog.FileName; return Task.CompletedTask; });
        w.Heading("P_FormatOptions");
        var options = Clone(r.DefaultOptions ?? new EncodeOptions(Pdf: new PdfPageOptions()));
        var optionHost = new StackPanel(); w.Body.Children.Add(optionHost);
        Func<EncodeOptions> read = () => options;
        void Rebuild() { optionHost.Children.Clear(); read = BuildEncodingFields((string)format.SelectedItem, options, new FieldSet(optionHost)); }
        Rebuild();
        format.SelectionChanged += (_, _) =>
        {
            try { options = read(); Rebuild(); w.Model.Error = null; file.Text = Path.ChangeExtension(file.Text, (string)format.SelectedItem); }
            catch (Exception ex) { w.Model.Error = ex.Message; }
        };
        SaveAsResult? result = null;
        w.Commit = async () =>
        {
            var encoded = read(); ValidateEncoding(encoded); var path = OutputPath(Path.ChangeExtension(file.Text, (string)format.SelectedItem));
            if (File.Exists(path) && !(await Confirm(new ConfirmRequest(Texts.Get("Overwrite"), path) { Owner = w })).Confirmed) throw new OperationCanceledException();
            result = new SaveAsResult(path, (string)format.SelectedItem, encoded);
        };
        await w.ShowDialogAsync(r.Owner); return result;
    }
    private static string FormatLabel(string extension) => extension.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "JPEG (.jpg)", "png" => "PNG (.png)", "bmp" => "BMP (.bmp)", "gif" => "GIF (.gif)", "tif" or "tiff" => "TIFF (.tif)", "webp" => "WebP (.webp)",
        "ico" => "Icon (.ico)", "pdf" => "PDF (.pdf)", "shotx" => Texts.Get("P_FormatShotx") + " (.shotx)", "mp4" => "MP4 video (.mp4)", var other => other.ToUpperInvariant() + " (." + other + ")",
    };
    public Task<BatchExportResult?> ShowBatchExport(BatchExportRequest r)
    {
        var w = new FormWindow("BatchExport"); var files = w.Text("Files", string.Join(Environment.NewLine, r.Files), true);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 }; w.Body.Children.Add(summary);
        var folder = w.Text("OutputFolder", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        w.InlineAction("Browse", () => { var d = new Microsoft.Win32.OpenFolderDialog(); if (d.ShowDialog(w) == true) folder.Text = d.FolderName; return Task.CompletedTask; });
        var formats = Need<IImageCodec>().SupportedFormats.Where(f => f.CanWrite).Select(f => f.Format.DefaultExtension().TrimStart('.')).ToArray();
        var format = FormWindow.MakeCombo(formats, formats.Contains("png") ? "png" : formats.FirstOrDefault() ?? "png");
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FuncConverter(v => FormatLabel(v?.ToString() ?? "")) }); template.VisualTree = factory; format.ItemTemplate = template;
        w.Body.Children.Add(FormWindow.Labelled("Format", format));
        var options = new EncodeOptions(Pdf: new PdfPageOptions()); var optionHost = new StackPanel(); w.Body.Children.Add(optionHost);
        Func<EncodeOptions> read = () => options;
        void Rebuild() { optionHost.Children.Clear(); if (format.SelectedItem is string f) read = BuildEncodingFields(f, options, new FieldSet(optionHost)); }
        Rebuild();
        format.SelectionChanged += (_, _) => { try { options = read(); Rebuild(); w.Model.Error = null; } catch (Exception ex) { w.Model.Error = ex.Message; } };
        w.Heading("Effects");
        var effects = new List<EffectSpec>(); var effectList = new ListBox { MinHeight = 70, Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(effectList, Texts.Get("Effects")); w.Body.Children.Add(effectList);
        var effectTemplate = new DataTemplate(); var effectFactory = new FrameworkElementFactory(typeof(TextBlock));
        effectFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FuncConverter(v => v is EffectSpec e ? Texts.Get(EffectTitleKey(e)) : "") }); effectTemplate.VisualTree = effectFactory; effectList.ItemTemplate = effectTemplate;
        var effectNames = typeof(EffectDefaults).GetProperties().Select(p => p.Name).ToArray();
        var effectType = FormWindow.MakeCombo(effectNames, "Border");
        var effectDisplay = new DataTemplate(); var effectDisplayFactory = new FrameworkElementFactory(typeof(TextBlock));
        effectDisplayFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FuncConverter(v => Texts.Get(EffectTitleKey((EffectSpec)typeof(EffectDefaults).GetProperty((string)v!)!.GetValue(null)!))) }); effectDisplay.VisualTree = effectDisplayFactory; effectType.ItemTemplate = effectDisplay;
        w.Body.Children.Add(FormWindow.Labelled("Effect", effectType));
        var effectRow = new StackPanel { Orientation = Orientation.Horizontal }; w.Body.Children.Add(effectRow);
        w.InlineAction("AddEffect", async () => { var initial = (EffectSpec)typeof(EffectDefaults).GetProperty((string)effectType.SelectedItem)!.GetValue(null)!; var edited = await ShowEffectEditor(new EffectEditorRequest(initial) { Owner = w }); if (edited is not null) effects.Add(edited.Effect); effectList.ItemsSource = effects.ToArray(); }, effectRow);
        w.InlineAction("RemoveEffect", () => { if (effectList.SelectedItem is EffectSpec item) effects.Remove(item); effectList.ItemsSource = effects.ToArray(); return Task.CompletedTask; }, effectRow);
        w.Heading("P_NamingSection");
        var auto = w.Check("UseAutomaticNaming", false); var naming = Clone(settings.Current.Share.Naming);
        w.InlineAction("AutomaticNaming", async () => { var result = await ShowAutoNaming(new AutoNamingRequest(naming) { Owner = w }); if (result is not null) naming = result.Settings; });
        files.TextChanged += (_, _) => summary.Text = $"{Lines(files.Text).Length} {Texts.Get("Files")}"; summary.Text = $"{Lines(files.Text).Length} {Texts.Get("Files")}";
        return Show(w, r, () => { var paths = Lines(files.Text); if (paths.Length == 0 || paths.Any(p => !File.Exists(p))) throw new ArgumentException(Texts.Get("FilesMissing")); if (!Directory.Exists(folder.Text)) throw new DirectoryNotFoundException(); var encoded = read(); ValidateEncoding(encoded); return new BatchExportResult(paths, effects.ToArray(), Path.GetFullPath(folder.Text), (string)format.SelectedItem, encoded, auto.IsChecked == true, naming); });
    }
    private static string[] Lines(string value) => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
