using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Common.Converters;

/// <summary>bool → Visibility (Collapsed when false; <see cref="Invert"/> flips; <see cref="UseHidden"/> uses Hidden).</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : UseHidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;
        return Invert ? !visible : visible;
    }
}

[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>null / empty string → Collapsed, otherwise Visible (<see cref="Invert"/> flips).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is not null && (value is not string s || s.Length > 0);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Core <see cref="Argb"/> ⇄ WPF <see cref="Color"/>.</summary>
public sealed class ArgbToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Argb a ? a.ToColor() : Colors.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Color c ? c.ToArgb() : Argb.Transparent;
}

/// <summary>Core <see cref="Argb"/> → frozen SolidColorBrush.</summary>
public sealed class ArgbToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Argb a ? a.ToBrush() : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SolidColorBrush b ? b.Color.ToArgb() : Argb.Transparent;
}

/// <summary>Enum value ⇄ bool for radio buttons: ConverterParameter is the enum member name.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string p)
        {
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (enumType.IsEnum && Enum.TryParse(enumType, p, true, out var result)) return result!;
        }
        return Binding.DoNothing;
    }
}

/// <summary>Core <see cref="BgraImage"/> → frozen BitmapSource.</summary>
public sealed class BgraImageToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is BgraImage img ? img.ToBitmapSource() : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Hotkey? → display text ("Ctrl+Shift+F9"); empty for null.</summary>
public sealed class HotkeyToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Hotkey h ? h.ToDisplayString() : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && Hotkey.TryParse(s, out var h) ? h : null;
}

/// <summary>0..1 ⇄ 0..100 for sliders showing percent.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? Math.Round(d * 100, 1) : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d / 100.0 : 0d;
}
