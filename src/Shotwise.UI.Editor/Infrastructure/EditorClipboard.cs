using System.Runtime.InteropServices;
using System.Windows;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Editor;
using Shotwise.UI.Common.Imaging;
using WpfClipboard = System.Windows.Clipboard;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>
/// System clipboard bridge for editor Cut/Copy/Paste: writes our annotation JSON + bitmap + PNG + text so pasting works
/// inside Shotwise (editable objects) and in other applications (flat image); reads images, text and our payload.
/// </summary>
internal static class EditorClipboard
{
    public static void Write(EditorContext ctx, ClipboardPayload payload)
    {
        var data = new DataObject();
        if (payload.HasAnnotations) data.SetData(ClipboardPayload.ClipboardFormat, payload.ToJson());
        if (payload.Image is { } img)
        {
            var flat = ctx.Prefs.PreventClipboardTransparency ? ImageOps.Flatten(img, Argb.White) : img;
            data.SetImage(flat.ToBitmapSource());
            data.SetData("PNG", new MemoryStream(PngCodec.Encode(img)), false);
        }
        if (!string.IsNullOrEmpty(payload.Text)) data.SetText(payload.Text, TextDataFormat.UnicodeText);
        SetWithRetry(data);
        // Our own copy must not be re-imported by "Listen to clipboard" (CONTRACT A27).
        Shotwise.UI.Common.Clipboard.WpfClipboardWriter.MarkOwnWrite(ctx.Services);
    }

    public static ClipboardPayload? Read(EditorContext ctx)
    {
        IDataObject? d = null;
        for (var attempt = 0; attempt < 8 && d is null; attempt++)
        {
            try { d = WpfClipboard.GetDataObject(); }
            catch (COMException) { Thread.Sleep(40); }
        }
        if (d is null) return null;
        try
        {
            if (d.GetDataPresent(ClipboardPayload.ClipboardFormat) && d.GetData(ClipboardPayload.ClipboardFormat) is string json
                && ClipboardPayload.FromJson(json) is { } own)
                return own;
            if (d.GetDataPresent("PNG") && d.GetData("PNG") is MemoryStream png)
            {
                var bytes = png.ToArray();
                if (bytes.Length > 0) return ClipboardPayload.FromImage(ctx.Codec.Decode(bytes));
            }
            if (d.GetDataPresent(DataFormats.Bitmap) && WpfClipboard.ContainsImage() && WpfClipboard.GetImage() is { } bmp)
                return ClipboardPayload.FromImage(bmp.ToBgraImage());
            if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files)
                    if (ImageFormatExtensions.TryFromExtension(Path.GetExtension(f), out _))
                        return ClipboardPayload.FromImage(ctx.Codec.Load(f));
            }
            if (d.GetDataPresent(DataFormats.UnicodeText) && d.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
                return new ClipboardPayload(Array.Empty<Annotation>(), Array.Empty<EmbeddedAsset>(), Rect2.Empty, null, text);
        }
        catch (Exception ex) when (ex is COMException or ShotwiseException or IOException)
        {
            return null;
        }
        return null;
    }

    /// <summary>True when the clipboard seems to hold something pasteable (cheap check for menu enablement).</summary>
    public static bool CanPaste()
    {
        try
        {
            return WpfClipboard.ContainsImage() || WpfClipboard.ContainsText() || WpfClipboard.ContainsFileDropList()
                || WpfClipboard.ContainsData(ClipboardPayload.ClipboardFormat);
        }
        catch (COMException)
        {
            return true;
        }
    }

    private static void SetWithRetry(DataObject data)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                WpfClipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
            catch (COMException ex)
            {
                throw new ShotwiseException(ErrorCode.ClipboardUnavailable, "The clipboard is in use by another application.", ex);
            }
        }
    }
}
