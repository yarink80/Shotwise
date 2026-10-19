using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Core.Styles;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Imaging;
using Button = System.Windows.Controls.Button;

namespace Shotwise.UI.Dialogs;

/// <summary>Cultures the application ships resources for (Preferences → General → Language, spell-check language).</summary>
internal static class ShippedLanguages
{
    /// <summary>Culture code ("" = follow Windows) and its native display name.</summary>
    public static IReadOnlyList<(string Code, string Name)> UiLanguages { get; } = new (string, string)[]
    {
        ("", ""), ("en-US", "English (United States)"), ("de-DE", "Deutsch (Deutschland)"), ("es-ES", "Español (España)"),
        ("fr-FR", "Français (France)"), ("ja-JP", "日本語 (日本)"), ("pt-BR", "Português (Brasil)"),
    };

    /// <summary>Languages the WPF spell checker has dictionaries for.</summary>
    public static IReadOnlyList<(string Code, string Name)> SpellCheckLanguages { get; } = new (string, string)[]
    {
        ("", ""), ("en-US", "English (United States)"), ("de-DE", "Deutsch (Deutschland)"), ("es-ES", "Español (España)"), ("fr-FR", "Français (France)"),
    };

    /// <summary>The list entry that matches a stored culture name (exact, then same two-letter language); unknown names map to "system default".</summary>
    public static string Normalize(IReadOnlyList<(string Code, string Name)> list, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        var exact = list.FirstOrDefault(l => string.Equals(l.Code, stored, StringComparison.OrdinalIgnoreCase));
        if (exact.Code is not null && exact.Code.Length > 0) return exact.Code;
        var two = stored.Split('-')[0];
        var sameLanguage = list.FirstOrDefault(l => l.Code.Length > 0 && l.Code.StartsWith(two + "-", StringComparison.OrdinalIgnoreCase));
        return sameLanguage.Code ?? "";
    }

    public static string DisplayName(IReadOnlyList<(string Code, string Name)> list, string code, string systemDefaultText)
        => code.Length == 0 ? systemDefaultText : list.FirstOrDefault(l => l.Code == code).Name ?? code;
}

public sealed partial class DialogService
{
    private static readonly string[] DragDropFormats = { "png", "jpg", "gif", "bmp", "tiff", "webp" };
    private static readonly string[] LogLevels = { "Trace", "Debug", "Information", "Warning", "Error", "Critical" };

