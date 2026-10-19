// Shotwise icon import (authoring-time tool). Converts Fluent UI System Icons (MIT, Microsoft) SVGs into WPF XAML.
// Usage: dotnet run --project tools/icon-import -c Release -- [manifest] [output-directory] [catalog.md path]
// Output: Icons.Regular.xaml, Icons.Filled.xaml, Icons.Color.xaml in the output directory + the markdown key table.
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

const string PackageVersion = "1.1.344"; // npm @fluentui/svg-icons
const string SourceCommit = "a347af95478f989038cccbf085978865264c048a"; // github.com/microsoft/fluentui-system-icons "publish 1.1.344"

var manifestPath = args.Length > 0 ? args[0] : Path.Combine("tools", "icon-import", "icons.manifest");
var outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "shotwise-icons");
var cacheDir = Path.Combine(Path.GetTempPath(), "shotwise-icon-cache", PackageVersion);
Directory.CreateDirectory(outDir);
Directory.CreateDirectory(cacheDir);
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var warnings = new List<string>();

async Task<string?> Fetch(string file)
{
    var cached = Path.Combine(cacheDir, file);
    if (File.Exists(cached)) return File.ReadAllText(cached);
    var url = $"https://unpkg.com/@fluentui/svg-icons@{PackageVersion}/icons/{file}";
    using var resp = await http.GetAsync(url);
    if (!resp.IsSuccessStatusCode) return null;
    var text = await resp.Content.ReadAsStringAsync();
    if (!text.Contains("<svg")) return null;
    File.WriteAllText(cached, text);
    return text;
}

var regular = new StringBuilder();
var filled = new StringBuilder();
var color = new StringBuilder();
var catalog = new StringBuilder();
catalog.AppendLine("| Key | Fluent source | Regular | Filled | Color |");
catalog.AppendLine("|---|---|---|---|---|");

foreach (var raw in File.ReadAllLines(manifestPath))
{
    var line = raw.Trim();
    if (line.Length == 0 || line.StartsWith('#')) continue;
    var parts = line.Split('|', StringSplitOptions.TrimEntries);
    var key = parts[0];
    var name = parts[1];
    var variants = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    string reg = "", fil = "", col = "";
    string? filledData = null;

    if (variants.Contains("r"))
    {
        var svg = await Fetch($"{name}_20_regular.svg");
        var scale = 1.2;
        if (svg is null) { svg = await Fetch($"{name}_24_regular.svg"); scale = 1; }
        if (svg is null) warnings.Add($"missing regular {name}");
        else
        {
            regular.AppendLine($"    <StreamGeometry x:Key=\"Icon.{key}.Regular\" po:Freeze=\"True\">{Svg.MonoGeometry(svg, scale, warnings, name)}</StreamGeometry>");
            reg = "✓";
        }
    }
    if (variants.Contains("f") || variants.Any(v => v.StartsWith("t:")))
    {
        var svg = await Fetch($"{name}_24_filled.svg");
        if (svg is null) warnings.Add($"missing filled {name}");
        else
        {
            filledData = Svg.MonoGeometry(svg, 1, warnings, name);
            if (variants.Contains("f"))
            {
                filled.AppendLine($"    <StreamGeometry x:Key=\"Icon.{key}.Filled\" po:Freeze=\"True\">{filledData}</StreamGeometry>");
                fil = "✓";
            }
        }
    }
    foreach (var v in variants)
    {
        if (v == "c" || v.StartsWith("c:"))
        {
            var colorName = v.Length > 2 ? v[2..] : name;
            var svg = await Fetch($"{colorName}_24_color.svg");
            if (svg is null) { warnings.Add($"missing color {colorName}"); continue; }
            color.AppendLine($"    <!-- {key}: Fluent Color '{colorName}' -->");
            color.Append(Svg.ColorDrawingImage(svg, $"Icon.{key}.Color", warnings, colorName));
            col = $"Fluent Color `{colorName}`";
        }
        else if (v.StartsWith("t:") && filledData is not null)
        {
            var stops = v[2..].Split(',');
            color.AppendLine($"    <!-- {key}: Fluent Filled '{name}' tinted with the Shotwise hue {stops[0]} -> {stops[1]} -->");
            color.AppendLine($"    <DrawingImage x:Key=\"Icon.{key}.Color\" po:Freeze=\"True\">");
            color.AppendLine("        <DrawingImage.Drawing>");
            color.AppendLine("            <DrawingGroup>");
            color.AppendLine("                <GeometryDrawing Brush=\"Transparent\" Geometry=\"M0 0H24V24H0Z\" />");
            color.AppendLine($"                <GeometryDrawing Geometry=\"{filledData}\">");
            color.AppendLine("                    <GeometryDrawing.Brush>");
            color.AppendLine("                        <LinearGradientBrush StartPoint=\"0.15,0\" EndPoint=\"0.85,1\">");
            color.AppendLine($"                            <GradientStop Offset=\"0\" Color=\"{stops[0]}\" />");
            color.AppendLine($"                            <GradientStop Offset=\"1\" Color=\"{stops[1]}\" />");
            color.AppendLine("                        </LinearGradientBrush>");
            color.AppendLine("                    </GeometryDrawing.Brush>");
            color.AppendLine("                </GeometryDrawing>");
            color.AppendLine("            </DrawingGroup>");
            color.AppendLine("        </DrawingImage.Drawing>");
            color.AppendLine("    </DrawingImage>");
            col = $"Tinted `{stops[0]}`→`{stops[1]}`";
        }
    }
    catalog.AppendLine($"| `Icon.{key}.*` | `{name}` | {reg} | {fil} | {col} |");
}

