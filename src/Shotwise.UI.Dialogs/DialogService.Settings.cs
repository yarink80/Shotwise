using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Effects;
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

namespace Shotwise.UI.Dialogs;

public sealed partial class DialogService
{
    public async Task ShowOcrResult(OcrResultRequest r)
    {
        var w = new FormWindow("OcrResult");
        var rich = w.Check("RichText", false);
        var text = new TextBox { Text = r.Result.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 220, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(text, Texts.Get("Text")); w.Body.Children.Add(text);
        var richBox = new RichTextBox { Document = new FlowDocument(new Paragraph(new Run(r.Result.Text))), MinHeight = 220, MaxHeight = 360, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(richBox, Texts.Get("FormattedText")); w.Body.Children.Add(richBox);
        rich.Checked += (_, _) => { richBox.Visibility = Visibility.Visible; text.Visibility = Visibility.Collapsed; };
        rich.Unchecked += (_, _) => { text.Visibility = Visibility.Visible; richBox.Visibility = Visibility.Collapsed; };
        string All() => rich.IsChecked == true ? new TextRange(richBox.Document.ContentStart, richBox.Document.ContentEnd).Text : text.Text;
        void Copy(bool all)
        {
            if (rich.IsChecked == true)
            {
                var range = all || richBox.Selection.IsEmpty ? new TextRange(richBox.Document.ContentStart, richBox.Document.ContentEnd) : richBox.Selection;
                using var memory = new MemoryStream(); range.Save(memory, DataFormats.Rtf);
                var data = new DataObject(); data.SetData(DataFormats.Rtf, System.Text.Encoding.UTF8.GetString(memory.ToArray())); data.SetText(range.Text); Clipboard.SetDataObject(data, true);
            }
            else Need<IClipboardWriter>().SetText(all || text.SelectionLength == 0 ? text.Text : text.SelectedText);
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; w.Body.Children.Add(buttons);
        w.InlineAction("Copy", () => { Copy(false); return Task.CompletedTask; }, buttons); w.InlineAction("CopyAll", () => { Copy(true); return Task.CompletedTask; }, buttons);
        w.InlineAction("SaveText", async () => { var d = new Microsoft.Win32.SaveFileDialog { Filter = "Text|*.txt", DefaultExt = ".txt" }; if (d.ShowDialog(w) == true) await File.WriteAllTextAsync(d.FileName, All(), w.Lifetime.Token); }, buttons);
        if (r.Table is not null) w.InlineAction("CopyAsTable", () => { Need<IClipboardWriter>().SetHtml(r.Table.ToHtml(), r.Table.ToTsv()); return Task.CompletedTask; }, buttons);
        await w.ShowDialogAsync(r.Owner);
    }

    /// <summary>Returns <paramref name="target"/> with every JSON leaf that differs between <paramref name="original"/> and <paramref name="changed"/> taken from <paramref name="changed"/>.</summary>
    internal static AppSettings MergeChangedLeaves(AppSettings original, AppSettings changed, AppSettings target)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(ShotwiseJson.Serialize(original));
        var c = System.Text.Json.Nodes.JsonNode.Parse(ShotwiseJson.Serialize(changed));
        var t = System.Text.Json.Nodes.JsonNode.Parse(ShotwiseJson.Serialize(target));
        if (o is not System.Text.Json.Nodes.JsonObject oo || c is not System.Text.Json.Nodes.JsonObject co || t is not System.Text.Json.Nodes.JsonObject to) return target;
        Merge(oo, co, to);
        return ShotwiseJson.Deserialize<AppSettings>(to.ToJsonString()) ?? target;

        static void Merge(System.Text.Json.Nodes.JsonObject o, System.Text.Json.Nodes.JsonObject c, System.Text.Json.Nodes.JsonObject t)
        {
            foreach (var (key, cv) in c.ToList())
            {
                var ov = o[key];
                if (cv is System.Text.Json.Nodes.JsonObject cObj && ov is System.Text.Json.Nodes.JsonObject oObj && t[key] is System.Text.Json.Nodes.JsonObject tObj) { Merge(oObj, cObj, tObj); continue; }
                if (System.Text.Json.Nodes.JsonNode.DeepEquals(ov, cv)) continue;
                t[key] = cv?.DeepClone();
            }
        }
    }

    public async Task<PresetImportExportResult?> ShowPresetImportExport(PresetImportExportRequest r)
    {
        var repository = Need<IPresetRepository>(); var w = new FormWindow("PresetTransfer");
        var path = w.Text("Path", "");
        w.InlineAction("Browse", () =>
        {
            if (r.Mode == PresetTransferMode.Import) { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Shotwise presets|*.shotpresets" }; if (d.ShowDialog(w) == true) path.Text = d.FileName; }
            else { var d = new Microsoft.Win32.SaveFileDialog { Filter = "Shotwise presets|*.shotpresets" }; if (d.ShowDialog(w) == true) path.Text = d.FileName; }
            return Task.CompletedTask;
        });
        var list = w.Field("Presets", new ListBox { ItemsSource = repository.All, DisplayMemberPath = "Name", SelectionMode = SelectionMode.Multiple, MinHeight = 160 });
        foreach (var preset in repository.All.Where(p => r.PreselectedIds?.Contains(p.Id) == true)) list.SelectedItems.Add(preset);
        PresetImportExportResult? result = null;
        w.Commit = async () => { var ids = list.SelectedItems.Cast<CapturePreset>().Select(p => p.Id).ToArray(); if (r.Mode == PresetTransferMode.Import) await repository.ImportAsync(path.Text); else { if (ids.Length == 0) throw new ArgumentException(Texts.Get("ChoosePreset")); await repository.ExportAsync(ids, OutputPath(path.Text)); } result = new PresetImportExportResult(path.Text, ids); };
        await w.ShowDialogAsync(r.Owner); return result;
    }

    // ---------------------------------------------------------------- colour dialog

    public Task<ColorPickerResult?> ShowColorPicker(ColorPickerRequest r)
    {
        var w = new FormWindow(r.Title ?? "ColorPicker");
        var cur = r.AllowAlpha ? r.Initial : Argb.FromRgb(r.Initial.R, r.Initial.G, r.Initial.B); bool sync = false;
        var swatch = new Border { Height = 56, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 8) };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Shotwise.BorderStrongBrush");
        var checker = new Border { Background = ColorPicker.CheckerBrush(8), CornerRadius = new CornerRadius(4) };
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 8) }; grid.Children.Add(checker); grid.Children.Add(swatch); swatch.Margin = new Thickness(0);
        w.Body.Children.Add(grid);
        var palette = new ColorPicker(cur, r.AllowAlpha, Texts.Get("ColorPicker")) { MyColors = settings.Current.Editor.MyColors };
        w.Body.Children.Add(FormWindow.Labelled("ColorPicker", palette));

        var hex = new TextBox { MinHeight = 28 }; var red = new TextBox { MinHeight = 28 }; var green = new TextBox { MinHeight = 28 }; var blue = new TextBox { MinHeight = 28 };
        var alpha = new Slider { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, IsMoveToPointEnabled = true };
        w.Group(FormWindow.Labelled("HexColor", hex));
        w.Group(FormWindow.Labelled("Red", red), FormWindow.Labelled("Green", green), FormWindow.Labelled("Blue", blue));
        if (r.AllowAlpha) w.Body.Children.Add(FormWindow.Labelled("Opacity", alpha));
        var named = FormWindow.MakeCombo(typeof(System.Windows.Media.Colors).GetProperties().Select(p => p.Name).ToList(), (string)null!);
        w.Body.Children.Add(FormWindow.Labelled("NamedColors", named));

        void Push(Argb c, Control? source)
        {
            sync = true;
            cur = r.AllowAlpha ? c : Argb.FromRgb(c.R, c.G, c.B);
            swatch.Background = cur.ToBrush();
            palette.Color = cur;
            if (!ReferenceEquals(source, hex)) hex.Text = r.AllowAlpha ? cur.ToHex() : cur.ToRgbHex();
            if (!ReferenceEquals(source, red)) red.Text = cur.R.ToString(CultureInfo.InvariantCulture);
            if (!ReferenceEquals(source, green)) green.Text = cur.G.ToString(CultureInfo.InvariantCulture);
            if (!ReferenceEquals(source, blue)) blue.Text = cur.B.ToString(CultureInfo.InvariantCulture);
            if (!ReferenceEquals(source, alpha)) alpha.Value = cur.A;
            sync = false;
        }
        hex.TextChanged += (_, _) => { if (!sync && Argb.TryParse(hex.Text.Trim().StartsWith('#') ? hex.Text.Trim() : "#" + hex.Text.Trim(), out var c)) Push(c, hex); };
        void Channel(TextBox box)
        {
            box.TextChanged += (_, _) =>
            {
                if (sync || !byte.TryParse(red.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rr) || !byte.TryParse(green.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gg) || !byte.TryParse(blue.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bb)) return;
                Push(Argb.FromArgb(cur.A, rr, gg, bb), box);
            };
        }
        Channel(red); Channel(green); Channel(blue);
        alpha.ValueChanged += (_, e) => { if (!sync) Push(Argb.FromArgb((byte)Math.Round(e.NewValue), cur.R, cur.G, cur.B), alpha); };
        named.SelectionChanged += (_, _) =>
        {
            if (named.SelectedItem is not string n || typeof(System.Windows.Media.Colors).GetProperty(n)?.GetValue(null) is not System.Windows.Media.Color c) return;
            Push(Argb.FromArgb(r.AllowAlpha && cur.A != 0 ? cur.A : c.A, c.R, c.G, c.B), null);
        };
        palette.ColorChanged += c => { if (!sync) Push(c, null); };
        Push(cur, null);

        w.Heading("MyColors");
        var mine = new WrapPanel(); w.Body.Children.Add(mine);
        void RefreshMine()
        {
            mine.Children.Clear();
            foreach (var color in settings.Current.Editor.MyColors.ToList())
            {
                var c = color;
                var button = new Wpf.Ui.Controls.Button { Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), MinWidth = 0, MinHeight = 0, ToolTip = c.ToRgbHex(), Content = new Border { Background = c.ToBrush(), Width = 20, Height = 20, CornerRadius = new CornerRadius(3) } };
                System.Windows.Automation.AutomationProperties.SetName(button, c.ToRgbHex());
                button.Click += (_, _) => Push(c, null);
                button.MouseRightButtonUp += (_, _) => { settings.Update(s => s.Editor.MyColors.Remove(c)); RefreshMine(); };
                mine.Children.Add(button);
            }
        }
        RefreshMine();
        var tools = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) }; w.Body.Children.Add(tools);
        w.InlineAction("AddColor", () => { var c = cur; settings.Update(s => { if (!s.Editor.MyColors.Contains(c)) s.Editor.MyColors.Add(c); }); RefreshMine(); return Task.CompletedTask; }, tools);
        w.InlineAction("DeleteColor", () => { var c = cur; settings.Update(s => s.Editor.MyColors.Remove(c)); RefreshMine(); return Task.CompletedTask; }, tools);
        w.InlineAction("Eyedropper", async () =>
        {
            w.Hide();
            try { await Task.Delay(200, w.Lifetime.Token); if (await PickScreenColorAsync(w.Lifetime.Token) is Argb picked) Push(r.AllowAlpha ? Argb.FromArgb(cur.A, picked.R, picked.G, picked.B) : picked, null); }
            finally { w.Show(); w.Activate(); }
        }, tools);
        return Show(w, r, () =>
        {
            if (!Argb.TryParse(hex.Text.Trim().StartsWith('#') ? hex.Text.Trim() : "#" + hex.Text.Trim(), out var parsed)) throw new ArgumentException(Texts.Get("HexColor"));
            return new ColorPickerResult(r.AllowAlpha ? parsed : Argb.FromRgb(parsed.R, parsed.G, parsed.B));
        });
    }

    public Task<TagEditorResult?> ShowTagEditor(TagEditorRequest r)
    {
        var w = new FormWindow("Tags"); var tags = w.Text("Tags", string.Join(Environment.NewLine, r.CurrentTags), true); var known = w.Choice("AllTags", r.AllTags, r.AllTags.FirstOrDefault());
        w.InlineAction("AddTag", () => { if (known.SelectedItem is string t) tags.Text += Environment.NewLine + t; return Task.CompletedTask; });
        return Show(w, r, () => { var selected = Lines(tags.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); return new TagEditorResult(selected.Except(r.CurrentTags, StringComparer.OrdinalIgnoreCase).ToArray(), r.CurrentTags.Except(selected, StringComparer.OrdinalIgnoreCase).ToArray()); });
    }
    public Task<LibraryLocationResult?> ShowLibraryLocation(LibraryLocationRequest r)
    {
        var w = new FormWindow("LibraryLocation"); var path = w.Text("Folder", r.CurrentRoot);
        w.InlineAction("Browse", () => { var d = new Microsoft.Win32.OpenFolderDialog(); if (d.ShowDialog(w) == true) path.Text = d.FolderName; return Task.CompletedTask; });
        var action = w.Enum("Action", LibraryLocationAction.Move);
        return Show(w, r, () => { var full = Path.GetFullPath(path.Text); if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(r.CurrentRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Texts.Get("ChooseDifferentFolder")); return new LibraryLocationResult(full, (LibraryLocationAction)action.SelectedItem); });
    }
    public async Task ShowMobileConnect(MobileConnectRequest r)
    {
        var w = new FormWindow("MobileConnect"); var server = Need<IMobileConnectServer>(); var name = w.Text("ConnectionName", settings.Current.Mobile.ConnectionName);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; w.Body.Children.Add(status); var qr = new Image { Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Left }; w.Body.Children.Add(qr);
        var fingerprint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 6, 0, 0) }; w.Body.Children.Add(fingerprint);
        var fingerprintNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12, Visibility = Visibility.Collapsed, Text = Texts.Get("P_FingerprintNote") }; w.Body.Children.Add(fingerprintNote);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; w.Body.Children.Add(row);
        // Sharing is only switched on while this dialog is open; closing it stops the listener again unless the user enabled it in Preferences.
        bool allowedBefore = settings.Current.Mobile.AllowSharing;
        w.InlineAction("Start", async () =>
        {
            settings.Update(s => { s.Mobile.ConnectionName = name.Text; s.Mobile.AllowSharing = true; });
            await server.StartAsync(w.Lifetime.Token);
            var pair = server.CreatePairing(TimeSpan.FromMinutes(settings.Current.Mobile.PairingValidityMinutes));
            status.Text = $"{pair.Url}\n{pair.ExpiresAt.LocalDateTime}";
            var match = System.Text.RegularExpressions.Regex.Match(pair.Url?.ToString() ?? "", "fp=([0-9A-Fa-f]{64})");
            fingerprint.Text = match.Success ? Texts.Get("P_Fingerprint") + "\n" + string.Join(":", Enumerable.Range(0, 32).Select(i => match.Groups[1].Value.Substring(i * 2, 2).ToUpperInvariant())) : "";
            fingerprintNote.Visibility = match.Success ? Visibility.Visible : Visibility.Collapsed;
            if (pair.QrCodePng is not null) { using var stream = new MemoryStream(pair.QrCodePng); var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = stream; bmp.EndInit(); bmp.Freeze(); qr.Source = bmp; }
        }, row);
        w.InlineAction("Stop", async () => { await server.StopAsync(); status.Text = Texts.Get("Stopped"); qr.Source = null; fingerprint.Text = ""; fingerprintNote.Visibility = Visibility.Collapsed; }, row); status.Text = Texts.Get(server.IsRunning ? "Running" : "Stopped");
        EventHandler<ReceivedFile> received = (_, file) => w.Dispatcher.BeginInvoke(() => status.Text = Texts.Get("Received") + ": " + file.OriginalName);
        server.FileReceived += received;
        try { await w.ShowDialogAsync(r.Owner); }
        finally
        {
            server.FileReceived -= received;
            if (!allowedBefore && settings.Current.Mobile.AllowSharing)
            {
                try { await server.StopAsync(); } catch (Exception ex) { log.LogDebug(ex, "Mobile server stop on dialog close failed"); }
                settings.Update(s => s.Mobile.AllowSharing = false);
            }
        }
    }
    public async Task ShowAbout(AboutRequest r)
    {
        var w = new FormWindow("About");
        // App icon + name + version (same artwork as the exe, taskbar and Start menu tile).
        var header = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new System.Windows.Thickness(0, 12, 0, 8) };
        header.Children.Add(new Shotwise.UI.Common.Controls.SwIcon { Source = "Icon.Brand.Color", Size = 64, Margin = new System.Windows.Thickness(0, 0, 16, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
        var names = new System.Windows.Controls.StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        names.Children.Add(new System.Windows.Controls.TextBlock { Text = "Shotwise", FontSize = 22, FontWeight = System.Windows.FontWeights.SemiBold });
        var version = new System.Windows.Controls.TextBlock { Text = Texts.Get("Version") + " " + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0"), Margin = new System.Windows.Thickness(0, 2, 0, 0) };
        version.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Shotwise.TextSecondaryBrush");
        names.Children.Add(version);
        header.Children.Add(names);
        w.Body.Children.Add(header);
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        var notices = File.Exists(path) ? await File.ReadAllTextAsync(path) : Texts.Get("LicensesMissing");
        var text = w.Text("Licenses", notices, true); text.IsReadOnly = true; text.MinHeight = 300; text.MaxHeight = 380; text.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto; await w.ShowDialogAsync(r.Owner);
    }
    public async Task ShowMessage(MessageRequest r)
    {
        var w = new FormWindow(r.Title); w.Body.Children.Add(new Shotwise.UI.Common.Controls.InfoBar { Title = r.Title, Message = r.Message, IsClosable = false, Severity = r.Kind.ToString() == "Error" ? Shotwise.UI.Common.Controls.InfoSeverity.Error : r.Kind.ToString() == "Warning" ? Shotwise.UI.Common.Controls.InfoSeverity.Warning : Shotwise.UI.Common.Controls.InfoSeverity.Informational }); if (r.Details is not null) { var details = w.Text("Details", r.Details, true); details.IsReadOnly = true; } await w.ShowDialogAsync(r.Owner);
    }
    public async Task<ConfirmResult> Confirm(ConfirmRequest r)
    {
        var w = new FormWindow(r.Title); w.Body.Children.Add(new Shotwise.UI.Common.Controls.InfoBar { Title = r.Title, Message = r.Message, IsClosable = false, Severity = r.IsDestructive ? Shotwise.UI.Common.Controls.InfoSeverity.Warning : Shotwise.UI.Common.Controls.InfoSeverity.Informational }); var check = r.DontAskAgainKey is null ? null : w.Check("DontAskAgain", false);
        ((System.Windows.Controls.Button)w.Actions.Children[0]).Content = r.CancelText ?? Texts.Get("Cancel"); ((System.Windows.Controls.Button)w.Actions.Children[1]).Content = r.ConfirmText ?? Texts.Get("OK");
        return new ConfirmResult(await w.ShowDialogAsync(r.Owner) == true, check?.IsChecked == true);
    }
}
