using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Shotwise.Capture;

public sealed class PrinterCaptureService : IPrinterCaptureService, IAsyncDisposable, IDisposable
{
    private readonly ILogger<PrinterCaptureService> logger;
    private readonly ISettingsService settings;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<string> files = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task processing;
    private readonly object sync = new();
    private FileSystemWatcher? watcher;
    private string folder, printerName = "Shotwise Printer";
    private bool disposed, managing;
    public event EventHandler<IReadOnlyList<BgraImage>>? PagesCaptured;
    public PrinterCaptureService(IAppPaths paths, ISettingsService settings, ILogger<PrinterCaptureService> logger)
    {
        this.logger = logger; this.settings = settings;
        printerName = settings.Current.Printer.PrinterName;
        folder = string.IsNullOrWhiteSpace(settings.Current.Printer.TempDirectory) ? Path.Combine(paths.Temp, "printer") : Path.GetFullPath(settings.Current.Printer.TempDirectory);
        ConfigureWatcher(); processing = Task.Run(ProcessFiles);
    }
    public bool IsInstalled
    {
        get { lock (sync) { using var printer = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers\" + printerName); return printer is not null; } }
    }
    public string WatchFolder
    {
        get { lock (sync) return folder; }
        set
        {
            var path = Path.GetFullPath(value);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (managing) throw new InvalidOperationException("Printer configuration is currently being changed.");
                if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return;
                if (IsInstalled) throw new InvalidOperationException("Uninstall the capture printer before changing its watch folder.");
                watcher?.Dispose(); folder = path; ConfigureWatcher();
                settings.Update(s => s.Printer.TempDirectory = path);
            }
        }
    }
    private void ConfigureWatcher()
    {
        Directory.CreateDirectory(folder);
        watcher = new(folder, "*.pdf") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Created += (_, e) => files.Writer.TryWrite(e.FullPath);
        watcher.Changed += (_, e) => files.Writer.TryWrite(e.FullPath);
        watcher.Renamed += (_, e) => files.Writer.TryWrite(e.FullPath);
        watcher.Error += (_, e) => logger.LogWarning(e.GetException(), "Printer folder watcher failed");
        watcher.EnableRaisingEvents = true;
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf")) files.Writer.TryWrite(file);
    }
    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
    public async Task InstallAsync(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName) || printerName.Length > 200 || printerName.Any(char.IsControl) || printerName.Contains('\\')) throw new ArgumentException("Invalid printer name.", nameof(printerName));
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync) { if (IsInstalled) throw new InvalidOperationException("Uninstall the current capture printer before installing another."); managing = true; }
            string path; lock (sync) path = Path.Combine(folder, "capture.pdf");
            // Never replace a pre-existing printer with the same name.
            var script = "$ErrorActionPreference='Stop'; Import-Module PrintManagement; " +
                "$n=" + Quote(printerName) + "; $p=" + Quote(path) + "; " +
                "if(Get-Printer -Name $n -ErrorAction SilentlyContinue){throw 'Printer name already exists'}; " +
                "if(!(Get-PrinterPort -Name $p -ErrorAction SilentlyContinue)){Add-PrinterPort -Name $p}; " +
                "Add-Printer -Name $n -DriverName 'Microsoft Print To PDF' -PortName $p";
            await Elevated(script).ConfigureAwait(false);
            lock (sync) this.printerName = printerName;
            settings.Update(s => { s.Printer.PrinterName = printerName; s.Printer.TempDirectory = WatchFolder; });
        }
        finally { lock (sync) managing = false; lifecycle.Release(); }
    }
    public async Task UninstallAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync) managing = true;
            string name, port; lock (sync) { name = printerName; port = Path.Combine(folder, "capture.pdf"); }
            var script = "$ErrorActionPreference='Stop'; Import-Module PrintManagement; $n=" + Quote(name) + "; $p=" + Quote(port) + "; " +
                "$printer=Get-Printer -Name $n -ErrorAction SilentlyContinue; if($printer){" +
                "if($printer.DriverName -ne 'Microsoft Print To PDF' -or $printer.PortName -ne $p){throw 'Printer configuration does not belong to this capture service'}; Remove-Printer -Name $n}; " +
                "if(!(Get-Printer | Where-Object PortName -eq $p)){if(Get-PrinterPort -Name $p -ErrorAction SilentlyContinue){Remove-PrinterPort -Name $p}}";
            await Elevated(script).ConfigureAwait(false);
        }
        finally { lock (sync) managing = false; lifecycle.Release(); }
    }
    private static async Task Elevated(string script)
    {
        // Base64 is decoded as data by a fixed command, avoiding command-line metacharacter injection.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var command = "$s=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encoded + "')); try { & ([scriptblock]::Create($s)); exit 0 } catch { exit 1 }";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.Arguments = "-NoProfile -NonInteractive -Command \"" + command + "\"";
        using var process = await Task.Run(() => Process.Start(info)).ConfigureAwait(false) ?? throw new ShotwiseException(ErrorCode.PrinterInstallFailed, "Printer management could not start.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new ShotwiseException(ErrorCode.PrinterInstallFailed, "Printer management failed.");
    }
    private async Task ProcessFiles()
    {
        try
        {
            await foreach (var file in files.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (!File.Exists(file)) continue;
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    // Deny concurrent writes while claiming the completed job, then release the fixed local-port name immediately.
                    var copy = Path.Combine(WatchFolder, Guid.NewGuid().ToString("N") + ".processing");
                    try
                    {
                        using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete)) File.Move(file, copy);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Still being spooled (or already claimed): retry for ~10 s.
                        if (!File.Exists(file)) break;
                        if (attempt == 39) logger.LogError(ex, "Printer document could not be claimed: {Path}", file);
                        else await Task.Delay(250, stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    await ProcessClaimed(copy).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    /// <summary>
    /// Renders a claimed job and removes it (R1-#11). The PDF is read through a stream this method owns and disposes before cleanup, so
    /// the claimed file is not held open by the WinRT document object. A cleanup failure after a successful import is a warning (the
    /// pages were delivered); a render failure keeps the job as <c>.failed</c> for diagnosis.
    /// </summary>
    private async Task ProcessClaimed(string copy)
    {
        IReadOnlyList<BgraImage> pages;
        try { pages = await Render(copy, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Shutting down mid-render: give the job back a .pdf name so the next start imports it instead of orphaning it.
            try { File.Move(copy, Path.ChangeExtension(copy, ".pdf"), false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogDebug(ex, "Interrupted printer job left at {Path}", copy); }
            throw;
        }
        catch (Exception ex) { Retain(copy, ".failed", ex); return; }
        try { PagesCaptured?.Invoke(this, pages); } catch (Exception ex) { logger.LogWarning(ex, "Printer capture subscriber failed"); }
        try { File.Delete(copy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Printed job was imported but its temporary file could not be removed: {Path}", copy); }
    }
    private void Retain(string copy, string suffix, Exception error)
    {
        try
        {
            File.Move(copy, copy + suffix, true);
            logger.LogError(error, "Claimed printer job failed to render; retained as {Path}", copy + suffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(error, "Claimed printer job failed to render; it remains at {Path} ({Reason})", copy, ex.Message);
        }
    }
    private static async Task<IReadOnlyList<BgraImage>> Render(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        using var source = fs.AsRandomAccessStream();
        var document = await PdfDocument.LoadFromStreamAsync(source).AsTask(ct).ConfigureAwait(false);
        var pages = new List<BgraImage>();
        long pixels = 0;
        for (uint i = 0; i < document.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested(); using var page = document.GetPage(i); using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Ceiling(page.Size.Width * 2), DestinationHeight = (uint)Math.Ceiling(page.Size.Height * 2) }).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0); var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            pixels += (long)decoder.PixelWidth * decoder.PixelHeight;
            if (pixels > 128_000_000) throw new ShotwiseException(ErrorCode.ImageTooLarge, "Printed document exceeds the import memory limit.");
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
            pages.Add(new((int)decoder.PixelWidth, (int)decoder.PixelHeight, data.DetachPixelData()) { DpiX = 192, DpiY = 192 });
        }
        return pages.AsReadOnly();
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; lock (sync) { disposed = true; watcher?.Dispose(); } stop.Cancel(); files.Writer.TryComplete(); await processing.ConfigureAwait(false); stop.Dispose(); }
        finally { lifecycle.Release(); }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
