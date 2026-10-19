using Shotwise.Core.Geometry;
using Shotwise.Core.Vision;

namespace Shotwise.Vision.Tests;

public class PiiDetectorTests
{
    private static IReadOnlyList<(RedactCategory Cat, string Text)> Find(string text)
        => PiiDetector.Find(text).Select(m => (m.Category, text.Substring(m.Start, m.Length))).ToList();

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]            // Visa test number
    [InlineData("4111-1111-1111-1111", true)]
    [InlineData("5555555555554444", true)]                // MasterCard test number
    [InlineData("378282246310005", true)]                 // Amex (15 digits)
    [InlineData("4111 1111 1111 1112", false)]            // fails Luhn
    [InlineData("1234 5678 9012 3456", false)]
    public void CreditCard_RequiresLuhn(string text, bool expected)
    {
        var hits = Find("Card: " + text + " exp");
        Assert.Equal(expected, hits.Any(h => h.Cat == RedactCategory.CreditCard));
    }

    [Fact]
    public void Luhn_Basics()
    {
        Assert.True(PiiDetector.IsLuhnValid("79927398713"));
        Assert.False(PiiDetector.IsLuhnValid("79927398710"));
        Assert.False(PiiDetector.IsLuhnValid(""));
    }

    [Fact]
    public void Email_IsFoundWithExactSpan()
    {
        var hits = Find("Contact john.doe+test@example.co.uk now");
        var e = Assert.Single(hits, h => h.Cat == RedactCategory.Email);
        Assert.Equal("john.doe+test@example.co.uk", e.Text);
        Assert.DoesNotContain(hits, h => h.Cat == RedactCategory.Url);       // the domain inside the email must not double-count
    }

    [Theory]
    [InlineData("SSN 123-45-6789 ok", true)]
    [InlineData("SSN 123 45 6789 ok", true)]
    [InlineData("SSN 000-12-3456 ok", false)]
    [InlineData("SSN 666-12-3456 ok", false)]
    [InlineData("SSN 123-00-6789 ok", false)]
    [InlineData("order 123456789 ok", false)]
    public void Ssn(string text, bool expected)
        => Assert.Equal(expected, Find(text).Any(h => h.Cat == RedactCategory.Ssn));

    [Theory]
    [InlineData("host 192.168.1.10 up", "192.168.1.10")]
    [InlineData("8.8.8.8", "8.8.8.8")]
    [InlineData("v6 2001:0db8:85a3:0000:0000:8a2e:0370:7334 end", "2001:0db8:85a3:0000:0000:8a2e:0370:7334")]
    [InlineData("loopback ::1 end", "::1")]
    [InlineData("addr fe80::1ff:fe23:4567:890a x", "fe80::1ff:fe23:4567:890a")]
    public void IpAddresses(string text, string expected)
    {
        var ip = Assert.Single(Find(text), h => h.Cat == RedactCategory.IpAddress);
        Assert.Equal(expected, ip.Text);
    }

    [Theory]
    [InlineData("time 12:30:45 now")]
    [InlineData("version 1.2.3 released")]
    [InlineData("ip 999.1.1.1")]
    public void NotIpAddresses(string text)
        => Assert.DoesNotContain(Find(text), h => h.Cat == RedactCategory.IpAddress);

    [Theory]
    [InlineData("see https://www.example.com/path?q=1, ok", "https://www.example.com/path?q=1")]
    [InlineData("go www.example.org.", "www.example.org")]
    [InlineData("open example.io/docs now", "example.io/docs")]
    public void Urls(string text, string expected)
        => Assert.Equal(expected, Assert.Single(Find(text), h => h.Cat == RedactCategory.Url).Text);

    [Theory]
    [InlineData("on 2024-03-15 we", "2024-03-15")]
    [InlineData("on 15/03/2024 we", "15/03/2024")]
    [InlineData("on 03.15.24 we", "03.15.24")]
    [InlineData("on March 5, 2024 we", "March 5, 2024")]
    [InlineData("on 5th of March 2024 we", "5th of March 2024")]
    [InlineData("on Sep 2023 we", "Sep 2023")]
    public void Dates(string text, string expected)
        => Assert.Equal(expected, Assert.Single(Find(text), h => h.Cat == RedactCategory.Date).Text);

    [Theory]
    [InlineData("2024-13-45")]
    [InlineData("99/99/2024")]
    public void InvalidDates_AreIgnored(string text)
        => Assert.DoesNotContain(Find(text), h => h.Cat == RedactCategory.Date);

    [Theory]
    [InlineData("call +44 20 7946 0958 today")]
    [InlineData("call +1 (415) 555-2671 today")]
    [InlineData("call (415) 555-2671 today")]
    [InlineData("call 415-555-2671 today")]
    [InlineData("call 415.555.2671 today")]
    [InlineData("call +49 30 901820 today")]
    [InlineData("call 020 7946 0958 today")]
    [InlineData("call 0044 20 7946 0958 today")]
    public void Phones(string text)
        => Assert.Contains(Find(text), h => h.Cat == RedactCategory.Phone);

    [Theory]
    [InlineData("quantity 12345 items")]
    [InlineData("on 2024-03-15 we")]
    [InlineData("on 15/03/2024 we")]
    public void NotPhones(string text)
        => Assert.DoesNotContain(Find(text), h => h.Cat == RedactCategory.Phone);

    [Theory]
    [InlineData("lives at 1600 Pennsylvania Avenue now", "1600 Pennsylvania Avenue")]
    [InlineData("send to 221B Baker Street, London", "221B Baker Street")]
    [InlineData("42 Wallaby Way, Apt 4B ok", "42 Wallaby Way, Apt 4B")]
    public void StreetAddresses(string text, string expectedPrefix)
    {
        var hit = Assert.Single(Find(text), h => h.Cat == RedactCategory.PostalAddress);
        Assert.StartsWith(expectedPrefix, hit.Text);
    }

    [Theory]
    [InlineData("Springfield, IL 62704")]
    [InlineData("zip SW1A 1AA")]
    [InlineData("ca K1A 0B1")]
    [InlineData("PO Box 1234")]
    public void PostcodesAndBoxes(string text)
        => Assert.Contains(Find(text), h => h.Cat == RedactCategory.PostalAddress);

    [Fact]
    public void Categories_FilterApplies()
    {
        var only = new HashSet<RedactCategory> { RedactCategory.Email };
        var hits = PiiDetector.Find("a@b.com and 8.8.8.8", only);
        Assert.Single(hits);
        Assert.Equal(RedactCategory.Email, hits[0].Category);
    }

    [Fact]
    public void OcrWordMapping_CutsPartialWords()
    {
        // one word "Mail:x@y.com" -> only the e-mail part (chars 5..12) should be boxed
        var word = new OcrWord("Mail:x@y.com", new Rect2(100, 20, 120, 12), 1);
        var ocr = new OcrResult("en", word.Text, new[] { new OcrLine(word.Text, word.Bounds, new[] { word }) }, 0);
        var hits = SmartRedactService.MatchLines(ocr, new HashSet<RedactCategory> { RedactCategory.Email });
        var h = Assert.Single(hits);
        Assert.Equal("x@y.com", h.Text);
        Assert.InRange(h.Bounds.Left, 100 + 120 * 5 / 12.0 - 2, 100 + 120 * 5 / 12.0 + 1);
        Assert.InRange(h.Bounds.Right, 218, 222);
    }

    [Fact]
    public void OcrWordMapping_SpansMultipleWords()
    {
        var w = new[]
        {
            new OcrWord("Card", new Rect2(10, 10, 40, 14), 1),
            new OcrWord("4111", new Rect2(60, 10, 40, 14), 1),
            new OcrWord("1111", new Rect2(110, 10, 40, 14), 1),
            new OcrWord("1111", new Rect2(160, 10, 40, 14), 1),
            new OcrWord("1111", new Rect2(210, 10, 40, 14), 1),
        };
        var ocr = new OcrResult("en", "", new[] { new OcrLine("Card 4111 1111 1111 1111", new Rect2(10, 10, 240, 14), w) }, 0);
        var hits = SmartRedactService.MatchLines(ocr, new HashSet<RedactCategory> { RedactCategory.CreditCard });
        var h = Assert.Single(hits);
        Assert.InRange(h.Bounds.Left, 58, 61);
        Assert.InRange(h.Bounds.Right, 249, 252);
    }
}
