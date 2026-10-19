using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Capture.Controls;
using Shotwise.UI.Capture.Coordinator;
using Shotwise.UI.Capture.Infrastructure;

namespace Shotwise.UI.Capture.Preferences;

/// <summary>
/// A capture preference page contributed to the Preferences window. <see cref="Create"/> builds a fresh control that edits the supplied
/// <see cref="AppSettings"/> object in place (a staged copy owned by the Preferences window, or a live clone via
/// <see cref="CapturePreferencePages.CreateLive"/>) and calls the callback after every edit. <c>Glyph</c> carries the shared-icon key
/// (e.g. <c>Icon.Settings.Regular</c>) of the page.
/// </summary>
public sealed record CapturePreferencePage(string Id, string Title, string Glyph, Func<AppSettings, IServiceProvider?, Action, UserControl> Create);

/// <summary>
/// Static provider of the capture preference pages (General / Capture / Hotkeys). They are registered into the shared Preferences
/// window as UI.Common <c>IPreferencesPage</c>s by <c>AddShotwiseUiCapture()</c> (see <see cref="CapturePreferencesPageContribution"/>).
/// </summary>
public static class CapturePreferencePages
{
    public const string GeneralId = "CaptureGeneral";
    public const string CaptureId = "Capture";
    public const string HotkeysId = "Hotkeys";

    public static IReadOnlyList<CapturePreferencePage> All { get; } = new[]
    {
        new CapturePreferencePage(GeneralId, CS.Get("Prefs_General"), "Icon.Settings.Regular", (s, sp, ch) => new CaptureGeneralPage(s, sp, ch)),
        new CapturePreferencePage(CaptureId, CS.Get("Prefs_Capture"), "Icon.Capture.Regular", (s, sp, ch) => new CaptureOptionsPage(s, sp, ch)),
        new CapturePreferencePage(HotkeysId, CS.Get("Prefs_Hotkeys"), "Icon.Keyboard.Regular", (s, sp, ch) => new CaptureHotkeysPage(s, sp, ch)),
    };

    /// <summary>Localized titles are resolved when the page list is created; call this to refresh them after a language change.</summary>
    public static IReadOnlyList<CapturePreferencePage> Create() => new[]
    {
        All[0] with { Title = CS.Get("Prefs_General") }, All[1] with { Title = CS.Get("Prefs_Capture") }, All[2] with { Title = CS.Get("Prefs_Hotkeys") },
    };

    /// <summary>Creates a page that applies every edit to <paramref name="settings"/> immediately (no staging).</summary>
    public static UserControl CreateLive(CapturePreferencePage page, ISettingsService settings, IServiceProvider? sp)
    {
        var working = Clone(settings.Current);
        Action? apply = null;
        apply = () => settings.Update(s =>
        {
            switch (page.Id)
            {
                case GeneralId: s.General = working.General; s.Updates = working.Updates; break;
                case CaptureId: s.Capture = working.Capture; s.Printer = working.Printer; break;
                case HotkeysId: s.Hotkeys = working.Hotkeys; break;
            }
        });
        return page.Create(working, sp, () => apply!());
    }

    private static AppSettings Clone(AppSettings s)
        => System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(s, Shotwise.Core.Serialization.ShotwiseJson.Compact), Shotwise.Core.Serialization.ShotwiseJson.Compact) ?? new AppSettings();
}

/// <summary>Capture Preferences - General.</summary>
public sealed class CaptureGeneralPage : UserControl
{
    public CaptureGeneralPage(AppSettings s, Action changed) : this(s, null, changed) { }

