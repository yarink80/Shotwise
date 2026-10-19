using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Shotwise.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Shotwise.UI.Common.Theming;

public enum EffectiveTheme { Light, Dark, HighContrast }

/// <summary>Applies the WPF-UI Fluent theme (Light / Dark / System) from <see cref="GeneralSettings.Theme"/> and follows Windows changes.</summary>
public interface IThemeService
{
    AppTheme RequestedTheme { get; }
    EffectiveTheme EffectiveTheme { get; }
    /// <summary>Applies the configured theme and starts following settings/system changes. Call once on the UI thread after Application creation.</summary>
    void Initialize();
    /// <summary>Applies a theme immediately (does not persist; use settings for that).</summary>
    void Apply(AppTheme theme);
    event EventHandler? ThemeChanged;
}

public sealed class ThemeService : IThemeService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger<ThemeService> _log;
    private bool _initialized;

    public ThemeService(ISettingsService settings, ILogger<ThemeService>? logger = null)
    {
        _settings = settings;
        _log = logger ?? NullLogger<ThemeService>.Instance;
        RequestedTheme = settings.Current.General.Theme;
    }

    public AppTheme RequestedTheme { get; private set; }
    public EffectiveTheme EffectiveTheme { get; private set; } = EffectiveTheme.Light;
    public event EventHandler? ThemeChanged;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Apply(_settings.Current.General.Theme);
        _settings.Changed += OnSettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Apply(AppTheme theme)
    {
        RequestedTheme = theme;
        var effective = Resolve(theme);
        void ApplyOnUi()
        {
            try
            {
                var appTheme = effective switch
                {
                    EffectiveTheme.Dark => ApplicationTheme.Dark,
                    EffectiveTheme.HighContrast => ApplicationTheme.HighContrast,
                    _ => ApplicationTheme.Light,
                };
                // Brand accent instead of the Windows accent (DESIGN-SYSTEM §2); Shotwise tokens follow the same theme.
                ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, false);
                ShotwiseTheme.Apply(effective);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                _log.LogWarning(ex, "Failed to apply theme {Theme}", effective);
            }
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) ApplyOnUi(); else dispatcher.Invoke(ApplyOnUi);
        var changed = effective != EffectiveTheme;
        EffectiveTheme = effective;
        if (changed) ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Maps the requested theme to light/dark/high-contrast using the current Windows setting for <see cref="AppTheme.System"/>.</summary>
    public static EffectiveTheme Resolve(AppTheme theme)
    {
        if (SystemParameters.HighContrast) return EffectiveTheme.HighContrast;
        return theme switch
        {
            AppTheme.Dark => EffectiveTheme.Dark,
            AppTheme.Light => EffectiveTheme.Light,
            _ => SystemPrefersDark() ? EffectiveTheme.Dark : EffectiveTheme.Light,
        };
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Affects(nameof(AppSettings.General)) && e.Settings.General.Theme != RequestedTheme)
            Apply(e.Settings.General.Theme);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle
            && (RequestedTheme == AppTheme.System || SystemParameters.HighContrast || EffectiveTheme == EffectiveTheme.HighContrast))
            Apply(RequestedTheme);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
