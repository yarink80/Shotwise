using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shotwise.Core.Serialization;

/// <summary>
/// Shared System.Text.Json configuration for every Shotwise file format:
/// camelCase members, camelCase string enums (integers accepted on read), nulls omitted, comments and trailing commas
/// tolerated, numbers accepted from strings. Type-level converters (Argb, Point2, Rect2, AssetId, Hotkey, Annotation,
/// EffectSpec) are attached with [JsonConverter] attributes so they apply with any options instance.
/// </summary>
public static class ShotwiseJson
{
    /// <summary>Compact output (document pages, clipboard payloads).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    /// <summary>Indented output (settings, presets, themes, templates — human-editable files).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    public static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 128,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize<T>(T value, bool indented = false) => JsonSerializer.Serialize(value, indented ? Indented : Compact);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Compact);
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Compact);
}
