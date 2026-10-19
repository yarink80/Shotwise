using System.Globalization;
using System.Windows.Markup;
using Shotwise.Core;

namespace Shotwise.UI.Common.Resources;

/// <summary>
/// XAML markup extension for string lookup by key: <c>Text="{res:Loc Tray_Exit}"</c>
/// (xmlns:res="clr-namespace:Shotwise.UI.Common.Resources;assembly=Shotwise.UI.Common").
/// Equivalent to <c>{x:Static res:Strings.Tray_Exit}</c> but tolerant of missing keys (shows "[key]").
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Get(Key);
}

/// <summary>Code-side string lookup (current UI culture, falling back to en-US).</summary>
public static class Localizer
{
    public static string Get(string key) => Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Localized message for an error code (resource key "Error_{Code}").</summary>
    public static string ErrorMessage(ErrorCode code)
        => Strings.ResourceManager.GetString("Error_" + code, CultureInfo.CurrentUICulture) ?? Strings.ResourceManager.GetString("Error_Unknown", CultureInfo.CurrentUICulture) ?? code.ToString();

    /// <summary>Applies a UI language (culture name; empty = keep Windows language) to the current thread and new threads.</summary>
    public static void ApplyLanguage(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return;
        try
        {
            var c = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.DefaultThreadCurrentUICulture = c;
            CultureInfo.CurrentUICulture = c;
        }
        catch (CultureNotFoundException)
        {
            // keep system language
        }
    }
}
