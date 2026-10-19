using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Platform;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.App.Hosting;

/// <summary>
/// Consumes <c>shotwise://import/{stamps|templates|themes}?url=https://…</c> protocol activations (PLT-06, R2-P2): the
/// Platform <see cref="UriActivation"/> validates the URI and raises <see cref="UriActivation.ImportRequested"/>; this
/// handler asks the user to confirm the source, downloads it over HTTPS only (size and time capped, no downgrade on
/// redirect) to a private temp file and imports it through the stamp catalog / template catalog / style repository.
/// Activations always consume the whole command line (no other switch or file from the same activation is honoured).
/// </summary>
public sealed class UriImportHandler : IDisposable
{
    /// <summary>Largest accepted download.</summary>
    public const long MaxDownloadBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly ILogger<UriImportHandler> _log;
    private readonly UriActivation? _activation;
    private bool _busy;

    public UriImportHandler(IServiceProvider services, ILogger<UriImportHandler> log)
    {
        _services = services;
        _log = log;
        _activation = services.GetService<UriActivation>();
        if (_activation is not null) _activation.ImportRequested += OnImportRequested;
    }

    /// <summary>
    /// Routes raw (initial or forwarded) arguments through the Platform validator BEFORE any other parsing. Returns true
    /// when the arguments were a protocol activation (valid or not) and must not be interpreted any further.
    /// </summary>
    public bool TryHandle(IReadOnlyList<string> rawArgs)
    {
        if (_activation is not null) return _activation.HandleArguments(rawArgs);
        // Without the Platform validator never interpret an activation's arguments.
        return IsActivation(rawArgs);
    }

    /// <summary>True when the arguments look like a protocol activation (used before the host exists).</summary>
    public static bool IsActivation(IEnumerable<string> rawArgs)
        => rawArgs.Any(a => a.StartsWith("shotwise:", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--uri", StringComparison.OrdinalIgnoreCase));

    /// <summary>File extension the importers expect for a kind.</summary>
    public static string ExtensionFor(string kind) => kind switch
    {
        "stamps" => ".shotstamps",
        "templates" => ".shottemplate",
        "themes" => ".shottheme",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void OnImportRequested(object? sender, UriActivation.ImportRequest request)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.InvokeAsync(() => ImportAsync(request)).Task.Unwrap().ContinueWith(t =>
        {
            if (t.Exception is { } ex) _log.LogWarning(ex, "Protocol import failed");
        }, TaskScheduler.Default);
    }

    private async Task ImportAsync(UriActivation.ImportRequest request)
    {
        if (_busy) return;
        _busy = true;
        var dialogs = _services.GetRequiredService<IDialogService>();
        try
        {
            var what = request.Kind switch { "stamps" => "a stamp pack", "templates" => "a template", _ => "a style theme" };
            var confirm = await dialogs.Confirm(new ConfirmRequest(
                "Import from the web",
                $"A link asks Shotwise to download and import {what} from:\n\n{request.Source.Host}\n{request.Source.AbsoluteUri}\n\nOnly continue if you trust this website.",
                "Download and import", "Cancel"));
            if (!confirm.Confirmed) { _log.LogInformation("Protocol import from {Host} declined", request.Source.Host); return; }

            var file = await DownloadAsync(request);
            try
            {
                switch (request.Kind)
                {
                    case "stamps":
                        await (_services.GetService<IStampCatalog>() ?? throw new ShotwiseException(ErrorCode.NotSupported, "Stamps are not available.")).ImportPackAsync(file);
                        break;
                    case "templates":
                        await (_services.GetService<ITemplateCatalog>() ?? throw new ShotwiseException(ErrorCode.NotSupported, "Templates are not available.")).ImportAsync(file);
                        break;
                    case "themes":
                        await (_services.GetService<IStyleRepository>() ?? throw new ShotwiseException(ErrorCode.NotSupported, "Themes are not available.")).ImportAsync(file);
                        break;
                }
            }
            finally
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _log.LogInformation("Imported {Kind} from {Host}", request.Kind, request.Source.Host);
            await dialogs.ShowMessage(new MessageRequest("Import from the web", $"Imported {what} from {request.Source.Host}.", MessageKind.Success));
        }
        catch (Exception ex) when (ex is ShotwiseException or HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _log.LogWarning(ex, "Protocol import from {Host} failed", request.Source.Host);
            await dialogs.ShowMessage(new MessageRequest("Import from the web", "The item could not be downloaded or imported.", MessageKind.Error, ex.Message));
        }
        finally { _busy = false; }
    }

    private async Task<string> DownloadAsync(UriActivation.ImportRequest request)
    {
        var paths = _services.GetRequiredService<IAppPaths>();
        var folder = Path.Combine(paths.Temp, "uri-import");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Guid.NewGuid().ToString("N") + ExtensionFor(request.Kind));
        using var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, UseCookies = false, UseDefaultCredentials = false };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        using var cts = new CancellationTokenSource(Timeout);
        using var response = await http.GetAsync(request.Source, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        // Never accept a redirect that left HTTPS.
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            throw new ShotwiseException(ErrorCode.AccessDenied, "The download was redirected to an insecure address.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The download is too large.");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, cts.Token)) > 0)
            {
                total += n;
                if (total > MaxDownloadBytes) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The download is too large.");
                await output.WriteAsync(buffer.AsMemory(0, n), cts.Token);
            }
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return target;
    }

    public void Dispose()
    {
        if (_activation is not null) _activation.ImportRequested -= OnImportRequested;
    }
}
