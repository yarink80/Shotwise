using SkiaSharp;
using static ReadmeAssets.Gfx;

namespace ReadmeAssets;

/// <summary>
/// Fictional sample "captures" drawn from scratch (no third-party UI): a small analytics web app, a customer profile card
/// with personal data (for Smart Redact) and an error dialog (for Grab Text). Element positions are exposed so the demo
/// can annotate them.
/// </summary>
internal static class SampleScenes
{
    public const int W = 960, H = 600;

    public static readonly SKRect RevenueCard = new(200, 130, 436, 218);
    public static readonly SKRect ExportButton = new(796, 74, 940, 110);
    public static readonly SKRect ChartCard = new(200, 234, 940, 404);
    public static readonly SKRect EmailColumn = new(352, 474, 598, 580);
    public static readonly SKRect StatusColumn = new(800, 474, 930, 580);

    private static readonly SKColor Ink = SKColor.Parse("#111827");
    private static readonly SKColor Muted = SKColor.Parse("#6B7280");
    private static readonly SKColor Line = SKColor.Parse("#E5E7EB");
    private static readonly SKColor Blue = SKColor.Parse("#2563EB");
    private static readonly SKColor Navy = SKColor.Parse("#1E3A8A");

    private static void Card(SKCanvas c, SKRect r)
    {
        using var sh = Fill(new SKColor(17, 24, 39, 14));
        var s = r; s.Offset(0, 1.5f);
        c.DrawRoundRect(s, 10, 10, sh);
        using var p = Fill(SKColors.White);
        c.DrawRoundRect(r, 10, 10, p);
        using var b = Stroke(Line, 1);
        c.DrawRoundRect(r, 10, 10, b);
    }

