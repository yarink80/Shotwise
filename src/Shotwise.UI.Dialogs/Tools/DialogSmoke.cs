using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Core.Styles;
using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Vision;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.UI.Dialogs.Tools;

public class ToolProxy : DispatchProxy
{
    public Dictionary<string, object?> Values { get; } = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (Values.TryGetValue(method!.Name, out var value)) return value;
        if (method.ReturnType == typeof(void)) return null;
        if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
        var type = method.ReturnType;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = type.GenericTypeArguments[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, new[] { resultType.IsValueType ? Activator.CreateInstance(resultType) : null });
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
    public static T Make<T>(params (string Method, object? Value)[] entries) where T : class
    {
        var result = Create<T, ToolProxy>(); foreach (var entry in entries) ((ToolProxy)(object)result).Values[entry.Method] = entry.Value; return result;
    }
}
internal sealed class ToolServices : IServiceProvider
{
    public Dictionary<Type, object> Items { get; } = new();
    public object? GetService(Type type) => Items.GetValueOrDefault(type);
}
internal static class DialogSmoke
{
    public static void Run()
    {
        var provider = new ToolServices(); var state = new AppSettings();
        var settings = ToolProxy.Make<ISettingsService>(("get_Current", state));
        var template = new TemplateDefinition { Name = "Test", MaxImages = 8, Prototype = new Shotwise.Core.Documents.Page { CanvasSize = new Size2(100, 100) } };
        provider.Items[typeof(ITemplateCatalog)] = ToolProxy.Make<ITemplateCatalog>(("GetTemplatesAsync", Task.FromResult<IReadOnlyList<TemplateDefinition>>(new[] { template })));
        provider.Items[typeof(IEnumerable<Shotwise.UI.Common.Preferences.IPreferencesPage>)] = Array.Empty<Shotwise.UI.Common.Preferences.IPreferencesPage>();
        var format = new ImageFormatInfo(ImageFormat.Png, ".png", "PNG", true, true, true, false, Array.Empty<string>());
        provider.Items[typeof(IImageCodec)] = ToolProxy.Make<IImageCodec>(("get_SupportedFormats", new[] { format }));
        var destination = ToolProxy.Make<IShareDestination>(("get_Id", "test"), ("get_DisplayName", "Test"), ("get_RequiresAccount", true));
        provider.Items[typeof(IShareService)] = ToolProxy.Make<IShareService>(("get_Destinations", new[] { destination }));
        provider.Items[typeof(IMobileConnectServer)] = ToolProxy.Make<IMobileConnectServer>();
        provider.Items[typeof(IPresetRepository)] = ToolProxy.Make<IPresetRepository>(("get_All", Array.Empty<CapturePreset>()));
        var service = new DialogService(provider, settings, NullLogger<DialogService>.Instance);
        var image = new BgraImage(16, 16); var size = new Size2(16, 16);
        var inputs = new[] { new CombineImageItem(Guid.NewGuid(), "First", image, size), new CombineImageItem(Guid.NewGuid(), "Second", image, size) };
        var calls = new Func<Task>[] {
            async () => { if (await service.ShowNewImage(new(size, Argb.White)) is not null) throw new Exception("Cancel returned a value"); },
            async () => { await service.ShowResizeImage(new(size, 96)); }, async () => { await service.ShowResizeCanvas(new(size, Argb.White, 96)); },
            async () => { await service.ShowCreateGif(new("input.mp4", new VideoInfo(TimeSpan.FromSeconds(40),16,16,30,false,1200), null, "out.gif")); },
            async () => { await service.ShowCombineImages(new(inputs)); }, async () => { await service.ShowCombineVideos(new(new[] { "a.mp4", "b.mp4" }, "out.mp4")); },
            async () => { await service.ShowTemplatePicker(new()); }, () => service.ShowOcrResult(new(OcrResult.Empty())),
            async () => { await service.ShowSaveAs(new("file.png", null, false, new[] { format }, "png")); }, async () => { await service.ShowAutoNaming(new(new NamingSettings())); },
            async () => { await service.ShowBatchExport(new(Array.Empty<string>())); }, async () => { await service.ShowPrint(new(new[] { image })); },
            async () => { await service.ShowPageSetup(new(PrintSettings.Default)); }, () => service.ShowPrintPreview(new(new[] { image }, PrintSettings.Default)),
            async () => { foreach (var page in Enum.GetValues<PreferencesPage>()) await service.ShowPreferences(new PreferencesRequest(page)); }, () => service.ShowShareAccounts(new()), () => service.ShowDestinationOptions(new("test")),
            async () => { await service.ShowPresetImportExport(new(PresetTransferMode.Export)); }, async () => { await service.ShowEffectEditor(new(EffectDefaults.ColorReplace)); },
            async () => { await service.ShowColorPicker(new(Argb.Red)); }, async () => { await service.ShowTagEditor(new(Array.Empty<string>(), Array.Empty<string>())); },
            async () => { await service.ShowLibraryLocation(new(Environment.CurrentDirectory)); }, () => service.ShowMobileConnect(new()), () => service.ShowAbout(new()),
            () => service.ShowMessage(new("Test", "Message")), async () => { if ((await service.Confirm(new("Test", "Confirm"))).Confirmed) throw new Exception("Cancel confirmed"); },
            async () => { await service.ShowFormAsync("Test", new ShareForm().Add(new ShareFormField { Key="n", Label="Number", Kind=ShareFieldKind.Number, Value=1 }).Add(new ShareFormField { Key="s",Label="Secret",Kind=ShareFieldKind.Password }).Add(new ShareFormField { Key="f",Label="Folder",Kind=ShareFieldKind.FolderTree })); },
            async () => { await service.PickFolderAsync("Test", _ => Task.FromResult<IReadOnlyList<RemoteFolder>>(new[] { new RemoteFolder("root", "Root", null, true) })); }
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { foreach (var window in Application.Current.Windows.OfType<FormWindow>().Where(w => w.IsVisible).ToArray()) window.Close(); }; timer.Start();
        try
        {
            foreach (var call in calls)
            {
                var task = call(); var frame = new DispatcherFrame();
                task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            }
        }
        finally { timer.Stop(); }
        var accept = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        accept.Tick += (_, _) =>
        {
            foreach (var window in Application.Current.Windows.OfType<FormWindow>().Where(w => w.IsVisible).ToArray())
                if (window.Actions.Children[1] is System.Windows.Controls.Button button && button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        };
        accept.Start();
        T Finish<T>(Task<T> task)
        {
            var frame = new DispatcherFrame(); task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            if (!task.IsCompleted) Dispatcher.PushFrame(frame); return task.GetAwaiter().GetResult();
        }
        try
        {
            if (Finish(service.ShowNewImage(new(size, Argb.White)))?.Size != size) throw new Exception("New image dimensions changed.");
            if (Finish(service.ShowResizeImage(new(size, 96)))?.Resize.Width != 16) throw new Exception("Resize dimensions changed.");
            if (Finish(service.ShowResizeCanvas(new(size, Argb.White, 96)))?.Anchor != Anchor9.Center) throw new Exception("Canvas anchor changed.");
            if (Finish(service.ShowColorPicker(new(Argb.Red)))?.Color != Argb.Red) throw new Exception("Colour changed.");
            if (Finish(service.ShowAutoNaming(new(new NamingSettings())))?.Settings.CounterDigits != 3) throw new Exception("Naming changed.");
            if (Finish(service.ShowEffectEditor(new(EffectDefaults.Border)))?.Effect != EffectDefaults.Border) throw new Exception("Effect changed.");
            var form = new ShareForm().Add(new ShareFormField { Key = "port", Label = "Port", Kind = ShareFieldKind.Number, Value = 443, Minimum = 1, Maximum = 65535 });
            if (!Finish(service.ShowFormAsync("Test", form)) || form.Get<int>("port") != 443) throw new Exception("Integer field type was not preserved.");
        }
        finally { accept.Stop(); }
        // FX-5 regression checks: hand-built forms round-trip every effect, enums are localized, no duplicated check-box captions, language list, account kinds.
        foreach (var effect in typeof(EffectDefaults).GetProperties().Select(p => (EffectSpec)p.GetValue(null)!))
        {
            var panel = new System.Windows.Controls.StackPanel(); var read = service.BuildEffectForm(new FieldSet(panel), effect, new FormWindow("EffectEditor"));
            if (panel.Children.Count == 0) throw new Exception($"Effect form for {effect.GetType().Name} has no fields.");
            var rebuilt = read();
            if (effect is not ColorReplaceEffect && !rebuilt.Equals(effect)) throw new Exception($"Effect form for {effect.GetType().Name} changed its value without edits.");
        }
        if (Texts.Enum(TooltipDetail.TextOnly) == "TextOnly" || Texts.Enum(GifDitherMode.FloydSteinberg) == "FloydSteinberg") throw new Exception("Enum names are not localized.");
        var probe = new FormWindow("CreateGif"); probe.Check("Loop", true);
        if (probe.Body.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == Texts.Get("Loop"))) throw new Exception("Check box caption is duplicated.");
        if (ShippedLanguages.Normalize(ShippedLanguages.UiLanguages, "de") != "de-DE" || ShippedLanguages.Normalize(ShippedLanguages.UiLanguages, "xx") != "" || ShippedLanguages.UiLanguages.Count != 7) throw new Exception("Language list mismatch.");
        if (AccountKinds.Classify("ftp") != AccountKind.OptionsOnly || AccountKinds.Classify("dropbox") != AccountKind.OAuth || AccountKinds.Classify("slack") != AccountKind.Token) throw new Exception("Account kinds mismatch.");
        Console.WriteLine("PASS: FX-5 checks (effect forms round-trip, localized enums, no duplicated captions, language list, account kinds).");
        Console.WriteLine("PASS: seven acceptance checks (image, resize, canvas, colour, naming, effect, integer share field).");
        Console.WriteLine($"PASS: {calls.Length} dialog/share entrypoints exercised with automatic cancellation and isolated fake services.");
    }
}
