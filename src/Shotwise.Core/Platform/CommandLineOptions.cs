namespace Shotwise.Core.Platform;

/// <summary>
/// Parsed command line (SPEC PLT-01): <c>Shotwise.exe [file…] [--capture[=preset]] [--editor] [--background]</c>.
/// Also accepts <c>/capture</c>-style switches and <c>shotwise://</c> URIs (passed through in <see cref="Uris"/>).
/// </summary>
public sealed record CommandLineOptions
{
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Uris { get; init; } = Array.Empty<string>();
    /// <summary>True when --capture was given; <see cref="CapturePreset"/> holds the optional preset id or name.</summary>
    public bool Capture { get; init; }
    public string? CapturePreset { get; init; }
    public bool OpenEditor { get; init; }
    /// <summary>Start in the tray without showing any window (autostart).</summary>
    public bool Background { get; init; }
    /// <summary>"--exit": ask the running instance to close gracefully (used by the uninstaller); never starts a new instance.</summary>
    public bool Exit { get; init; }
    public IReadOnlyList<string> Unknown { get; init; } = Array.Empty<string>();

    public bool HasWork => Files.Count > 0 || Uris.Count > 0 || Capture || OpenEditor;

    public static CommandLineOptions Parse(IEnumerable<string> args)
    {
        var files = new List<string>();
        var uris = new List<string>();
        var unknown = new List<string>();
        bool capture = false, editor = false, background = false, exit = false;
        string? preset = null;
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var a = raw.Trim();
            if (a.StartsWith("shotwise:", StringComparison.OrdinalIgnoreCase)) { uris.Add(a); continue; }
            var isSwitch = a.StartsWith("--", StringComparison.Ordinal) || (a.StartsWith('/') && a.Length > 1 && !a.Contains('\\') && a.IndexOf('/', 1) < 0 && !File.Exists(a));
            if (!isSwitch) { files.Add(a); continue; }
            var body = a.TrimStart('-', '/');
            var eq = body.IndexOfAny(new[] { '=', ':' });
            var name = (eq < 0 ? body : body[..eq]).ToLowerInvariant();
            var value = eq < 0 ? null : body[(eq + 1)..].Trim('"');
            switch (name)
            {
                case "capture": capture = true; preset = string.IsNullOrWhiteSpace(value) ? null : value; break;
                case "editor": editor = true; break;
                case "background" or "tray" or "minimized": background = true; break;
                case "exit" or "quit": exit = true; break;
                default: unknown.Add(a); break;
            }
        }
        return new CommandLineOptions { Files = files, Uris = uris, Capture = capture, CapturePreset = preset, OpenEditor = editor, Background = background, Exit = exit, Unknown = unknown };
    }
}
