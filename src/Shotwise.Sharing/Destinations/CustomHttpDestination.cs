using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Serialization;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations;

/// <summary>
/// ShareX-like custom uploader definition (JSON, camelCase). Example:
/// <code>
/// { "name": "My host", "requestUrl": "https://example.com/upload", "method": "POST",
///   "headers": { "Authorization": "Bearer {secret:token}" }, "arguments": { "title": "{title}" },
///   "body": "multipart", "fileFormName": "file", "url": "{json:data.link}", "errorMessage": "{json:error.message}" }
/// </code>
/// Request placeholders: {filename} {name} {ext} {title} {mime} {secret:KEY}.
/// Response placeholders: {response} {status} {json:path} {regex:pattern} {header:Name}.
/// </summary>
public sealed class CustomUploaderDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string RequestUrl { get; set; } = "";
    public string Method { get; set; } = "POST";
    public Dictionary<string, string> Headers { get; set; } = new();
    public Dictionary<string, string> Arguments { get; set; } = new();
    /// <summary>"multipart" | "form" (file base64 in a form field) | "json" (file base64 in a JSON field) | "binary" (raw body).</summary>
    public string Body { get; set; } = "multipart";
    public string FileFormName { get; set; } = "file";
    /// <summary>Template for the resulting URL.</summary>
    public string Url { get; set; } = "{response}";
    public string? ThumbnailUrl { get; set; }
    public string? DeletionUrl { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Accepted format keys (png, jpg, gif, agif, mp4, ...); empty = all images.</summary>
    public List<string> Formats { get; set; } = new();
}

public static class CustomUploaderParser
{
    private static readonly Regex HeaderName = new("^[A-Za-z0-9!#$%&'*+.^_`|~-]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH" };
    private static readonly HashSet<string> Bodies = new(StringComparer.OrdinalIgnoreCase) { "multipart", "form", "json", "binary" };

    /// <summary>Parses and validates a definition; throws <see cref="ShotwiseException"/> with a readable message when invalid.</summary>
    public static CustomUploaderDefinition Parse(string json)
    {
        CustomUploaderDefinition? d;
        try { d = ShotwiseJson.Deserialize<CustomUploaderDefinition>(json); }
        catch (JsonException ex) { throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The uploader definition is not valid JSON: " + ex.Message, ex); }
        if (d is null) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The uploader definition is empty.");
        Validate(d);
        return d;
    }

    public static void Validate(CustomUploaderDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The uploader needs a name.");
        if (!Uri.TryCreate(d.RequestUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new ShotwiseException(ErrorCode.InvalidFileFormat, "requestUrl must be an absolute http(s) URL.");
        if (!Methods.Contains(d.Method)) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "method must be POST, PUT or PATCH.");
        if (!Bodies.Contains(d.Body)) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "body must be multipart, form, json or binary.");
        foreach (var h in d.Headers)
        {
            if (!HeaderName.IsMatch(h.Key) || h.Value.Contains('\r') || h.Value.Contains('\n'))
                throw new ShotwiseException(ErrorCode.InvalidFileFormat, $"Invalid header '{h.Key}'.");
        }
        if (string.IsNullOrWhiteSpace(d.Url)) d.Url = "{response}";
    }
}

/// <summary>Response mapping for custom uploaders: {response} {status} {json:path} {regex:pattern} {header:Name}.</summary>
public static class ResponseTemplate
{
    private static readonly Regex Token = new(@"\{(?<kind>response|status|json|regex|header)(?::(?<arg>[^{}]*))?\}", RegexOptions.Compiled);

