using System.Text.Json;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Styles;

public enum ToolKind { Select, Arrow, Line, Shape, Callout, Text, Stamp, Step, Pen, Highlighter, Fill, Eraser, Magnify, Blur, Crop, CutOut, Selection, MagicWand, Move, Simplify, GrabText, Cursor, Favorites }

/// <summary>
/// A saved set of tool properties. For annotation tools <see cref="Prototype"/> holds a template annotation whose
/// style fields (stroke, fill, shadow, caps, text style…) are applied to new/selected objects. Non-annotation tools
/// (Fill, Eraser, CutOut, Selection, MagicWand, Crop) store their options in <see cref="Options"/>.
/// </summary>
public sealed class QuickStyle
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ToolKind Tool { get; set; }
    public Annotation? Prototype { get; set; }
    /// <summary>Tool-specific options for tools without an annotation prototype (free-form JSON owned by the editor).</summary>
    public JsonElement? Options { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsBuiltIn { get; set; }

    public QuickStyle Clone() => new()
    {
        Id = Id,
        Name = Name,
        Tool = Tool,
        Prototype = Prototype?.DeepClone(),
        Options = Options,
        IsFavorite = IsFavorite,
        IsBuiltIn = IsBuiltIn,
    };
}

/// <summary>Named collection of quick styles + theme colours + font + shadow flag (<c>.shottheme</c> JSON).</summary>
public sealed class Theme
{
    public const string FileFormat = "shottheme";
    public string Format { get; set; } = FileFormat;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public List<QuickStyle> QuickStyles { get; set; } = new();
    /// <summary>Up to 8 theme colours.</summary>
    public List<Argb> ThemeColors { get; set; } = new();
    public string FontFamily { get; set; } = "Segoe UI";
    public bool DropShadow { get; set; }
    public bool IsDefault { get; set; }
    public bool IsBuiltIn { get; set; }

    public IEnumerable<QuickStyle> StylesFor(ToolKind tool) => QuickStyles.Where(q => q.Tool == tool);
    public IEnumerable<QuickStyle> Favorites => QuickStyles.Where(q => q.IsFavorite);
}

/// <summary>Named colour list (Simplify palettes, colour picker palettes; <c>.shotpalette</c> JSON).</summary>
public sealed class Palette
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<Argb> Colors { get; set; } = new();
    public bool IsBuiltIn { get; set; }
}

public interface IStyleRepository
{
    Task<IReadOnlyList<Theme>> GetThemesAsync();
    Theme ActiveTheme { get; }
    /// <summary>Switches the active theme (persists the choice).</summary>
    Task SetActiveThemeAsync(string themeId);
    Task SaveThemeAsync(Theme theme);
    Task DeleteThemeAsync(string id);
    Task<Theme> ImportAsync(string path);
    Task ExportAsync(Theme theme, string path);
    Task RestoreDefaultsAsync();
    IReadOnlyList<Palette> Palettes { get; }
    Task SavePaletteAsync(Palette palette);
    Task DeletePaletteAsync(string id);
    event EventHandler Changed;
}

public sealed record StampCategory(string Id, string Name, int Count, bool IsUser);

public sealed record StampInfo(string Id, string CategoryId, string Name, IReadOnlyList<string> Keywords, string MediaType, bool IsFavorite, bool IsUser);

public interface IStampCatalog
{
    Task<IReadOnlyList<StampCategory>> GetCategoriesAsync();
    /// <summary>Keyword/name search (localized keywords for <paramref name="uiCulture"/>); null category = all.</summary>
    Task<IReadOnlyList<StampInfo>> SearchAsync(string query, string? category, string uiCulture);
    Task<byte[]> LoadAsync(string stampId);
    /// <summary>Imports a <c>.shotstamps</c> pack (zip with packageinfo.json + svg files).</summary>
    Task ImportPackAsync(string path);
    /// <summary>Adds a single image/svg as a user stamp ("Add to My Stamps").</summary>
    Task<StampInfo> AddUserStampAsync(byte[] data, string mediaType, string name);
    void ToggleFavorite(string stampId);
    event EventHandler? Changed;
}

public interface ITemplateCatalog
{
    Task<IReadOnlyList<TemplateDefinition>> GetTemplatesAsync();
    Task ImportAsync(string path);
    Task ExportAsync(TemplateDefinition template, string path);
    Task DeleteAsync(string templateId);
    event EventHandler? Changed;
}
