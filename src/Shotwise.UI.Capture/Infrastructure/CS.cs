using System.Globalization;
using System.Resources;

namespace Shotwise.UI.Capture;

/// <summary>Localized strings of the capture UI (Resources/CaptureStrings.resx; satellite files per language are optional).</summary>
public static class CS
{
    private static readonly ResourceManager Rm = new("Shotwise.UI.Capture.Resources.CaptureStrings", typeof(CS).Assembly);

    /// <summary>String for the current UI culture; "[key]" when missing so gaps are visible but never crash.</summary>
    public static string Get(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Localized text for a warning key emitted by the recorder (e.g. "Recording_LowDiskSpace"). The shared recorder keys
    /// live in UI.Common <c>Strings.resx</c>; capture-local keys are looked up next; unknown keys are returned verbatim.
    /// </summary>
    public static string Warning(string keyOrMessage)
        => Shotwise.UI.Common.Resources.Strings.ResourceManager.GetString(keyOrMessage, CultureInfo.CurrentUICulture)
           ?? Rm.GetString(keyOrMessage, CultureInfo.CurrentUICulture)
           ?? keyOrMessage;
}