    public static string Render(string template, int status, IReadOnlyDictionary<string, string> headers, string body)
    {
        JsonElement? json = null;
        var jsonTried = false;
        return Token.Replace(template, m =>
        {
            var arg = m.Groups["arg"].Success ? m.Groups["arg"].Value : "";
            switch (m.Groups["kind"].Value)
            {
                case "response": return body.Trim();
                case "status": return status.ToString(CultureInfo.InvariantCulture);
                case "header": return headers.TryGetValue(arg, out var h) ? h : "";
                case "regex":
                    try
                    {
                        var rm = Regex.Match(body, arg, RegexOptions.None, TimeSpan.FromSeconds(1));
                        return !rm.Success ? "" : rm.Groups.Count > 1 ? rm.Groups[1].Value : rm.Value;
                    }
                    catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { return ""; }
                case "json":
                    if (!jsonTried)
                    {
                        jsonTried = true;
                        try { json = JsonDocument.Parse(body).RootElement.Clone(); } catch (JsonException) { json = null; }
                    }
                    return json is { } root ? JsonPathLite.Select(root, arg) ?? "" : "";
                default: return m.Value;
            }
        });
    }
}

/// <summary>Tiny JSONPath subset: <c>$.a.b[0].c</c>, <c>a['b-c']</c>. Returns scalars as text, objects/arrays as raw JSON.</summary>
public static class JsonPathLite
{
    private static readonly Regex Step = new(@"\.?(?<name>[A-Za-z0-9_\-$@]+)|\[(?<idx>\d+)\]|\[['""](?<key>[^'""]+)['""]\]", RegexOptions.Compiled);

    public static string? Select(JsonElement root, string path)
    {
        var p = path.Trim();
        if (p.StartsWith('$')) p = p[1..];
        var cur = root;
        var pos = 0;
        while (pos < p.Length)
        {
            var m = Step.Match(p, pos);
            if (!m.Success || m.Index != pos) return null;
            pos += m.Length;
            if (m.Groups["idx"].Success)
            {
                if (cur.ValueKind != JsonValueKind.Array || !int.TryParse(m.Groups["idx"].Value, out var i) || i >= cur.GetArrayLength()) return null;
                cur = cur[i];
            }
            else
            {
                var name = m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["key"].Value;
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(name, out cur)) return null;
            }
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => cur.GetRawText(),
        };
    }
}

/// <summary>SHR-12 custom HTTP uploader: definitions live in %APPDATA%\Shotwise\share\custom\*.json; secrets only in the credential store.</summary>
public sealed class CustomHttpDestination : ShareDestinationBase
{
    private readonly HttpClient _http;
    private CustomUploaderDefinition? _chosen;
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(5);

    // Never redirect an upload: even non-Authorization headers can contain secrets.
    private static readonly HttpClient SafeHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    public CustomHttpDestination(ShareContext ctx, HttpClient? http = null) : base(ctx) => _http = http ?? SafeHttp;

    public override string Id => "custom";
    public override string DisplayName => "Custom uploader";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Custom;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Everything;
    public override bool CanShowOptions => true;
    public override bool IsConfigured => Definitions().Count > 0;
    public override IReadOnlyList<Core.Platform.PolicyFeature> RequiredPolicies => new[] { Core.Platform.PolicyFeature.CloudSharing };

    public string DefinitionsFolder => Path.Combine(Ctx.Store.FolderPath, "custom");

