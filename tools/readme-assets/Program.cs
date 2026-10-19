using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Effects;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Vision;
using Shotwise.Rendering;

namespace ReadmeAssets;

/// <summary>
/// Renders the README images into docs/images.
/// <code>
/// dotnet run -c Release --project tools/readme-assets -- [REPO] [--only hero,shots,recording,stamps,effects,redact,ocr,demo]
/// </code>
/// Inputs: app screenshots (the hero, shots and recording steps; read from docs/screenshots, which is not tracked by git, or
/// from the folder in SHOTWISE_README_SHOTS — render them from the app, e.g. with --smoke-screenshots), the app icon and the
/// stamp packs. Without screenshots, run only the self-contained steps: --only stamps,effects,redact,ocr,demo.
/// Everything else (sample captures, annotations, effects, OCR, Smart Redact, GIF encoding) is produced here with Shotwise's
/// own libraries, so the output is reproducible on any Windows 10/11 machine with an English OCR language installed.
/// Data root is redirected to a temp folder so your real Shotwise profile is never touched.
/// </summary>
internal static class Program
{
    internal static IOcrService? Ocr;

    private static async Task<int> Main(string[] args)
    {
        string Opt(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var repo = args.Length > 0 && !args[0].StartsWith("--") ? Path.GetFullPath(args[0]) : FindRepo();
        var only = Opt("--only", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Run(string step) => only.Count == 0 || only.Contains(step);

        Gfx.Repo = repo;
        Gfx.OutDir = Path.Combine(repo, "docs", "images");
        Directory.CreateDirectory(Gfx.OutDir);
        Environment.SetEnvironmentVariable("SHOTWISE_DATA_ROOT", Path.Combine(Path.GetTempPath(), "shotwise-readme-assets"));

        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddShotwiseCore()
            .AddShotwiseRendering()
            .AddShotwiseMedia()
            .AddShotwiseVision();
        using var sp = services.BuildServiceProvider();
        var codec = sp.GetRequiredService<IImageCodec>();
        var renderer = new SkiaDocumentRenderer(codec);
        Ocr = sp.GetRequiredService<IOcrService>();

        Console.WriteLine($"repo {repo}\nout  {Gfx.OutDir}");
        if (Run("hero")) { Showcase.Hero(false); Showcase.Hero(true); }
        if (Run("shots")) { Showcase.Screenshots(); Showcase.Selection(); }
        if (Run("recording")) Showcase.Recording();
        if (Run("stamps")) Showcase.Stamps();
        if (Run("effects")) Showcase.Effects(sp.GetRequiredService<IEffectProcessor>());
        if (Run("redact")) await Showcase.SmartRedactAsync(sp.GetRequiredService<ISmartRedactService>(), renderer, codec);
        if (Run("ocr")) await Showcase.GrabTextAsync(sp.GetRequiredService<IOcrService>());
        if (Run("demo")) await Demo.RunAsync(sp.GetRequiredService<IGifService>(), renderer, codec);

        var total = Directory.GetFiles(Gfx.OutDir).Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"docs/images total: {total / 1024.0 / 1024.0:0.00} MB");
        var tooBig = Directory.GetFiles(Gfx.OutDir).Where(f => new FileInfo(f).Length > (f.EndsWith(".gif") ? 8_000_000 : 1_500_000)).ToList();
        foreach (var f in tooBig) Console.Error.WriteLine($"TOO BIG: {Path.GetFileName(f)}");
        return tooBig.Count == 0 && total < 15_000_000 ? 0 : 1;
    }

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Shotwise.sln"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }
}
