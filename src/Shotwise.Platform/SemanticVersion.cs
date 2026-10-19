using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.RegularExpressions;
namespace Shotwise.Platform;
/// <summary>SemVer 2.0 precedence; build metadata does not affect ordering.</summary>
public sealed record SemanticVersion : IComparable<SemanticVersion>
{
    public string Text { get; }
    public Version Version { get; }
    public string[] Prerelease { get; }
    private SemanticVersion(string text, Version version, string[] prerelease) { Text = text; Version = version; Prerelease = prerelease; }
    public static SemanticVersion Parse(string text)
    {
        var m = Regex.Match(text, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!m.Success) throw new FormatException("Invalid semantic version.");
        var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : [];
        if (pre.Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit))) throw new FormatException("Leading zero in prerelease.");
        return new(text, new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)), pre);
    }
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { version = Parse(text.Trim()); return true; }
        catch (Exception e) when (e is FormatException or OverflowException or RegexMatchTimeoutException) { return false; }
    }
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        int c = Version.CompareTo(other.Version); if (c != 0) return c;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0) return Prerelease.Length == other.Prerelease.Length ? 0 : Prerelease.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var a = Prerelease[i]; var b = other.Prerelease[i]; bool an = a.All(char.IsAsciiDigit), bn = b.All(char.IsAsciiDigit);
            c = an && bn ? BigInteger.Parse(a).CompareTo(BigInteger.Parse(b)) : an != bn ? an ? -1 : 1 : string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }
}