    public IReadOnlyList<CustomUploaderDefinition> Definitions()
    {
        var list = new List<CustomUploaderDefinition>();
        if (!Directory.Exists(DefinitionsFolder)) return list;
        foreach (var file in Directory.EnumerateFiles(DefinitionsFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try { list.Add(CustomUploaderParser.Parse(File.ReadAllText(file))); }
            catch (Exception ex) when (ex is ShotwiseException or IOException) { Log.LogWarning(ex, "Ignoring invalid custom uploader {File}", file); }
        }
        return list;
    }

    /// <summary>Validates and stores a definition file; returns its name.</summary>
    public string Import(string json)
    {
        var d = CustomUploaderParser.Parse(json);
        Directory.CreateDirectory(DefinitionsFolder);
        var safe = Naming.NamingEngine.Sanitize(d.Name);
        var path = Path.Combine(DefinitionsFolder, (safe.Length == 0 ? "uploader" : safe) + ".json");
        // Imported IDs are untrusted; a new definition must never inherit credentials.
        d.Id = Guid.NewGuid();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(output);
        writer.Write(ShotwiseJson.Serialize(d));
        return d.Name;
    }

    public void Remove(string name)
    {
        var safe = Naming.NamingEngine.Sanitize(name);
        var p = Path.Combine(DefinitionsFolder, safe + ".json");
        if (File.Exists(p)) File.Delete(p);
    }

    public void SetSecret(string uploaderName, string key, string value)
    {
        var def = Definitions().Single(d => d.Name == uploaderName);
        if (def.Id == Guid.Empty) throw new ShotwiseException(ErrorCode.InvalidArgument, "Re-import this legacy definition before storing secrets.");
        Ctx.Credentials.Write(CredentialKeys.Make("custom", $"{def.Id:D}:{key}"), value);
    }

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        var defs = Definitions().Where(d => files.All(f => Accepts(d, f))).ToList();
        if (defs.Count == 0)
        {
            await ui.ShowMessageAsync("Custom uploader", "No custom uploader accepts these files. Add a definition in the options.").ConfigureAwait(false);
            return false;
        }
        if (defs.Count == 1) { _chosen = defs[0]; return true; }
        var settings = LoadSettings<CustomSettings>();
        var form = new ShareForm { OkText = "Upload" };
        form.Add(new ShareFormField
        {
            Key = "u", Label = "Uploader", Kind = ShareFieldKind.Choice,
            Value = defs.Any(d => d.Name == settings.Last) ? settings.Last : defs[0].Name, Choices = defs.Select(d => (d.Name, d.Name)).ToList(),
        });
        if (!await ui.ShowFormAsync("Custom uploader", form).ConfigureAwait(false)) return false;
        _chosen = defs.First(d => d.Name == form.Get<string>("u"));
        settings.Last = _chosen.Name;
        SaveSettings(settings);
        return true;
    }

    public sealed class CustomSettings { public string Last { get; set; } = ""; }

