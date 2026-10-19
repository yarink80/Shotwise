using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Sharing;
using Shotwise.UI.Common.Dialogs;
using CorePrintRequest = Shotwise.Core.Sharing.PrintRequest;
using DialogPrintRequest = Shotwise.UI.Common.Dialogs.PrintRequest;

namespace Shotwise.UI.Dialogs;

/// <summary>
/// Core <see cref="IPrintService"/> (SPEC SHR-03, CONTRACT A17) over the dialog layer's page setup / printer choice / preview
/// code: files are decoded with <see cref="IImageCodec"/> (all pages of TIFF/PDF), laid out by <see cref="DialogService"/>
/// and printed through WPF/System.Printing. The capture printer is refused (loop prevention) by <see cref="DialogService.ShowPrint"/>.
/// </summary>
public sealed class PrintService : IPrintService
{
    private readonly DialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly ILogger<PrintService> _log;

    public PrintService(DialogService dialogs, IServiceProvider services, ILogger<PrintService> log)
    {
        _dialogs = dialogs;
        _services = services;
        _log = log;
    }

    public async Task<bool> PrintAsync(CorePrintRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Files.Count == 0) return false;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(() => PrintAsync(request, ct)).Task.Unwrap().ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        var codec = _services.GetService<IImageCodec>();
        var pages = new List<BgraImage>();
        var names = new List<string>();
        foreach (var file in request.Files)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<BgraImage> decoded = await Task.Run(() => codec is not null
                ? codec.LoadAllPages(file)
                : (IReadOnlyList<BgraImage>)new[] { PngCodec.Decode(File.ReadAllBytes(file)) }, ct).ConfigureAwait(true);
            foreach (var page in decoded) { pages.Add(page); names.Add(Path.GetFileName(file)); }
        }
        if (pages.Count == 0) return false;

        var settings = ToSettings(request);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (request.Preview)
        {
            // The preview's own Print command goes through the guarded ShowPrint path.
            await _dialogs.ShowPrintPreview(new PrintPreviewRequest(pages, settings) { Owner = owner }).ConfigureAwait(true);
            return true;
        }
        var result = await _dialogs.ShowPrint(new DialogPrintRequest(pages, names, settings, request.Quick) { Owner = owner }).ConfigureAwait(true);
        _log.LogInformation("Print of {Count} page(s): {Outcome}", pages.Count, result?.Printed == true ? "submitted" : "cancelled");
        return result?.Printed == true;
    }

    /// <summary>Maps the Core page options to the dialog layer's print settings (points → inches; images per page → grid).</summary>
    public static PrintSettings ToSettings(CorePrintRequest request)
    {
        var p = request.Page;
        int perPage = Math.Clamp(request.ImagesPerPage, 1, 400);
        int columns = (int)Math.Ceiling(Math.Sqrt(perPage));
        int rows = (int)Math.Ceiling(perPage / (double)columns);
        var m = p.MarginsPoints;
        bool hasMargins = m.Left > 0 || m.Top > 0 || m.Right > 0 || m.Bottom > 0;
        return PrintSettings.Default with
        {
            PrinterName = string.IsNullOrWhiteSpace(request.PrinterName) ? null : request.PrinterName,
            Paper = p.Paper is PaperSize.FitToImage or PaperSize.Custom ? PaperSize.Letter : p.Paper,
            Orientation = p.Orientation,
            Layout = p.Layout,
            Position = p.Position,
            ScalePercent = p.ScalePercent > 0 ? p.ScalePercent : 100,
            MarginsInches = hasMargins ? new Thickness2(m.Left / 72, m.Top / 72, m.Right / 72, m.Bottom / 72) : PrintSettings.Default.MarginsInches,
            Columns = columns,
            Rows = rows,
        };
    }
}