    public async Task ShowPreferences(PreferencesRequest r)
    {
        var copy = Clone(settings.Current); var w = new FormWindow("Preferences");
        w.Body.Width = 840; w.Body.Margin = new Thickness(24, 8, 24, 0);
        var nav = new ListBox { Width = 180, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 16, 0) };
        AutomationSetName(nav, Texts.Get("Preferences"));
        var content = new ContentControl { Width = PageBuilder.PageWidth + 40 };
        var row = new DockPanel { Height = Math.Clamp(SystemParameters.WorkArea.Height - 250, 380, 740) };
        var rail = new StackPanel { Width = 180, Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(rail, Dock.Left); row.Children.Add(rail); row.Children.Add(nav); row.Children.Add(content); w.Body.Children.Add(row);
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var progressText = new TextBlock { Opacity = 0.75 };
        w.Body.Children.Add(progress); w.Body.Children.Add(progressText);
        var reporter = new Progress<double>(v => { progress.Visibility = Visibility.Visible; progress.Value = Math.Clamp(v, 0, 1); progressText.Text = $"{v:P0}"; });
        void Fail(Exception ex) => w.Model.Error = ex.GetBaseException() is Shotwise.Core.ShotwiseException f ? Shotwise.UI.Common.Resources.Localizer.ErrorMessage(f.Code) : ex.GetBaseException().Message;
        void Changed() { }
        PageBuilder NewPage() => new(Changed, Fail);
        var originalLanguage = copy.General.Language;
        // Advanced → "Desktop shortcut" mirrors the file itself (no setting): null = page not opened / unchanged.
        var desktopChoice = new System.Runtime.CompilerServices.StrongBox<bool?>();

        var factories = new Dictionary<string, Func<FrameworkElement>>(); var titles = new Dictionary<string, string>(); var order = new Dictionary<string, int>(); var built = new Dictionary<string, FrameworkElement>();
        void Register(string id, int sort, Func<FrameworkElement> factory) { factories[id] = factory; titles[id] = Texts.Get(id); order[id] = sort; }
        Register("General", 0, () => BuildGeneralPage(copy, NewPage(), originalLanguage));
        Register("Editor", 300, () => BuildEditorPage(copy, NewPage()));
        Register("Library", 400, () => BuildLibraryPage(copy, NewPage(), w, reporter));
        Register("Models", 450, () => BuildModelsPage(NewPage(), w));
        Register("Share", 500, () => BuildSharePage(copy, NewPage(), w));
        Register("Advanced", 600, () => BuildAdvancedPage(copy, NewPage(), w, desktopChoice));
        Register("Experiments", 700, () => BuildExperimentsPage(copy, NewPage()));

        // Contributed pages (CONTRACT A6; UI.Capture registers CaptureGeneral/Capture/Hotkeys) edit one shared staged copy.
        // A contributed page replaces a built-in page with the same id; on OK only the leaves the contributed pages changed are merged.
        var original = Clone(settings.Current); var contributed = Clone(settings.Current); bool contributedChanged = false;
        var contributedCommits = new List<Func<Task>>();
        var context = new Shotwise.UI.Common.Preferences.PreferencesPageContext(contributed, services, () => contributedChanged = true, contributedCommits.Add);
        foreach (var page in services.GetServices<Shotwise.UI.Common.Preferences.IPreferencesPage>())
        {
            var p = page; factories[p.Id] = () => p.CreatePage(context); titles[p.Id] = p.Title; order[p.Id] = p.Order;
        }
        var navItems = factories.Keys.OrderBy(k => order.GetValueOrDefault(k, int.MaxValue)).Select(k => new KeyValuePair<string, string>(k, titles.GetValueOrDefault(k, k))).ToList();
        nav.ItemsSource = navItems; nav.DisplayMemberPath = "Value"; nav.SelectedValuePath = "Key";
        var rails = new Dictionary<string, NavItem>();
        foreach (var item in navItems)
        {
            var iconName = item.Key switch { "General" => "Settings", "CaptureGeneral" => "History", "Capture" => "Capture", "Hotkeys" => "Keyboard", "Editor" => "Editor", "Library" => "Library", "Models" => "Apps", "Share" => "Share", "Experiments" => "Lightbulb", _ => "Properties" };
            var entry = new NavItem { Content = item.Value, Icon = "Icon." + iconName + ".Regular", GroupName = "Preferences", Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
            entry.SetResourceReference(FrameworkElement.StyleProperty, "Shotwise.NavListItem");
            entry.Checked += (_, _) => nav.SelectedValue = item.Key;
            rails[item.Key] = entry; rail.Children.Add(entry);
        }
        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedValue is not string id) return;
            rails[id].IsChecked = true;
            if (!built.TryGetValue(id, out var page)) built[id] = page = factories[id]();
            content.Content = page;
        };
        var initial = r.InitialPage.ToString();
        nav.SelectedValue = factories.ContainsKey(initial) ? initial : initial == "General" && factories.ContainsKey("CaptureGeneral") ? "CaptureGeneral" : navItems.First().Key;

