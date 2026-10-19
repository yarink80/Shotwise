using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shotwise.Core.Documents;
using Shotwise.Core.Settings;

namespace Shotwise.Sharing.Naming;

/// <summary>Values available to naming tokens.</summary>
public sealed record NamingContext
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string? App { get; init; }
    public string? Window { get; init; }
    public string? Title { get; init; }
    public string? Preset { get; init; }
    public string? OriginalName { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Counter { get; init; }

    public static NamingContext FromDocument(ShotDocument doc, int counter = 0, string? title = null)
    {
        var m = doc.Metadata;
        var p = doc.Pages.Count > 0 ? doc.Pages[0] : null;
        return new NamingContext
        {
            Timestamp = m.CaptureDate,
            App = m.AppName,
            Window = m.WindowTitle,
            Title = title ?? m.WindowTitle ?? m.AppName,
            Preset = m.SourcePresetId,
            Width = (int)(p?.CanvasSize.Width ?? 0),
            Height = (int)(p?.CanvasSize.Height ?? 0),
            Counter = counter,
        };
    }
}

/// <summary>
/// Automatic naming (SHR-01/14). Tokens: {date} {date:fmt} {time} {time:fmt} {app} {window} {title} {preset}
/// {counter} {counter:digits} {width} {height} {original}. Unknown tokens are kept verbatim.
/// </summary>
public static class NamingEngine
{
    private static readonly Regex TokenRx = new(@"\{(?<name>[A-Za-z]+)(?::(?<arg>[^{}]*))?\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public const int MaxBaseNameLength = 120;

    /// <summary>Expands the pattern only (no prefix, no extension, not sanitised).</summary>
    public static string Expand(string pattern, NamingContext c, NamingSettings s, bool omitCaptureSource)
    {
        return TokenRx.Replace(pattern, m =>
        {
            var name = m.Groups["name"].Value.ToLowerInvariant();
            var arg = m.Groups["arg"].Success ? m.Groups["arg"].Value : null;
            switch (name)
            {
                case "date": return FormatDate(c.Timestamp, arg ?? s.DateFormat, s.DateFormat);
                case "time": return FormatDate(c.Timestamp, arg ?? s.TimeFormat, s.TimeFormat);
                case "app": return omitCaptureSource ? "" : c.App ?? "";
                case "window": return omitCaptureSource ? "" : c.Window ?? "";
                case "title": return omitCaptureSource ? "" : c.Title ?? "";
                case "preset": return c.Preset ?? "";
                case "original": return c.OriginalName ?? "";
                case "width": return c.Width > 0 ? c.Width.ToString(CultureInfo.InvariantCulture) : "";
                case "height": return c.Height > 0 ? c.Height.ToString(CultureInfo.InvariantCulture) : "";
                case "counter":
                    var digits = s.CounterDigits;
                    if (arg is not null && int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var d)) digits = d;
                    digits = Math.Clamp(digits, 1, 12);
                    return c.Counter.ToString("D" + digits, CultureInfo.InvariantCulture);
                default: return m.Value;
            }
        });
    }

    /// <summary>Full base name: prefix + expanded pattern, cleaned for the file system. Never empty.</summary>
    public static string BuildName(NamingContext c, NamingSettings s, bool omitCaptureSource)
    {
        var pattern = string.IsNullOrWhiteSpace(s.Pattern) ? "{date}_{time}" : s.Pattern;
        var raw = (s.Prefix ?? "") + Expand(pattern, c, s, omitCaptureSource);
        var name = Sanitize(raw);
        return name.Length == 0 ? "Shotwise" : name;
    }

    /// <summary>Replaces invalid file name characters, collapses separators left by empty tokens, trims and shortens.</summary>
    public static string Sanitize(string raw, bool collapseSeparators = true)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var s = sb.ToString();
        s = Regex.Replace(s, @"\s+", " ");
        if (collapseSeparators)
        {
            s = Regex.Replace(s, @"([_\- ])\1+", "$1");
            s = Regex.Replace(s, @"[_\- ]{2,}", m => m.Value[0].ToString());
        }
        s = s.Trim(' ', '.', '_', '-');
        if (s.Length > MaxBaseNameLength) s = s[..MaxBaseNameLength].TrimEnd(' ', '.', '_', '-');
        if (IsReservedDeviceName(s)) s = "_" + s;
        return s;
    }

    private static bool IsReservedDeviceName(string s)
    {
        var stem = s.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$");
    }

    private static string FormatDate(DateTimeOffset t, string format, string fallback)
    {
        try { return t.ToString(format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return t.ToString(fallback, CultureInfo.InvariantCulture); }
    }

    /// <summary>Returns <paramref name="folder"/>\name.ext, appending " (n)" until the path is free (unless overwrite).</summary>
    public static string UniquePath(string folder, string baseName, string extension, bool overwrite = false)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var path = Path.Combine(folder, baseName + ext);
        if (overwrite || !File.Exists(path)) return path;
        for (var i = 2; i < 10_000; i++)
        {
            path = Path.Combine(folder, $"{baseName} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(folder, $"{baseName}_{Guid.NewGuid():N}{ext}");
    }
}

/// <summary>Allocates the next counter value (persisted in <see cref="NamingSettings.NextCounter"/>).</summary>
public sealed class NamingCounter
{
    private readonly Shotwise.Core.Settings.ISettingsService? _settings;
    private int _local = 1;
    private readonly object _gate = new();

    public NamingCounter(Shotwise.Core.Settings.ISettingsService? settings) => _settings = settings;

    public int Next()
    {
        lock (_gate)
        {
            if (_settings is null) return _local++;
            var value = 0;
            _settings.Update(s =>
            {
                var n = s.Share.Naming;
                if (n.NextCounter < n.CounterStart) n.NextCounter = n.CounterStart;
                value = n.NextCounter;
                n.NextCounter = value + 1;
            });
            return value;
        }
    }
}