    private static bool Accepts(CustomUploaderDefinition d, ShareFile f)
        => d.Formats.Count == 0 ? FormatKeys.Images.Contains(FormatKeys.Of(f)) || FormatKeys.Of(f) == "agif"
                                : d.Formats.Contains(FormatKeys.Of(f), StringComparer.OrdinalIgnoreCase);

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var defs = Definitions();
        var form = new ShareForm
        {
            Description = "Installed: " + (defs.Count == 0 ? "none" : string.Join(", ", defs.Select(d => d.Name))) +
                          ". Enter the path of a .json definition to add; or the name of an uploader to remove (prefix with '-'). To store a secret use 'Name:key=value'.",
        };
        form.Add(new ShareFormField { Key = "cmd", Label = "Definition file / command" });
        if (!await ui.ShowFormAsync("Custom uploaders", form).ConfigureAwait(false)) return;
        var cmd = (form.Get<string>("cmd") ?? "").Trim().Trim('"');
        if (cmd.Length == 0) return;
        try
        {
            if (cmd.StartsWith('-')) Remove(cmd[1..].Trim());
            else if (cmd.Contains('=') && cmd.Contains(':') && !File.Exists(cmd))
            {
                var colon = cmd.IndexOf(':');
                var eq = cmd.IndexOf('=');
                if (colon > 0 && eq > colon) SetSecret(cmd[..colon].Trim(), cmd[(colon + 1)..eq].Trim(), cmd[(eq + 1)..]);
            }
            else
            {
                var json = File.ReadAllText(cmd);
                var definition = CustomUploaderParser.Parse(json);
                var target = new Uri(definition.RequestUrl);
                var confirmation = new ShareForm { Description = $"Install uploader '{definition.Name}' sending files to {target.GetLeftPart(UriPartial.Authority)}?" +
                    (target.Scheme == "http" ? " Warning: HTTP sends files and secrets without encryption." : ""), OkText = "Install" };
                confirmation.Add(new ShareFormField { Key = "confirm", Label = "I trust this upload server", Kind = ShareFieldKind.Checkbox, Value = false });
                if (await ui.ShowFormAsync("Confirm uploader server", confirmation).ConfigureAwait(false) && confirmation.Get<bool>("confirm")) Import(json);
            }
        }
        catch (Exception ex) when (ex is ShotwiseException or IOException or UnauthorizedAccessException)
        {
            await ui.ShowMessageAsync("Custom uploader", ex.Message, true).ConfigureAwait(false);
        }
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var def = _chosen ?? Definitions().FirstOrDefault(d => files.All(f => Accepts(d, f)))
                  ?? throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "No custom uploader is configured.");
        var links = new List<string>();
        for (var i = 0; i < files.Count; i++)
            links.Add(await UploadOneAsync(def, files[i], Slice(progress, i, files.Count), ct).ConfigureAwait(false));
        return new ShareResult(ShareOutcome.Succeeded, $"Uploaded with {def.Name}.", links[0], links[0], links, OpenLinkAction(links[0]));
    }

    private string Substitute(CustomUploaderDefinition def, string text, ShareFile f)
    {
        var name = Path.GetFileName(f.Path);
        return Regex.Replace(text, @"\{(?<k>filename|name|ext|title|mime|secret)(?::(?<a>[^{}]*))?\}", m => m.Groups["k"].Value switch
        {
            "filename" => name,
            "name" => Path.GetFileNameWithoutExtension(name),
            "ext" => Path.GetExtension(name).TrimStart('.'),
            "title" => f.Title,
            "mime" => FormatKeys.ContentType(f.Path),
            "secret" => def.Id == Guid.Empty ? "" : Ctx.Credentials.Read(CredentialKeys.Make("custom", $"{def.Id:D}:{m.Groups["a"].Value}")) ?? "",
            _ => m.Value,
        });
    }

    private async Task<string> UploadOneAsync(CustomUploaderDefinition def, ShareFile f, IProgress<double>? progress, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(UploadTimeout);
        ct = deadline.Token;
        var args = def.Arguments.ToDictionary(kv => kv.Key, kv => Substitute(def, kv.Value, f));
        var headers = def.Headers.ToDictionary(kv => kv.Key, kv => Substitute(def, kv.Value, f));
        foreach (var h in headers.Values)
            if (h.Contains('\r') || h.Contains('\n')) throw new ShotwiseException(ErrorCode.InvalidArgument, "A header value contains a line break.");
        var url = def.RequestUrl;
        var mime = FormatKeys.ContentType(f.Path);

        using var resp = await HttpHelpers.SendAsync(_http, () =>
        {
            var target = url;
            HttpContent content;
            switch (def.Body.ToLowerInvariant())
            {
                case "binary":
                    if (args.Count > 0)
                        target += (target.Contains('?') ? "&" : "?") + string.Join('&', args.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
                    content = new ProgressFileContent(f.Path, mime, v => progress?.Report(v));
                    break;
                case "form":
                {
                    var fields = new Dictionary<string, string>(args) { [def.FileFormName] = Convert.ToBase64String(File.ReadAllBytes(f.Path)) };
                    content = new FormUrlEncodedContent(fields);
                    break;
                }
                case "json":
                {
                    var obj = new Dictionary<string, object>(args.ToDictionary(kv => kv.Key, kv => (object)kv.Value)) { [def.FileFormName] = Convert.ToBase64String(File.ReadAllBytes(f.Path)) };
                    content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");
                    break;
                }
                default:
                {
                    var form = new MultipartFormDataContent();
                    foreach (var kv in args) form.Add(new StringContent(kv.Value), kv.Key);
                    form.Add(new ProgressFileContent(f.Path, mime, v => progress?.Report(v)), def.FileFormName, Path.GetFileName(f.Path));
                    content = form;
                    break;
                }
            }
            var req = new HttpRequestMessage(new HttpMethod(def.Method.ToUpperInvariant()), target) { Content = content };
            foreach (var h in headers)
            {
                if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value)) content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            return req;
        }, ct).ConfigureAwait(false);

        var body = await HttpHelpers.ReadBoundedAsync(resp.Content, ct).ConfigureAwait(false);
        var respHeaders = resp.Headers.Concat(resp.Content.Headers)
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(",", g.SelectMany(x => x.Value)), StringComparer.OrdinalIgnoreCase);
        var status = (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode)
        {
            throw new ShotwiseException(ErrorCode.UploadRejected, $"Uploader answered HTTP {status}.");
        }
        var result = ResponseTemplate.Render(def.Url, status, respHeaders, body).Trim();
        if (result.Length == 0)
        {
            throw new ShotwiseException(ErrorCode.UploadRejected, "Uploader accepted the upload but returned no URL.");
        }
        return result;
    }
}
