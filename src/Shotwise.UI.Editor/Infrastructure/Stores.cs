using System.Text.Json;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Persisted window/panel layout (editor-layout.json).</summary>
internal sealed class EditorLayout
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }
    public bool ShowSidePanel { get; set; } = true;
    public double SideWidth { get; set; } = 320;
    public string SideTab { get; set; } = "Properties";
    public bool ShowTray { get; set; } = true;
    public double TrayHeight { get; set; } = 112;
    /// <summary>Tools not pinned to the top bar (they are in the "More" flyout). New layouts start with <see cref="DefaultHiddenTools"/>.</summary>
    public List<string> HiddenTools { get; set; } = DefaultHiddenTools.ToList();
    public string? UndoWindowPlacement { get; set; }
    /// <summary>Destination used by the main segment of the Share split button (last one used).</summary>
    public string? LastShareDestination { get; set; }
    /// <summary>Expanded/collapsed state of the "Tool Properties" section per tool (missing = tool default: collapsed for Stamp, expanded otherwise).</summary>
    public Dictionary<string, bool> PropsExpanded { get; set; } = new();
    /// <summary>Most recently used stamp ids (newest first).</summary>
    public List<string> RecentStamps { get; set; } = new();

    /// <summary>Secondary tools that start in the "More" flyout (DESIGN-SYSTEM §4: primary set stays on the bar).</summary>
    public static readonly string[] DefaultHiddenTools =
    {
        "Move", "Line", "Pen", "Highlighter", "Fill", "Eraser", "Magnify", "CutOut", "MagicWand", "Simplify", "GrabText", "Cursor",
    };
}

/// <summary>Loads/saves <see cref="EditorLayout"/> (best effort; never throws).</summary>
internal sealed class LayoutStore
{
    private readonly string _path;
    public LayoutStore(string path) => _path = path;

    public EditorLayout Load()
    {
        try
        {
            if (File.Exists(_path)) return ShotwiseJson.Deserialize<EditorLayout>(File.ReadAllText(_path)) ?? new EditorLayout();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new EditorLayout();
    }

    public void Save(EditorLayout layout)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, ShotwiseJson.Serialize(layout, indented: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>User gallery of Quick Effects (named effect combinations), stored as quick-effects.json.</summary>
internal sealed class QuickEffectStore
{
    private readonly string _path;
    private List<QuickEffect>? _items;
    public QuickEffectStore(string path) => _path = path;

    public event EventHandler? Changed;

    public IReadOnlyList<QuickEffect> Items => _items ??= Load();

    private List<QuickEffect> Load()
    {
        try
        {
            if (File.Exists(_path))
                return ShotwiseJson.Deserialize<List<QuickEffect>>(File.ReadAllText(_path)) ?? Defaults();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return Defaults();
    }

    private static List<QuickEffect> Defaults() => new()
    {
        new("builtin.shadow", "Soft shadow", new EffectSpec[] { EffectDefaults.Shadow }),
        new("builtin.torn", "Torn bottom edge", new EffectSpec[] { EffectDefaults.Edge }),
        new("builtin.frame", "Border and shadow", new EffectSpec[] { EffectDefaults.Border, EffectDefaults.Shadow }),
        new("builtin.mono", "Grayscale", new EffectSpec[] { EffectDefaults.Filter }),
        new("builtin.curl", "Page curl", new EffectSpec[] { EffectDefaults.PageCurl }),
    };

    public QuickEffect Add(string name, IReadOnlyList<EffectSpec> effects)
    {
        var q = new QuickEffect(Guid.NewGuid().ToString("N"), name, effects.ToList());
        var list = Items.ToList();
        list.Add(q);
        Commit(list);
        return q;
    }

    public void Remove(string id)
    {
        var list = Items.Where(q => q.Id != id).ToList();
        Commit(list);
    }

    public void Rename(string id, string name)
    {
        var list = Items.Select(q => q.Id == id ? q with { Name = name } : q).ToList();
        Commit(list);
    }

    private void Commit(List<QuickEffect> list)
    {
        _items = list;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, ShotwiseJson.Serialize(list, indented: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
