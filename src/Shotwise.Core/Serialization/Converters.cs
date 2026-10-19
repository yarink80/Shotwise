using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;

namespace Shotwise.Core.Serialization;

/// <summary>Argb ⇄ "#AARRGGBB" (reads "#RRGGBB", "#ARGB", "#RGB" and raw numbers too).</summary>
public sealed class ArgbJsonConverter : JsonConverter<Argb>
{
    public override Argb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var s = reader.GetString();
                return Argb.TryParse(s, out var c) ? c : throw new JsonException($"Invalid colour '{s}'.");
            case JsonTokenType.Number:
                return reader.TryGetUInt32(out var u) ? new Argb(u) : new Argb((uint)reader.GetInt64());
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for colour.");
        }
    }

    public override void Write(Utf8JsonWriter writer, Argb value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToHex());
}

/// <summary>Point2 ⇄ {"x":..,"y":..}; also reads "x,y" strings and [x,y] arrays.</summary>
public sealed class Point2JsonConverter : JsonConverter<Point2>
{
    public override Point2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return JsonReadHelpers.TryParsePoint(reader.GetString(), out var p) ? p : throw new JsonException("Invalid point string.");
            case JsonTokenType.StartArray:
                {
                    reader.Read(); var x = reader.GetDouble();
                    reader.Read(); var y = reader.GetDouble();
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Point array must have 2 elements.");
                    return new Point2(x, y);
                }
            case JsonTokenType.StartObject:
                {
                    double x = 0, y = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var name = reader.GetString();
                        reader.Read();
                        if (string.Equals(name, "x", StringComparison.OrdinalIgnoreCase)) x = JsonReadHelpers.ReadDouble(ref reader);
                        else if (string.Equals(name, "y", StringComparison.OrdinalIgnoreCase)) y = JsonReadHelpers.ReadDouble(ref reader);
                        else reader.Skip();
                    }
                    return new Point2(x, y);
                }
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for point.");
        }
    }

    public override void Write(Utf8JsonWriter writer, Point2 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteEndObject();
    }
}

/// <summary>Rect2 ⇄ {"x","y","width","height"}; also reads "x,y,w,h" strings and "w"/"h" short names.</summary>
public sealed class Rect2JsonConverter : JsonConverter<Rect2>
{
    public override Rect2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var parts = (reader.GetString() ?? "").Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                return new Rect2(Num(parts[0]), Num(parts[1]), Num(parts[2]), Num(parts[3]));
            throw new JsonException("Invalid rectangle string.");
        }
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException($"Unexpected token {reader.TokenType} for rectangle.");
        double x = 0, y = 0, w = 0, h = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()?.ToLowerInvariant();
            reader.Read();
            switch (name)
            {
                case "x": x = JsonReadHelpers.ReadDouble(ref reader); break;
                case "y": y = JsonReadHelpers.ReadDouble(ref reader); break;
                case "width" or "w": w = JsonReadHelpers.ReadDouble(ref reader); break;
                case "height" or "h": h = JsonReadHelpers.ReadDouble(ref reader); break;
                default: reader.Skip(); break;
            }
        }
        return new Rect2(x, y, w, h);

        static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, Rect2 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("width", value.Width);
        writer.WriteNumber("height", value.Height);
        writer.WriteEndObject();
    }
}

/// <summary>AssetId ⇄ string. Invalid ids are rejected on read (path-safety).</summary>
public sealed class AssetIdJsonConverter : JsonConverter<AssetId>
{
    public override AssetId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s)) return default;
        if (!AssetId.IsValidValue(s)) throw new JsonException($"Invalid asset id '{s}'.");
        return new AssetId(s);
    }

    public override void Write(Utf8JsonWriter writer, AssetId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value ?? "");
}

/// <summary>Hotkey ⇄ "Ctrl+Shift+F9".</summary>
public sealed class HotkeyJsonConverter : JsonConverter<Hotkey>
{
    public override Hotkey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return default;
            return Hotkey.TryParse(s, out var h) ? h : throw new JsonException($"Invalid hotkey '{s}'.");
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            VirtualKey key = VirtualKey.None;
            ModifierKeys mods = ModifierKeys.None;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                if (string.Equals(name, "key", StringComparison.OrdinalIgnoreCase))
                    key = reader.TokenType == JsonTokenType.Number ? (VirtualKey)reader.GetUInt16() : Enum.Parse<VirtualKey>(reader.GetString()!, true);
                else if (string.Equals(name, "modifiers", StringComparison.OrdinalIgnoreCase))
                    mods = reader.TokenType == JsonTokenType.Number ? (ModifierKeys)reader.GetInt32() : Enum.Parse<ModifierKeys>(reader.GetString()!, true);
                else reader.Skip();
            }
            return new Hotkey(key, mods);
        }
        throw new JsonException($"Unexpected token {reader.TokenType} for hotkey.");
    }

    public override void Write(Utf8JsonWriter writer, Hotkey value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToDisplayString());
}