    public static SKBitmap Dashboard()
    {
        var bmp = NewBitmap(W, H, SKColor.Parse("#F4F6FA"));
        using var c = new SKCanvas(bmp);

        // top bar
        using (var p = Fill(Navy)) c.DrawRect(0, 0, W, 56, p);
        using (var p = Fill(SKColor.Parse("#60A5FA"))) c.DrawRoundRect(new SKRect(22, 17, 44, 39), 6, 6, p);
        Text(c, "Lumen", 54, 35, Font(19, SKFontStyleWeight.Bold), SKColors.White);
        var nav = Font(14, SKFontStyleWeight.Medium);
        float nx = 560;
        foreach (var item in new[] { "Overview", "Reports", "Customers", "Settings" })
        {
            Text(c, item, nx, 34, nav, item == "Overview" ? SKColors.White : new SKColor(255, 255, 255, 170));
            nx += Measure(item, nav) + 26;
        }
        using (var p = Fill(SKColor.Parse("#93C5FD"))) c.DrawCircle(928, 28, 14, p);
        Text(c, "JC", 928, 33, Font(12, SKFontStyleWeight.Bold), Navy, SKTextAlign.Center);

        // sidebar
        using (var p = Fill(SKColors.White)) c.DrawRect(0, 56, 180, H - 56, p);
        using (var p = Stroke(Line, 1)) c.DrawLine(180, 56, 180, H, p);
        var side = Font(14);
        float sy = 96;
        foreach (var item in new[] { "Dashboard", "Orders", "Customers", "Products", "Analytics", "Messages", "Settings" })
        {
            if (item == "Dashboard")
            {
                using var p = Fill(SKColor.Parse("#EFF4FF"));
                c.DrawRoundRect(new SKRect(12, sy - 22, 168, sy + 10), 8, 8, p);
                using var bar = Fill(Blue);
                c.DrawRoundRect(new SKRect(12, sy - 16, 16, sy + 4), 2, 2, bar);
            }
            using (var ip = Fill(item == "Dashboard" ? Blue : SKColor.Parse("#9CA3AF"))) c.DrawRoundRect(new SKRect(28, sy - 13, 42, sy + 1), 3, 3, ip);
            Text(c, item, 54, sy, side, item == "Dashboard" ? Blue : SKColor.Parse("#374151"));
            sy += 44;
        }

        // header
        Text(c, "Quarterly overview", 200, 96, Font(22, SKFontStyleWeight.Bold), Ink);
        Text(c, "Q3 2026 · updated 5 minutes ago", 200, 118, Font(13), Muted);
        using (var p = Fill(Blue)) c.DrawRoundRect(ExportButton, 8, 8, p);
        Text(c, "Export report", ExportButton.MidX, ExportButton.MidY + 5, Font(14, SKFontStyleWeight.SemiBold), SKColors.White, SKTextAlign.Center);
        using (var p = Stroke(BorderStrong, 1)) c.DrawRoundRect(new SKRect(672, 74, 784, 110), 8, 8, p);
        Text(c, "Last 90 days", 728, 97, Font(13, SKFontStyleWeight.Medium), SKColor.Parse("#374151"), SKTextAlign.Center);

        // KPI cards
        var kpis = new[] { ("Revenue", "$48,240", "+18%", true), ("Active users", "12,480", "+6%", true), ("Conversion", "3.9%", "−0.4%", false) };
        for (var i = 0; i < 3; i++)
        {
            var r = new SKRect(200 + i * 252, 130, 200 + i * 252 + 236, 218);
            Card(c, r);
            var (label, value, delta, up) = kpis[i];
            Text(c, label, r.Left + 18, r.Top + 28, Font(13, SKFontStyleWeight.Medium), Muted);
            Text(c, value, r.Left + 18, r.Top + 68, Font(28, SKFontStyleWeight.Bold), Ink);
            var df = Font(12, SKFontStyleWeight.SemiBold);
            var dw = Measure(delta, df) + 16;
            var chip = new SKRect(r.Right - 18 - dw, r.Top + 48, r.Right - 18, r.Top + 70);
            using var cp = Fill(up ? SKColor.Parse("#DCFCE7") : SKColor.Parse("#FEE2E2"));
            c.DrawRoundRect(chip, 11, 11, cp);
            Text(c, delta, chip.MidX, chip.MidY + 4, df, up ? SKColor.Parse("#15803D") : SKColor.Parse("#B91C1C"), SKTextAlign.Center);
        }

        // chart
        Card(c, ChartCard);
        Text(c, "Monthly revenue", ChartCard.Left + 18, ChartCard.Top + 28, Font(15, SKFontStyleWeight.SemiBold), Ink);
        Text(c, "in thousands of dollars", ChartCard.Left + 18, ChartCard.Top + 47, Font(12), Muted);
        var vals = new[] { 22, 26, 24, 31, 29, 34, 33, 38, 36, 41, 44, 48 };
        var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        float baseY = ChartCard.Bottom - 30, top = ChartCard.Top + 64, x0 = ChartCard.Left + 34, step = (ChartCard.Width - 60) / 12f;
        using (var gl = Stroke(SKColor.Parse("#F1F2F6"), 1))
            for (var g = 0; g < 4; g++) c.DrawLine(ChartCard.Left + 18, baseY - g * (baseY - top) / 3, ChartCard.Right - 18, baseY - g * (baseY - top) / 3, gl);
        var mf = Font(11);
        for (var i = 0; i < 12; i++)
        {
            var h = (baseY - top) * vals[i] / 50f;
            var bx = x0 + i * step;
            using var bp = Fill(i == 11 ? SKColor.Parse("#F59E0B") : SKColor.Parse("#3B82F6"));
            c.DrawRoundRect(new SKRect(bx, baseY - h, bx + step * 0.56f, baseY), 4, 4, bp);
            Text(c, months[i], bx + step * 0.28f, baseY + 17, mf, Muted, SKTextAlign.Center);
        }

        // table
        var table = new SKRect(200, 420, 940, 588);
        Card(c, table);
        Text(c, "Recent orders", table.Left + 18, table.Top + 28, Font(15, SKFontStyleWeight.SemiBold), Ink);
        var hf = Font(11, SKFontStyleWeight.SemiBold);
        float[] cols = { 218, 362, 640, 806 };
        string[] heads = { "CUSTOMER", "EMAIL", "AMOUNT", "STATUS" };
        for (var i = 0; i < 4; i++) Text(c, heads[i], cols[i], table.Top + 54, hf, SKColor.Parse("#9CA3AF"));
        var rows = new[]
        {
            ("Jane Cooper", "jane.cooper@example.com", "$1,240.00", "Paid"),
            ("Rahul Mehta", "rahul.mehta@example.net", "$860.50", "Pending"),
            ("Ana Souza", "ana.souza@example.org", "$2,015.00", "Paid"),
        };
        var rf = Font(14);
        for (var i = 0; i < rows.Length; i++)
        {
            var y = table.Top + 86 + i * 34;
            using (var lp = Stroke(Line, 1)) c.DrawLine(table.Left + 16, y - 22, table.Right - 16, y - 22, lp);
            var (n, e, a, s) = rows[i];
            Text(c, n, cols[0], y, Font(14, SKFontStyleWeight.Medium), Ink);
            Text(c, e, cols[1], y, rf, SKColor.Parse("#374151"));
            Text(c, a, cols[2], y, rf, Ink);
            var paid = s == "Paid";
            var sf = Font(12, SKFontStyleWeight.SemiBold);
            var sw = Measure(s, sf) + 20;
            var chip = new SKRect(cols[3], y - 15, cols[3] + sw, y + 6);
            using var cp = Fill(paid ? SKColor.Parse("#DCFCE7") : SKColor.Parse("#FEF3C7"));
            c.DrawRoundRect(chip, 10, 10, cp);
            Text(c, s, chip.MidX, chip.MidY + 4, sf, paid ? SKColor.Parse("#15803D") : SKColor.Parse("#B45309"), SKTextAlign.Center);
        }
        return bmp;
    }

