using System.Globalization;
using System.Text.Json.Serialization;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Imaging;

/// <summary>Straight (non-premultiplied) 32-bit colour, packed 0xAARRGGBB. JSON form: "#AARRGGBB".</summary>
[JsonConverter(typeof(ArgbJsonConverter))]
public readonly record struct Argb(uint Value)
{
    public byte A => (byte)(Value >> 24);
    public byte R => (byte)(Value >> 16);
    public byte G => (byte)(Value >> 8);
    public byte B => (byte)Value;

    public static Argb FromArgb(byte a, byte r, byte g, byte b) => new(((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b);
    public static Argb FromRgb(byte r, byte g, byte b) => FromArgb(255, r, g, b);

    public Argb WithAlpha(byte a) => FromArgb(a, R, G, B);
    public Argb WithOpacity(double opacity) => WithAlpha((byte)Math.Clamp(Math.Round(A * opacity), 0, 255));
    public bool IsTransparent => A == 0;
    public bool IsOpaque => A == 255;

    /// <summary>Formats as "#AARRGGBB".</summary>
    public string ToHex() => "#" + Value.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Formats as "#RRGGBB" (alpha dropped).</summary>
    public string ToRgbHex() => "#" + (Value & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    public override string ToString() => ToHex();

    /// <summary>Parses "#AARRGGBB", "#RRGGBB", "#ARGB", "#RGB" (leading '#' optional) or "0xAARRGGBB".</summary>
    public static Argb Parse(string s)
        => TryParse(s, out var c) ? c : throw new FormatException($"Invalid colour '{s}'. Expected #AARRGGBB or #RRGGBB.");

    public static bool TryParse(string? s, out Argb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var span = s.AsSpan().Trim();
        if (span.StartsWith("#")) span = span[1..];
        else if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
        if (!uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        switch (span.Length)
        {
            case 8: color = new Argb(v); return true;
            case 6: color = new Argb(0xFF000000u | v); return true;
            case 4:
                {
                    uint a = (v >> 12) & 0xF, r = (v >> 8) & 0xF, g = (v >> 4) & 0xF, b = v & 0xF;
                    color = FromArgb((byte)(a * 17), (byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
                    return true;
                }
            case 3:
                {
                    uint r = (v >> 8) & 0xF, g = (v >> 4) & 0xF, b = v & 0xF;
                    color = FromArgb(255, (byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
                    return true;
                }
            default: return false;
        }
    }

    /// <summary>Linear interpolation in straight-alpha space.</summary>
    public static Argb Lerp(Argb a, Argb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static byte L(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return FromArgb(L(a.A, b.A, t), L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t));
    }

    /// <summary>Relative luminance 0..1 (sRGB, ignoring alpha).</summary>
    public double Luminance
    {
        get
        {
            static double Lin(byte c) { var s = c / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }
    }

    /// <summary>Euclidean RGBA distance normalised to 0..1 (used for tolerance comparisons).</summary>
    public static double Distance(Argb a, Argb b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B, da = a.A - b.A;
        return Math.Sqrt(dr * dr + dg * dg + db * db + da * da) / 510.0;
    }

    public static Argb Transparent => new(0x00000000);
    public static Argb Black => new(0xFF000000);
    public static Argb White => new(0xFFFFFFFF);
    public static Argb Red => new(0xFFE53935);
    public static Argb Green => new(0xFF43A047);
    public static Argb Blue => new(0xFF1E88E5);
    public static Argb Yellow => new(0xFFFDD835);
    public static Argb Orange => new(0xFFFB8C00);
    public static Argb Purple => new(0xFF8E24AA);
    public static Argb Gray => new(0xFF808080);
    public static Argb LightGray => new(0xFFD3D3D3);
    public static Argb DarkGray => new(0xFF404040);
    /// <summary>Default semi-transparent highlighter yellow.</summary>
    public static Argb HighlightYellow => new(0x80FFEB3B);
}
