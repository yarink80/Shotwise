using System.IO;

namespace Shotwise.App.Smoke;

/// <summary>
/// Hidden integration smoke mode (P3 C2). <c>Shotwise.exe --smoke [--smoke-out=DIR] [--smoke-screenshots=DIR] [--smoke-no-video]</c>
/// runs a scripted sequence against the fully composed app (no user input), writes <c>smoke-results.md</c> and exits with
/// 0 (all passed) or 2 (failures). Always run it with <c>SHOTWISE_DATA_ROOT</c> pointing at a throw-away folder.
/// </summary>
public sealed record SmokeOptions(string OutputDirectory, string? ScreenshotDirectory, bool RecordVideo)
{
    /// <summary>Default overall limit for a smoke run (<c>--smoke-timeout=SECONDS</c> overrides it).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Overall watchdog: when exceeded the run writes a failure report and exits with code 2.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>Removes the smoke switches from <paramref name="args"/>; returns null when <c>--smoke</c> is absent.</summary>
    public static SmokeOptions? TryParse(ref string[] args)
    {
        if (!args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase))) return null;
        string? output = null, shots = null;
        bool video = true;
        var timeout = DefaultTimeout;
        var rest = new List<string>();
        foreach (var a in args)
        {
            if (string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase)) continue;
            if (a.StartsWith("--smoke-timeout=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(a["--smoke-timeout=".Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sec) && sec > 0)
                    timeout = TimeSpan.FromSeconds(Math.Min(sec, 3600));
                continue;
            }
            if (a.StartsWith("--smoke-out=", StringComparison.OrdinalIgnoreCase)) { output = a["--smoke-out=".Length..].Trim('"'); continue; }
            if (a.StartsWith("--smoke-screenshots=", StringComparison.OrdinalIgnoreCase)) { shots = a["--smoke-screenshots=".Length..].Trim('"'); continue; }
            if (string.Equals(a, "--smoke-no-video", StringComparison.OrdinalIgnoreCase)) { video = false; continue; }
            rest.Add(a);
        }
        args = rest.ToArray();
        output ??= Path.Combine(Path.GetTempPath(), "shotwise-smoke-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        return new SmokeOptions(Path.GetFullPath(output), shots is null ? null : Path.GetFullPath(shots), video) { Timeout = timeout };
    }

    /// <summary>
    /// Writes <c>smoke-results.md</c> for a run that could not reach (or finish) the scripted steps: startup failure, a second
    /// instance, or the watchdog. Best effort; never throws.
    /// </summary>
    public void WriteFailure(string stage, string detail, IReadOnlyList<string>? completedSteps = null)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Shotwise smoke run").AppendLine();
            sb.AppendLine($"- Date: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"- Machine: {Environment.MachineName}, {Environment.OSVersion.VersionString}");
            sb.AppendLine($"- Data root: `{Environment.GetEnvironmentVariable("SHOTWISE_DATA_ROOT") ?? "(profile!)"}`");
            sb.AppendLine("- Result: **FAILED before completion** (exit code 2)").AppendLine();
            sb.AppendLine("| Step | Result | Detail | ms |").AppendLine("|---|---|---|---|");
            foreach (var s in completedSteps ?? []) sb.AppendLine(s);
            sb.AppendLine($"| {stage} | **FAIL** | {detail.Replace("|", "/").Replace("\r", " ").Replace("\n", " ")} | |");
            Shotwise.Core.Serialization.AtomicFile.WriteAllText(Path.Combine(OutputDirectory, "smoke-results.md"), sb.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
