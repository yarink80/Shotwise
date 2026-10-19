using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
namespace Shotwise.Platform;
public sealed class SingleInstance : ISingleInstance, IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe = "Shotwise." + WindowsIdentity.GetCurrent().User!.Value + "." + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _log;
    private readonly Task? _listener;
    private int _disposed;
    public bool IsFirstInstance { get; }
    public event EventHandler<string[]>? ArgumentsReceived;
    public SingleInstance(ILogger<SingleInstance> log)
    {
        _log = LogRedaction.Wrap(log);
        // Existence holds the lease without thread-affine mutex ownership.
        _mutex = new Mutex(false, @"Local\Shotwise.SingleInstance", out var created);
        IsFirstInstance = created;
        if (!created) _mutex.Dispose();
        if (created) _listener = ListenAsync(_stop.Token);
    }
    public Task<bool> ForwardToFirstInstanceAsync(string[] args) => ForwardToFirstInstanceAsync(args, _stop.Token);
    public async Task<bool> ForwardToFirstInstanceAsync(string[] args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToUtf8Bytes(args, ShotwiseJson.Compact);
            if (data.Length > 65536) throw new ArgumentException("Arguments exceed IPC limit.", nameof(args));
            await pipe.WriteAsync(BitConverter.GetBytes(data.Length), timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(data, timeout.Token).ConfigureAwait(false);
            var ack = new byte[1]; await pipe.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false); return ack[0] == 1;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { _log.LogWarning(e, "Argument forwarding failed"); return false; }
    }
    private async Task ListenAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4]; await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var length = BitConverter.ToInt32(header); if (length is < 2 or > 65536) continue;
                var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                var args = JsonSerializer.Deserialize<string[]>(bytes, ShotwiseJson.Compact);
                if (args is null || args.Any(a => a is null)) continue;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { ArgumentsReceived?.Invoke(this, args); }
                    catch (Exception e) { _log.LogError(e, "Forwarded argument handler failed"); }
                });
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.LogWarning("IPC request failed: {Error}", LogRedaction.Redact(e.ToString()));
                try { await Task.Delay(FailureDelay(failures++), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }
    internal static TimeSpan FailureDelay(int failures) => TimeSpan.FromMilliseconds(Math.Min(30000, 250 * (1 << Math.Clamp(failures, 0, 7))));
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _stop.Cancel(); _listener?.GetAwaiter().GetResult(); _mutex.Dispose(); _stop.Dispose(); }
}