    /// <summary>Customer profile with personal data in many formats (Smart Redact input).</summary>
    public static SKBitmap Profile()
    {
        const int w = 720, h = 470;
        var bmp = NewBitmap(w, h, SKColor.Parse("#F4F6FA"));
        using var c = new SKCanvas(bmp);
        var card = new SKRect(24, 24, w - 24, h - 24);
        Card(c, card);
        using (var p = Fill(SKColor.Parse("#DBEAFE"))) c.DrawCircle(card.Left + 52, card.Top + 52, 28, p);
        Text(c, "JC", card.Left + 52, card.Top + 60, Font(20, SKFontStyleWeight.Bold), Navy, SKTextAlign.Center);
        Text(c, "Jane Cooper", card.Left + 96, card.Top + 48, Font(22, SKFontStyleWeight.Bold), Ink);
        Text(c, "Gold plan · 128 orders", card.Left + 96, card.Top + 72, Font(14), Muted);
        var rows = new[]
        {
            ("Email", "jane.cooper@example.com"),
            ("Phone", "+1 (555) 013-4477"),
            ("Card", "4242 4242 4242 4242"),
            ("IP address", "192.168.40.12"),
            ("Website", "https://example.com/orders/8841"),
            ("Last order", "2026-10-14"),
        };
        var lf = Font(17, SKFontStyleWeight.Medium);
        var vf = Font(19);
        for (var i = 0; i < rows.Length; i++)
        {
            var y = card.Top + 128 + i * 50;
            using (var lp = Stroke(Line, 1)) c.DrawLine(card.Left + 24, y - 30, card.Right - 24, y - 30, lp);
            Text(c, rows[i].Item1, card.Left + 28, y, lf, Muted);
            Text(c, rows[i].Item2, card.Left + 196, y, vf, Ink);
        }
        return bmp;
    }

    /// <summary>A plain error dialog (Grab Text input).</summary>
    public static SKBitmap ErrorDialog()
    {
        const int w = 640, h = 330;
        var bmp = NewBitmap(w, h, SKColor.Parse("#E9EBF2"));
        using var c = new SKCanvas(bmp);
        var d = new SKRect(20, 20, w - 20, h - 20);
        Card(c, d);
        using (var p = Fill(SKColor.Parse("#FEE2E2"))) c.DrawCircle(d.Left + 44, d.Top + 48, 20, p);
        Text(c, "!", d.Left + 44, d.Top + 57, Font(24, SKFontStyleWeight.Bold), SKColor.Parse("#DC2626"), SKTextAlign.Center);
        Text(c, "Sync failed", d.Left + 80, d.Top + 56, Font(24, SKFontStyleWeight.SemiBold), Ink);
        var bf = Font(17);
        Text(c, "We couldn't upload 3 files to the shared folder", d.Left + 32, d.Top + 108, bf, SKColor.Parse("#374151"));
        Text(c, "\"Design reviews / October\".", d.Left + 32, d.Top + 134, bf, SKColor.Parse("#374151"));
        Text(c, "Error code: 0x80070005 (access denied)", d.Left + 32, d.Top + 172, Font(17, SKFontStyleWeight.Normal, false), Muted);
        Text(c, "Check your permissions and try again.", d.Left + 32, d.Top + 198, bf, SKColor.Parse("#374151"));
        var retry = new SKRect(d.Right - 236, d.Bottom - 60, d.Right - 132, d.Bottom - 24);
        using (var p = Fill(Blue)) c.DrawRoundRect(retry, 8, 8, p);
        Text(c, "Retry", retry.MidX, retry.MidY + 6, Font(15, SKFontStyleWeight.SemiBold), SKColors.White, SKTextAlign.Center);
        var det = new SKRect(d.Right - 120, d.Bottom - 60, d.Right - 24, d.Bottom - 24);
        using (var p = Stroke(BorderStrong, 1)) c.DrawRoundRect(det, 8, 8, p);
        Text(c, "Details", det.MidX, det.MidY + 6, Font(15, SKFontStyleWeight.Medium), Ink, SKTextAlign.Center);
        return bmp;
    }
}