    public CaptureGeneralPage(AppSettings s, IServiceProvider? sp, Action changed)
    {
        var b = new PrefsBuilder(changed);
        b.Section(CS.Get("Prefs_General"));
        b.Check(CS.Get("Prefs_RunAtStartup"), () => s.General.RunAtWindowsStartup, v => s.General.RunAtWindowsStartup = v);
        b.Check(CS.Get("Prefs_ShowCaptureWindow"), () => s.General.ShowCaptureWindowOnStartup, v => s.General.ShowCaptureWindowOnStartup = v);
        b.Check(CS.Get("Prefs_ShowWidget"), () => s.General.ShowCaptureWidget, v => s.General.ShowCaptureWidget = v);
        b.Choice(CS.Get("Prefs_Theme"), new[] { (AppTheme.System, CS.Get("Theme_System")), (AppTheme.Light, CS.Get("Theme_Light")), (AppTheme.Dark, CS.Get("Theme_Dark")) },
            () => s.General.Theme, v => s.General.Theme = v);

        b.Section(CS.Get("Prefs_Updates"));
        if (sp?.GetService<IUpdateService>() is { } updates) b.Add(new UpdateStatusPanel(updates, () => s.Updates.Channel));
        b.Choice(CS.Get("Prefs_UpdateChannel"), new[] { (UpdateChannel.Stable, UpdateText.Channel(UpdateChannel.Stable)), (UpdateChannel.Beta, UpdateText.Channel(UpdateChannel.Beta)) },
            () => s.Updates.Channel, v => s.Updates.Channel = v, 180);
        b.Check(CS.Get("Prefs_AutoUpdate"), () => s.Updates.AutomaticallyCheck, v => s.Updates.AutomaticallyCheck = v);
        b.Check(CS.Get("Prefs_UpdateDownload"), () => s.Updates.DownloadAutomatically, v => s.Updates.DownloadAutomatically = v);
        b.Check(CS.Get("Prefs_PromptUpdate"), () => s.Updates.PromptBeforeCheck, v => s.Updates.PromptBeforeCheck = v);
        b.Number(CS.Get("Prefs_UpdateDays"), () => s.Updates.DaysBetweenChecks, v => s.Updates.DaysBetweenChecks = v, 1, 365);
        b.Note(CS.Get("Prefs_PrivacyNote"));
        Content = b.Wrap();
    }
}

/// <summary>Capture Preferences - Capture (selection aids, video recording defaults, printer capture).</summary>
public sealed class CaptureOptionsPage : UserControl
{
    private readonly TextBlock _printerStatus = Ui.Label("", 12, secondary: true, wrap: true);