string Wrap(StringBuilder body, string what) =>
    "<!-- GENERATED by tools/icon-import from Fluent UI System Icons (MIT, (c) Microsoft Corporation),\n" +
    $"     npm @fluentui/svg-icons {PackageVersion}, github.com/microsoft/fluentui-system-icons commit {SourceCommit}.\n" +
    $"     {what}. Do not edit by hand: change tools/icon-import/icons.manifest and re-run the tool. -->\n" +
    "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
    "                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
    "                    xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"\n" +
    "                    xmlns:po=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation/options\"\n" +
    "                    mc:Ignorable=\"po\">\n" + body + "</ResourceDictionary>\n";

File.WriteAllText(Path.Combine(outDir, "Icons.Regular.xaml"), Wrap(regular, "Regular glyphs as Geometry in a 24x24 box (20 px designs scaled 1.2)"));
File.WriteAllText(Path.Combine(outDir, "Icons.Filled.xaml"), Wrap(filled, "Filled glyphs as Geometry in a 24x24 box"));
File.WriteAllText(Path.Combine(outDir, "Icons.Color.xaml"), Wrap(color, "Colour icons as 24x24 DrawingImage (Fluent Color gradients kept; tinted glyphs use Shotwise hues)"));
var catalogPath = args.Length > 2 ? args[2] : Path.Combine(outDir, "icon-catalog.md");
File.WriteAllText(catalogPath, catalog.ToString());
foreach (var w in warnings) Console.WriteLine("WARN " + w);
Console.WriteLine($"Wrote icons to {outDir}");

static class Svg
{
    static readonly XNamespace Ns = "http://www.w3.org/2000/svg";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string MonoGeometry(string svgText, double scale, List<string> warnings, string name)
    {
        var doc = XDocument.Parse(svgText);
        var sb = new StringBuilder();
        var evenOdd = false;
        foreach (var el in Shapes(doc.Root!))
        {
            if ((string?)el.Attribute("fill-rule") == "evenodd") evenOdd = true;
            sb.Append(PathData(el, scale, warnings, name));
        }
        return (evenOdd ? "F0 " : "F1 ") + sb;
    }

    static IEnumerable<XElement> Shapes(XElement root)
    {
        foreach (var el in root.Elements())
        {
            var n = el.Name.LocalName;
            if (n is "defs" or "title" or "desc") continue;
            if (n == "g") { foreach (var c in Shapes(el)) yield return c; continue; }
            yield return el;
        }
    }

