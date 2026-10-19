using System.Diagnostics;
using System.Net;
using MimeKit;
using MimeKit.Utils;
using Shotwise.Core;
using Shotwise.Core.Imaging;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations;

/// <summary>SHR-02: image (bitmap + PNG, optional HTML) or file-drop list to the clipboard through IClipboardWriter.</summary>
public sealed class ClipboardDestination : ShareDestinationBase
{
    public ClipboardDestination(ShareContext ctx) : base(ctx) { }

    public override string Id => "clipboard";
    public override string DisplayName => "Clipboard";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.BuiltIn;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "jpg", "gif", "bmp", "tiff", "webp", "pdf", "mp4", "agif");
    public override bool IsConfigured => Ctx.Clipboard is not null;

    public override Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var cb = Ctx.Clipboard ?? throw new ShotwiseException(ErrorCode.ClipboardUnavailable, "The clipboard is not available.");
        return Task.Run(() =>
        {
            var editor = Ctx.Settings?.Current.Editor;
            var onlyStillImages = files.Count == 1 && files[0].Kind == Core.Library.MediaKind.Image && Ctx.Codec is not null &&
                                  FormatKeys.Of(files[0]) is "png" or "jpg" or "gif" or "bmp" or "tiff" or "webp";
            if (onlyStillImages)
            {
                var img = Ctx.Codec!.Load(files[0].Path);
                var html = editor?.AddHtmlToClipboard == true
                    ? $"<img src=\"{WebUtility.HtmlEncode(new Uri(Path.GetFullPath(files[0].Path)).AbsoluteUri)}\" width=\"{img.Width}\" height=\"{img.Height}\" alt=\"{WebUtility.HtmlEncode(files[0].Title)}\">"
                    : null;
                cb.SetContent(new ClipboardContent { Image = img, PreventTransparency = editor?.PreventClipboardTransparency ?? false, Html = html });
                return ShareResult.Success();
            }
            cb.SetFileDropList(files.Select(f => f.Path).ToList());
            return new ShareResult(ShareOutcome.Succeeded, files.Count == 1 ? "File copied to the clipboard." : $"{files.Count} files copied to the clipboard.");
        }, ct);
    }
}

public sealed class PrintDestinationSettings
{
    /// <summary>Empty = default printer (shell "print" verb); otherwise the shell "printto" verb is used.</summary>
    public string PrinterName { get; set; } = "";
}

/// <summary>
/// SHR-03. Prints through Core <see cref="IPrintService"/> (page setup, layout/grid, preview and printer choice; implemented
/// in UI.Dialogs) when registered; otherwise falls back to the shell "print"/"printto" verb on the exported file.
/// </summary>
public sealed class PrintDestination : ShareDestinationBase
{
    private readonly Func<IPrintService?> _printService;

    public PrintDestination(ShareContext ctx, Func<IPrintService?>? printService = null) : base(ctx)
        => _printService = printService ?? (() => null);

    public override string Id => "print";
    public override string DisplayName => "Print";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.BuiltIn;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "jpg", "gif", "bmp", "tiff", "webp", "pdf");
    public override bool CanShowOptions => true;

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<PrintDestinationSettings>();
        var form = new ShareForm { Description = "Leave the printer empty to use the Windows default printer." };
        form.Add(new ShareFormField { Key = "printer", Label = "Printer name", Value = s.PrinterName });
        if (!await ui.ShowFormAsync("Print options", form).ConfigureAwait(false)) return;
        s.PrinterName = (form.Get<string>("printer") ?? "").Trim();
        SaveSettings(s);
    }

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<PrintDestinationSettings>();
        if (_printService() is { } printer)
        {
            var request = new PrintRequest(files.Select(f => f.Path).ToList(), string.IsNullOrWhiteSpace(s.PrinterName) ? null : s.PrinterName, new PdfPageOptions());
            var printed = await printer.PrintAsync(request, ct).ConfigureAwait(false);
            progress?.Report(1);
            return printed ? new ShareResult(ShareOutcome.Succeeded, "Sent to the printer.") : ShareResult.Cancel();
        }
        return await ShellPrintAsync(files, s, progress, ct).ConfigureAwait(false);
    }

    private static Task<ShareResult> ShellPrintAsync(IReadOnlyList<ShareFile> files, PrintDestinationSettings s, IProgress<double>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var psi = new ProcessStartInfo(files[i].Path) { UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                if (string.IsNullOrWhiteSpace(s.PrinterName)) psi.Verb = "print";
                else { psi.Verb = "printto"; psi.Arguments = "\"" + s.PrinterName.Replace("\"", "") + "\""; }
                try { using var _ = Process.Start(psi); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    throw new ShotwiseException(ErrorCode.ShareFailed, "Windows has no print handler for this file type: " + ex.Message, ex);
                }
                progress?.Report((i + 1) / (double)files.Count);
            }
            return new ShareResult(ShareOutcome.Succeeded, "Sent to the printer.");
        }, ct);
    }
}

