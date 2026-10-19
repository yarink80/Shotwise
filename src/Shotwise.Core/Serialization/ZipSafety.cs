using System.IO.Compression;

namespace Shotwise.Core.Serialization;

/// <summary>Defensive limits and helpers for reading untrusted ZIP containers (.shotx, .snagx, packs, archives).</summary>
public static class ZipSafety
{
    public const int MaxEntries = 10_000;
    public const long MaxTotalUncompressed = 2L * 1024 * 1024 * 1024;
    /// <summary>Upper bound for a single JSON entry.</summary>
    public const int MaxJsonEntryBytes = 64 * 1024 * 1024;
    /// <summary>Upper bound for a single binary entry.</summary>
    public const long MaxBinaryEntryBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Validates entry count, total declared size, names (no traversal / absolute / device paths) and duplicates.
    /// Throws <see cref="ShotwiseException"/> with TooManyEntries / FileTooLarge / PathTraversal / CorruptFile.
    /// </summary>
    public static void Validate(ZipArchive zip, int maxEntries = MaxEntries, long maxTotal = MaxTotalUncompressed)
    {
        if (zip.Entries.Count > maxEntries)
            throw new ShotwiseException(ErrorCode.TooManyEntries, $"Archive has {zip.Entries.Count} entries (limit {maxEntries}).");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            if (!IsSafeEntryName(e.FullName))
                throw new ShotwiseException(ErrorCode.PathTraversal, $"Unsafe entry name '{e.FullName}'.") { Detail = e.FullName };
            if (!names.Add(e.FullName))
                throw new ShotwiseException(ErrorCode.CorruptFile, $"Duplicate entry '{e.FullName}'.");
            if (e.Length < 0) throw new ShotwiseException(ErrorCode.CorruptFile, "Negative entry size.");
            total += e.Length;
            if (total > maxTotal)
                throw new ShotwiseException(ErrorCode.FileTooLarge, "Archive expands beyond the supported size.");
        }
    }

    /// <summary>Relative forward-slash path without "..", ".", empty segments, drive letters, colons, backslashes or control chars.</summary>
    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512) return false;
        if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':')) return false;
        foreach (var c in name) if (char.IsControl(c)) return false;
        var trimmed = name.EndsWith('/') ? name[..^1] : name; // directory entries
        foreach (var seg in trimmed.Split('/'))
        {
            if (seg.Length == 0 || seg == "." || seg == "..") return false;
            if (seg.EndsWith(' ') || seg.EndsWith('.')) return false;
            var stem = seg.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')) return false;
        }
        return true;
    }

    /// <summary>Reads an entry fully, enforcing both the declared size and <paramref name="limit"/> (defends against lying headers).</summary>
    public static byte[] ReadAllBytes(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit) throw new ShotwiseException(ErrorCode.FileTooLarge, $"Entry '{entry.FullName}' is too large.");
        var expected = (int)entry.Length;
        var buffer = new byte[expected];
        using var s = entry.Open();
        var read = 0;
        while (read < expected)
        {
            var n = s.Read(buffer, read, expected - read);
            if (n == 0) throw new ShotwiseException(ErrorCode.CorruptFile, $"Entry '{entry.FullName}' is truncated.");
            read += n;
        }
        Span<byte> probe = stackalloc byte[1];
        if (s.Read(probe) != 0) throw new ShotwiseException(ErrorCode.CorruptFile, $"Entry '{entry.FullName}' is larger than declared.");
        return buffer;
    }

    public static ZipArchiveEntry? Find(ZipArchive zip, string name)
        => zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
}
