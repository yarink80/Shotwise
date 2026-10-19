using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Imaging;
using CorePage = System.Windows.Documents.FixedPage;

namespace Shotwise.UI.Dialogs;

public sealed partial class DialogService
{
    private static void ValidatePrint(PrintSettings s)
    {
        var m = s.MarginsInches;
        if (s.Columns is < 1 or > 20 || s.Rows is < 1 or > 20 || s.Copies is < 1 or > 999 || !double.IsFinite(s.ScalePercent) || s.ScalePercent <= 0 || s.SpacingInches < 0 || !double.IsFinite(s.SpacingInches) || new[] { m.Left, m.Top, m.Right, m.Bottom }.Any(n => n < 0 || !double.IsFinite(n))) throw new ArgumentException(Texts.Get("InvalidPageSetup"));
    }
    public async Task<PrintResult?> ShowPrint(PrintRequest r)
    {
        var config = r.Settings ?? PrintSettings.Default;
        if (!r.QuickPrint)
        {
            var setup = await ShowPageSetup(new PageSetupRequest(config) { Owner = r.Owner }); if (setup is null) return null; config = setup.Settings;
        }
        ValidatePrint(config);
        var dialog = new PrintDialog { UserPageRangeEnabled = false };
        using var server = new LocalPrintServer();
        using var queue = string.IsNullOrWhiteSpace(config.PrinterName) ? LocalPrintServer.GetDefaultPrintQueue() : server.GetPrintQueue(config.PrinterName);
        dialog.PrintQueue = queue;
        if (!r.QuickPrint && dialog.ShowDialog() != true) return null;
        using var selected = dialog.PrintQueue;
        if (selected is null) return null;
        if (string.Equals(selected.Name, PrinterCaptureSettings.DefaultPrinterName, StringComparison.OrdinalIgnoreCase) || string.Equals(selected.Name, settings.Current.Printer.PrinterName, StringComparison.OrdinalIgnoreCase))
        { await ShowMessage(new MessageRequest(Texts.Get("Print"), Texts.Get("CapturePrinterRefused"), MessageKind.Warning) { Owner = r.Owner }); return new PrintResult(false, config); }
        config = config with { PrinterName = selected.Name };
        dialog.PrintTicket.CopyCount = config.Copies;
        dialog.PrintTicket.PageOrientation = (config.Orientation == Shotwise.Core.Imaging.PageOrientation.Landscape || config.Orientation == Shotwise.Core.Imaging.PageOrientation.Auto && r.Pages.Count > 0 && r.Pages[0].Width > r.Pages[0].Height) ? System.Printing.PageOrientation.Landscape : System.Printing.PageOrientation.Portrait;
        var paperName = config.Paper switch { PaperSize.A3 => PageMediaSizeName.ISOA3, PaperSize.A4 => PageMediaSizeName.ISOA4, PaperSize.A5 => PageMediaSizeName.ISOA5, PaperSize.Legal => PageMediaSizeName.NorthAmericaLegal, PaperSize.Tabloid => PageMediaSizeName.NorthAmericaTabloid, _ => PageMediaSizeName.NorthAmericaLetter };
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(paperName);
        var doc = PrintDocument(r.Pages, config, r.Names);
        dialog.PrintDocument(doc.DocumentPaginator, "Shotwise");
        return new PrintResult(true, config);
    }
    public async Task ShowPrintPreview(PrintPreviewRequest r)
    {
        ValidatePrint(r.Settings); var w = new FormWindow("PrintPreview");
        w.Body.Children.Add(new DocumentViewer { Document = PrintDocument(r.Pages, r.Settings), Width = Math.Min(900, SystemParameters.WorkArea.Width - 100), Height = Math.Min(650, SystemParameters.WorkArea.Height - 220) });
        // Route print through the guarded service instead of DocumentViewer's built-in printer command.
        System.Windows.Input.CommandManager.AddPreviewExecutedHandler(w, async (_, e) =>
        {
            if (e.Command != System.Windows.Input.ApplicationCommands.Print) return;
            e.Handled = true;
            try { await ShowPrint(new PrintRequest(r.Pages, Settings: r.Settings) { Owner = w }); }
            catch (Exception ex) { w.Model.Error = ex.Message; }
        });
        await w.ShowDialogAsync(r.Owner);
    }
    private static FixedDocument PrintDocument(IReadOnlyList<BgraImage> images, PrintSettings s, IReadOnlyList<string>? names = null)
    {
        var doc = new FixedDocument();
        var paper = s.Paper switch { PaperSize.A3 => new Size(1122.52, 1587.4), PaperSize.A4 => new Size(793.7, 1122.52), PaperSize.A5 => new Size(559.37, 793.7), PaperSize.Legal => new Size(816, 1344), PaperSize.Tabloid => new Size(1056, 1632), _ => new Size(816, 1056) };
        bool landscape = s.Orientation == Shotwise.Core.Imaging.PageOrientation.Landscape || s.Orientation == Shotwise.Core.Imaging.PageOrientation.Auto && images.Count > 0 && images[0].Width > images[0].Height;
        if (landscape) paper = new Size(paper.Height, paper.Width);
        var m = s.MarginsInches; double space = s.SpacingInches * 96;
        double width = (paper.Width - (m.Left + m.Right) * 96 - space * (s.Columns - 1)) / s.Columns;
        double height = (paper.Height - (m.Top + m.Bottom) * 96 - space * (s.Rows - 1)) / s.Rows;
        if (width <= 0 || height <= 0) throw new ArgumentException(Texts.Get("MarginsTooLarge"));
        for (int start = 0; start < images.Count; start += s.Columns * s.Rows)
        {
            var page = new CorePage { Width = paper.Width, Height = paper.Height, Background = Brushes.White };
            for (int slot = 0; slot < s.Columns * s.Rows && start + slot < images.Count; slot++)
            {
                var source = images[start + slot]; double labelHeight = s.PrintFileNames ? 22 : 0; double usableH = height - labelHeight;
                double nativeW = source.Width * 96 / source.DpiX, nativeH = source.Height * 96 / source.DpiY;
                double scale = s.Layout switch { PageImageLayout.FillPage => Math.Max(width / nativeW, usableH / nativeH), PageImageLayout.ActualSize => 1, PageImageLayout.CustomScale => s.ScalePercent / 100, PageImageLayout.FitWidth => width / nativeW, _ => Math.Min(width / nativeW, usableH / nativeH) };
                double iw = s.KeepAspect ? nativeW * scale : width, ih = s.KeepAspect ? nativeH * scale : usableH;
                int anchor = (int)s.Position;
                var cell = new Canvas { Width = width, Height = height, ClipToBounds = true };
                var image = new Image { Source = BitmapInterop.ToBitmapSource(source), Width = iw, Height = ih, Stretch = Stretch.Fill };
                Canvas.SetLeft(image, (width - iw) * (anchor % 3) / 2); Canvas.SetTop(image, (usableH - ih) * (anchor / 3) / 2); cell.Children.Add(image);
                if (s.PrintFileNames && names is not null && start + slot < names.Count) { var label = new TextBlock { Text = names[start + slot], Foreground = Brushes.Black, Width = width, TextTrimming = TextTrimming.CharacterEllipsis }; Canvas.SetTop(label, usableH); cell.Children.Add(label); }
                FixedPage.SetLeft(cell, m.Left * 96 + slot % s.Columns * (width + space)); FixedPage.SetTop(cell, m.Top * 96 + slot / s.Columns * (height + space)); page.Children.Add(cell);
            }
            var content = new PageContent(); ((System.Windows.Markup.IAddChild)content).AddChild(page); doc.Pages.Add(content);
        }
        return doc;
    }
}

