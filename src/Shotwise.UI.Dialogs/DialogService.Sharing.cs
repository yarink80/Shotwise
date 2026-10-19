using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Sharing;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.UI.Dialogs;

public sealed partial class DialogService
{
    public async Task<bool> ShowFormAsync(string title, ShareForm form)
    {
        if (!Application.Current.Dispatcher.CheckAccess()) return await Application.Current.Dispatcher.InvokeAsync(() => ShowFormAsync(title, form)).Task.Unwrap();
        var w = new FormWindow(title); if (form.Description is not null) w.Label(form.Description);
        var staged = new List<(ShareFormField Field, Func<object?> Read)>();
        foreach (var field in form.Fields)
        {
            var f = field; Func<object?> read;
            switch (f.Kind)
            {
                case ShareFieldKind.Label: w.Label(f.Value?.ToString() ?? f.Label); continue;
                case ShareFieldKind.Checkbox:
                    var check = w.Check(f.Label, f.Value is true); read = () => check.IsChecked == true; break;
                case ShareFieldKind.Choice:
                    var choice = w.Choice(f.Label, f.Choices.Select(c => c.Key), f.Value?.ToString());
                    choice.ItemTemplate = ChoiceTemplate(f.Choices); read = () => choice.SelectedItem; break;
                case ShareFieldKind.Password:
                    var password = w.Field(f.Label, new PasswordBox { Password = f.Value?.ToString() ?? "" }); read = () => password.Password; break;
                case ShareFieldKind.FolderTree:
                    var folder = w.Text(f.Label, f.Value?.ToString()); folder.IsReadOnly = true;
                    var button = new Wpf.Ui.Controls.Button { Content = Texts.Get("Browse"), Margin = new Thickness(0, 0, 0, 8) }; w.Body.Children.Add(button);
                    button.Click += async (_, _) => { button.IsEnabled = false; try { if (f.LoadChildren is null) throw new InvalidOperationException(Texts.Get("FolderLoaderMissing")); var selected = await PickFolderAsync(f.Label, f.LoadChildren); if (selected is not null) folder.Text = selected; } catch (Exception ex) { w.Model.Error = ex.Message; } finally { button.IsEnabled = true; } }; read = () => folder.Text; break;
                case ShareFieldKind.Number:
                    var number = w.Text(f.Label, f.Value?.ToString()); read = () =>
                    {
                        var value = FormWindow.Number(number, f.Minimum ?? -1e15, f.Maximum ?? 1e15);
                        if (f.Value is int) { if (value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue) throw new ArgumentException(Texts.Get("WholeNumberRequired")); return (object)(int)value; }
                        if (f.Value is long) { if (value != Math.Truncate(value)) throw new ArgumentException(Texts.Get("WholeNumberRequired")); return (object)checked((long)value); }
                        return value;
                    }; break;
                default:
                    var text = w.Text(f.Label, f.Value?.ToString(), f.Kind == ShareFieldKind.MultilineText); text.ToolTip = f.Placeholder; read = () => text.Text; break;
            }
            if (!string.IsNullOrWhiteSpace(f.HelpText)) w.Label(f.HelpText);
            staged.Add((f, read));
        }
        ((System.Windows.Controls.Button)w.Actions.Children[1]).Content = form.OkText;
        w.Commit = () =>
        {
            var values = staged.Select(s => (s.Field, Value: s.Read())).ToArray();
            foreach (var v in values) if (v.Field.Required && (v.Value is null || v.Value is string s && string.IsNullOrWhiteSpace(s))) throw new ArgumentException(v.Field.Label + ": " + Texts.Get("Required"));
            foreach (var v in values) v.Field.Value = v.Value; return Task.CompletedTask;
        };
        return await w.ShowDialogAsync() == true;
    }
    private static DataTemplate ChoiceTemplate(IReadOnlyList<(string Key, string Label)> choices)
    {
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new ChoiceConverter(choices) }); template.VisualTree = factory; return template;
    }
    private sealed class ChoiceConverter(IReadOnlyList<(string Key, string Label)> choices) : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => choices.FirstOrDefault(c => c.Key == value?.ToString()).Label ?? value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => BindingSentinel;
        private static object BindingSentinel => System.Windows.Data.Binding.DoNothing;
    }
    public async Task<string?> PickFolderAsync(string title, Func<string?, Task<IReadOnlyList<RemoteFolder>>> loadChildren)
    {
        if (!Application.Current.Dispatcher.CheckAccess()) return await Application.Current.Dispatcher.InvokeAsync(() => PickFolderAsync(title, loadChildren)).Task.Unwrap();
        var w = new FormWindow(title); var tree = w.Field("Folders", new TreeView { MinHeight = 280 });
        var loaded = new HashSet<string>();
        var retry = w.Action("Refresh", async () => { tree.Items.Clear(); loaded.Clear(); await Load(tree.Items, null); });
        async Task Load(ItemCollection collection, string? parent)
        {
            var children = await loadChildren(parent).WaitAsync(w.Lifetime.Token); if (!w.IsVisible) return;
            foreach (var folder in children)
            {
                var node = new TreeViewItem { Header = folder.Name, Tag = folder };
                if (folder.HasChildren)
                {
                    node.Items.Add(new TreeViewItem { Header = Texts.Get("Loading") });
                    node.Expanded += async (_, e) =>
                    {
                        e.Handled = true; if (!loaded.Add(folder.Id)) return; node.IsEnabled = false;
                        try { node.Items.Clear(); await Load(node.Items, folder.Id); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { loaded.Remove(folder.Id); node.Items.Add(new TreeViewItem { Header = Texts.Get("Retry") }); w.Model.Error = ex.Message; log.LogWarning(ex, "Remote folder expansion failed"); }
                        finally { node.IsEnabled = true; }
                    };
                }
                collection.Add(node);
            }
        }
        w.Loaded += async (_, _) => { try { await Load(tree.Items, null); } catch (OperationCanceledException) { } catch (Exception ex) { w.Model.Error = ex.Message; log.LogWarning(ex, "Remote folder loading failed"); } };
        var result = await Show(w, new MessageRequest(title, ""), () => ((tree.SelectedItem as TreeViewItem)?.Tag as RemoteFolder)?.Id ?? throw new ArgumentException(Texts.Get("ChooseFolder")));
        return result;
    }
    public Task ShowMessageAsync(string title, string message, bool isError = false)
    {
        if (!Application.Current.Dispatcher.CheckAccess()) return Application.Current.Dispatcher.InvokeAsync(() => ShowMessageAsync(title, message, isError)).Task.Unwrap();
        return ShowMessage(new MessageRequest(title, message, isError ? MessageKind.Error : MessageKind.Info));
    }
    public Task<bool> OpenBrowserForAuthAsync(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) return Task.FromResult(false);
        try { using var process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); return Task.FromResult(process is not null); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { log.LogWarning(ex, "Authentication browser could not be opened"); return Task.FromResult(false); }
    }
}


