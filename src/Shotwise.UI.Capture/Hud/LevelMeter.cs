using System.Windows;
using System.Windows.Media;

namespace Shotwise.UI.Capture.Hud;

/// <summary>Five-segment audio level meter for the dark recording HUD (green, then amber, then coral at the top).</summary>
internal sealed class LevelMeter : FrameworkElement
{
    private const int Segments = 5;
    private const double BarW = 3, Gap = 2, MaxH = 16, MinH = 6;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public LevelMeter()
    {
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 0, 6, 0);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Segments * BarW + (Segments - 1) * Gap, MaxH);

    protected override void OnRender(DrawingContext dc)
    {
        var track = Brush("Capture.HudMeterTrackBrush", Color.FromArgb(0x33, 255, 255, 255));
        var lit = new[]
        {
            Brush("Shotwise.SuccessBrush", Color.FromRgb(0x22, 0xC5, 0x5E)), Brush("Shotwise.SuccessBrush", Color.FromRgb(0x22, 0xC5, 0x5E)),
            Brush("Shotwise.SuccessBrush", Color.FromRgb(0x22, 0xC5, 0x5E)), Brush("Shotwise.WarningBrush", Color.FromRgb(0xFB, 0xBF, 0x24)),
            Brush("Shotwise.CaptureSolidBrush", Color.FromRgb(0xFF, 0x4D, 0x5E)),
        };
        // a little headroom so ordinary speech lights 2-3 segments
        double level = Math.Clamp(Math.Sqrt(Math.Clamp(Value, 0, 1)), 0, 1) * Segments;
        for (int i = 0; i < Segments; i++)
        {
            double h = MinH + (MaxH - MinH) * i / (Segments - 1);
            var r = new Rect(i * (BarW + Gap), MaxH - h, BarW, h);
            dc.DrawRoundedRectangle(level > i + 0.15 ? lit[i] : track, null, r, 1.5, 1.5);
        }
    }

    private static Brush Brush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
