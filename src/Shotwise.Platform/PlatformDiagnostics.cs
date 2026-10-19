using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;

namespace Shotwise.Platform;

/// <summary>Local diagnostics only. No telemetry transport or remote endpoint exists.</summary>
public sealed class PlatformDiagnostics(ISettingsService settings, IPolicyService policy, IAppPaths paths, ILogger<PlatformDiagnostics> log) : IHostedService, IDisposable
{
    private int _started;
    private int _writingDump;

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 0) AppDomain.CurrentDomain.UnhandledException += Unhandled;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { Dispose(); return Task.CompletedTask; }

    /// <summary>Callers supply a stable event identifier only; arbitrary payloads are deliberately unsupported.</summary>
    public void RecordUsage(string eventName)
    {
        if (!policy.IsAllowed(PolicyFeature.Telemetry)) return;
        if (eventName.Length is < 1 or > 64 || eventName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("Use a stable event identifier without personal data.", nameof(eventName));
        log.LogInformation("Local usage event: {Event}", eventName);
    }

    private void Unhandled(object sender, UnhandledExceptionEventArgs args)
    {
        if (!args.IsTerminating || !settings.Current.Advanced.WriteCrashDumps || Interlocked.Exchange(ref _writingDump, 1) != 0) return;
        try
        {
            Directory.CreateDirectory(paths.Logs);
            var filename = Path.Combine(paths.Logs, $"crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.dmp");
            using var file = new FileStream(filename, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId, file.SafeFileHandle, 0, 0, 0, 0))
                log.LogError("Local crash dump failed: {Error}", Marshal.GetLastWin32Error());
            else LogRedaction.Wrap(log).LogCritical("Local crash dump written: {Path}", filename);
        }
        catch (Exception e) { LogRedaction.Wrap(log).LogError(e, "Could not write local crash dump"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) != 0) AppDomain.CurrentDomain.UnhandledException -= Unhandled;
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(nint process, uint processId, SafeFileHandle file, uint type, nint exceptionInfo, nint userStreams, nint callback);
}
