using System.Windows;
using System.Windows.Controls;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.UI.Dialogs;

public sealed partial class DialogService
{
    private static readonly int[] BitDepths = { 1, 4, 8, 16, 24, 32, 48, 64 };
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

    /// <summary>Adds the option fields that matter for <paramref name="format"/> and returns the reader that folds them into <paramref name="options"/>.</summary>
    private Func<EncodeOptions> BuildEncodingFields(string format, EncodeOptions options, FieldSet f)
    {
        var mine = settings.Current.Editor.MyColors;
        switch (format.TrimStart('.').ToLowerInvariant())
        {
            case "jpg" or "jpeg":
            {
                var quality = f.Integer("JpegQuality", options.JpegQuality, 1, 100); var sub = f.Choice("Subsampling", options.Subsampling); var matte = f.Color("MatteColor", options.MatteColor, false, mine);
                return () => options with { JpegQuality = quality(), Subsampling = sub(), MatteColor = matte() };
            }
            case "png" or "bmp" or "gif":
            {
                var depth = f.Pick("BitDepth", BitDepths.Contains(options.BitDepth) ? options.BitDepth : 32, BitDepths, d => d + " bit"); var alpha = f.Check("Transparency", options.Transparency); var matte = f.Color("MatteColor", options.MatteColor, false, mine);
                return () => options with { BitDepth = depth(), Transparency = alpha(), MatteColor = matte() };
            }
            case "tif" or "tiff":
            {
                var depth = f.Pick("BitDepth", BitDepths.Contains(options.BitDepth) ? options.BitDepth : 32, BitDepths, d => d + " bit"); var alpha = f.Check("Transparency", options.Transparency); var matte = f.Color("MatteColor", options.MatteColor, false, mine);
                var compression = f.Pick("TiffCompression", options.TiffCompression, new[] { "lzw", "zip", "none" }, c => Texts.Get("P_Tiff_" + c));
                return () => options with { BitDepth = depth(), Transparency = alpha(), MatteColor = matte(), TiffCompression = compression() };
            }
            case "webp":
            {
                var quality = f.Integer("WebPQuality", options.WebPQuality, 1, 100); var lossless = f.Check("WebPLossless", options.WebPLossless); var alpha = f.Check("Transparency", options.Transparency); var matte = f.Color("MatteColor", options.MatteColor, false, mine);
                return () => options with { WebPQuality = quality(), WebPLossless = lossless(), Transparency = alpha(), MatteColor = matte() };
            }
            case "ico":
            {
                var panel = new WrapPanel(); var boxes = new List<(int Size, CheckBox Box)>();
                foreach (var size in IconSizes)
                {
                    var box = new CheckBox { Content = size + " px", IsChecked = options.IcoSizes.Contains(size), Margin = new Thickness(0, 0, 14, 4) }; panel.Children.Add(box); boxes.Add((size, box));
                }
                f.Add("IcoSizes", panel);
                var depth = f.Pick("BitDepth", BitDepths.Contains(options.BitDepth) ? options.BitDepth : 32, BitDepths, d => d + " bit"); var alpha = f.Check("Transparency", options.Transparency);
                return () => options with { IcoSizes = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Size).ToArray(), BitDepth = depth(), Transparency = alpha() };
            }
            case "pdf":
            {
                var pdf = options.Pdf ?? new PdfPageOptions();
                var paper = f.Choice("Paper", pdf.Paper); var orientation = f.Choice("Orientation", pdf.Orientation); var layout = f.Choice("Layout", pdf.Layout);
                var position = f.Choice("Position", pdf.Position); var scale = f.Number("ScalePercent", pdf.ScalePercent, 1, 1000, "P_Percent");
                var margin = f.Number("P_MarginPoints", pdf.MarginsPoints.Left, 0, 500, "P_Points");
                var title = f.Text("Title", pdf.Title); var author = f.Text("Author", pdf.Author); var subject = f.Text("Subject", pdf.Subject); var keywords = f.Text("Keywords", pdf.Keywords);
                var quality = f.Integer("JpegQuality", options.JpegQuality, 1, 100); var matte = f.Color("MatteColor", options.MatteColor, false, mine);
                static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
                return () => options with
                {
                    Pdf = pdf with { Paper = paper(), Orientation = orientation(), Layout = layout(), Position = position(), ScalePercent = scale(), MarginsPoints = new Thickness2(margin()), Title = Blank(title()), Author = Blank(author()), Subject = Blank(subject()), Keywords = Blank(keywords()) },
                    JpegQuality = quality(), MatteColor = matte(),
                };
            }
            default:
                return () => options;
        }
    }

    private static void ValidateEncoding(EncodeOptions o)
    {
        if (o.JpegQuality is < 1 or > 100 || o.WebPQuality is < 1 or > 100 || !BitDepths.Contains(o.BitDepth)) throw new ArgumentException(Texts.Get("InvalidEncoding"));
    }

    // ---------------------------------------------------------------- page setup

    public async Task<PageSetupResult?> ShowPageSetup(PageSetupRequest r)
    {
        var w = new FormWindow("PageSetup"); var c = r.Current; var f = new FieldSet(w.Body);
        var copies = f.Integer("P_Copies", c.Copies, 1, 999);
        var paper = f.Choice("Paper", c.Paper, Enum.GetValues<PaperSize>().Where(p => p != PaperSize.Custom && p != PaperSize.FitToImage)); var orientation = f.Choice("Orientation", c.Orientation);
        var layout = f.Choice("Layout", c.Layout); var position = f.Choice("Position", c.Position); var scale = f.Number("ScalePercent", c.ScalePercent, 1, 1000, "P_Percent"); var aspect = f.Check("KeepAspect", c.KeepAspect);
        w.Heading("P_Margins");
        var left = f.Number("Left", c.MarginsInches.Left, 0, 10, "P_Inches"); var top = f.Number("Top", c.MarginsInches.Top, 0, 10, "P_Inches"); var right = f.Number("Right", c.MarginsInches.Right, 0, 10, "P_Inches"); var bottom = f.Number("P_Bottom", c.MarginsInches.Bottom, 0, 10, "P_Inches");
        w.Heading("P_PerPage");
        var columns = f.Integer("P_Columns", c.Columns, 1, 20); var rows = f.Integer("P_Rows", c.Rows, 1, 20); var spacing = f.Number("P_Spacing", c.SpacingInches, 0, 10, "P_Inches");
        var names = f.Check("PrintFileNames", c.PrintFileNames); var remember = f.Check("P_RememberSettings", c.RememberSettings);
        var result = await Show(w, r, () =>
        {
            var next = c with { Copies = copies(), Paper = paper(), Orientation = orientation(), Layout = layout(), Position = position(), ScalePercent = scale(), KeepAspect = aspect(), MarginsInches = new Thickness2(left(), top(), right(), bottom()), Columns = columns(), Rows = rows(), SpacingInches = spacing(), PrintFileNames = names(), RememberSettings = remember() };
            ValidatePrint(next); return new PageSetupResult(next);
        });
        return result;
    }

    // ---------------------------------------------------------------- automatic naming

    public async Task<AutoNamingResult?> ShowAutoNaming(AutoNamingRequest r)
    {
        var w = new FormWindow("AutomaticNaming"); var value = Clone(r.Current); var f = new FieldSet(w.Body);
        var prefix = f.Text("Prefix", value.Prefix); var pattern = f.Text("Pattern", value.Pattern);
        w.Note(Texts.Get("P_NamingTokens"));
        var date = f.Text("DateFormat", value.DateFormat); var time = f.Text("TimeFormat", value.TimeFormat);
        var digits = f.Integer("CounterDigits", value.CounterDigits, 1, 12); var start = f.Integer("CounterStart", value.CounterStart, 0, 1_000_000_000);
        var overwrite = f.Check("PromptBeforeOverwrite", value.PromptBeforeOverwrite);
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        w.Body.Children.Add(FormWindow.Labelled("P_NamingPreview", preview));
        NamingSettings Read()
        {
            value.Prefix = prefix(); value.Pattern = pattern(); value.DateFormat = date(); value.TimeFormat = time(); value.CounterDigits = digits(); value.CounterStart = start(); value.PromptBeforeOverwrite = overwrite();
            return value;
        }
        void Preview() { try { preview.Text = NamingPreview(Read(), r.Sample); w.Model.Error = null; } catch (Exception ex) { preview.Text = ""; w.Model.Error = ex.Message; } }
        f.Changed += Preview; Preview();
        return await Show(w, r, () => { var n = Read(); _ = NamingPreview(n, r.Sample); return new AutoNamingResult(n); });
    }

    private static string NamingPreview(NamingSettings n, DocumentMetadata? md)
    {
        if (n.CounterDigits is < 1 or > 12 || n.CounterStart < 0) throw new ArgumentException(Texts.Get("InvalidCounter"));
        var now = md?.CaptureDate ?? DateTimeOffset.Now;
        var name = n.Prefix + n.Pattern;
        foreach (var pair in new Dictionary<string, string> { ["date"] = now.ToString(n.DateFormat), ["time"] = now.ToString(n.TimeFormat), ["counter"] = n.CounterStart.ToString("D" + n.CounterDigits), ["app"] = md?.AppName ?? "App", ["window"] = md?.WindowTitle ?? "Window", ["preset"] = md?.SourcePresetId ?? "Capture", ["width"] = "1920", ["height"] = "1080" }) name = name.Replace("{" + pair.Key + "}", pair.Value);
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return name;
    }

    // ---------------------------------------------------------------- video export (combine videos)

    private static Func<VideoExportOptions> BuildVideoExportFields(VideoExportOptions o, FieldSet f)
    {
        var width = f.OptionalInteger("Width", o.Width, 16, 16384, "P_Pixels"); var height = f.OptionalInteger("Height", o.Height, 16, 16384, "P_Pixels");
        var fps = f.OptionalNumber("Fps", o.Fps, 1, 120); var quality = f.Integer("P_VideoQuality", o.Quality, 1, 100);
        var hardware = f.Check("P_HardwareEncoding", o.HardwareEncoding); var audio = f.Check("P_IncludeAudio", o.IncludeAudio); var bitrate = f.Integer("P_AudioBitrate", o.AudioBitrateKbps, 32, 512, "P_Kbps");
        return () => o with { Width = width(), Height = height(), Fps = fps(), Quality = quality(), HardwareEncoding = hardware(), IncludeAudio = audio(), AudioBitrateKbps = bitrate() };
    }
}
