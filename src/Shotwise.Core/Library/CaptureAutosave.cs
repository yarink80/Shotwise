using Shotwise.Core.Documents;

namespace Shotwise.Core.Library;

/// <summary>
/// Single owner of the library autosave of NEW captures (CONTRACT A22/A26). The composition root subscribes to
/// <see cref="Capture.ICaptureEvents"/> and saves each published capture exactly once (respecting LIB-01 preferences);
/// the editor never creates library items when a capture is opened, it asks this service for the item instead.
/// </summary>
public interface ICaptureAutosave
{
    /// <summary>
    /// The library item saved for the published <see cref="Capture.CaptureResult"/> whose metadata instance is
    /// <paramref name="metadata"/> (the coordinator builds the editor document from the same result, so
    /// <c>doc.Metadata</c> is that instance). Completes when the autosave finished; null when the capture was not
    /// published, autosave is off, or it failed.
    /// </summary>
    Task<LibraryItem?> ForImageAsync(DocumentMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// The library item for a published <see cref="Capture.VideoCaptureResult"/> by the path it was published with
    /// (recordings are moved from the temp folder into the library). Null when not published / autosave off / failed.
    /// </summary>
    Task<LibraryItem?> ForVideoAsync(string publishedPath, CancellationToken ct = default);
}