    static string PathData(XElement el, double scale, List<string> warnings, string name)
    {
        if (el.Attribute("transform") is not null) warnings.Add($"{name}: transform on <{el.Name.LocalName}> ignored");
        string d;
        switch (el.Name.LocalName)
        {
            case "path": d = (string?)el.Attribute("d") ?? ""; break;
            case "circle":
            {
                double cx = N(el, "cx"), cy = N(el, "cy"), r = N(el, "r");
                d = $"M{F(cx - r)} {F(cy)}A{F(r)} {F(r)} 0 1 0 {F(cx + r)} {F(cy)}A{F(r)} {F(r)} 0 1 0 {F(cx - r)} {F(cy)}Z";
                break;
            }
            case "ellipse":
            {
                double cx = N(el, "cx"), cy = N(el, "cy"), rx = N(el, "rx"), ry = N(el, "ry");
                d = $"M{F(cx - rx)} {F(cy)}A{F(rx)} {F(ry)} 0 1 0 {F(cx + rx)} {F(cy)}A{F(rx)} {F(ry)} 0 1 0 {F(cx - rx)} {F(cy)}Z";
                break;
            }
            case "rect":
            {
                double x = N(el, "x"), y = N(el, "y"), w = N(el, "width"), h = N(el, "height");
                double rx = el.Attribute("rx") is null ? N(el, "ry") : N(el, "rx");
                double ry = el.Attribute("ry") is null ? rx : N(el, "ry");
                rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
                d = rx <= 0
                    ? $"M{F(x)} {F(y)}H{F(x + w)}V{F(y + h)}H{F(x)}Z"
                    : $"M{F(x + rx)} {F(y)}H{F(x + w - rx)}A{F(rx)} {F(ry)} 0 0 1 {F(x + w)} {F(y + ry)}V{F(y + h - ry)}A{F(rx)} {F(ry)} 0 0 1 {F(x + w - rx)} {F(y + h)}H{F(x + rx)}A{F(rx)} {F(ry)} 0 0 1 {F(x)} {F(y + h - ry)}V{F(y + ry)}A{F(rx)} {F(ry)} 0 0 1 {F(x + rx)} {F(y)}Z";
                break;
            }
            default:
                warnings.Add($"{name}: unsupported element <{el.Name.LocalName}>");
                return "";
        }
        return NormalizePath(d, scale);
    }

    static double N(XElement el, string attr) => double.Parse(((string?)el.Attribute(attr) ?? "0").TrimEnd('%'), Inv);
    static string F(double v) => Math.Round(v, 3).ToString("0.###", Inv);

