using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Imaging;
using Shotwise.Core.Sharing;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Common.Clipboard;

/// <summary>
/// <see cref="IClipboardWriter"/> over the WPF clipboard. Runs on the application dispatcher (or a temporary STA
/// thread when no WPF application exists) and retries while another process holds the clipboard.
/// Image formats written: CF_BITMAP/CF_DIB (via WPF) + "PNG" stream (keeps transparency for apps that read it).
/// </summary>
public sealed class WpfClipboardWriter : IClipboardWriter
{
    private const int Retries = 10;
    private readonly ILogger<WpfClipboardWriter> _log;
    private readonly IServiceProvider? _services;

    /// <param name="services">Optional; used to resolve <see cref="IClipboardImageWatcher"/> lazily so our own writes are not re-imported.</param>
    public WpfClipboardWriter(ILogger<WpfClipboardWriter>? logger = null, IServiceProvider? services = null)
    {
        _log = logger ?? NullLogger<WpfClipboardWriter>.Instance;
        _services = services;
    }

    /// <summary>Tells the clipboard watcher (when registered) that the current clipboard contents are ours.</summary>
    public static void MarkOwnWrite(IServiceProvider? services, ILogger? log = null)
    {
        if (services is null) return;
        try { (services.GetService(typeof(Core.Capture.IClipboardImageWatcher)) as Core.Capture.IClipboardImageWatcher)?.IgnoreCurrentSequence(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { log?.LogDebug(ex, "Clipboard watcher unavailable"); }
    }

    public void SetImage(BgraImage image, bool preventTransparency) => SetContent(new ClipboardContent { Image = image, PreventTransparency = preventTransparency });
    public void SetText(string text) => SetContent(new ClipboardContent { Text = text });
    public void SetHtml(string html, string? plainTextFallback = null) => SetContent(new ClipboardContent { Html = html, Text = plainTextFallback });
    public void SetFileDropList(IReadOnlyList<string> paths) => SetContent(new ClipboardContent { FilePaths = paths });

    public void SetContent(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        RunSta(() =>
        {
            var data = new DataObject();
            if (content.Image is { } img)
            {
                var source = content.PreventTransparency ? ImageOps.Flatten(img, Argb.White) : img;
                data.SetImage(source.ToBitmapSource());
                data.SetData("PNG", new MemoryStream(PngCodec.Encode(source)), false);
            }
            if (!string.IsNullOrEmpty(content.Text)) data.SetText(content.Text, TextDataFormat.UnicodeText);
            if (!string.IsNullOrEmpty(content.Html)) data.SetData(DataFormats.Html, CfHtml.Wrap(content.Html));
            if (content.FilePaths is { Count: > 0 } files)
            {
                var sc = new StringCollection();
                sc.AddRange(files.ToArray());
                data.SetFileDropList(sc);
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetDataObject(data, copy: true);
                    MarkOwnWrite(_services, _log);
                    return;
                }
                catch (COMException ex) when (attempt < Retries)
                {
                    _log.LogDebug(ex, "Clipboard busy (attempt {Attempt})", attempt);
                    Thread.Sleep(50);
                }
                catch (COMException ex)
                {
                    throw new ShotwiseException(ErrorCode.ClipboardUnavailable, "The clipboard is in use by another application.", ex);
                }
            }
        });
    }

    private static void RunSta(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            if (dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action);
            return;
        }
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }
}

/// <summary>Builds the Windows CF_HTML clipboard envelope (UTF-8 byte offsets).</summary>
public static class CfHtml
{
    public static string Wrap(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><body>\r\n<!--StartFragment-->";
        const string post = "<!--EndFragment-->\r\n</body></html>";
        var headerLen = Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var startHtml = headerLen;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(pre);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(post);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, header, startHtml, endHtml, startFragment, endFragment) + pre + fragment + post;
    }
}
