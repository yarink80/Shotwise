namespace Shotwise.Core.Platform;

/// <summary>
/// Default <see cref="IAppPaths"/>. Set environment variable <c>SHOTWISE_DATA_ROOT</c> (or pass a root) to redirect
/// every per-user folder under one directory (portable mode / tests).
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string AppFolderName = "Shotwise";
    public const string DataRootEnvironmentVariable = "SHOTWISE_DATA_ROOT";

    public AppPaths() : this(Environment.GetEnvironmentVariable(DataRootEnvironmentVariable)) { }

    /// <param name="dataRoot">When non-empty, all per-user data lives under this folder.</param>
    public AppPaths(string? dataRoot, string? contentRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            var root = Path.GetFullPath(dataRoot);
            AppData = Path.Combine(root, "Roaming");
            LocalAppData = Path.Combine(root, "Local");
            Temp = Path.Combine(root, "Temp");
            DefaultLibraryRoot = Path.Combine(root, "Library");
            DefaultSaveFolder = Path.Combine(root, "Documents");
        }
        else
        {
            AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
            LocalAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
            Temp = Path.Combine(Path.GetTempPath(), AppFolderName);
            DefaultSaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppFolderName);
            DefaultLibraryRoot = Path.Combine(DefaultSaveFolder, "Library");
        }
        Logs = Path.Combine(LocalAppData, "logs");
        Models = Path.Combine(LocalAppData, "models");
        Content = contentRoot ?? Path.Combine(AppContext.BaseDirectory, "Content");
    }

    public string AppData { get; }
    public string LocalAppData { get; }
    public string Logs { get; }
    public string Temp { get; }
    public string Models { get; }
    public string Content { get; }
    public string DefaultLibraryRoot { get; }
    public string DefaultSaveFolder { get; }

    public string SettingsFile => Path.Combine(AppData, "settings.json");
    public string PresetsFile => Path.Combine(AppData, "presets.json");
    public string ThemesFolder => Path.Combine(AppData, "themes");
    public string PalettesFolder => Path.Combine(AppData, "palettes");
    public string UserStampsFolder => Path.Combine(AppData, "stamps");
    public string UserTemplatesFolder => Path.Combine(AppData, "templates");
    public string ShareSettingsFolder => Path.Combine(AppData, "share");
    public string CatalogFile => Path.Combine(LocalAppData, "library.db");
    public string ThumbnailsFolder => Path.Combine(LocalAppData, "thumbnails");

    /// <summary>Creates the per-user folders (idempotent).</summary>
    public void EnsureCreated()
    {
        foreach (var d in new[] { AppData, LocalAppData, Logs, Temp, Models })
            Directory.CreateDirectory(d);
    }
}

public static class AppPathsExtensions
{
    public static string SettingsFile(this IAppPaths p) => Path.Combine(p.AppData, "settings.json");
    public static string PresetsFile(this IAppPaths p) => Path.Combine(p.AppData, "presets.json");
}
