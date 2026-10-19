using System.Diagnostics;
using System.Text;
using Shotwise.Core;
using Shotwise.Core.Library;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations;

/// <summary>
/// Argument templating for the Program destination. The template is split into arguments first (quotes group words),
/// then placeholders are substituted inside each argument, and the result is passed through ProcessStartInfo.ArgumentList.
/// A file name can therefore never add, split or inject arguments, whatever characters it contains.
/// Placeholders: {file} {dir} {name} (no extension) {ext} {title} {files} (expands to one argument per file).
/// </summary>
public static class ProgramArguments
{
    public static IReadOnlyList<string> Tokenize(string template)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(template)) return result;
        var sb = new StringBuilder();
        var inQuote = false;
        var has = false;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '"') { inQuote = !inQuote; has = true; }
            else if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (has) { result.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else { sb.Append(c); has = true; }
        }
        if (has) result.Add(sb.ToString());
        return result;
    }

    /// <summary>Expands the template for one file. An empty template means just "{file}".</summary>
    public static IReadOnlyList<string> Expand(string template, string file, string? title = null)
        => Expand(template, new[] { file }, title);

    public static IReadOnlyList<string> Expand(string template, IReadOnlyList<string> files, string? title = null)
    {
        var tokens = Tokenize(string.IsNullOrWhiteSpace(template) ? "{file}" : template);
        var first = files.Count > 0 ? files[0] : "";
        var result = new List<string>();
        foreach (var t in tokens)
        {
            if (t == "{files}") { result.AddRange(files); continue; }
            result.Add(t
                .Replace("{file}", first, StringComparison.Ordinal)
                .Replace("{dir}", Path.GetDirectoryName(first) ?? "", StringComparison.Ordinal)
                .Replace("{name}", Path.GetFileNameWithoutExtension(first), StringComparison.Ordinal)
                .Replace("{ext}", Path.GetExtension(first).TrimStart('.'), StringComparison.Ordinal)
                .Replace("{title}", title ?? Path.GetFileNameWithoutExtension(first), StringComparison.Ordinal));
        }
        return result;
    }

    /// <summary>Human-readable command line (for logs / option previews only; never executed through a shell).</summary>
    public static string Quote(IEnumerable<string> args)
        => string.Join(' ', args.Select(a => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
}

public sealed class ProgramEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "\"{file}\"";
    public bool AcceptImages { get; set; } = true;
    public bool AcceptVideo { get; set; } = true;
    public bool AcceptGif { get; set; } = true;
    public string WorkingDirectory { get; set; } = "";

    /// <summary>"Send to video editor" preset: a program that accepts video and GIF files only.</summary>
    public static ProgramEntry VideoEditor(string name, string executable) => new()
    {
        Name = name, Executable = executable, AcceptImages = false, AcceptVideo = true, AcceptGif = true,
    };

    internal bool Accepts(ShareFile f) => f.Kind switch
    {
        MediaKind.Video => AcceptVideo,
        MediaKind.Gif => AcceptGif,
        _ => AcceptImages,
    };
}

public sealed class ProgramDestinationSettings
{
    public List<ProgramEntry> Programs { get; set; } = new();
    public string? LastUsed { get; set; }
}

/// <summary>SHR-06: launches user-configured executables with an argument template. Scripts (.bat/.cmd) are refused.</summary>
public sealed class ProgramDestination : ShareDestinationBase
{
    private ProgramEntry? _chosen;

    public ProgramDestination(ShareContext ctx) : base(ctx) { }

