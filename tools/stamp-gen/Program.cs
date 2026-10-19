using System.Diagnostics;

namespace StampGen;

/// <summary>
/// Shotwise stamp generator.
/// <code>
/// dotnet run -c Release --project tools/stamp-gen -- fetch  [--cache DIR]
/// dotnet run -c Release --project tools/stamp-gen -- build  REPO [--cache DIR] [--only folder,folder]
/// dotnet run -c Release --project tools/stamp-gen -- sheets REPO [--preview DIR] [--only folder,folder]
/// dotnet run -c Release --project tools/stamp-gen -- all    REPO [--cache DIR] [--preview DIR]
/// </code>
/// <c>build</c> replaces every folder under src/Shotwise.App/Content/stamps (this tool owns them) and fails if any stamp does
/// not render through Svg.Skia. Output is deterministic for the same pins.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: stamp-gen fetch|build|sheets|all REPO [--cache DIR] [--preview DIR] [--only a,b]"); return 2; }
        var cmd = args[0];
        string Opt(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var repo = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : ".");
        var cache = Path.GetFullPath(Opt("--cache", Path.Combine(Path.GetTempPath(), "shotwise-stamp-gen")));
        var preview = Opt("--preview", "");
        var only = Opt("--only", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stamps = Path.Combine(repo, "src", "Shotwise.App", "Content", "stamps");
        Txt.CacheDir = cache;
        var sw = Stopwatch.StartNew();
        void Log(string s) => Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] {s}");

        if (cmd is "fetch" or "build" or "all")
        {
            Log("fetch → " + cache);
            Sources.Fetch(cache);
        }
        if (cmd is "build" or "all")
        {
            var packs = new List<Pack>();
            void Add(Func<IEnumerable<Pack>> make, params string[] folders)
            {
                if (only.Count > 0 && !folders.Any(only.Contains)) return;
                var t = sw.Elapsed;
                var made = make().ToList();
                packs.AddRange(made);
                Log($"  {string.Join(", ", made.Select(p => $"{p.Folder}={p.Entries.Count}"))}  ({(sw.Elapsed - t).TotalSeconds:0.0}s)");
            }
            Add(() => new[] { LabelsPack.Build() }, "labels");
            Add(SymbolsPack.Build, "badges", "arrows", "marks");
            Add(() => new[] { StickersPack.Build() }, "stickers");
            Add(() => new[] { KeyboardPack.Build() }, "keyboard");
            Add(() => new[] { InterfacePack.Build() }, "interface");
            Add(() => FluentPack.Build(cache, Log), "emoji-people", "emoji-nature", "emoji-food", "emoji-activities", "emoji-travel", "emoji-objects", "emoji-symbols");

            Directory.CreateDirectory(stamps);
            foreach (var dir in Directory.GetDirectories(stamps))
            {
                var folder = Path.GetFileName(dir);
                if (only.Count > 0 && !only.Contains(folder)) continue;
                Directory.Delete(dir, true);
            }
            foreach (var p in packs) p.Write(stamps);
            var failures = Render.ValidateAll(stamps, Log);
            foreach (var f in failures.Take(50)) Console.Error.WriteLine("  FAIL " + f);
            Report(stamps, Log);
            if (failures.Count > 0) return 1;
        }
        if (cmd is "sheets" or "all")
        {
            Render.Sheets(stamps, Path.Combine(repo, "docs", "screenshots"), preview.Length > 0 ? Path.GetFullPath(preview) : null, Log,
                only.Count > 0 ? only : null);
        }
        if (cmd is "validate")
        {
            var failures = Render.ValidateAll(stamps, Log);
            foreach (var f in failures) Console.Error.WriteLine("  FAIL " + f);
            Report(stamps, Log);
            return failures.Count > 0 ? 1 : 0;
        }
        Log("done");
        return 0;
    }

    private static void Report(string stamps, Action<string> log)
    {
        long total = 0; var count = 0;
        foreach (var dir in Directory.GetDirectories(stamps).OrderBy(d => d))
        {
            var files = Directory.GetFiles(dir);
            var bytes = files.Sum(f => new FileInfo(f).Length);
            var n = files.Count(f => f.EndsWith(".svg"));
            total += bytes; count += n;
            log($"  {Path.GetFileName(dir),-18} {n,5} stamps {bytes / 1024.0 / 1024.0,7:0.00} MB");
        }
        log($"  TOTAL              {count,5} stamps {total / 1024.0 / 1024.0,7:0.00} MB");
    }
}