    public CaptureOptionsPage(AppSettings s, IServiceProvider? sp, Action changed)
    {
        var c = s.Capture;
        var b = new PrefsBuilder(changed);
        b.Section(CS.Get("Prefs_Selection"));
        b.Check(CS.Get("Prefs_HideApp"), () => c.HideAppDuringCapture, v => c.HideAppDuringCapture = v);
        b.Check(CS.Get("Prefs_SystemTools"), () => c.OpenScreenshotsFromSystemTools, v => c.OpenScreenshotsFromSystemTools = v);
        b.Check(CS.Get("Prefs_Magnifier"), () => c.ShowMagnifier, v => c.ShowMagnifier = v);
        b.Check(CS.Get("Prefs_SimpleCrosshair"), () => c.SimplifiedCrosshairs, v => c.SimplifiedCrosshairs = v);
        b.Check(CS.Get("Prefs_Guides"), () => c.ShowSelectionGuides, v => c.ShowSelectionGuides = v);
        b.Check(CS.Get("Prefs_HelpHint"), () => c.ShowSelectionHelpHint, v => c.ShowSelectionHelpHint = v);
        b.Check(CS.Get("Prefs_ScrollStart"), () => c.RequireStartButtonForScrolling, v => c.RequireStartButtonForScrolling = v);
        b.Check(CS.Get("Prefs_Hdr"), () => c.SupportHdr, v => c.SupportHdr = v);

        b.Section(CS.Get("Prefs_Video"));
        b.Check(CS.Get("Prefs_AutoStart"), () => c.AutoStartVideoRecording, v => c.AutoStartVideoRecording = v);
        b.Check(CS.Get("Prefs_Countdown"), () => c.ShowVideoCountdown, v => c.ShowVideoCountdown = v);
        b.Number(CS.Get("Prefs_CountdownSeconds"), () => c.CountdownSeconds, v => c.CountdownSeconds = v, 1, 30, CS.Get("Timing_Seconds"));
        b.Check(CS.Get("Prefs_ReadyWindow"), () => c.ShowReadyToRecordWindow, v => c.ShowReadyToRecordWindow = v);
        b.Check(CS.Get("Prefs_MinimizeControls"), () => c.MinimizeControlsWhenFullscreenOneMonitor, v => c.MinimizeControlsWhenFullscreenOneMonitor = v);
        b.Check(CS.Get("Prefs_Scale1080"), () => c.ScaleTo1080p, v => c.ScaleTo1080p = v);
        b.Check(CS.Get("Prefs_Camera"), () => c.EnableCamera, v => c.EnableCamera = v);
        b.Check(CS.Get("Prefs_NoHwAccel"), () => c.DisableHardwareAcceleratedRecording, v => c.DisableHardwareAcceleratedRecording = v);
        b.Check(CS.Get("Prefs_MicAlert"), () => c.AlertWhenMicrophoneMuted, v => c.AlertWhenMicrophoneMuted = v);
        b.Choice(CS.Get("Prefs_Fps"), Shotwise.Core.Media.RecordingOptions.SupportedFps.Select(f => (f, f + " fps")), () => c.VideoFps, v => c.VideoFps = v, 110);
        b.Number(CS.Get("Prefs_Quality"), () => c.VideoQuality, v => c.VideoQuality = v, 1, 100, "%");
        b.Number(CS.Get("Prefs_LowDiskWarn"), () => c.LowDiskWarningMb, v => c.LowDiskWarningMb = v, 100, 1_000_000, "MB");
        b.Number(CS.Get("Prefs_LowDiskStop"), () => c.LowDiskStopMb, v => c.LowDiskStopMb = v, 50, 1_000_000, "MB");
        b.Number(CS.Get("Prefs_DrawFade"), () => c.ScreenDrawFadeSeconds, v => c.ScreenDrawFadeSeconds = v, 0, 600, CS.Get("Timing_Seconds"));
        b.Note(CS.Get("Prefs_FpsNote"));

        b.Section(CS.Get("Prefs_Printer"));
        b.Note(CS.Get("Prefs_PrinterNote"));
        var folder = new TextBox { Text = s.Printer.TempDirectory, MinWidth = 260, MinHeight = 32 }.WithStyle("Shotwise.TextBox");
        folder.TextChanged += (_, _) => { s.Printer.TempDirectory = folder.Text.Trim(); changed(); };
        var browse = Ui.TextButton(CS.Get("Prefs_Browse"), () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = CS.Get("Prefs_PrinterFolder") };
            if (dlg.ShowDialog() == true) folder.Text = dlg.FolderName;
        }, "Icon.Folder.Regular");
        browse.Margin = new Thickness(8, 0, 0, 0);
        var folderRow = Ui.Row(folder, browse);
        b.Add(PrinterRow(CS.Get("Prefs_PrinterFolder"), folderRow));
        var name = new TextBox { Text = s.Printer.PrinterName, MinWidth = 220, MinHeight = 32 }.WithStyle("Shotwise.TextBox");
        name.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) { s.Printer.PrinterName = name.Text.Trim(); changed(); } };
        b.Add(PrinterRow(CS.Get("Prefs_PrinterName"), name));
        b.Check(CS.Get("Prefs_PrinterClose"), () => s.Printer.CloseAfterCaptureIfNotRunning, v => s.Printer.CloseAfterCaptureIfNotRunning = v);

        var printer = sp?.GetService<IPrinterCaptureService>();
        Button? install = null;
        install = Ui.TextButton(printer?.IsInstalled == true ? CS.Get("Prefs_PrinterReinstall") : CS.Get("Prefs_PrinterInstall"), async () => await RunPrinterAsync(printer, s, install: true), "Icon.Print.Regular", accent: false);
        var uninstall = Ui.TextButton(CS.Get("Prefs_PrinterUninstall"), async () => await RunPrinterAsync(printer, s, install: false), "Icon.Delete.Regular");
        install.IsEnabled = uninstall.IsEnabled = printer is not null;
        uninstall.Margin = new Thickness(8, 0, 0, 0);
        var btnRow = Ui.Row(install, uninstall);
        btnRow.Margin = new Thickness(6, 8, 6, 0);
        b.Add(btnRow);
        _printerStatus.Margin = new Thickness(6, 8, 6, 4);
        _printerStatus.Text = printer is null ? CS.Get("Prefs_PrinterUnavailable") : printer.IsInstalled ? CS.Get("Prefs_PrinterInstalled") : CS.Get("Prefs_PrinterNotInstalled");
        b.Add(_printerStatus);
        Content = b.Wrap();
    }

    private static UIElement PrinterRow(string label, UIElement control)
    {
        var col = new StackPanel { Margin = new Thickness(6, 6, 6, 0) };
        var l = Ui.Label(label, 12, secondary: true);
        l.Margin = new Thickness(0, 0, 0, 4);
        col.Children.Add(l);
        col.Children.Add(control);
        return col;
    }

    private async Task RunPrinterAsync(IPrinterCaptureService? printer, AppSettings s, bool install)
    {
        if (printer is null) return;
        _printerStatus.Text = CS.Get("Prefs_PrinterWorking");
        try
        {
            if (install)
            {
                if (!string.IsNullOrWhiteSpace(s.Printer.TempDirectory)) printer.WatchFolder = s.Printer.TempDirectory;
                await printer.InstallAsync(s.Printer.PrinterName);
            }
            else await printer.UninstallAsync();
            _printerStatus.Text = printer.IsInstalled ? CS.Get("Prefs_PrinterInstalled") : CS.Get("Prefs_PrinterNotInstalled");
        }
        catch (Exception ex)
        {
            _printerStatus.Text = ex is ShotwiseException se ? Shotwise.UI.Common.Resources.Localizer.ErrorMessage(se.Code) : ex.Message;
        }
    }
}

