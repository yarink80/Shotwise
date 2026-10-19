using System.Globalization;
using System.Resources;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>
/// Editor string lookup. Every call site passes the English text as the fallback so the UI stays readable if a satellite
/// resource is missing; the neutral values live in <c>Resources/EditorStrings.resx</c> (translations are added as
/// <c>EditorStrings.&lt;culture&gt;.resx</c>).
/// </summary>
internal static class ES
{
    private static readonly ResourceManager Manager = new("Shotwise.UI.Editor.Resources.EditorStrings", typeof(ES).Assembly);

    public static string T(string key, string english)
    {
        try
        {
            return Manager.GetString(key, CultureInfo.CurrentUICulture) ?? english;
        }
        catch (MissingManifestResourceException)
        {
            return english;
        }
    }

    public static string F(string key, string english, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, T(key, english), args);
}
