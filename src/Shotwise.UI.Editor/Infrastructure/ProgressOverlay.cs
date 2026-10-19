using System.Windows;
using System.Windows.Controls;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Modal-looking progress card with cancel over a scrim; used for renders, GIFs, shares.</summary>
internal sealed class ProgressOverlay : Grid
{
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Margin = new Thickness(0, 4, 0, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 6, MinWidth = 340, Margin = new Thickness(0, 16, 0, 0) };
    private readonly Button _cancel;
    private CancellationTokenSource? _cts;

    public ProgressOverlay()
    {
        Visibility = Visibility.Collapsed;
        SetResourceReference(BackgroundProperty, Tok.Scrim);
        _title.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text);
        _detail.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        _bar.SetResourceReference(Control.ForegroundProperty, Tok.Accent);
        _cancel = new Button { Content = ES.T("Common_Cancel", "Cancel"), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96 }.Styled("Shotwise.SecondaryButton");
        _cancel.Click += (_, _) => _cts?.Cancel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new SwIcon { Source = "Icon.Brand.Color", Size = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top });
        var texts = new StackPanel { MaxWidth = 360 };
        texts.Children.Add(_title);
        texts.Children.Add(_detail);
        head.Children.Add(texts);
        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(_bar);
        sp.Children.Add(_cancel);
        var card = new Card { Content = sp, Padding = new Thickness(24, 20, 24, 20), Elevation = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 400 };
        Children.Add(card);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && _cancel.IsVisible) { _cts?.Cancel(); e.Handled = true; } };
    }

    public bool IsBusy => Visibility == Visibility.Visible;

    /// <summary>Runs <paramref name="work"/> with progress (0..1); returns false if cancelled. Exceptions propagate.</summary>
    public async Task<bool> RunAsync(string title, string? detail, bool cancellable, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running.");
        _cts = new CancellationTokenSource();
        _title.Text = title;
        _detail.Text = detail ?? "";
        _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        _bar.IsIndeterminate = true;
        _bar.Value = 0;
        _cancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
        Focusable = true;
        Focus();
        var progress = new Progress<double>(v =>
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(v, 0, 1);
        });
        try
        {
            await work(progress, _cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Visibility = Visibility.Collapsed;
            _cts.Dispose();
            _cts = null;
        }
    }
}