    public override string Id => "program";
    public override string DisplayName => "Program";
    public override string IconGlyph => "\uE756";
    public override DestinationCategory Category => DestinationCategory.BuiltIn;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Everything;
    public override bool CanShowOptions => true;
    public override bool IsConfigured => LoadSettings<ProgramDestinationSettings>().Programs.Count > 0;

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        var s = LoadSettings<ProgramDestinationSettings>();
        var eligible = s.Programs.Where(p => files.All(p.Accepts)).ToList();
        if (eligible.Count == 0)
        {
            await ui.ShowMessageAsync("Program", s.Programs.Count == 0
                ? "No programs are configured yet. Add one in the Program options."
                : "None of the configured programs accepts this kind of file.").ConfigureAwait(false);
            return false;
        }
        if (eligible.Count == 1) { _chosen = eligible[0]; return true; }
        var form = new ShareForm { OkText = "Open" };
        form.Add(new ShareFormField
        {
            Key = "program", Label = "Program", Kind = ShareFieldKind.Choice,
            Value = eligible.Any(p => p.Id == s.LastUsed) ? s.LastUsed : eligible[0].Id,
            Choices = eligible.Select(p => (p.Id, p.Name)).ToList(),
        });
        if (!await ui.ShowFormAsync("Send to program", form).ConfigureAwait(false)) return false;
        _chosen = eligible.FirstOrDefault(p => p.Id == form.Get<string>("program")) ?? eligible[0];
        s.LastUsed = _chosen.Id;
        SaveSettings(s);
        return true;
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<ProgramDestinationSettings>();
        var form = new ShareForm { Description = "Add a program. Use {file}, {dir}, {name}, {ext}, {title} or {files} in the arguments." };
        form.Add(new ShareFormField { Key = "name", Label = "Name", Placeholder = "Image editor" });
        form.Add(new ShareFormField { Key = "exe", Label = "Executable path", Placeholder = @"C:\Program Files\...\app.exe" });
        form.Add(new ShareFormField { Key = "args", Label = "Arguments", Value = "\"{file}\"" });
        form.Add(new ShareFormField { Key = "images", Label = "Accept images", Kind = ShareFieldKind.Checkbox, Value = true });
        form.Add(new ShareFormField { Key = "video", Label = "Accept video", Kind = ShareFieldKind.Checkbox, Value = true });
        form.Add(new ShareFormField { Key = "gif", Label = "Accept animated GIF", Kind = ShareFieldKind.Checkbox, Value = true });
        if (!await ui.ShowFormAsync("Add program", form).ConfigureAwait(false)) return;
        var exe = (form.Get<string>("exe") ?? "").Trim().Trim('"');
        if (exe.Length == 0) return;
        try { exe = ResolveExecutable(exe); }
        catch (ShotwiseException ex)
        {
            await ui.ShowMessageAsync("Program", ex.Message, true).ConfigureAwait(false);
            return;
        }
        if (IsScript(exe))
        {
            await ui.ShowMessageAsync("Program", "Batch and command scripts are not allowed. Choose an executable.", true).ConfigureAwait(false);
            return;
        }
        s.Programs.Add(new ProgramEntry
        {
            Name = string.IsNullOrWhiteSpace(form.Get<string>("name")) ? Path.GetFileNameWithoutExtension(exe) : form.Get<string>("name")!.Trim(),
            Executable = exe,
            Arguments = form.Get<string>("args") ?? "\"{file}\"",
            AcceptImages = form.Get<bool>("images"),
            AcceptVideo = form.Get<bool>("video"),
            AcceptGif = form.Get<bool>("gif"),
        });
        SaveSettings(s);
    }

    public override Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var entry = _chosen ?? LoadSettings<ProgramDestinationSettings>().Programs.FirstOrDefault(p => files.All(p.Accepts))
                    ?? throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "No program is configured.");
        return Task.Run(() =>
        {
            var exe = ResolveExecutable(entry.Executable);
            if (IsScript(exe)) throw new ShotwiseException(ErrorCode.ShareFailed, "Scripts cannot be used as share programs.");
            var paths = files.Select(f => Path.GetFullPath(f.Path)).ToList();
            var perFile = !entry.Arguments.Contains("{files}", StringComparison.Ordinal);
            var batches = perFile ? paths.Select(p => (IReadOnlyList<string>)new[] { p }).ToList() : new List<IReadOnlyList<string>> { paths };
            for (var i = 0; i < batches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var a in ProgramArguments.Expand(entry.Arguments, batches[i], files.Count == 1 ? files[0].Title : null)) psi.ArgumentList.Add(a);
                if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory) && Directory.Exists(entry.WorkingDirectory)) psi.WorkingDirectory = entry.WorkingDirectory;
                try { using var _ = Process.Start(psi); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    throw new ShotwiseException(ErrorCode.ProgramNotFound, $"Could not start {entry.Name}: {ex.Message}", ex);
                }
                progress?.Report((i + 1) / (double)batches.Count);
            }
            return new ShareResult(ShareOutcome.Succeeded, $"Opened in {entry.Name}.");
        }, ct);
    }

    internal static bool IsScript(string path) => Path.GetExtension(path).ToLowerInvariant() is ".bat" or ".cmd";

    internal static string ResolveExecutable(string exe)
    {
        if (Path.IsPathFullyQualified(exe) && File.Exists(exe)) return Path.GetFullPath(exe);
        if (!exe.Contains(Path.DirectorySeparatorChar) && !exe.Contains(Path.AltDirectorySeparatorChar))
        {
            var names = Path.HasExtension(exe) ? new[] { exe } : new[] { exe + ".exe", exe };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                foreach (var n in names)
                {
                    try { if (!Path.IsPathFullyQualified(dir.Trim('"'))) continue; var p = Path.Combine(dir.Trim('"'), n); if (File.Exists(p)) return p; } catch (ArgumentException) { }
                }
        }
        throw new ShotwiseException(ErrorCode.ProgramNotFound, "Program not found: " + exe);
    }
}
