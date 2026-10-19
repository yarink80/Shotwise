using Microsoft.Extensions.Logging;

namespace Shotwise.Media;

internal static class OutputFile
{
    public static string Temporary(string output, string extension)
    {
        string path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{extension}");
    }
    public static void Delete(string path) { if (File.Exists(path)) File.Delete(path); }
    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="source"/>, retrying transient sharing violations
    /// (antivirus, indexer, thumbnailer) with backoff. Never deletes <paramref name="source"/>; the last error is rethrown.
    /// </summary>
    public static void Replace(string source, string destination, Action<string, string>? move = null, int attempts = 5, int initialDelayMs = 50)
    {
        move ??= static (from, to) => File.Move(from, to, true);
        var delay = initialDelayMs;
        for (var attempt = 1; ; attempt++)
        {
            try { move(source, destination); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < attempts && File.Exists(source))
            {
                Thread.Sleep(delay); delay = Math.Min(delay * 2, 1000);
            }
        }
    }
    /// <summary>
    /// Publishes a finalized recording: like <see cref="Replace"/>, but when the move keeps failing the finalized file
    /// is kept where it is and its path returned, because it is the only copy of the recorded content.
    /// </summary>
    public static string PublishOrKeep(string finalized, string destination, ILogger logger, Action<string, string>? move = null)
    {
        try { Replace(finalized, destination, move); return destination; }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(finalized))
        {
            logger.LogWarning(e, "Finalized video could not be moved to {Destination}; it was kept at {Path}", destination, finalized);
            return finalized;
        }
    }
}
