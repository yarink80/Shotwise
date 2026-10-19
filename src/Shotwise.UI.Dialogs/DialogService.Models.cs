using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Common.Controls;
using Button = System.Windows.Controls.Button;

namespace Shotwise.UI.Dialogs;

public sealed partial class DialogService
{
    internal static string FormatSize(long bytes)
        => bytes >= 1L << 30 ? (bytes / 1073741824d).ToString("0.0", CultureInfo.CurrentCulture) + " GB" : (bytes / 1048576d).ToString("0.#", CultureInfo.CurrentCulture) + " MB";

    private static string HostOf(ModelInfo m) => m.Url.IsAbsoluteUri ? m.Url.Host : "";

    private static TextBlock LicenseLine(ModelInfo m)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run(Texts.Format("P_ModelSizeLicense", FormatSize(m.SizeBytes), m.License) + " "));
        if (m.LicenseUrl is { IsAbsoluteUri: true } url && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
        {
            var link = new Hyperlink(new Run(Texts.Get("P_ViewLicense"))) { NavigateUri = url };
            link.SetResourceReference(Hyperlink.ForegroundProperty, "Shotwise.AccentTextBrush");
            link.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } e.Handled = true; };
            text.Inlines.Add(link);
        }
        return text;
    }

    private static string ConsentText(ModelInfo m) => Texts.Format("P_ModelConsent", m.Name, FormatSize(m.SizeBytes), HostOf(m), m.License);

    /// <summary>The Preferences → Models page: every downloadable model with size, licence, status and a Download button (explicit consent, progress).</summary>
    private FrameworkElement BuildModelsPage(PageBuilder b, FormWindow w)
    {
        b.Section("Models");
        b.Note("P_ModelsNote");
        var manager = services.GetService<IModelManager>();
        if (manager is null) { b.Note("P_ModelsUnavailable"); return b.Wrap(); }
        foreach (var model in manager.Models)
        {
            var m = model;
            var card = new Card { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10) };
            
            var stack = new StackPanel(); card.Content = stack;
            stack.Children.Add(new PanelHeader { Content = m.Name, Actions = new SwIcon { Source = "Icon.Apps.Regular", Size = 24 } });
            stack.Children.Add(new TextBlock { Text = m.Purpose, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 4) });
            stack.Children.Add(LicenseLine(m));
            var status = new TextBlock { Margin = new Thickness(0, 6, 0, 4), Opacity = 0.85 };
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
            var download = new Button { MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
            void Refresh()
            {
                bool available = manager.IsAvailable(m.Id);
                status.Text = Texts.Get(available ? "P_ModelInstalled" : "P_ModelNotInstalled");
                download.Content = Texts.Get("P_Download"); download.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            }
            download.Click += async (_, _) =>
            {
                download.IsEnabled = false;
                try
                {
                    if (!(await Confirm(new ConfirmRequest(Texts.Get("P_Download"), ConsentText(m), Texts.Get("P_Download")) { Owner = w })).Confirmed) return;
                    bar.Visibility = Visibility.Visible; bar.Value = 0;
                    await manager.EnsureAsync(m.Id, new Progress<double>(v => bar.Value = Math.Clamp(v, 0, 1)), w.Lifetime.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { w.Model.Error = ex.GetBaseException().Message; log.LogWarning(ex, "Model download failed"); }
                finally { bar.Visibility = Visibility.Collapsed; download.IsEnabled = true; Refresh(); }
            };
            AutomationSetName(download, Texts.Format("P_DownloadModel", m.Name));
            download.SetResourceReference(FrameworkElement.StyleProperty, "Shotwise.SecondaryButton");
            download.SetResourceReference(SwUi.IconProperty, "Icon.Download.Regular");
            status.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.AccentTextBrush");
            Refresh();
            stack.Children.Add(DialogVisuals.Pill(status)); stack.Children.Add(bar); stack.Children.Add(download);
            b.Add(card);
        }
        return b.Wrap();
    }

    public async Task<bool> ShowModelDownload(ModelDownloadRequest r)
    {
        var manager = services.GetService<IModelManager>();
        if (manager is null) { await ShowMessage(new MessageRequest(Texts.Get("Models"), Texts.Get("P_ModelsUnavailable"), MessageKind.Warning) { Owner = r.Owner }); return false; }
        var wanted = r.ModelIds.Select(id => manager.Models.FirstOrDefault(m => m.Id == id)).OfType<ModelInfo>().ToList();
        if (wanted.Count == 0) return false;
        var missing = wanted.Where(m => !manager.IsAvailable(m.Id)).ToList();
        if (missing.Count == 0) return true;

        var w = new FormWindow("P_ModelsDownloadTitle");
        if (!string.IsNullOrWhiteSpace(r.Reason)) w.Label(r.Reason);
        foreach (var m in missing)
        {
            var card = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            card.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold });
            card.Children.Add(new TextBlock { Text = m.Purpose, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            card.Children.Add(LicenseLine(m));
            w.Body.Children.Add(card);
        }
        w.Note(Texts.Format("P_ModelsConsentAll", string.Join(", ", missing.Select(HostOf).Distinct(StringComparer.OrdinalIgnoreCase)), FormatSize(missing.Sum(m => m.SizeBytes))));
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) }; w.Body.Children.Add(bar);
        ((System.Windows.Controls.Button)w.Actions.Children[1]).Content = Texts.Get("P_Download");
        w.Commit = async () =>
        {
            bar.Visibility = Visibility.Visible;
            for (int i = 0; i < missing.Count; i++)
            {
                int index = i;
                await manager.EnsureAsync(missing[i].Id, new Progress<double>(v => bar.Value = (index + Math.Clamp(v, 0, 1)) / missing.Count), w.Lifetime.Token);
            }
        };
        var ok = await w.ShowDialogAsync(r.Owner) == true;
        return ok && wanted.All(m => manager.IsAvailable(m.Id));
    }

    // ---------------------------------------------------------------- eyedropper

    public async Task<ColorPickerResult?> ShowEyedropper(EyedropperRequest r)
    {
        var color = await PickScreenColorAsync(CancellationToken.None);
        return color is { } c ? new ColorPickerResult(c) : null;
    }

    /// <summary>Freezes every monitor and lets the user click a pixel; a live swatch + hex follows the cursor, Esc cancels.</summary>
    internal async Task<Argb?> PickScreenColorAsync(CancellationToken ct)
    {
        var snapshot = await Need<IScreenCaptureService>().SnapshotAllAsync(false, true, ct);
        var completion = new TaskCompletionSource<Argb?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays = new List<Window>();
        try
        {
            foreach (var (monitor, bitmap) in snapshot.Monitors)
            {
                var pick = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, Left = monitor.BoundsPx.X / monitor.DpiScale, Top = monitor.BoundsPx.Y / monitor.DpiScale, Width = monitor.BoundsPx.Width / monitor.DpiScale, Height = monitor.BoundsPx.Height / monitor.DpiScale, Cursor = Cursors.Cross };
                var img = new Image { Source = BitmapInterop.ToBitmapSource(bitmap), Stretch = Stretch.Fill };
                var swatch = new Border { Width = 96, Height = 44, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                var swatchText = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
                swatch.SetResourceReference(Border.BorderBrushProperty, "Shotwise.HudTextBrush");
                swatchText.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.HudTextBrush");
                var swatchHost = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed }; swatchHost.Children.Add(swatch); swatchHost.Children.Add(swatchText);
                var canvas = new Canvas { IsHitTestVisible = false }; canvas.Children.Add(swatchHost);
                var hint = new TextBlock { Text = Texts.Get("P_EyedropperHint"), Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0), IsHitTestVisible = false };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.HudTextBrush"); hint.SetResourceReference(TextBlock.BackgroundProperty, "Shotwise.HudBrush");
                var grid = new Grid(); grid.Children.Add(img); grid.Children.Add(canvas); grid.Children.Add(hint); pick.Content = grid;
                Argb At(Point p)
                {
                    int x = Math.Clamp((int)(p.X / img.ActualWidth * bitmap.Width), 0, bitmap.Width - 1), y = Math.Clamp((int)(p.Y / img.ActualHeight * bitmap.Height), 0, bitmap.Height - 1); int i = (y * bitmap.Width + x) * 4;
                    return Argb.FromRgb(bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]);
                }
                img.MouseMove += (_, e) =>
                {
                    var p = e.GetPosition(img); var c = At(p);
                    swatch.Background = c.ToBrush(); swatchText.Text = c.ToRgbHex(); swatchHost.Width = swatch.Width; swatchHost.Height = swatch.Height;
                    Canvas.SetLeft(swatchHost, Math.Min(p.X + 18, Math.Max(0, img.ActualWidth - 110))); Canvas.SetTop(swatchHost, Math.Min(p.Y + 18, Math.Max(0, img.ActualHeight - 60)));
                    swatchHost.Visibility = swatch.Visibility = Visibility.Visible;
                };
                img.MouseLeftButtonDown += (_, e) => completion.TrySetResult(At(e.GetPosition(img)));
                pick.KeyDown += (_, e) => { if (e.Key == Key.Escape) completion.TrySetResult(null); };
                img.MouseRightButtonDown += (_, _) => completion.TrySetResult(null);
                pick.Closed += (_, _) => completion.TrySetResult(null); overlays.Add(pick); pick.Show();
            }
            if (overlays.Count == 0) completion.TrySetResult(null); else overlays[0].Activate();
            return await completion.Task.WaitAsync(ct);
        }
        finally { foreach (var overlay in overlays) overlay.Close(); }
    }
}
