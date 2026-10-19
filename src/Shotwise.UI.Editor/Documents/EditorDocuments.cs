using System.Windows.Media;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Editor;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Documents;

/// <summary>One open document (image/template/video/GIF) in the editor; plain data + events, no WPF visuals.</summary>
internal abstract class EditorDocument : IDisposable
{
    private string _title = "";
    private ImageSource? _thumbnail;

    public Guid Id { get; } = Guid.NewGuid();
    public string? FilePath { get; set; }
    public LibraryItem? LibraryItem { get; set; }
    public bool IsPinned { get; set; }
    public abstract bool IsVideo { get; }
    public abstract bool IsDirty { get; }

    public string Title
    {
        get => _title;
        set { _title = value; RaiseChanged(); }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; RaiseChanged(); }
    }

    /// <summary>Raised when title/dirty state/thumbnail changed (tray and window title refresh).</summary>
    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public abstract void Dispose();
}

internal sealed class ImageEditorDocument : EditorDocument
{
    private bool _unsavedNew;

    public ImageEditorDocument(ShotDocument document, EditorContext ctx)
    {
        Document = document;
        Session = new EditorSession(document, ctx.EditorServices);
        FilePath = document.FilePath;
        _unsavedNew = document.FilePath is null;
        Session.Undo.Changed += (_, _) => { LastChangeUtc = DateTime.UtcNow; ThumbnailDirty = true; RaiseChanged(); };
        Session.Invalidated += (_, _) => ThumbnailDirty = true;
        Session.SnappingEnabled = ctx.Prefs.CanvasSnapping;
        Zoom = 1;
        FitToWindow = ctx.Prefs.ZoomToFitOnOpen;
        ShowPixelGrid = ctx.Prefs.ShowPixelGrid;
        Title = TitleFor(document);
    }

    public ShotDocument Document { get; }
    public EditorSession Session { get; }
    public double Zoom { get; set; }
    public bool FitToWindow { get; set; }
    public bool ShowPixelGrid { get; set; }
    public Point2 Pan { get; set; }
    public bool ThumbnailDirty { get; set; } = true;
    public DateTime LastChangeUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Flat format of the last "Save As" (Save re-exports to <see cref="FilePath"/> in this format); null = .shotx.</summary>
    public ImageFormat? ExportFormat { get; set; }
    public EncodeOptions? ExportOptions { get; set; }

    public override bool IsVideo => false;
    public override bool IsDirty => Session.HasUnsavedChanges || _unsavedNew;

    /// <summary>Serializes writes of this document (an older snapshot must never replace a newer one on disk).</summary>
    public SemaphoreSlim SaveGate { get; } = new(1, 1);

    /// <summary>Content revision to capture right before the document is snapshotted for a save (R5-#1).</summary>
    public long Revision => Session.Revision;

    /// <summary>
    /// Records a completed write of the snapshot taken at <paramref name="savedRevision"/> to <paramref name="path"/>.
    /// The document becomes clean only when it did not change after the snapshot; edits made while the write was in flight
    /// keep it dirty so autosave / the close prompt still persist them. Returns true when the document is now clean.
    /// </summary>
    public bool MarkSaved(string? path, long savedRevision)
    {
        if (path is not null)
        {
            FilePath = path;
            Document.FilePath = path;
            Title = Path.GetFileNameWithoutExtension(path);
        }
        _unsavedNew = false;
        var clean = Session.CompleteSave(savedRevision);
        RaiseChanged();
        return clean;
    }

    /// <summary>Marks the current state as saved (e.g. a freshly opened file that was not edited yet).</summary>
    public void MarkSaved(string? path) => MarkSaved(path, Session.Revision);

    public static string TitleFor(ShotDocument doc)
    {
        if (!string.IsNullOrEmpty(doc.FilePath)) return Path.GetFileNameWithoutExtension(doc.FilePath);
        var md = doc.Metadata;
        var stamp = md.CaptureDate.ToLocalTime().ToString("yyyy-MM-dd HH.mm.ss");
        return string.IsNullOrWhiteSpace(md.AppName) ? $"Capture {stamp}" : $"{md.AppName} {stamp}";
    }

    // SaveGate is not disposed: a pending autosave may still be waiting on it (SemaphoreSlim holds no unmanaged state here).
    public override void Dispose() => Session.Dispose();
}

internal sealed class VideoEditorDocument : EditorDocument
{
    public VideoEditorDocument(string path, VideoInfo info, bool isGif, AnimatedImage? gif)
    {
        FilePath = path;
        Info = info;
        IsGif = isGif;
        Gif = gif;
        Edl = new VideoEditList(path, info.Duration);
        // Video edit commands ignore the document; a placeholder keeps UndoStack happy.
        Placeholder = ShotDocument.Create(new Size2(1, 1), Argb.Transparent);
        Undo = new UndoStack(Placeholder);
        Undo.Changed += (_, _) => RaiseChanged();
        Title = Path.GetFileNameWithoutExtension(path);
    }

    public VideoInfo Info { get; }
    public bool IsGif { get; }
    public AnimatedImage? Gif { get; }
    public VideoEditList Edl { get; }
    public UndoStack Undo { get; }
    public ShotDocument Placeholder { get; }

    public override bool IsVideo => true;
    public override bool IsDirty => !Undo.IsClean;

    public void Execute(IEditCommand cmd) => Undo.Execute(cmd, Placeholder);

    public override void Dispose() { }
}