/// <summary>List of (From, To) colour pairs ⇄ [{"from":"#..","to":"#.."}].</summary>
public sealed class ColorPairListJsonConverter : JsonConverter<IReadOnlyList<(Argb From, Argb To)>>
{
    public override IReadOnlyList<(Argb From, Argb To)> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected array of colour pairs.");
        var list = new List<(Argb, Argb)>();
        var conv = new ArgbJsonConverter();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected colour pair object.");
            Argb from = default, to = default;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                if (string.Equals(name, "from", StringComparison.OrdinalIgnoreCase)) from = conv.Read(ref reader, typeof(Argb), options);
                else if (string.Equals(name, "to", StringComparison.OrdinalIgnoreCase)) to = conv.Read(ref reader, typeof(Argb), options);
                else reader.Skip();
            }
            list.Add((from, to));
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<(Argb From, Argb To)> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var (f, t) in value)
        {
            writer.WriteStartObject();
            writer.WriteString("from", f.ToHex());
            writer.WriteString("to", t.ToHex());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Polymorphic annotation converter with discriminator <c>"kind"</c> (written first). Reads the discriminator from any
/// position (case-insensitive). Unknown kinds deserialize to null (callers drop them and log; count in <see cref="SkippedUnknownKinds"/>).
/// Unknown fields land in <see cref="Annotation.Extra"/> and are written back.
/// </summary>
public sealed class AnnotationJsonConverter : JsonConverter<Annotation>
{
    [ThreadStatic] private static int t_skipped;

    /// <summary>Number of unknown-kind annotations skipped on this thread since the last <see cref="ResetSkipped"/>.</summary>
    public static int SkippedUnknownKinds => t_skipped;
    public static void ResetSkipped() => t_skipped = 0;

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(Annotation);

    public override Annotation? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Annotation must be a JSON object.");
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        string? kindName = null;
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, "kind", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) { kindName = p.Value.GetString(); break; }
        if (!AnnotationKindExtensions.TryParseJsonName(kindName, out var kind))
        {
            t_skipped++;
            return null;
        }
        var a = (Annotation?)root.Deserialize(kind.ClrType(), options) ?? throw new JsonException("Annotation deserialized to null.");
        if (a.Extra is { } extra)
        {
            foreach (var key in extra.Keys.Where(k => string.Equals(k, "kind", StringComparison.OrdinalIgnoreCase)).ToList()) extra.Remove(key);
            if (extra.Count == 0) a.Extra = null;
        }
        return a;
    }

    public override void Write(Utf8JsonWriter writer, Annotation value, JsonSerializerOptions options)
    {
        var element = JsonSerializer.SerializeToElement(value, value.GetType(), options);
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToJsonName());
        foreach (var p in element.EnumerateObject())
            if (!string.Equals(p.Name, "kind", StringComparison.OrdinalIgnoreCase)) p.WriteTo(writer);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Polymorphic effect converter with discriminator <c>"type"</c> (camelCase type name without "Effect": "border",
/// "pageCurl", "spotlightMagnify"…). Unknown types round-trip unchanged as <see cref="UnknownEffect"/>.
/// </summary>
public sealed class EffectSpecJsonConverter : JsonConverter<EffectSpec>
{
    private static readonly (string Name, Type Type)[] Map =
    {
        ("border", typeof(BorderEffect)), ("edge", typeof(EdgeEffect)), ("shadow", typeof(ShadowEffect)),
        ("perspective", typeof(PerspectiveEffect)), ("pageCurl", typeof(PageCurlEffect)), ("colorAdjust", typeof(ColorAdjustEffect)),
        ("colorReplace", typeof(ColorReplaceEffect)), ("filter", typeof(FilterEffect)), ("watermark", typeof(WatermarkEffect)),
        ("caption", typeof(CaptionEffect)), ("resize", typeof(ResizeEffect)), ("rotate", typeof(RotateEffect)), ("trim", typeof(TrimEffect)),
        ("spotlightMagnify", typeof(SpotlightMagnifyEffect)), ("transparency", typeof(TransparencyEffect)),
    };

    public static string TypeNameOf(EffectSpec e) => e is UnknownEffect u ? u.TypeName : Map.First(m => m.Type == e.GetType()).Name;

    public static IReadOnlyList<string> KnownTypeNames { get; } = Map.Select(m => m.Name).ToArray();

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(EffectSpec);

    public override EffectSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Effect must be a JSON object.");
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        string? typeName = null;
        bool? enabled = null;
        foreach (var p in root.EnumerateObject())
        {
            if (string.Equals(p.Name, "type", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) typeName = p.Value.GetString();
            else if (string.Equals(p.Name, "enabled", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = p.Value.GetBoolean();
        }
        var match = Map.FirstOrDefault(m => string.Equals(m.Name, typeName, StringComparison.OrdinalIgnoreCase));
        if (match.Type is null)
            return new UnknownEffect(typeName ?? "", root.Clone()) { Enabled = enabled ?? true };
        return (EffectSpec?)root.Deserialize(match.Type, options) ?? throw new JsonException("Effect deserialized to null.");
    }

    public override void Write(Utf8JsonWriter writer, EffectSpec value, JsonSerializerOptions options)
    {
        if (value is UnknownEffect u)
        {
            u.Raw.WriteTo(writer);
            return;
        }
        var element = JsonSerializer.SerializeToElement(value, value.GetType(), options);
        writer.WriteStartObject();
        writer.WriteString("type", TypeNameOf(value));
        foreach (var p in element.EnumerateObject())
            if (!string.Equals(p.Name, "type", StringComparison.OrdinalIgnoreCase)) p.WriteTo(writer);
        writer.WriteEndObject();
    }
}

internal static class JsonReadHelpers
{
    public static double ReadDouble(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.GetDouble(),
        JsonTokenType.String => double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new JsonException("Invalid number."),
        _ => throw new JsonException($"Unexpected token {reader.TokenType} for number."),
    };

    public static bool TryParsePoint(string? s, out Point2 p)
    {
        p = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
        p = new Point2(x, y);
        return true;
    }
}