        w.Commit = async () =>
        {
            if (contributedChanged) copy = MergeChangedLeaves(original, contributed, copy);
            if (copy.Library.MaxTrayItems is < 1 or > 200) throw new ArgumentException(Texts.Get("InvalidTrayCount"));
            if (copy.Experiments.Enabled && !copy.Experiments.AgreedToTerms) throw new ArgumentException(Texts.Get("AgreementRequired"));
            copy.Normalize();
            settings.Update(s =>
            {
                // Runtime state owned by other components stays as it is now (it may have changed while this window was open).
                copy.Share.ClientIds = new Dictionary<string, string>(s.Share.ClientIds); copy.Share.LoopbackPorts = new Dictionary<string, int>(s.Share.LoopbackPorts);
                copy.Share.Naming.NextCounter = s.Share.Naming.NextCounter; copy.Share.SaveAs.LastFolder = s.Share.SaveAs.LastFolder;
                copy.Editor.ActiveThemeId = s.Editor.ActiveThemeId; copy.Editor.LastOpenFolder = s.Editor.LastOpenFolder; copy.Editor.ToolbarTools = s.Editor.ToolbarTools;
                copy.Library.PinnedItems = s.Library.PinnedItems; copy.Library.LibraryRoot = s.Library.LibraryRoot;
                copy.Updates.LastCheck = s.Updates.LastCheck;
                copy.Hotkeys.PrintScreenConflictPrompted |= s.Hotkeys.PrintScreenConflictPrompted;
                copy.Hotkeys.EffectiveGlobalCapture = s.Hotkeys.EffectiveGlobalCapture; copy.Hotkeys.FallbackNoticeShown |= s.Hotkeys.FallbackNoticeShown;
                s.General = copy.General; s.Capture = copy.Capture; s.Hotkeys = copy.Hotkeys; s.Editor = copy.Editor; s.Library = copy.Library; s.Share = copy.Share;
                s.Advanced = copy.Advanced; s.Experiments = copy.Experiments; s.Printer = copy.Printer; s.Mobile = copy.Mobile; s.Updates = copy.Updates; s.Privacy = copy.Privacy;
            });
            if (copy.General.Language != originalLanguage) Shotwise.UI.Common.Resources.Localizer.ApplyLanguage(copy.General.Language);
            services.GetService<IAutostartService>()?.SetEnabled(copy.General.RunAtWindowsStartup);
            // Advanced → "Show Shotwise in the Start menu": add or remove the per-user shortcut right away.
            if (copy.Advanced.ShowInStartMenu != original.Advanced.ShowInStartMenu && services.GetService<IStartMenuShortcut>() is { } startMenu && Environment.ProcessPath is { } exe)
            {
                if (copy.Advanced.ShowInStartMenu) startMenu.Ensure(exe); else startMenu.Remove();
            }
            if (desktopChoice.Value is { } wantDesktop && services.GetService<IDesktopShortcut>() is { } desktop && Environment.ProcessPath is { } exePath && wantDesktop != desktop.Exists)
            {
                if (wantDesktop) desktop.Ensure(exePath); else desktop.Remove();
            }
            // Contributed pages that stage data outside AppSettings (e.g. preset hotkeys) apply it now; hotkeys re-register on change.
            foreach (var commit in contributedCommits) await commit();
        };
        await w.ShowDialogAsync(r.Owner);
    }

    private static void AutomationSetName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    // ---------------------------------------------------------------- General

    private FrameworkElement BuildGeneralPage(AppSettings s, PageBuilder b, string originalLanguage)
    {
        var g = s.General;
        b.Section("P_Interface");
        b.Choice("Theme", new[] { AppTheme.Light, AppTheme.Dark, AppTheme.System }, () => g.Theme, v => g.Theme = v);
        var languages = ShippedLanguages.UiLanguages.Select(l => l.Code).ToList();
        TextBlock? restart = null;
        b.Choice("Language", languages, () => ShippedLanguages.Normalize(ShippedLanguages.UiLanguages, g.Language),
            v => { g.Language = v; if (restart is not null) restart.Visibility = v != ShippedLanguages.Normalize(ShippedLanguages.UiLanguages, originalLanguage) ? Visibility.Visible : Visibility.Collapsed; },
            code => ShippedLanguages.DisplayName(ShippedLanguages.UiLanguages, code, Texts.Get("P_SystemDefault")), 300);
        restart = b.Note("P_LanguageRestart"); restart.Visibility = Visibility.Collapsed;

        b.Section("P_Notifications");
        b.Check("ShowOnboarding", () => g.ShowOnboarding, v => g.ShowOnboarding = v);
        b.Choice("Tooltips", Enum.GetValues<TooltipDetail>(), () => g.Tooltips, v => g.Tooltips = v);
        b.Choice("HelpNotifications", Enum.GetValues<HelpNotificationMode>(), () => g.HelpNotifications, v => g.HelpNotifications = v);
        b.Check("ShowToasts", () => g.ShowToasts, v => g.ShowToasts = v);
        b.Check("StayInTray", () => g.StayInTray, v => g.StayInTray = v);

        b.Section("Privacy");
        // Only one real switch exists: nothing is sent unless the user opts in (the Anonymous / Full split has no backend).
        b.Check("P_UsageData", () => s.Privacy.UsageData != UsageDataMode.Off, v => s.Privacy.UsageData = !v ? UsageDataMode.Off : s.Privacy.UsageData == UsageDataMode.Full ? UsageDataMode.Full : UsageDataMode.Anonymous);
        b.Note("P_UsageDataNote");
        b.Check("StripMetadataOnShare", () => s.Privacy.StripMetadataOnShare, v => s.Privacy.StripMetadataOnShare = v);
        return b.Wrap();
    }

    // ---------------------------------------------------------------- Editor

    private FrameworkElement BuildEditorPage(AppSettings s, PageBuilder b)
    {
        var e = s.Editor;
        b.Section("P_Workspace");
        FrameworkElement? customRow = null;
        b.Choice("WorkspaceColor", Enum.GetValues<WorkspaceColor>(), () => e.WorkspaceColor, v => { e.WorkspaceColor = v; if (customRow is not null) customRow.Visibility = v == WorkspaceColor.Custom ? Visibility.Visible : Visibility.Collapsed; });
        var customPicker = new ColorPicker(e.CustomWorkspaceColor, false, Texts.Get("CustomWorkspaceColor")) { MyColors = e.MyColors };
        customPicker.ColorChanged += c => e.CustomWorkspaceColor = c;
        b.Row("CustomWorkspaceColor", customPicker); customRow = b.LastRow!;
        customRow.Visibility = e.WorkspaceColor == WorkspaceColor.Custom ? Visibility.Visible : Visibility.Collapsed;
        b.Choice("RecentThumbnailSize", Enum.GetValues<ThumbnailSize>(), () => e.RecentThumbnailSize, v => e.RecentThumbnailSize = v);
        b.Check("ShowToolbarLabels", () => e.ShowToolbarLabels, v => e.ShowToolbarLabels = v);
        b.Check("ShowCursorQuickStylePopup", () => e.ShowCursorQuickStylePopup, v => e.ShowCursorQuickStylePopup = v);
        b.Check("PreviewRecentCaptures", () => e.PreviewRecentCaptures, v => e.PreviewRecentCaptures = v);
        b.Check("ZoomToFitOnOpen", () => e.ZoomToFitOnOpen, v => e.ZoomToFitOnOpen = v);
        b.Check("ShowPixelGrid", () => e.ShowPixelGrid, v => e.ShowPixelGrid = v);

        b.Section("P_Editing");
        b.Check("CanvasSnapping", () => e.CanvasSnapping, v => e.CanvasSnapping = v);
        b.Check("AutoExpandCanvas", () => e.AutoExpandCanvas, v => e.AutoExpandCanvas = v);
        b.Check("KeepOriginalImageDimensions", () => e.KeepOriginalImageDimensions, v => e.KeepOriginalImageDimensions = v);
        b.Check("DeleteCroppedContent", () => e.DeleteCroppedContent, v => e.DeleteCroppedContent = v);
        b.Check("AutoFlattenPastedImages", () => e.AutoFlattenPastedImages, v => e.AutoFlattenPastedImages = v);
        b.Check("EscapeClosesEditor", () => e.EscapeClosesEditor, v => e.EscapeClosesEditor = v);
        b.Check("KeepEditorRunningInBackground", () => e.KeepEditorRunningInBackground, v => e.KeepEditorRunningInBackground = v);

        b.Section("P_Text");
        var spellLanguage = default(FrameworkElement);
        b.Check("EnableSpellCheck", () => e.EnableSpellCheck, v => { e.EnableSpellCheck = v; if (spellLanguage is not null) spellLanguage.IsEnabled = v; });
        var spell = b.Choice("SpellCheckLanguage", ShippedLanguages.SpellCheckLanguages.Select(l => l.Code).ToList(), () => ShippedLanguages.Normalize(ShippedLanguages.SpellCheckLanguages, e.SpellCheckLanguage),
            v => e.SpellCheckLanguage = v, code => ShippedLanguages.DisplayName(ShippedLanguages.SpellCheckLanguages, code, Texts.Get("P_SameAsInterface")));
        spellLanguage = spell; spell.IsEnabled = e.EnableSpellCheck;
        b.Check("BackgroundOcr", () => e.BackgroundOcr, v => e.BackgroundOcr = v);
        e.OcrEngine = OcrEngineKind.Windows; // the only engine that exists; Tesseract is not offered.

        b.Section("P_Clipboard");
        b.Check("AddHtmlToClipboard", () => e.AddHtmlToClipboard, v => e.AddHtmlToClipboard = v);
        b.Check("PreventClipboardTransparency", () => e.PreventClipboardTransparency, v => e.PreventClipboardTransparency = v);
        b.Check("EmbedEmailImages", () => e.EmbedEmailImages, v => e.EmbedEmailImages = v);
        b.Choice("DragAndDropFileType", DragDropFormats, () => DragDropFormats.Contains(e.DragAndDropFileType) ? e.DragAndDropFileType : "png", v => e.DragAndDropFileType = v, v => v.ToUpperInvariant(), 120);

        b.Section("MyColors");
        b.Note("P_MyColorsNote");
        AddMyColors(e, b);
        return b.Wrap();
    }

    private void AddMyColors(EditorSettings e, PageBuilder b)
    {
        var swatches = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        void Refresh()
        {
            swatches.Children.Clear();
            foreach (var color in e.MyColors.ToList())
            {
                var c = color;
                var button = new Button { Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), MinWidth = 0, MinHeight = 0, ToolTip = Texts.Format("P_RemoveColor", c.ToRgbHex()), Content = new Border { Background = c.ToBrush(), Width = 20, Height = 20, CornerRadius = new CornerRadius(3) } };
                AutomationSetName(button, Texts.Format("P_RemoveColor", c.ToRgbHex()));
                button.Click += (_, _) => { e.MyColors.Remove(c); Refresh(); };
                swatches.Children.Add(button);
            }
        }
        Refresh(); b.Add(swatches);
        var picker = new ColorPicker(Argb.Red, true, Texts.Get("AddColor")) { MyColors = e.MyColors };
        picker.ColorChanged += c => { if (!e.MyColors.Contains(c)) { e.MyColors.Add(c); Refresh(); } };
        b.Row("AddColor", picker);
    }

    // ---------------------------------------------------------------- Library

    private FrameworkElement BuildLibraryPage(AppSettings s, PageBuilder b, FormWindow w, IProgress<double> reporter)
    {
        var l = s.Library;
        b.Section("P_Saving");
        b.Check("AutosaveImages", () => l.AutosaveImages, v => l.AutosaveImages = v);
        b.Check("AutosaveVideos", () => l.AutosaveVideos, v => l.AutosaveVideos = v);
        b.Check("PromptBeforeDelete", () => l.PromptBeforeDelete, v => l.PromptBeforeDelete = v);
        b.Check("ShowImportPastCapturesPrompt", () => l.ShowImportPastCapturesPrompt, v => l.ShowImportPastCapturesPrompt = v);
        b.Section("P_Tray");
        b.Number("MaxTrayItems", () => l.MaxTrayItems, v => l.MaxTrayItems = v, 1, LibrarySettings.MaxTrayItemsLimit);
        b.Check("RemoveUnpinnedFromTrayOnClose", () => l.RemoveUnpinnedFromTrayOnClose, v => l.RemoveUnpinnedFromTrayOnClose = v);

        b.Section("P_LibraryFolder");
        var rootNow = settings.Current.Library.LibraryRoot;
        var root = new TextBlock { Text = string.IsNullOrWhiteSpace(rootNow) ? (services.GetService<ILibraryService>()?.LibraryRoot ?? "") : rootNow, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        b.Add(root);
        var actions = b.ButtonRow();
        b.Action("LibraryLocation", async () =>
        {
            var library = Need<ILibraryService>();
            var location = await ShowLibraryLocation(new LibraryLocationRequest(library.LibraryRoot) { Owner = w }); if (location is null) return;
            if (location.Action == LibraryLocationAction.UseWithoutMoving) settings.Update(st => st.Library.LibraryRoot = location.NewRoot);
            else await library.MoveLibraryAsync(location.NewRoot, reporter, w.Lifetime.Token);
            root.Text = location.NewRoot;
        }, actions);

        b.Section("P_BackupSection");
        var backup = b.ButtonRow();
        b.Action("Backup", async () => { var d = new Microsoft.Win32.SaveFileDialog { Filter = "Shotwise archive|*.shotarchive" }; if (d.ShowDialog(w) == true) await Need<ILibraryService>().BackupAsync(d.FileName, reporter, w.Lifetime.Token); }, backup);
        b.Action("RestoreBackup", async () => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Shotwise archive|*.shotarchive" }; if (d.ShowDialog(w) == true && (await Confirm(new ConfirmRequest(Texts.Get("RestoreBackup"), d.FileName) { Owner = w })).Confirmed) await Need<ILibraryService>().RestoreAsync(d.FileName, reporter, w.Lifetime.Token); }, backup);
        return b.Wrap();
    }

    // ---------------------------------------------------------------- Share

    private FrameworkElement BuildSharePage(AppSettings s, PageBuilder b, FormWindow w)
    {
        var sh = s.Share;
        b.Section("P_Behaviour");
        b.Check("CopyLinkToClipboard", () => sh.CopyLinkToClipboard, v => sh.CopyLinkToClipboard = v);
        b.Check("DoNotIncludeCaptureSourceInNames", () => sh.DoNotIncludeCaptureSourceInNames, v => sh.DoNotIncludeCaptureSourceInNames = v);

        b.Section("P_SavingFiles");
        var folderRow = default(FrameworkElement);
        b.Choice("FolderMode", Enum.GetValues<SaveFolderMode>(), () => sh.SaveAs.FolderMode, v => { sh.SaveAs.FolderMode = v; if (folderRow is not null) folderRow.Visibility = v == SaveFolderMode.Fixed ? Visibility.Visible : Visibility.Collapsed; });
        b.Text("FixedFolder", () => sh.SaveAs.FixedFolder, v => sh.SaveAs.FixedFolder = v, 280);
        folderRow = b.LastRow!; folderRow.Visibility = sh.SaveAs.FolderMode == SaveFolderMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        b.Choice("NameMode", Enum.GetValues<SaveNameMode>(), () => sh.SaveAs.NameMode, v => sh.SaveAs.NameMode = v);
        b.Check("RememberLastFormat", () => sh.SaveAs.RememberLastFormat, v => sh.SaveAs.RememberLastFormat = v);
        b.Choice("DefaultFormat", DragDropFormats.Concat(new[] { "shotx" }).ToList(), () => sh.SaveAs.DefaultFormat, v => sh.SaveAs.DefaultFormat = v, v => v.ToUpperInvariant(), 120);
        b.Number("JpegQuality", () => sh.SaveAs.JpegQuality, v => sh.SaveAs.JpegQuality = v, 1, 100);
        var naming = b.ButtonRow();
        b.Action("AutomaticNaming", async () => { var result = await ShowAutoNaming(new AutoNamingRequest(sh.Naming) { Owner = w }); if (result is not null) sh.Naming = result.Settings; }, naming);

        b.Section("P_Destinations");
        b.Note("P_DestinationsNote");
        foreach (var destination in services.GetService<IShareService>()?.Destinations ?? Array.Empty<IShareDestination>())
        {
            var d = destination;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var enabled = new ToggleSwitchRow { Content = d.DisplayName, IsChecked = !sh.DisabledDestinations.Contains(d.Id), VerticalAlignment = VerticalAlignment.Center };
            enabled.Checked += (_, _) => sh.DisabledDestinations.Remove(d.Id);
            enabled.Unchecked += (_, _) => { if (!sh.DisabledDestinations.Contains(d.Id)) sh.DisabledDestinations.Add(d.Id); };
            AutomationSetName(enabled, d.DisplayName);
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, Text = d.RequiresAccount ? Texts.Get(d.IsConfigured ? "Configured" : "NotConfigured") : "" };
            Grid.SetColumn(status, 1);
            row.Children.Add(enabled); row.Children.Add(status);
            if (d.CanShowOptions)
            {
                var options = new Button { Content = Texts.Get("P_Options"), MinWidth = 100, Margin = new Thickness(4, 0, 0, 0) };
                options.SetResourceReference(FrameworkElement.StyleProperty, "Shotwise.SecondaryButton");
                Grid.SetColumn(options, 2); AutomationSetName(options, Texts.Format("P_OptionsFor", d.DisplayName));
                options.Click += async (_, _) =>
                {
                    options.IsEnabled = false;
                    try { await ShowDestinationOptions(new DestinationOptionsRequest(d.Id) { Owner = w }); if (d.RequiresAccount) status.Text = Texts.Get(d.IsConfigured ? "Configured" : "NotConfigured"); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { w.Model.Error = ex.GetBaseException().Message; }
                    finally { options.IsEnabled = true; }
                };
                row.Children.Add(options);
            }
            b.Add(row);
        }
        var accounts = b.ButtonRow();
        b.Action("Accounts", () => ShowShareAccounts(new ShareAccountsRequest { Owner = w }), accounts);
        return b.Wrap();
    }

    // ---------------------------------------------------------------- Advanced

    private FrameworkElement BuildAdvancedPage(AppSettings s, PageBuilder b, FormWindow w, System.Runtime.CompilerServices.StrongBox<bool?> desktopChoice)
    {
        var a = s.Advanced;
        b.Section("P_Performance");
        b.Check("HardwareAcceleratedRendering", () => a.HardwareAcceleratedRendering, v => a.HardwareAcceleratedRendering = v);
        b.Check("CleanTempOnStartup", () => a.CleanTempOnStartup, v => a.CleanTempOnStartup = v);

        b.Section("P_Diagnostics");
        b.Choice("LogLevel", LogLevels, () => LogLevels.Contains(a.LogLevel) ? a.LogLevel : "Information", v => a.LogLevel = v, v => v, 160);
        b.Number("MaxLogFiles", () => a.MaxLogFiles, v => a.MaxLogFiles = v, 1, 1000);
        b.Check("WriteCrashDumps", () => a.WriteCrashDumps, v => a.WriteCrashDumps = v);

        b.Section("Mobile");
        b.Check("AllowSharing", () => s.Mobile.AllowSharing, v => s.Mobile.AllowSharing = v);
        b.Text("ConnectionName", () => s.Mobile.ConnectionName, v => s.Mobile.ConnectionName = v, 240);
        b.Number("Port", () => s.Mobile.Port, v => s.Mobile.Port = v, 1024, 65535);
        var mobile = b.ButtonRow();
        b.Action("P_ConnectMobile", () => ShowMobileConnect(new MobileConnectRequest { Owner = w }), mobile);

        b.Section("P_Maintenance");
        b.Check("ShowInStartMenu", () => a.ShowInStartMenu, v => a.ShowInStartMenu = v);
        if (services.GetService<IDesktopShortcut>() is { } desktopLink)
            b.Check("ShowOnDesktop", () => desktopChoice.Value ?? desktopLink.Exists, v => desktopChoice.Value = v);
        var maintenance = b.ButtonRow();
        b.Action("FileAssociations", () => { Need<IShellRegistration>().RegisterFileAssociations(); return Task.CompletedTask; }, maintenance);
        b.Action("ResetStyles", async () => { if ((await Confirm(new ConfirmRequest(Texts.Get("ResetStyles"), Texts.Get("ResetStylesWarning")) { Owner = w })).Confirmed) await Need<IStyleRepository>().RestoreDefaultsAsync(); }, maintenance);
        b.Action("ResetSettings", async () => { if ((await Confirm(new ConfirmRequest(Texts.Get("ResetSettings"), Texts.Get("ResetSettingsWarning")) { Owner = w, IsDestructive = true })).Confirmed) { await settings.ResetAsync(); w.DialogResult = false; } }, maintenance);
        return b.Wrap();
    }

    // ---------------------------------------------------------------- Experiments

    private static FrameworkElement BuildExperimentsPage(AppSettings s, PageBuilder b)
    {
        var x = s.Experiments;
        b.Section("Experiments");
        b.Note("P_ExperimentsNote");
        b.Check("P_ExperimentsAgree", () => x.AgreedToTerms, v => x.AgreedToTerms = v);
        b.Check("P_ExperimentsEnable", () => x.Enabled, v => x.Enabled = v);
        return b.Wrap();
    }
}
