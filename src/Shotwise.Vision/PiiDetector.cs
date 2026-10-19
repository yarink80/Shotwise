using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>A sensitive-data match: character span [Start, Start+Length) within the searched text.</summary>
public readonly record struct PiiMatch(RedactCategory Category, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Text classifiers for Smart Redact (VIS-05): email, phone (international/NANP/national), credit card (Luhn), US SSN,
/// IPv4/IPv6, URL, dates (several formats) and a postal-address heuristic. Overlaps are resolved by category priority.
/// </summary>
public static class PiiDetector
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private const string Months = @"(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)";
    private const string StreetSuffix = @"(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Lane|Ln|Drive|Dr|Court|Ct|Way|Place|Pl|Square|Sq|Terrace|Ter|Parkway|Pkwy|Highway|Hwy|Circle|Cir|Alley|Trail|Trl)";

    private static Regex R(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, Opts | extra, Timeout);

    private static readonly Regex CardCandidate = R(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)");
    private static readonly Regex SsnRx = R(@"(?<![\d\-])(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}(?![\d\-])");
    private static readonly Regex EmailRx = R(@"(?<![\w.+\-])[A-Za-z0-9](?:[A-Za-z0-9._%+\-]*[A-Za-z0-9])?@[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,}(?![\w\-])");
    private static readonly Regex UrlScheme = R(@"(?<![@\w])(?:(?:https?|ftp)://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase);
    private static readonly Regex UrlBare = R(@"(?<![@\w.\-])(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+(?:com|org|net|edu|gov|io|co|app|dev|ai|info|biz|me|us|uk|de|fr|ca|au|nl|ru|jp|cn|in|br|xyz|online|site|tech|cloud|eu|ch|es|it|se|no)(?::\d{1,5})?(?:/[^\s<>""']*)?(?![\w@\-])", RegexOptions.IgnoreCase);
    private static readonly Regex Ipv4Rx = R(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d]|\.\d)");
    private static readonly Regex Ipv6Rx = R(@"(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])");

    private static readonly Regex[] DateRx =
    {
        R(@"(?<!\d)\d{4}[-/.]\d{1,2}[-/.]\d{1,2}(?!\d)"),
        R(@"(?<![\d.])\d{1,2}[/.\-]\d{1,2}[/.\-](?:\d{4}|\d{2})(?![\d])"),
        R(@"(?<![A-Za-z])" + Months + @"\.?\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}(?!\d)", RegexOptions.IgnoreCase),
        R(@"(?<!\d)\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?" + Months + @"\.?,?\s+\d{4}(?!\d)", RegexOptions.IgnoreCase),
        R(@"(?<![A-Za-z])" + Months + @"\s+\d{4}(?!\d)", RegexOptions.IgnoreCase),
    };

    private static readonly Regex[] PhoneRx =
    {
        // international: +CC or 00CC followed by grouped digits
        R(@"(?<![\w+])(?:\+|00)[1-9]\d{0,2}(?:[\s.\-]?\(?\d{1,5}\)?){2,5}(?!\d)"),
        // North American
        R(@"(?<![\w+])(?:\+?1[\s.\-]?)?(?:\(\d{3}\)\s?|\d{3}[\s.\-])\d{3}[\s.\-]\d{4}(?!\d)"),
        // national with trunk prefix 0 and separators (e.g. 020 7946 0958, 0171-1234567, 06 12 34 56 78)
        R(@"(?<![\w.+\-])0\d{1,4}[\s.\-/]\d{2,4}(?:[\s.\-]\d{2,5}){1,3}(?![\d\-])"),
    };

    private static readonly Regex StreetRx = R(
        @"(?<![\w])\d{1,6}[A-Za-z]?\s+(?:[A-Za-z0-9.'\-]+\s+){0,4}?(?i:" + StreetSuffix + @")\b\.?" +
        @"(?:,?\s+(?i:Apt|Apartment|Suite|Ste|Unit|Floor|Fl|#)\.?\s*[\w\-]+)?" +
        @"(?:,\s*[A-Z][A-Za-z.' \-]+?(?:,\s*[A-Z]{2}(?:\s+\d{5}(?:-\d{4})?)?)?)?");
    private static readonly Regex PoBoxRx = R(@"(?<!\w)P\.?\s?O\.?\s+Box\s+\d{1,6}\b", RegexOptions.IgnoreCase);
    private static readonly Regex UsZipRx = R(@"(?<![\w])([A-Z]{2})[,\s]+\d{5}(?:-\d{4})?(?![\d\-])");
    private static readonly Regex UkPostcodeRx = R(@"(?<![\w])[A-Z]{1,2}\d[A-Z\d]?\s?\d[A-Z]{2}(?!\w)");
    private static readonly Regex CaPostcodeRx = R(@"(?<![\w])[ABCEGHJ-NPRSTVXY]\d[ABCEGHJ-NPRSTV-Z][ \-]?\d[ABCEGHJ-NPRSTV-Z]\d(?!\w)");

    private static readonly HashSet<string> UsStates = new(StringComparer.Ordinal)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS","MO",
        "MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC"
    };

    /// <summary>Luhn checksum of a digit string (non-digits ignored).</summary>
    public static bool IsLuhnValid(string digits)
    {
        var sum = 0;
        var alt = false;
        var count = 0;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var ch = digits[i];
            if (ch < '0' || ch > '9') continue;
            var d = ch - '0';
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            alt = !alt;
            count++;
        }
        return count > 0 && sum % 10 == 0;
    }

    /// <summary>Finds every sensitive span in <paramref name="text"/>; category priority resolves overlaps.</summary>
    public static IReadOnlyList<PiiMatch> Find(string text, IReadOnlySet<RedactCategory>? categories = null)
    {
        var all = new List<PiiMatch>();
        bool Want(RedactCategory c) => categories is null || categories.Contains(c);
        if (string.IsNullOrWhiteSpace(text)) return all;

        // Evaluated in priority order; later categories lose overlaps with earlier ones.
        if (Want(RedactCategory.CreditCard)) FindCards(text, all);
        if (Want(RedactCategory.Ssn)) foreach (Match m in SsnRx.Matches(text)) all.Add(new(RedactCategory.Ssn, m.Index, m.Length));
        if (Want(RedactCategory.Email)) foreach (Match m in EmailRx.Matches(text)) all.Add(new(RedactCategory.Email, m.Index, m.Length));
        if (Want(RedactCategory.Url)) FindUrls(text, all);
        if (Want(RedactCategory.IpAddress)) FindIps(text, all);
        if (Want(RedactCategory.Date)) FindDates(text, all);
        if (Want(RedactCategory.Phone)) FindPhones(text, all);
        if (Want(RedactCategory.PostalAddress)) FindAddresses(text, all);

        return ResolveOverlaps(all);
    }

    private static IReadOnlyList<PiiMatch> ResolveOverlaps(List<PiiMatch> all)
    {
        var kept = new List<PiiMatch>();
        // all[] is in priority order already (category blocks); keep a match only when it doesn't overlap a kept one.
        foreach (var m in all)
        {
            var clash = false;
            foreach (var k in kept) if (m.Start < k.End && k.Start < m.End) { clash = true; break; }
            if (!clash) kept.Add(m);
        }
        kept.Sort((a, b) => a.Start.CompareTo(b.Start));
        return kept;
    }

    private static void FindCards(string text, List<PiiMatch> all)
    {
        foreach (Match m in CardCandidate.Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is < 13 or > 19) continue;
            if (!IsLuhnValid(digits)) continue;
            all.Add(new(RedactCategory.CreditCard, m.Index, m.Length));
        }
    }

    private static void FindUrls(string text, List<PiiMatch> all)
    {
        foreach (var rx in new[] { UrlScheme, UrlBare })
            foreach (Match m in rx.Matches(text))
            {
                var len = m.Length;
                while (len > 0 && ".,;:!?)]}".IndexOf(m.Value[len - 1]) >= 0) len--;
                if (len < 4) continue;
                var v = m.Value[..len];
                // "1.2.3.4" style or version numbers are not URLs
                if (rx == UrlBare && v.All(c => char.IsDigit(c) || c == '.')) continue;
                all.Add(new(RedactCategory.Url, m.Index, len));
            }
    }

    private static void FindIps(string text, List<PiiMatch> all)
    {
        foreach (Match m in Ipv4Rx.Matches(text)) all.Add(new(RedactCategory.IpAddress, m.Index, m.Length));
        foreach (Match m in Ipv6Rx.Matches(text))
        {
            var v = m.Value;
            if (v.Count(c => c == ':') < 2) continue;
            if (!IPAddress.TryParse(v, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) continue;
            all.Add(new(RedactCategory.IpAddress, m.Index, m.Length));
        }
    }

    private static void FindDates(string text, List<PiiMatch> all)
    {
        for (var i = 0; i < DateRx.Length; i++)
            foreach (Match m in DateRx[i].Matches(text))
            {
                if (i <= 1 && !NumericDateOk(m.Value)) continue;
                all.Add(new(RedactCategory.Date, m.Index, m.Length));
            }
    }

    private static bool NumericDateOk(string v)
    {
        var parts = v.Split('-', '/', '.');
        if (parts.Length != 3) return false;
        var n = new int[3];
        for (var i = 0; i < 3; i++) if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        if (parts[0].Length == 4) return n[1] is >= 1 and <= 12 && n[2] is >= 1 and <= 31;                 // y-m-d
        // d/m/y or m/d/y
        var dayMonthOk = n[0] is >= 1 and <= 31 && n[1] is >= 1 and <= 12;
        var monthDayOk = n[0] is >= 1 and <= 12 && n[1] is >= 1 and <= 31;
        return (dayMonthOk || monthDayOk) && (parts[2].Length == 4 || parts[2].Length == 2);
    }

    private static void FindPhones(string text, List<PiiMatch> all)
    {
        foreach (var rx in PhoneRx)
            foreach (Match m in rx.Matches(text))
            {
                var v = m.Value;
                // trim trailing separators/brackets that the greedy groups may have taken
                var len = v.Length;
                while (len > 0 && !char.IsDigit(v[len - 1]) && v[len - 1] != ')') len--;
                v = v[..len];
                var digits = v.Count(char.IsDigit);
                if (digits is < 7 or > 15) continue;
                if (rx == PhoneRx[0] && digits < 8) continue;
                if (rx == PhoneRx[2] && digits < 9) continue;
                all.Add(new(RedactCategory.Phone, m.Index, len));
            }
    }

    private static void FindAddresses(string text, List<PiiMatch> all)
    {
        foreach (Match m in StreetRx.Matches(text))
        {
            var len = m.Length;
            while (len > 0 && (m.Value[len - 1] == ',' || char.IsWhiteSpace(m.Value[len - 1]))) len--;
            all.Add(new(RedactCategory.PostalAddress, m.Index, len));
        }
        foreach (Match m in PoBoxRx.Matches(text)) all.Add(new(RedactCategory.PostalAddress, m.Index, m.Length));
        foreach (Match m in UsZipRx.Matches(text))
            if (UsStates.Contains(m.Groups[1].Value)) all.Add(new(RedactCategory.PostalAddress, m.Groups[1].Index, m.Length - (m.Groups[1].Index - m.Index)));
        foreach (Match m in UkPostcodeRx.Matches(text))
            if (m.Value.Any(char.IsDigit) && m.Value.Length >= 5) all.Add(new(RedactCategory.PostalAddress, m.Index, m.Length));
        foreach (Match m in CaPostcodeRx.Matches(text)) all.Add(new(RedactCategory.PostalAddress, m.Index, m.Length));
    }
}
