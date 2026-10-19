using System.Text.RegularExpressions;

namespace Shotwise.Core.Platform;

/// <summary>Plain-text excerpts of Markdown release notes (update dialogs, Preferences → Startup &amp; Updates).</summary>
public static class ReleaseNotes
{
    /// <summary>
    /// Headings, rules, tables, code fences, emphasis/code marks and link targets removed; list items joined with " · "; cut at a word
    /// boundary with "…" when longer than <paramref name="maxLength"/>.
    /// </summary>
    public static string Snippet(string? markdown, int maxLength = 280)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var parts = new List<string>();
        var fenced = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (fenced || line.Length == 0 || line.StartsWith('#') || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith('|') || line.StartsWith('>')) continue;
            line = Regex.Replace(line, @"^([-*+]|\d+\.)\s+", "", RegexOptions.None, TimeSpan.FromSeconds(1));
            line = Regex.Replace(line, @"!?\[([^\]]*)\]\([^)]*\)", "$1", RegexOptions.None, TimeSpan.FromSeconds(1));
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = Regex.Replace(line, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
            if (line.Length > 0) parts.Add(line);
        }
        var text = string.Join(" · ", parts);
        if (text.Length <= maxLength) return text;
        var cut = text.LastIndexOf(' ', Math.Max(0, maxLength - 1));
        var head = text[..(cut > maxLength / 2 ? cut : maxLength)];
        return head.TrimEnd(' ', '·', ',', ';', ':') + "…";
    }
}
