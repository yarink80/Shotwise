using System.Globalization;
using System.Text;

namespace Shotwise.Core.Serialization;

/// <summary>
/// Minimal RTF → plain-text extractor (used by importers). Handles groups, destinations (\*, fonttbl, colortbl,
/// stylesheet, info, pict…), \par/\line/\tab, \'hh code-page escapes, \uN unicode with \ucN fallback skipping, and
/// escaped braces/backslashes. Formatting is discarded.
/// </summary>
public static class RtfText
{
    private static readonly HashSet<string> SkipDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "headerl", "headerr", "footerl", "footerr",
        "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "themedata", "colorschememapping", "datastore", "latentstyles",
        "filetbl", "revtbl", "pgdsctbl", "mmathPr", "fldinst",
    };

    public static string ToPlainText(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return "";
        if (!rtf.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal)) return rtf;

        var sb = new StringBuilder(rtf.Length / 2);
        var stack = new Stack<(bool Skip, int Uc)>();
        var skip = false;
        var uc = 1;
        var pendingSkip = 0; // chars to skip after \uN
        var codePage = 1252;
        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    stack.Push((skip, uc));
                    i++;
                    break;
                case '}':
                    if (stack.Count > 0) (skip, uc) = stack.Pop();
                    i++;
                    break;
                case '\\':
                    {
                        i++;
                        if (i >= rtf.Length) break;
                        var n = rtf[i];
                        if (n is '\\' or '{' or '}')
                        {
                            if (!skip) Emit(sb, n, ref pendingSkip);
                            i++;
                            break;
                        }
                        if (n == '*') { skip = true; i++; break; }
                        if (n == '\'')
                        {
                            if (i + 2 < rtf.Length && byte.TryParse(rtf.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                            {
                                if (!skip)
                                {
                                    if (pendingSkip > 0) pendingSkip--;
                                    else sb.Append(DecodeByte(b, codePage));
                                }
                                i += 3;
                            }
                            else i++;
                            break;
                        }
                        if (n is '\r' or '\n') { if (!skip) sb.Append('\n'); i++; break; }
                        if (n == '~') { if (!skip) sb.Append(' '); i++; break; }
                        if (n is '-' or '_') { i++; break; }
                        // control word
                        var start = i;
                        while (i < rtf.Length && char.IsAsciiLetter(rtf[i])) i++;
                        var word = rtf[start..i];
                        var numStart = i;
                        if (i < rtf.Length && (rtf[i] == '-' || char.IsAsciiDigit(rtf[i])))
                        {
                            i++;
                            while (i < rtf.Length && char.IsAsciiDigit(rtf[i])) i++;
                        }
                        int? param = i > numStart && int.TryParse(rtf.AsSpan(numStart, i - numStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pv) ? pv : null;
                        if (i < rtf.Length && rtf[i] == ' ') i++;
                        if (SkipDestinations.Contains(word)) { skip = true; break; }
                        if (skip) break;
                        switch (word)
                        {
                            case "par" or "line" or "sect" or "page": sb.Append('\n'); break;
                            case "tab": sb.Append('\t'); break;
                            case "emdash": sb.Append('—'); break;
                            case "endash": sb.Append('–'); break;
                            case "bullet": sb.Append('•'); break;
                            case "lquote": sb.Append('‘'); break;
                            case "rquote": sb.Append('’'); break;
                            case "ldblquote": sb.Append('“'); break;
                            case "rdblquote": sb.Append('”'); break;
                            case "uc": uc = param ?? 1; break;
                            case "ansicpg": codePage = param ?? 1252; break;
                            case "u":
                                if (param is { } u)
                                {
                                    sb.Append((char)(u < 0 ? u + 65536 : u));
                                    pendingSkip = uc;
                                }
                                break;
                        }
                        break;
                    }
                case '\r' or '\n':
                    i++;
                    break;
                default:
                    if (!skip) Emit(sb, c, ref pendingSkip);
                    i++;
                    break;
            }
        }
        var text = sb.ToString();
        // RTF editors usually end with a trailing \par
        return text.EndsWith('\n') ? text.TrimEnd('\n') : text;
    }

    private static void Emit(StringBuilder sb, char c, ref int pendingSkip)
    {
        if (pendingSkip > 0) { pendingSkip--; return; }
        sb.Append(c);
    }

    private static char DecodeByte(byte b, int codePage)
    {
        if (b < 0x80) return (char)b;
        // Windows-1252 differs from Latin-1 only in 0x80..0x9F
        if (codePage == 1252 && b is >= 0x80 and <= 0x9F)
        {
            ReadOnlySpan<char> map = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";
            return map[b - 0x80];
        }
        return (char)b;
    }
}
