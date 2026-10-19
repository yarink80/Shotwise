using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using Shotwise.Library;

namespace Shotwise.Library.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shotwise-lib-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(Path, recursive: true); return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}

/// <summary>A library rooted in temp folders; catalog and thumbnails are isolated from the real profile.</summary>
internal sealed class LibraryHarness : IDisposable
{
    private readonly TempDir _dir;
    public LibraryHarness(TempDir? dir = null, bool watcher = false)
    {
        _dir = dir ?? new TempDir();
        OwnsDir = dir is null;
        Root = _dir.Sub("lib");
        CatalogPath = _dir.Sub("local\\library.db");
        ThumbsDir = _dir.Sub("local\\thumbs");
        Service = Create(Root, watcher);
    }

    public bool OwnsDir { get; }
    public string Base => _dir.Path;
    public string Root { get; }
    public string CatalogPath { get; }
    public string ThumbsDir { get; }
    public LibraryService Service { get; private set; }

    public LibraryService Create(string root, bool watcher)
    {
        var s = new LibraryService(root, CatalogPath, ThumbsDir, new ShotxSerializer())
        {
            WatcherEnabled = watcher,
            DebounceMs = 100,
            MirrorDelayMs = 60_000,
        };
        return s;
    }

    /// <summary>Disposes the current service and creates a fresh one over the same folders (simulates an app restart).</summary>
    public LibraryService Restart(bool watcher = false)
    {
        Service.Dispose();
        Service = Create(Root, watcher);
        return Service;
    }

    public void Dispose()
    {
        Service.Dispose();
        if (OwnsDir) _dir.Dispose();
    }
}

internal static class Samples
{
    public static BgraImage Image(int w = 800, int h = 600)
    {
        var img = new BgraImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                img.SetPixel(x, y, Argb.FromRgb((byte)(x * 255 / w), (byte)(y * 255 / h), 90));
        return img;
    }

    public static ShotDocument Doc(string? app = "Browser", string? window = "Home page", string? url = "https://docs.example.com/guide",
        string? ocr = null, string? callout = null, string[]? tags = null, DateTimeOffset? when = null, int w = 800, int h = 600)
    {
        var meta = DocumentMetadata.Create(when ?? DateTimeOffset.Now) with
        {
            AppName = app, WindowTitle = window, Url = url, OcrText = ocr, Tags = tags ?? Array.Empty<string>(),
        };
        var doc = ShotDocument.FromImage(Image(w, h), meta);
        if (callout is not null)
        {
            doc.Pages[0].Annotations.Add(new CalloutAnnotation
            {
                Start = new Point2(10, 10), End = new Point2(200, 80),
                Text = RichText.FromPlain(callout),
            });
        }
        return doc;
    }

    public static byte[] Png(int w = 40, int h = 30) => PngCodec.Encode(Image(w, h));

    public static async Task<bool> WaitUntil(Func<Task<bool>> cond, int timeoutMs = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (await cond()) return true;
            await Task.Delay(100);
        }
        return await cond();
    }
}
