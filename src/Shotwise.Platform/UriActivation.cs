namespace Shotwise.Platform;

/// <summary>Validates protocol activations; the App must confirm before importing the returned HTTPS source.</summary>
public sealed class UriActivation
{
    public sealed record ImportRequest(string Kind, Uri Source);
    public event EventHandler<ImportRequest>? ImportRequested;
    public bool HandleArguments(IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        var raw = args.FirstOrDefault(a => a.StartsWith("shotwise:", StringComparison.OrdinalIgnoreCase));
        if (raw is null) return args.Contains("--uri", StringComparer.OrdinalIgnoreCase);
        if (TryParse(raw, out var request)) ImportRequested?.Invoke(this, request!);
        // The presence of a protocol activation consumes ALL arguments, including malformed activations.
        return true;
    }
    public static bool TryParse(string raw, out ImportRequest? request)
    {
        request = null;
        if (raw.Length > 8192 || raw.Any(c => char.IsControl(c) || c is '"' or '\\') || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme != "shotwise" || uri.Host != "import" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Fragment.Length != 0) return false;
        var kind = uri.AbsolutePath.Trim('/');
        if (kind is not ("stamps" or "templates" or "themes") || !uri.Query.StartsWith("?url=", StringComparison.Ordinal) || uri.Query.Contains('&')) return false;
        var value = Uri.UnescapeDataString(uri.Query[5..]);
        if (value.Any(c => char.IsControl(c) || c is '"' or '\\') || !Uri.TryCreate(value, UriKind.Absolute, out var source)
            || source.Scheme != "https" || source.UserInfo.Length != 0 || source.Fragment.Length != 0 || source.IsLoopback) return false;
        request = new(kind, source); return true;
    }
}
