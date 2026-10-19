using System.Text;

namespace Shotwise.Core.Serialization;

/// <summary>Crash-safe file replacement: write to a sibling temp file, flush to disk, then rename over the target.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(contents));

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = TempPathFor(full);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
        => await WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(contents), ct).ConfigureAwait(false);

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = TempPathFor(full);
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await fs.WriteAsync(data, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Writes through a stream callback (large files such as .shotx), then atomically replaces the target.</summary>
    public static async Task WriteWithAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = TempPathFor(full);
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await write(fs, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static string TempPathFor(string full)
        => Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