    /// <summary>Parses SVG path data (any compact form), converts it to absolute commands, scales it and re-emits WPF-safe markup.</summary>
    public static string NormalizePath(string d, double scale)
    {
        // Source coordinates are made absolute, scaled and rounded to 0.01 (below 0.05 device px even for 96 px icons), then
        // re-emitted as compact RELATIVE commands measured from the previously emitted (rounded) point, so no error accumulates.
        var tokens = Tokenize(d);
        var w = new PathWriter();
        double cx = 0, cy = 0, sx = 0, sy = 0;   // source space, absolute
        int i = 0;
        char cmd = 'M';
        double R(double v) => Math.Round(v * scale, 2);
        while (i < tokens.Count)
        {
            if (tokens[i] is char c) { cmd = c; i++; if (char.ToUpperInvariant(c) == 'Z') { w.Close(); cx = sx; cy = sy; continue; } }
            double Num() => (double)tokens[i++];
            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    double x = Num(), y = Num();
                    if (rel) { x += cx; y += cy; }
                    w.MoveTo(R(x), R(y)); cx = sx = x; cy = sy = y;
                    cmd = rel ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    double x = Num(), y = Num();
                    if (rel) { x += cx; y += cy; }
                    w.Seg('l', R(x), R(y)); cx = x; cy = y;
                    break;
                }
                case 'H':
                {
                    double x = Num(); if (rel) x += cx;
                    w.H(R(x)); cx = x;
                    break;
                }
                case 'V':
                {
                    double y = Num(); if (rel) y += cy;
                    w.V(R(y)); cy = y;
                    break;
                }
                case 'C':
                {
                    var a = new double[6];
                    for (int k = 0; k < 6; k++) a[k] = R(Num() + (rel ? (k % 2 == 0 ? cx : cy) : 0));
                    cx = a[4] / scale; cy = a[5] / scale;
                    w.Seg('c', a);
                    break;
                }
                case 'S':
                case 'Q':
                {
                    var a = new double[4];
                    for (int k = 0; k < 4; k++) a[k] = R(Num() + (rel ? (k % 2 == 0 ? cx : cy) : 0));
                    cx = a[2] / scale; cy = a[3] / scale;
                    w.Seg(char.ToUpperInvariant(cmd) == 'S' ? 's' : 'q', a);
                    break;
                }
                case 'T':
                {
                    double x = Num(), y = Num();
                    if (rel) { x += cx; y += cy; }
                    w.Seg('t', R(x), R(y)); cx = x; cy = y;
                    break;
                }
                case 'A':
                {
                    double rx = Num(), ry = Num(), rot = Num(), large = Num(), sweep = Num(), x = Num(), y = Num();
                    if (rel) { x += cx; y += cy; }
                    w.Arc(R(rx), R(ry), Math.Round(rot, 2), large != 0, sweep != 0, R(x), R(y)); cx = x; cy = y;
                    break;
                }
                default: throw new FormatException("Unknown path command " + cmd);
            }
        }
        return w.ToString();
    }

    /// <summary>Writes WPF path markup with relative commands (absolute M) and minimal separators.</summary>
    sealed class PathWriter
    {
        private readonly StringBuilder _sb = new();
        private double _x, _y, _sx, _sy;   // emitted (rounded) current point and figure start
        private char _last;                // last command letter written

        private void Cmd(char c) { if (_last != c) { _sb.Append(c); _last = c; } else _sb.Append(' '); _first = true; }
        private bool _first;
        private void N(double v)
        {
            v = Math.Round(v, 2);
            if (v == 0) v = 0; // drop negative zero
            var s = v.ToString("0.##", Inv);
            if (s.StartsWith("0.")) s = s[1..]; else if (s.StartsWith("-0.")) s = "-" + s[2..];
            if (!_first && !s.StartsWith('-')) _sb.Append(' ');
            _sb.Append(s);
            _first = false;
        }

        public void MoveTo(double x, double y) { _sb.Append('M'); _last = 'M'; _first = true; N(x); N(y); _x = _sx = x; _y = _sy = y; }
        public void Close() { _sb.Append('z'); _last = 'z'; _x = _sx; _y = _sy; }
        public void H(double x) { Cmd('h'); N(x - _x); _x = x; }
        public void V(double y) { Cmd('v'); N(y - _y); _y = y; }

        /// <summary>Segment given in absolute (rounded) coordinates; the last pair is the end point.</summary>
        public void Seg(char c, params double[] abs)
        {
            Cmd(c);
            for (int k = 0; k < abs.Length; k += 2) { N(abs[k] - _x); N(abs[k + 1] - _y); }
            _x = abs[^2]; _y = abs[^1];
        }

        public void Arc(double rx, double ry, double rot, bool large, bool sweep, double x, double y)
        {
            Cmd('a');
            N(rx); N(ry); N(rot);
            _sb.Append(' ').Append(large ? '1' : '0').Append(' ').Append(sweep ? '1' : '0');
            N(x - _x); N(y - _y);
            _x = x; _y = y;
        }

        public override string ToString() => _sb.ToString();
    }

    static List<object> Tokenize(string d)
    {
        var list = new List<object>();
        int i = 0, argIndex = 0;
        char cmd = ' ';
        while (i < d.Length)
        {
            var ch = d[i];
            if (char.IsWhiteSpace(ch) || ch == ',') { i++; continue; }
            if (char.IsLetter(ch) && ch is not ('e' or 'E')) { cmd = ch; list.Add(ch); argIndex = 0; i++; continue; }
            if (char.ToUpperInvariant(cmd) == 'A' && (argIndex % 7 == 3 || argIndex % 7 == 4))
            {
                list.Add((double)(ch - '0')); argIndex++; i++; continue;
            }
            int start = i;
            if (ch is '+' or '-') i++;
            bool dot = false, exp = false;
            while (i < d.Length)
            {
                var c = d[i];
                if (char.IsDigit(c)) { i++; continue; }
                if (c == '.' && !dot && !exp) { dot = true; i++; continue; }
                if ((c is 'e' or 'E') && !exp) { exp = true; i++; if (i < d.Length && d[i] is '+' or '-') i++; continue; }
                break;
            }
            list.Add(double.Parse(d.AsSpan(start, i - start), NumberStyles.Float, Inv));
            argIndex++;
        }
        return list;
    }

    // ---------------- Colour icons ----------------

    public static string ColorDrawingImage(string svgText, string key, List<string> warnings, string name)
    {
        var doc = XDocument.Parse(svgText);
        var defs = doc.Descendants().Where(e => e.Name.LocalName is "linearGradient" or "radialGradient")
                      .ToDictionary(e => (string)e.Attribute("id")!, e => e);
        var sb = new StringBuilder();
        sb.AppendLine($"    <DrawingImage x:Key=\"{key}\" po:Freeze=\"True\">");
        sb.AppendLine("        <DrawingImage.Drawing>");
        sb.AppendLine("            <DrawingGroup>");
        sb.AppendLine("                <GeometryDrawing Brush=\"Transparent\" Geometry=\"M0 0H24V24H0Z\" />");
        Emit(doc.Root!, sb, defs, warnings, name, "                ", inheritedFill: (string?)doc.Root!.Attribute("fill"));
        sb.AppendLine("            </DrawingGroup>");
        sb.AppendLine("        </DrawingImage.Drawing>");
        sb.AppendLine("    </DrawingImage>");
        return sb.ToString();
    }

    static void Emit(XElement parent, StringBuilder sb, Dictionary<string, XElement> defs, List<string> warnings, string name, string indent, string? inheritedFill)
    {
        foreach (var el in parent.Elements())
        {
            var n = el.Name.LocalName;
            if (n is "defs" or "title" or "desc" or "linearGradient" or "radialGradient") continue;
            if (el.Attribute("mask") is not null || el.Attribute("filter") is not null || el.Attribute("clip-path") is not null)
                warnings.Add($"{name}: mask/filter/clip-path on <{n}> ignored");
            var fill = (string?)el.Attribute("fill") ?? inheritedFill;
            var opacity = (string?)el.Attribute("opacity");
            var inner = indent;
            if (opacity is not null) { sb.AppendLine($"{indent}<DrawingGroup Opacity=\"{opacity}\">"); inner = indent + "    "; }
            if (n == "g")
            {
                Emit(el, sb, defs, warnings, name, inner, fill);
            }
            else if (fill is not null && fill != "none")
            {
                var data = PathData(el, 1, warnings, name);
                var rule = (string?)el.Attribute("fill-rule") == "evenodd" ? "F0 " : "F1 ";
                var fillOpacity = (string?)el.Attribute("fill-opacity");
                if (fill.StartsWith("url(#"))
                {
                    var id = fill[5..^1];
                    sb.AppendLine($"{inner}<GeometryDrawing Geometry=\"{rule}{data}\">");
                    sb.AppendLine($"{inner}    <GeometryDrawing.Brush>");
                    Gradient(defs, id, fillOpacity, sb, inner + "        ", warnings, name);
                    sb.AppendLine($"{inner}    </GeometryDrawing.Brush>");
                    sb.AppendLine($"{inner}</GeometryDrawing>");
                }
                else
                {
                    var a = fillOpacity is null ? 1 : double.Parse(fillOpacity, Inv);
                    sb.AppendLine($"{inner}<GeometryDrawing Brush=\"{ColorHex(fill, a)}\" Geometry=\"{rule}{data}\" />");
                }
            }
            if (opacity is not null) sb.AppendLine($"{indent}</DrawingGroup>");
        }
    }

    static void Gradient(Dictionary<string, XElement> defs, string id, string? fillOpacity, StringBuilder sb, string indent, List<string> warnings, string name)
    {
        var g = defs[id];
        // xlink:href inheritance of stops/attributes
        XElement? baseEl = null;
        var href = (string?)g.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")) ?? (string?)g.Attribute("href");
        if (href is not null && defs.TryGetValue(href.TrimStart('#'), out var b)) baseEl = b;
        string? A(string attr) => (string?)g.Attribute(attr) ?? (string?)baseEl?.Attribute(attr);
        var user = A("gradientUnits") == "userSpaceOnUse";
        var mapping = user ? " MappingMode=\"Absolute\"" : "";
        var transform = A("gradientTransform") is { } t ? $" Transform=\"{ParseTransform(t)}\"" : "";
        var op = fillOpacity is null ? "" : $" Opacity=\"{fillOpacity}\"";
        if (A("spreadMethod") is { } sm && sm != "pad") warnings.Add($"{name}: spreadMethod {sm} not supported");
        string Coord(string? v, double def) => v is null ? F(def) : v.EndsWith('%') ? F(double.Parse(v[..^1], Inv) / 100) : F(double.Parse(v, Inv));
        string tag;
        if (g.Name.LocalName == "linearGradient")
        {
            tag = "LinearGradientBrush";
            sb.AppendLine($"{indent}<LinearGradientBrush{mapping} StartPoint=\"{Coord(A("x1"), 0)},{Coord(A("y1"), 0)}\" EndPoint=\"{Coord(A("x2"), 1)},{Coord(A("y2"), 0)}\"{transform}{op}>");
        }
        else
        {
            tag = "RadialGradientBrush";
            var cx = Coord(A("cx"), 0.5); var cy = Coord(A("cy"), 0.5); var r = Coord(A("r"), 0.5);
            var fx = A("fx") is null ? cx : Coord(A("fx"), 0.5); var fy = A("fy") is null ? cy : Coord(A("fy"), 0.5);
            sb.AppendLine($"{indent}<RadialGradientBrush{mapping} Center=\"{cx},{cy}\" GradientOrigin=\"{fx},{fy}\" RadiusX=\"{r}\" RadiusY=\"{r}\"{transform}{op}>");
        }
        var stops = g.Elements().Where(e => e.Name.LocalName == "stop").ToList();
        if (stops.Count == 0 && baseEl is not null) stops = baseEl.Elements().Where(e => e.Name.LocalName == "stop").ToList();
        foreach (var s in stops)
        {
            var off = (string?)s.Attribute("offset") ?? "0";
            var offset = off.EndsWith('%') ? double.Parse(off[..^1], Inv) / 100 : double.Parse(off, Inv);
            var sc = (string?)s.Attribute("stop-color") ?? "#000";
            var so = (string?)s.Attribute("stop-opacity") is { } x ? double.Parse(x, Inv) : 1;
            sb.AppendLine($"{indent}    <GradientStop Offset=\"{F(offset)}\" Color=\"{ColorHex(sc, so)}\" />");
        }
        sb.AppendLine($"{indent}</{tag}>");
    }

    static string ColorHex(string c, double alpha)
    {
        c = c.Trim().ToLowerInvariant();
        c = c switch { "white" => "#ffffff", "black" => "#000000", "currentcolor" => "#212121", _ => c };
        if (!c.StartsWith('#')) throw new FormatException("Unsupported colour " + c);
        var hex = c[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        var a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        return "#" + a.ToString("X2") + hex.ToUpperInvariant();
    }

    // SVG transform list -> WPF matrix string "m11,m12,m21,m22,ox,oy" (SVG matrix(a b c d e f) maps 1:1).
    static string ParseTransform(string t)
    {
        var m = new[] { 1.0, 0, 0, 1, 0, 0 };
        int i = 0;
        while (i < t.Length)
        {
            while (i < t.Length && (char.IsWhiteSpace(t[i]) || t[i] == ',')) i++;
            if (i >= t.Length) break;
            int open = t.IndexOf('(', i), close = t.IndexOf(')', open);
            var fn = t[i..open].Trim();
            var nums = Tokenize("Z" + t[(open + 1)..close]).OfType<double>().ToArray();
            i = close + 1;
            double[] f = fn switch
            {
                "matrix" => nums,
                "translate" => new[] { 1.0, 0, 0, 1, nums[0], nums.Length > 1 ? nums[1] : 0 },
                "scale" => new[] { nums[0], 0, 0, nums.Length > 1 ? nums[1] : nums[0], 0, 0 },
                "rotate" => Rotate(nums),
                "skewX" => new[] { 1.0, 0, Math.Tan(nums[0] * Math.PI / 180), 1, 0, 0 },
                "skewY" => new[] { 1.0, Math.Tan(nums[0] * Math.PI / 180), 0, 1, 0, 0 },
                _ => throw new FormatException("transform " + fn),
            };
            m = Compose(m, f);
        }
        return string.Join(",", m.Select(v => Math.Round(v, 5).ToString("0.#####", Inv)));
    }

    static double[] Rotate(double[] n)
    {
        var a = n[0] * Math.PI / 180;
        var r = new[] { Math.Cos(a), Math.Sin(a), -Math.Sin(a), Math.Cos(a), 0, 0 };
        if (n.Length < 3) return r;
        return Compose(Compose(new[] { 1.0, 0, 0, 1, n[1], n[2] }, r), new[] { 1.0, 0, 0, 1, -n[1], -n[2] });
    }

    // A∘B (B applied first), both as SVG [a b c d e f].
    static double[] Compose(double[] A, double[] B) => new[]
    {
        A[0] * B[0] + A[2] * B[1],
        A[1] * B[0] + A[3] * B[1],
        A[0] * B[2] + A[2] * B[3],
        A[1] * B[2] + A[3] * B[3],
        A[0] * B[4] + A[2] * B[5] + A[4],
        A[1] * B[4] + A[3] * B[5] + A[5],
    };
}