public sealed class EmailDestinationSettings
{
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool PromptEachTime { get; set; } = true;
    /// <summary>Null = follow Preferences → Editor "embed email images".</summary>
    public bool? InlineImages { get; set; }
}

/// <summary>SHR-04: builds an unsent .eml draft (X-Unsent) with inline or attached images and opens it in the default mail client.</summary>
public sealed class EmailDestination : ShareDestinationBase
{
    private EmailDestinationSettings? _pending;

    public EmailDestination(ShareContext ctx) : base(ctx) { }

    public override string Id => "email";
    public override string DisplayName => "Email";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.BuiltIn;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "jpg", "gif", "agif", "bmp", "tiff", "webp", "pdf", "mp4");
    public override bool CanShowOptions => true;

    public override Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        _pending = LoadSettings<EmailDestinationSettings>();
        return _pending.PromptEachTime ? AskAsync(ui, _pending, "Email", false) : Task.FromResult(true);
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<EmailDestinationSettings>();
        if (await AskAsync(ui, s, "Email options", true).ConfigureAwait(false)) SaveSettings(s);
    }

    private static async Task<bool> AskAsync(IShareUi ui, EmailDestinationSettings s, string title, bool options)
    {
        var form = new ShareForm { OkText = options ? "Save" : "Create draft" };
        form.Add(new ShareFormField { Key = "to", Label = "To", Value = s.To });
        form.Add(new ShareFormField { Key = "cc", Label = "Cc", Value = s.Cc });
        form.Add(new ShareFormField { Key = "subject", Label = "Subject", Value = s.Subject });
        form.Add(new ShareFormField { Key = "body", Label = "Message", Kind = ShareFieldKind.MultilineText, Value = s.Body });
        if (options) form.Add(new ShareFormField { Key = "prompt", Label = "Ask every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptEachTime });
        if (!await ui.ShowFormAsync(title, form).ConfigureAwait(false)) return false;
        s.To = (form.Get<string>("to") ?? "").Trim();
        s.Cc = (form.Get<string>("cc") ?? "").Trim();
        s.Subject = form.Get<string>("subject") ?? "";
        s.Body = form.Get<string>("body") ?? "";
        if (options) s.PromptEachTime = form.Get<bool>("prompt");
        return true;
    }

    public override Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = _pending ?? LoadSettings<EmailDestinationSettings>();
        var inline = s.InlineImages ?? Ctx.Settings?.Current.Editor.EmbedEmailImages ?? true;
        return Task.Run(() =>
        {
            var msg = BuildMessage(s, files, inline);
            var dir = Path.Combine(Ctx.Paths.Temp, "email");
            Directory.CreateDirectory(dir);
            var eml = Path.Combine(dir, $"draft-{Guid.NewGuid():N}.eml");
            using (var fs = File.Create(eml)) msg.WriteTo(fs, ct);
            progress?.Report(0.8);
            try { using var _ = Process.Start(new ProcessStartInfo(eml) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new ShotwiseException(ErrorCode.ShareFailed, "No email program is registered to open .eml files: " + ex.Message, ex);
            }
            return new ShareResult(ShareOutcome.Succeeded, "Email draft opened.", null, null, new[] { eml });
        }, ct);
    }

    internal static MimeMessage BuildMessage(EmailDestinationSettings s, IReadOnlyList<ShareFile> files, bool inline)
    {
        var msg = new MimeMessage();
        msg.Headers.Add("X-Unsent", "1"); // opens as an editable draft in Outlook / Windows Mail
        AddAddresses(msg.To, s.To);
        AddAddresses(msg.Cc, s.Cc);
        msg.Subject = string.IsNullOrWhiteSpace(s.Subject) ? files.FirstOrDefault()?.Title ?? "Screenshot" : s.Subject;

        var b = new BodyBuilder { TextBody = s.Body };
        var html = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(s.Body)) html.Append("<p>").Append(WebUtility.HtmlEncode(s.Body).Replace("\r\n", "<br>").Replace("\n", "<br>")).Append("</p>");
        foreach (var f in files)
        {
            var isInlineImage = inline && f.Kind == Core.Library.MediaKind.Image && FormatKeys.Of(f) is "png" or "jpg" or "gif" or "bmp" or "webp";
            if (isInlineImage)
            {
                var res = b.LinkedResources.Add(f.Path);
                res.ContentId = MimeUtils.GenerateMessageId();
                html.Append("<p><img src=\"cid:").Append(res.ContentId).Append("\" alt=\"").Append(WebUtility.HtmlEncode(f.Title)).Append("\"></p>");
            }
            else b.Attachments.Add(f.Path);
        }
        if (html.Length > 0) b.HtmlBody = "<html><body>" + html + "</body></html>";
        msg.Body = b.ToMessageBody();
        return msg;
    }

    private static void AddAddresses(InternetAddressList list, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (InternetAddressList.TryParse(text.Replace(';', ','), out var parsed)) list.AddRange(parsed);
    }
}
