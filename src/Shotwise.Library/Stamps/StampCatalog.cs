using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Styles;

namespace Shotwise.Library.Stamps;

/// <summary>
/// Stamp catalog. A <i>pack</i> is a folder (built-in: <c>Content\stamps\&lt;category&gt;\</c>, user: <c>%APPDATA%\Shotwise\stamps\&lt;pack&gt;\</c>)
/// containing <c>packageinfo.json</c> and the stamp files. See <see cref="StampPackageInfo"/> for the schema. Stamp ids are
/// <c>&lt;categoryId&gt;/&lt;file stem&gt;</c>; user categories are prefixed <c>user.</c>.
/// </summary>
public sealed class StampCatalog : IStampCatalog
{
    public const string PackageInfoFile = "packageinfo.json";
    public const string PackExtension = ".shotstamps";
    public const string MyStampsPackId = "my-stamps";
    public const string FavoritesCategoryId = "favorites";
    private const long MaxStampBytes = 16L * 1024 * 1024;
    private const long MaxPackBytes = 256L * 1024 * 1024;
    private const int MaxPackStamps = 5000;

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp", [".gif"] = "image/gif", [".pdf"] = "application/pdf",
    };

    private sealed record Category(string Id, Dictionary<string, string> Names, int Order, bool IsUser, string Dir);

    private sealed record Stamp(string Id, Category Category, string FullPath, string MediaType, string Stem,
        Dictionary<string, string> Names, Dictionary<string, List<string>> Keywords, int Index);

    private readonly string _builtInRoot;
    private readonly string _userRoot;
    private readonly string _favoritesFile;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private List<Category>? _categories;
    private List<Stamp>? _stamps;
    private HashSet<string>? _favorites;

    public StampCatalog(IAppPaths paths, ILogger<StampCatalog>? logger = null) : this(Path.Combine(paths.Content, "stamps"), Path.Combine(paths.AppData, "stamps"), logger) { }

    internal StampCatalog(string builtInRoot, string userRoot, ILogger? logger = null)
    {
        _builtInRoot = builtInRoot;
        _userRoot = userRoot;
        _favoritesFile = Path.Combine(userRoot, "favorites.json");
        _log = Shotwise.Core.Platform.LogRedaction.Wrap(logger ?? NullLogger.Instance);
    }

    public event EventHandler? Changed;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log.LogWarning(ex, "A stamp Changed handler threw."); }
    }

    /// <summary>Forces a rescan of the built-in and user folders on next use.</summary>
    public void Reload() { lock (_lock) { _categories = null; _stamps = null; } }

    // ------------------------------------------------------------------ loading

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_stamps is not null) return;
            var cats = new List<Category>();
            var stamps = new List<Stamp>();
            LoadRoot(_builtInRoot, false, cats, stamps);
            LoadRoot(_userRoot, true, cats, stamps);
            _categories = cats;
            _stamps = stamps;
            _favorites ??= ReadFavorites();
        }
    }

    private void LoadRoot(string root, bool user, List<Category> cats, List<Stamp> stamps)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var info = Path.Combine(dir, PackageInfoFile);
            if (!File.Exists(info)) continue;
            try
            {
                if (new FileInfo(info).Length > 8 * 1024 * 1024) continue;
                var pkg = StampPackageInfo.Parse(File.ReadAllBytes(info));
                var folder = Path.GetFileName(dir);
                var baseId = SafeId(string.IsNullOrWhiteSpace(pkg.Id) ? folder : pkg.Id!);
                var id = user ? "user." + baseId : baseId;
                if (cats.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                var names = pkg.Category.Count > 0 ? pkg.Category : new Dictionary<string, string> { ["en"] = Humanize(folder) };
                var cat = new Category(id, names, pkg.Order ?? (user ? 10_000 : 1000), user, dir);
                cats.Add(cat);
                var dirFull = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                var idx = 0;
                foreach (var e in pkg.Stamps)
                {
                    if (string.IsNullOrWhiteSpace(e.Filename)) continue;
                    var full = Path.GetFullPath(Path.Combine(dir, e.Filename));
                    if (!full.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue; // traversal / missing
                    var ext = Path.GetExtension(full);
                    if (!MediaTypes.TryGetValue(ext, out var mt)) continue;
                    var stem = Path.GetFileNameWithoutExtension(full);
                    var sid = id + "/" + stem;
                    if (stamps.Any(s => s.Id.Equals(sid, StringComparison.OrdinalIgnoreCase))) continue;
                    var nm = e.Name.Count > 0 ? e.Name : new Dictionary<string, string> { ["en"] = Humanize(stem) };
                    stamps.Add(new Stamp(sid, cat, full, mt, stem, nm, e.Keywords, idx++));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Skipping stamp pack '{Dir}'.", dir);
            }
        }
    }

    private static string SafeId(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-', '.');

    private static string Humanize(string stem)
    {
        var s = Regex.Replace(stem, @"[-_.]+", " ").Trim();
        return s.Length == 0 ? stem : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private HashSet<string> ReadFavorites()
    {
        try
        {
            if (!File.Exists(_favoritesFile)) return new(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_favoritesFile));
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("favorites", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var v in arr.EnumerateArray()) if (v.GetString() is { } s) set.Add(s);
            return set;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    // ------------------------------------------------------------------ culture helpers

    internal static IReadOnlyList<string> CultureChain(string? uiCulture)
    {
        var chain = new List<string>();
        void Add(string s) { if (!string.IsNullOrWhiteSpace(s) && !chain.Contains(s, StringComparer.OrdinalIgnoreCase)) chain.Add(s); }
        var c = (uiCulture ?? "").Trim();
        Add(c);
        var i = c.IndexOf('-');
        if (i > 0) Add(c[..i]);
        Add("en");
        return chain;
    }

    private static string? PickName(Dictionary<string, string> map, IReadOnlyList<string> chain)
    {
        foreach (var c in chain) if (map.TryGetValue(c, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return map.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    internal static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ IStampCatalog

    public Task<IReadOnlyList<StampCategory>> GetCategoriesAsync()
    {
        EnsureLoaded();
        var chain = CultureChain(CultureInfo.CurrentUICulture.Name);
        lock (_lock)
        {
            IReadOnlyList<StampCategory> list = _categories!
                .OrderBy(c => c.IsUser).ThenBy(c => c.Order).ThenBy(c => PickName(c.Names, chain), StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new StampCategory(c.Id, PickName(c.Names, chain) ?? c.Id, _stamps!.Count(s => s.Category == c), c.IsUser))
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<StampInfo>> SearchAsync(string query, string? category, string uiCulture)
    {
        EnsureLoaded();
        var chain = CultureChain(uiCulture);
        var tokens = Fold(query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        lock (_lock)
        {
            IEnumerable<Stamp> pool = _stamps!;
            if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
                pool = category.Equals(FavoritesCategoryId, StringComparison.OrdinalIgnoreCase)
                    ? pool.Where(s => _favorites!.Contains(s.Id))
                    : pool.Where(s => s.Category.Id.Equals(category, StringComparison.OrdinalIgnoreCase));

            var scored = new List<(Stamp S, int Score)>();
            foreach (var s in pool)
            {
                var score = 0;
                if (tokens.Length > 0)
                {
                    var kws = chain.Where(c => s.Keywords.ContainsKey(c)).SelectMany(c => s.Keywords[c]).Select(Fold).Where(k => k.Length > 0).Distinct().ToList();
                    var name = Fold(PickName(s.Names, chain) ?? s.Stem);
                    var stem = Fold(s.Stem.Replace('-', ' ').Replace('_', ' '));
                    var catName = Fold(PickName(s.Category.Names, chain) ?? "");
                    var all = true;
                    foreach (var t in tokens)
                    {
                        var best = 0;
                        if (kws.Contains(t)) best = 4;
                        else if (kws.Any(k => k.StartsWith(t, StringComparison.Ordinal))) best = 3;
                        else if (name.Contains(t) || stem.Contains(t)) best = 2;
                        else if (kws.Any(k => k.Contains(t)) || catName.Contains(t)) best = 1;
                        if (best == 0) { all = false; break; }
                        score += best;
                    }
                    if (!all) continue;
                }
                scored.Add((s, score));
            }
            IReadOnlyList<StampInfo> res = scored
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => _favorites!.Contains(x.S.Id))
                .ThenBy(x => x.S.Category.IsUser).ThenBy(x => x.S.Category.Order).ThenBy(x => x.S.Category.Id, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.S.Index)
                .Select(x => ToInfo(x.S, chain))
                .ToList();
            return Task.FromResult(res);
        }
    }

    private StampInfo ToInfo(Stamp s, IReadOnlyList<string> chain)
    {
        var kws = chain.Where(c => s.Keywords.ContainsKey(c)).Select(c => s.Keywords[c]).FirstOrDefault() ?? new List<string>();
        return new StampInfo(s.Id, s.Category.Id, PickName(s.Names, chain) ?? s.Stem, kws.ToList(), s.MediaType, _favorites!.Contains(s.Id), s.Category.IsUser);
    }

    public async Task<byte[]> LoadAsync(string stampId)
    {
        EnsureLoaded();
        Stamp? s;
        lock (_lock) s = _stamps!.FirstOrDefault(x => x.Id.Equals(stampId, StringComparison.OrdinalIgnoreCase));
        if (s is null) throw new ShotwiseException(ErrorCode.FileNotFound, "Unknown stamp.") { Detail = stampId };
        try
        {
            if (new FileInfo(s.FullPath).Length > MaxStampBytes) throw new ShotwiseException(ErrorCode.FileTooLarge, "Stamp file is too large.") { Detail = stampId };
            return await File.ReadAllBytesAsync(s.FullPath).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex) { throw new ShotwiseException(ErrorCode.FileNotFound, "Stamp file is missing.", ex) { Detail = stampId }; }
    }

    public void ToggleFavorite(string stampId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_stamps!.Any(s => s.Id.Equals(stampId, StringComparison.OrdinalIgnoreCase))) return;
            if (!_favorites!.Remove(stampId)) _favorites.Add(stampId);
            try
            {
                AtomicFile.WriteAllText(_favoritesFile, JsonSerializer.Serialize(new { favorites = _favorites.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() }, ShotwiseJson.Indented));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Could not persist stamp favourites."); }
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ user packs

    public async Task ImportPackAsync(string path)
    {
        var staging = Path.Combine(_userRoot, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            if (!File.Exists(path)) throw new ShotwiseException(ErrorCode.FileNotFound, "Stamp pack not found.") { Detail = path };
            string packId;
            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(path);
                ZipSafety.Validate(zip, maxEntries: 20_000, maxTotal: MaxPackBytes);
                var prefix = "";
                var infoEntry = ZipSafety.Find(zip, PackageInfoFile);
                if (infoEntry is null)
                {
                    var tops = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName.Split('/')[0]).Distinct().ToList();
                    if (tops.Count == 1 && ZipSafety.Find(zip, tops[0] + "/" + PackageInfoFile) is { } nested) { infoEntry = nested; prefix = tops[0] + "/"; }
                }
                if (infoEntry is null) throw new ShotwiseException(ErrorCode.StampImportFailed, "The pack has no packageinfo.json.") { Detail = path };
                var infoBytes = ZipSafety.ReadAllBytes(infoEntry, 8 * 1024 * 1024);
                var pkg = StampPackageInfo.Parse(infoBytes);
                if (pkg.Version > StampPackageInfo.CurrentVersion) throw new ShotwiseException(ErrorCode.NewerFormatVersion, "The pack was created by a newer version.") { Detail = path };
                if (pkg.Stamps.Count > MaxPackStamps) throw new ShotwiseException(ErrorCode.TooManyEntries, "The pack has too many stamps.");
                packId = SafeId(string.IsNullOrWhiteSpace(pkg.Id) ? Path.GetFileNameWithoutExtension(path) : pkg.Id!);
                if (packId.Length == 0 || packId == MyStampsPackId) packId = "pack-" + Guid.NewGuid().ToString("N")[..6];
                if (!ZipSafety.IsSafeEntryName(packId)) throw new ShotwiseException(ErrorCode.PathTraversal, "Unsafe pack id.");
                if (Directory.Exists(Path.Combine(_userRoot, packId))) packId += "-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(staging);
                long total = 0;
                var kept = new List<StampPackageEntry>();
                foreach (var e in pkg.Stamps)
                {
                    if (!ZipSafety.IsSafeEntryName(e.Filename) || e.Filename.EndsWith('/') || !MediaTypes.ContainsKey(Path.GetExtension(e.Filename))) continue;
                    var entry = ZipSafety.Find(zip, prefix + e.Filename);
                    if (entry is null) continue;
                    var bytes = ZipSafety.ReadAllBytes(entry, MaxStampBytes);
                    total += bytes.Length;
                    if (total > MaxPackBytes) throw new ShotwiseException(ErrorCode.FileTooLarge, "The pack is too large.");
                    var dest = Path.GetFullPath(Path.Combine(staging, e.Filename));
                    if (!dest.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new ShotwiseException(ErrorCode.PathTraversal, "Unsafe path in stamp pack.") { Detail = e.Filename };
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, bytes);
                    kept.Add(e);
                }
                if (kept.Count == 0) throw new ShotwiseException(ErrorCode.StampImportFailed, "The pack contains no usable stamps.") { Detail = path };
                pkg.Stamps = kept;
                pkg.Id = packId;
                File.WriteAllText(Path.Combine(staging, PackageInfoFile), JsonSerializer.Serialize(pkg.ToJsonModel(), ShotwiseJson.Indented), new UTF8Encoding(false));
                var final = Path.Combine(_userRoot, packId);
                Directory.Move(staging, final);
            }).ConfigureAwait(false);
        }
        catch (ShotwiseException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ShotwiseException(ErrorCode.StampImportFailed, "The stamp pack could not be imported: " + ex.Message, ex) { Detail = path };
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Reload();
        RaiseChanged();
    }

    public async Task<StampInfo> AddUserStampAsync(byte[] data, string mediaType, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new ShotwiseException(ErrorCode.InvalidArgument, "The stamp is empty.");
        if (data.Length > MaxStampBytes) throw new ShotwiseException(ErrorCode.FileTooLarge, "The stamp is too large.");
        var ext = MediaTypes.FirstOrDefault(kv => kv.Value.Equals(mediaType, StringComparison.OrdinalIgnoreCase) && kv.Key != ".jpeg").Key;
        if (ext is null || ext == ".pdf") throw new ShotwiseException(ErrorCode.UnsupportedFormat, "Unsupported stamp type.") { Detail = mediaType };
        var display = string.IsNullOrWhiteSpace(name) ? "Stamp" : name.Trim();
        var stem = SafeId(display);
        if (stem.Length == 0) stem = "stamp";
        var dir = Path.Combine(_userRoot, MyStampsPackId);
        Directory.CreateDirectory(dir);
        string file;
        lock (_lock)
        {
            var infoPath = Path.Combine(dir, PackageInfoFile);
            var pkg = File.Exists(infoPath) ? StampPackageInfo.Parse(File.ReadAllBytes(infoPath)) : new StampPackageInfo();
            pkg.Id = MyStampsPackId;
            if (pkg.Category.Count == 0) pkg.Category["en"] = "My Stamps";
            var unique = stem;
            for (var i = 2; File.Exists(Path.Combine(dir, unique + ext)); i++) unique = stem + "-" + i;
            file = unique + ext;
            File.WriteAllBytes(Path.Combine(dir, file), data);
            pkg.Stamps.Add(new StampPackageEntry
            {
                Filename = file,
                Name = new Dictionary<string, string> { ["en"] = display },
                Keywords = new Dictionary<string, List<string>> { ["en"] = Regex.Matches(display.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).Distinct().ToList() },
            });
            AtomicFile.WriteAllText(infoPath, JsonSerializer.Serialize(pkg.ToJsonModel(), ShotwiseJson.Indented));
            _categories = null; _stamps = null;
        }
        EnsureLoaded();
        await Task.CompletedTask.ConfigureAwait(false);
        var id = "user." + MyStampsPackId + "/" + Path.GetFileNameWithoutExtension(file);
        StampInfo? info;
        lock (_lock) info = _stamps!.Where(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(s => ToInfo(s, CultureChain("en"))).FirstOrDefault();
        RaiseChanged();
        return info ?? new StampInfo(id, "user." + MyStampsPackId, display, Array.Empty<string>(), mediaType, false, true);
    }
}
