using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Shotwise.UI.Common.Theming;

/// <summary>
/// Shotwise design-token switching (docs/DESIGN-SYSTEM.md). The token dictionary (Themes/Tokens.Light.xaml / Tokens.Dark.xaml, or a
/// generated high-contrast set) lives somewhere in the application's merged dictionaries - normally inside
/// <c>Resources/SharedResources.xaml</c> - and is swapped in place, so every <c>{DynamicResource Shotwise.*}</c> updates live.
/// Also sets the WPF-UI accent to the Shotwise brand accent. Called by <see cref="ThemeService"/>; call it directly only in tools/tests.
/// </summary>
public static class ShotwiseTheme
{
    /// <summary>Marker key present in every token dictionary.</summary>
    public const string MarkerKey = "Shotwise.ThemeName";

    public static readonly Uri LightTokensUri = new("pack://application:,,,/Shotwise.UI.Common;component/Themes/Tokens.Light.xaml", UriKind.Absolute);
    public static readonly Uri DarkTokensUri = new("pack://application:,,,/Shotwise.UI.Common;component/Themes/Tokens.Dark.xaml", UriKind.Absolute);
    /// <summary>The all-in-one dictionary (tokens + metrics + icons + controls).</summary>
    public static readonly Uri ShotwiseDictionaryUri = new("pack://application:,,,/Shotwise.UI.Common;component/Themes/Shotwise.xaml", UriKind.Absolute);

    /// <summary>Brand accent (light theme) #5B4CF5.</summary>
    public static Color BrandAccentLight { get; } = Color.FromRgb(0x5B, 0x4C, 0xF5);
    /// <summary>Brand accent (dark theme) #7C70FF.</summary>
    public static Color BrandAccentDark { get; } = Color.FromRgb(0x7C, 0x70, 0xFF);

    /// <summary>Theme whose tokens are currently applied.</summary>
    public static EffectiveTheme Current { get; private set; } = EffectiveTheme.Light;

    /// <summary>Raised after tokens were swapped.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Swaps the token dictionary inside <paramref name="root"/> (default: <c>Application.Current.Resources</c>) and applies the brand accent
    /// to WPF-UI. If no token dictionary is merged yet, the full Shotwise dictionary is merged (so tools get a working setup).
    /// </summary>
    public static void Apply(EffectiveTheme theme, ResourceDictionary? root = null)
    {
        root ??= Application.Current?.Resources;
        if (root is null) return;
        var tokens = CreateTokens(theme);
        if (!Replace(root, tokens))
        {
            var shotwise = new ResourceDictionary { Source = ShotwiseDictionaryUri };
            root.MergedDictionaries.Add(shotwise);
            Replace(root, tokens);
        }
        Current = theme;
        if (Application.Current is { } app && ReferenceEquals(root, app.Resources)) ApplyAccent(theme, root);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Loads the token dictionary for a theme.</summary>
    public static ResourceDictionary CreateTokens(EffectiveTheme theme) => theme switch
    {
        EffectiveTheme.Dark => new ResourceDictionary { Source = DarkTokensUri },
        EffectiveTheme.HighContrast => CreateHighContrastTokens(),
        _ => new ResourceDictionary { Source = LightTokensUri },
    };

    /// <summary>Points the WPF-UI accent resources at the brand accent and keeps white text on accent fills.</summary>
    public static void ApplyAccent(EffectiveTheme theme, ResourceDictionary? resources = null)
    {
        try
        {
            if (theme == EffectiveTheme.Dark)
                ApplicationAccentColorManager.Apply(BrandAccentDark, Color.FromRgb(0x8F, 0x85, 0xFF), BrandAccentDark, Color.FromRgb(0xAB, 0xA4, 0xFF));
            else if (theme == EffectiveTheme.Light)
                ApplicationAccentColorManager.Apply(BrandAccentLight, BrandAccentLight, Color.FromRgb(0x4A, 0x3B, 0xE0), Color.FromRgb(0x3C, 0x2F, 0xC7));
            else return; // high contrast keeps the system colours
        }
        catch (InvalidOperationException) { return; }
        resources ??= Application.Current?.Resources;
        if (resources is null) return;
        // WPF-UI picks dark text when the accent's HSV value is > 80 %, which our saturated violet is; white reads better (contrast 5.6:1).
        var white = Colors.White;
        resources["TextOnAccentFillColorPrimary"] = white;
        resources["TextOnAccentFillColorSecondary"] = Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);
        resources["TextOnAccentFillColorDisabled"] = Color.FromArgb(0x87, 0xFF, 0xFF, 0xFF);
        resources["TextOnAccentFillColorSelectedText"] = white;
        resources["TextOnAccentFillColorPrimaryBrush"] = Frozen(new SolidColorBrush(white));
        resources["TextOnAccentFillColorSecondaryBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)));
        resources["TextOnAccentFillColorDisabledBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x87, 0xFF, 0xFF, 0xFF)));
        resources["TextOnAccentFillColorSelectedTextBrush"] = Frozen(new SolidColorBrush(white));
    }

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    private static bool Replace(ResourceDictionary parent, ResourceDictionary tokens)
    {
        var list = parent.MergedDictionaries;
        for (var i = 0; i < list.Count; i++)
        {
            // ResourceDictionary.Contains also searches merged dictionaries; only the token dictionary itself owns the marker key.
            if (list[i].Keys.OfType<string>().Contains(MarkerKey)) { list[i] = tokens; return true; }
            if (Replace(list[i], tokens)) return true;
        }
        return false;
    }

    /// <summary>High-contrast tokens built from the Windows system colours (same keys as the XAML token dictionaries).</summary>
    public static ResourceDictionary CreateHighContrastTokens()
    {
        var light = new ResourceDictionary { Source = LightTokensUri };
        var d = new ResourceDictionary();
        Color window = SystemColors.WindowColor, text = SystemColors.WindowTextColor, highlight = SystemColors.HighlightColor,
              highlightText = SystemColors.HighlightTextColor, gray = SystemColors.GrayTextColor, control = SystemColors.ControlColor;
        foreach (var key in light.Keys.OfType<string>())
        {
            var role = Role(key);
            Color c = role switch
            {
                "accent" => highlight, "onaccent" => highlightText, "surface" => window, "raised" => control,
                "border" => text, "text" => text, "muted" => gray, "status" => text, _ => text,
            };
            switch (light[key])
            {
                case Color: d[key] = c; break;
                case SolidColorBrush: d[key] = Frozen(new SolidColorBrush(c)); break;
                case GradientBrush: d[key] = Frozen(new SolidColorBrush(role == "accent" || key.Contains("Capture") ? highlight : c)); break;
                case System.Windows.Media.Effects.Effect: d[key] = new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0 }; break;
                default: d[key] = light[key]; break;
            }
        }
        d[MarkerKey] = "HighContrast";
        return d;

        static string Role(string key)
        {
            if (key.Contains("OnAccent") || key.Contains("SwitchKnob")) return "onaccent";
            if (key.Contains("Subtle") && !key.Contains("Border")) return "surface";
            if (key.Contains("Accent") || key.Contains("Capture") || key.Contains(".Info")) return "accent";
            if (key.Contains("Surface2") || key.Contains("Surface3") || key.Contains("SwitchOffTrack")) return "raised";
            if (key.Contains("Surface") || key.Contains("Canvas") || key.Contains("Subtle") || key.Contains("Hud") || key.Contains("Scrim")) return "surface";
            if (key.Contains("Border")) return "border";
            if (key.Contains("Tertiary") || key.Contains("Disabled")) return "muted";
            return "text";
        }
    }
}
