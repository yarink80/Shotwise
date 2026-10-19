using System.Diagnostics;

namespace StampGen;

/// <summary>
/// Pinned upstream inputs. Everything is fetched into a cache directory outside the repository (downloads are treated as data:
/// parsed, never executed). Bump a pin only together with a review of the licence and a regenerated contact sheet.
/// </summary>
internal static class Sources
{
    // Microsoft Fluent Emoji — MIT. Only metadata.json and the Color (fallback: Flat) SVGs are checked out (sparse, blobless).
    public const string FluentRepo = "https://github.com/microsoft/fluentui-emoji.git";
    public const string FluentCommit = "1ffb34c752ecf5d402f04cfb4b392c77f57c54bc";

    // Unicode CLDR annotations (localized emoji names + keywords) — Unicode License v3.
    public const string CldrTag = "48.2.3";
    public static readonly string[] CldrLocales = { "en", "de", "es", "fr", "ja", "pt" };

    // Fonts whose glyph outlines are converted to paths at authoring time (no font file is shipped).
    public const string GoogleFontsCommit = "47d54ca1ebb14fd864695f1d39dc734f39017c33";
    public static readonly (string Key, string Path)[] Fonts =
    {
        ("fira-bold", "ofl/firasans/FiraSans-Bold.ttf"),
        ("fira-semibold", "ofl/firasans/FiraSans-SemiBold.ttf"),
        ("fira-medium", "ofl/firasans/FiraSans-Medium.ttf"),
        ("fira-regular", "ofl/firasans/FiraSans-Regular.ttf"),
        ("fira-ofl", "ofl/firasans/OFL.txt"),
        ("anton", "ofl/anton/Anton-Regular.ttf"),
        ("anton-ofl", "ofl/anton/OFL.txt"),
        ("bangers", "ofl/bangers/Bangers-Regular.ttf"),
        ("bangers-ofl", "ofl/bangers/OFL.txt"),
        ("marker", "apache/permanentmarker/PermanentMarker-Regular.ttf"),
        ("marker-license", "apache/permanentmarker/LICENSE.txt"),
    };

    // Canonical emoji order (Unicode emoji-test.txt, Unicode License v3) — used only to sort the emoji categories.
    public const string EmojiTestUrl = "https://www.unicode.org/Public/emoji/16.0/emoji-test.txt";
    public static string EmojiTestFile(string cache) => Path.Combine(cache, "cldr", "emoji-test-16.0.txt");

    public static string FluentDir(string cache) => Path.Combine(cache, "fluent");
    public static string CldrFile(string cache, string kind, string loc) => Path.Combine(cache, "cldr", $"{kind}-{loc}.json");
    public static string FontFile(string cache, string key) => Path.Combine(cache, "fonts", key + Path.GetExtension(Fonts.First(f => f.Key == key).Path));

    public static void Fetch(string cache)
    {
        Directory.CreateDirectory(cache);
        FetchFluent(cache);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("shotwise-stamp-gen/1.0");
        foreach (var loc in CldrLocales)
        {
            Download(http, $"https://raw.githubusercontent.com/unicode-org/cldr-json/{CldrTag}/cldr-json/cldr-annotations-full/annotations/{loc}/annotations.json", CldrFile(cache, "ann", loc));
            Download(http, $"https://raw.githubusercontent.com/unicode-org/cldr-json/{CldrTag}/cldr-json/cldr-annotations-derived-full/annotationsDerived/{loc}/annotations.json", CldrFile(cache, "der", loc));
        }
        Download(http, $"https://raw.githubusercontent.com/unicode-org/cldr-json/{CldrTag}/LICENSE", Path.Combine(cache, "cldr", "LICENSE"));
        Download(http, EmojiTestUrl, EmojiTestFile(cache));
        foreach (var (key, path) in Fonts)
            Download(http, $"https://raw.githubusercontent.com/google/fonts/{GoogleFontsCommit}/{path}", FontFile(cache, key));
    }

    private static void Download(HttpClient http, string url, string dest)
    {
        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Console.WriteLine("  get " + url);
        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        File.WriteAllBytes(dest + ".part", bytes);
        File.Move(dest + ".part", dest, true);
    }

    private static void FetchFluent(string cache)
    {
        var dir = FluentDir(cache);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            Git(cache, "clone", "--filter=blob:none", "--no-checkout", FluentRepo, "fluent");
        }
        var head = Git(dir, "rev-parse", "HEAD").Trim();
        Git(dir, "sparse-checkout", "set", "--no-cone", "/assets/*/metadata.json", "/assets/*/Color/*.svg", "/assets/*/Default/Color/*.svg",
            "/assets/*/Flat/*.svg", "/assets/*/Default/Flat/*.svg", "/LICENSE");
        if (!head.Equals(FluentCommit, StringComparison.OrdinalIgnoreCase))
        {
            try { Git(dir, "cat-file", "-e", FluentCommit + "^{commit}"); }
            catch (InvalidOperationException) { Git(dir, "fetch", "--filter=blob:none", "origin", FluentCommit); }
            Git(dir, "checkout", "--detach", FluentCommit);
        }
        else Git(dir, "checkout", "--detach", FluentCommit);
        Console.WriteLine($"  fluent emoji @ {FluentCommit[..10]}: {Directory.GetDirectories(Path.Combine(dir, "assets")).Length} assets");
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Result;
    }
}
