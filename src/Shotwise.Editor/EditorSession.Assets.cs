using Microsoft.Extensions.Logging;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;

namespace Shotwise.Editor;

// Lifetime of full-resolution base rasters produced by editing (R5-#4).
public sealed partial class EditorSession
{
    private readonly HashSet<AssetId> _editorRasters = new();

    /// <summary>
    /// Stores a base raster produced by an edit (raster tool, effect, crop, cut-out) and remembers it as editor-owned when it
    /// is new to the store. Editor-owned rasters are only ever referenced by page backgrounds and by asset-retaining commands
    /// (<c>ReplaceBaseImageCommand</c>, <c>ApplyEffectCommand</c>, <c>CropCommand</c>, <c>CutOutCommand</c>), so once their
    /// history commands were dropped (redo branch discarded, history depth exceeded) and no page uses them they can never be
    /// reached again and are removed from the document store (bytes + decoded cache).
    /// </summary>
    internal AssetId AddEditorRaster(BgraImage image)
    {
        var before = Document.Assets.Count;
        var id = Document.Assets.AddImage(image);
        if (Document.Assets.Count > before) _editorRasters.Add(id);
        return id;
    }

    /// <summary>Number of editor-owned rasters still in the document store (diagnostics/tests).</summary>
    internal int EditorRasterCount => _editorRasters.Count;

    /// <summary>
    /// Removes editor-owned rasters that no history command retains and no page references. A raster that is ever seen
    /// referenced by an annotation (identical bytes are deduplicated by the store) is no longer considered editor-owned,
    /// because annotation snapshots inside non-retaining commands could still need it.
    /// </summary>
    private void PruneReleasedRasters()
    {
        if (_editorRasters.Count == 0) return;
        var store = Document.Assets;
        var backgrounds = new HashSet<AssetId>();
        foreach (var page in Document.Pages)
        {
            if (page.Background.BaseImage is { } bi) backgrounds.Add(bi.Asset);
            foreach (var a in page.AllAnnotations())
                foreach (var r in ReferencedAssets(a))
                    _editorRasters.Remove(r); // shared with an annotation: never prune it
        }
        List<AssetId>? candidates = null;
        foreach (var id in _editorRasters)
            if (store.GetRefCount(id) <= 0 && !backgrounds.Contains(id)) (candidates ??= new()).Add(id);
        if (candidates is null) return;
        var removed = 0;
        foreach (var id in candidates)
        {
            if (store.Remove(id)) removed++;
            _editorRasters.Remove(id);
        }
        if (removed > 0) _log.LogDebug("Released {Count} unreachable base raster(s)", removed);
    }
}
