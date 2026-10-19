using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml.Linq;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Office;

public enum OfficeInsertPosition { Cursor, EndOfDocument }
public enum SlideLayoutChoice { Blank, TitleOnly, TitleAndContent }

public class OfficeSettings
{
    /// <summary>False = embed the picture; true = link to a persistent copy in <see cref="LinkFolder"/>.</summary>
    public bool Link { get; set; }
    public string LinkFolder { get; set; } = "";
    /// <summary>True = always create a new document / workbook / presentation instead of using the active one.</summary>
    public bool NewDocument { get; set; }
    public bool PromptEachTime { get; set; }
}

public sealed class WordSettings : OfficeSettings
{
    public OfficeInsertPosition Position { get; set; } = OfficeInsertPosition.Cursor;
}

public sealed class ExcelSettings : OfficeSettings
{
    public bool NewSheet { get; set; }
}

public sealed class PowerPointSettings : OfficeSettings
{
    public bool NewSlide { get; set; } = true;
    public SlideLayoutChoice Layout { get; set; } = SlideLayoutChoice.Blank;
}

public sealed class OneNoteSettings
{
    public bool NewPage { get; set; }
    /// <summary>Section to create new pages in; empty = the section currently shown.</summary>
    public string SectionName { get; set; } = "";
    public bool PromptEachTime { get; set; }
}

/// <summary>Shared plumbing: STA execution, form, COM error mapping, "bring to front" completion action.</summary>
public abstract class OfficeDestinationBase<TSettings> : ShareDestinationBase where TSettings : class, new()
{
    private TSettings? _pending;

    protected OfficeDestinationBase(ShareContext ctx) : base(ctx) { }

    protected abstract string ProgId { get; }
    protected virtual string[] FallbackProgIds => Array.Empty<string>();
    protected abstract string AppName { get; }

    public override DestinationCategory Category => DestinationCategory.Microsoft;
    public override bool CanShowOptions => true;
    public override bool IsConfigured => OfficeCom.IsRegistered(new[] { ProgId }.Concat(FallbackProgIds).ToArray());
    public override IReadOnlyList<PolicyFeature> RequiredPolicies => Array.Empty<PolicyFeature>(); // local desktop automation, not cloud

    protected abstract bool PromptEachTime(TSettings s);
    protected abstract void AddFormFields(ShareForm form, TSettings s);
    protected abstract void ReadForm(ShareForm form, TSettings s);
    protected abstract string Insert(TSettings s, IReadOnlyList<ShareFile> files, IProgress<double>? progress);
    protected abstract void Activate(object app);

    public override async Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        var s = LoadSettings<TSettings>();
        _pending = s;
        if (!PromptEachTime(s)) return true;
        var form = new ShareForm { OkText = "Insert" };
        AddFormFields(form, s);
        if (!await ui.ShowFormAsync(AppName, form).ConfigureAwait(false)) return false;
        ReadForm(form, s);
        return true;
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<TSettings>();
        var form = new ShareForm { OkText = "Save" };
        AddFormFields(form, s);
        form.Add(new ShareFormField { Key = "__prompt", Label = "Ask every time", Kind = ShareFieldKind.Checkbox, Value = PromptEachTime(s) });
        if (!await ui.ShowFormAsync(AppName + " options", form).ConfigureAwait(false)) return;
        ReadForm(form, s);
        SetPrompt(s, form.Get<bool>("__prompt"));
        SaveSettings(s);
    }

    protected abstract void SetPrompt(TSettings s, bool value);

    public override async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = _pending ?? LoadSettings<TSettings>();
        try
        {
            var message = await OfficeCom.Sta.RunAsync(() => Insert(s, files, progress), ct).ConfigureAwait(false);
            return new ShareResult(ShareOutcome.Succeeded, message, null, null, null, () => _ = OfficeCom.Sta.RunAsync(() =>
            {
                var app = OfficeCom.TryGetRunning(ProgId);
                if (app is not null) try { Activate(app); } catch (COMException) { }
            }));
        }
        catch (COMException ex) { throw OfficeCom.Wrap(ex, AppName); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
        {
            throw new ShotwiseException(ErrorCode.ShareFailed, $"{AppName} did not accept the request ({ex.Message}). The installed version may be unsupported.", ex);
        }
    }

    protected string LinkPath(OfficeSettings s, ShareFile f)
    {
        if (!s.Link) return f.Path;
        var folder = string.IsNullOrWhiteSpace(s.LinkFolder)
            ? Path.Combine(Ctx.Paths.DefaultSaveFolder, "Linked")
            : s.LinkFolder;
        return OfficeCom.PersistForLinking(f.Path, folder);
    }

    protected static void AddCommonFields(ShareForm form, OfficeSettings s, string newDocLabel)
    {
        form.Add(new ShareFormField { Key = "new", Label = newDocLabel, Kind = ShareFieldKind.Checkbox, Value = s.NewDocument });
        form.Add(new ShareFormField { Key = "link", Label = "Link to the file instead of embedding", Kind = ShareFieldKind.Checkbox, Value = s.Link });
        form.Add(new ShareFormField { Key = "linkfolder", Label = "Folder for linked files", Value = s.LinkFolder, Placeholder = @"Documents\Shotwise\Linked" });
    }

    protected static void ReadCommonFields(ShareForm form, OfficeSettings s)
    {
        s.NewDocument = form.Get<bool>("new");
        s.Link = form.Get<bool>("link");
        s.LinkFolder = (form.Get<string>("linkfolder") ?? "").Trim();
    }
}

/// <summary>SHR-07 Word: insert at the cursor / end of the active document, or a new document.</summary>
public sealed class WordDestination : OfficeDestinationBase<WordSettings>
{
    public WordDestination(ShareContext ctx) : base(ctx) { }
    public override string Id => "word";
    public override string DisplayName => "Word";
    public override string IconGlyph => "";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Raster5;
    protected override string ProgId => "Word.Application";
    protected override string AppName => "Microsoft Word";
    protected override bool PromptEachTime(WordSettings s) => s.PromptEachTime;
    protected override void SetPrompt(WordSettings s, bool v) => s.PromptEachTime = v;
    protected override void Activate(object app) { dynamic a = app; a.Activate(); }

    protected override void AddFormFields(ShareForm form, WordSettings s)
    {
        AddCommonFields(form, s, "Always create a new document");
        form.Add(new ShareFormField
        {
            Key = "pos", Label = "Insert at", Kind = ShareFieldKind.Choice, Value = s.Position.ToString(),
            Choices = new[] { ("Cursor", "Cursor position"), ("EndOfDocument", "End of document") },
        });
    }

    protected override void ReadForm(ShareForm form, WordSettings s)
    {
        ReadCommonFields(form, s);
        if (Enum.TryParse<OfficeInsertPosition>(form.Get<string>("pos"), out var p)) s.Position = p;
    }

    protected override string Insert(WordSettings s, IReadOnlyList<ShareFile> files, IProgress<double>? progress)
    {
        dynamic app = OfficeCom.GetApplication(ProgId, true, AppName);
        app.Visible = true;
        dynamic doc = s.NewDocument || (int)app.Documents.Count == 0 ? app.Documents.Add() : app.ActiveDocument;
        dynamic sel = app.Selection;
        if (s.Position == OfficeInsertPosition.EndOfDocument) sel.EndKey(6); // wdStory
        for (var i = 0; i < files.Count; i++)
        {
            var path = LinkPath(s, files[i]);
            dynamic range = sel.Range;
            range.Collapse(0); // wdCollapseEnd: never replace a selection
            dynamic shape = doc.InlineShapes.AddPicture(path, s.Link, !s.Link, range);
            try
            {
                dynamic page = doc.PageSetup;
                double usable = (double)page.PageWidth - (double)page.LeftMargin - (double)page.RightMargin;
                shape.LockAspectRatio = -1;
                if ((double)shape.Width > usable) shape.Width = usable;
            }
            catch (COMException) { /* sizing is cosmetic */ }
            dynamic after = shape.Range;
            after.InsertParagraphAfter();
            after.Collapse(0);
            after.Select();
            progress?.Report((i + 1) / (double)files.Count);
        }
        app.Activate();
        return files.Count == 1 ? "Inserted into Word." : $"Inserted {files.Count} pictures into Word.";
    }
}

/// <summary>SHR-07 Excel: pictures anchored at the active cell, on a new sheet, or in a new workbook.</summary>
public sealed class ExcelDestination : OfficeDestinationBase<ExcelSettings>
{
    public ExcelDestination(ShareContext ctx) : base(ctx) { }
    public override string Id => "excel";
    public override string DisplayName => "Excel";
    public override string IconGlyph => "";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Raster5;
    protected override string ProgId => "Excel.Application";
    protected override string AppName => "Microsoft Excel";
    protected override bool PromptEachTime(ExcelSettings s) => s.PromptEachTime;
    protected override void SetPrompt(ExcelSettings s, bool v) => s.PromptEachTime = v;
    protected override void Activate(object app) { dynamic a = app; a.Visible = true; }

    protected override void AddFormFields(ShareForm form, ExcelSettings s)
    {
        AddCommonFields(form, s, "Always create a new workbook");
        form.Add(new ShareFormField { Key = "sheet", Label = "Put pictures on a new worksheet", Kind = ShareFieldKind.Checkbox, Value = s.NewSheet });
    }

    protected override void ReadForm(ShareForm form, ExcelSettings s)
    {
        ReadCommonFields(form, s);
        s.NewSheet = form.Get<bool>("sheet");
    }

    protected override string Insert(ExcelSettings s, IReadOnlyList<ShareFile> files, IProgress<double>? progress)
    {
        dynamic app = OfficeCom.GetApplication(ProgId, true, AppName);
        app.Visible = true;
        var freshBook = s.NewDocument || (int)app.Workbooks.Count == 0;
        dynamic wb = freshBook ? app.Workbooks.Add() : app.ActiveWorkbook;
        var freshSheet = !freshBook && s.NewSheet;
        dynamic sheet = freshSheet ? wb.Worksheets.Add() : wb.ActiveSheet;
        dynamic anchor = !freshBook && !freshSheet ? app.ActiveCell : sheet.Range("A1");
        double left = (double)anchor.Left, top = (double)anchor.Top;
        for (var i = 0; i < files.Count; i++)
        {
            var path = LinkPath(s, files[i]);
            // msoTrue = -1, msoFalse = 0; width/height -1 = natural size.
            dynamic pic = sheet.Shapes.AddPicture(path, s.Link ? -1 : 0, s.Link ? 0 : -1, left, top, -1, -1);
            top += (double)pic.Height + 10;
            progress?.Report((i + 1) / (double)files.Count);
        }
        return files.Count == 1 ? "Inserted into Excel." : $"Inserted {files.Count} pictures into Excel.";
    }
}

/// <summary>SHR-07 PowerPoint: current/new slide with a layout; pictures, animated GIFs, MP4 video and step-capture slides.</summary>
public sealed class PowerPointDestination : OfficeDestinationBase<PowerPointSettings>
{
    public PowerPointDestination(ShareContext ctx) : base(ctx) { }
    public override string Id => "powerpoint";
    public override string DisplayName => "PowerPoint";
    public override string IconGlyph => "";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Set("png", "jpg", "gif", "agif", "bmp", "tiff", "mp4");
    public override bool SupportsStepCapture => true;
    protected override string ProgId => "PowerPoint.Application";
    protected override string AppName => "Microsoft PowerPoint";
    protected override bool PromptEachTime(PowerPointSettings s) => s.PromptEachTime;
    protected override void SetPrompt(PowerPointSettings s, bool v) => s.PromptEachTime = v;
    protected override void Activate(object app) { dynamic a = app; a.Activate(); }

    protected override void AddFormFields(ShareForm form, PowerPointSettings s)
    {
        AddCommonFields(form, s, "Always create a new presentation");
        form.Add(new ShareFormField { Key = "slide", Label = "Add a new slide (otherwise use the current slide)", Kind = ShareFieldKind.Checkbox, Value = s.NewSlide });
        form.Add(new ShareFormField
        {
            Key = "layout", Label = "New slide layout", Kind = ShareFieldKind.Choice, Value = s.Layout.ToString(),
            Choices = new[] { ("Blank", "Blank"), ("TitleOnly", "Title only"), ("TitleAndContent", "Title and content") },
        });
    }

    protected override void ReadForm(ShareForm form, PowerPointSettings s)
    {
        ReadCommonFields(form, s);
        s.NewSlide = form.Get<bool>("slide");
        if (Enum.TryParse<SlideLayoutChoice>(form.Get<string>("layout"), out var l)) s.Layout = l;
    }

    protected override string Insert(PowerPointSettings s, IReadOnlyList<ShareFile> files, IProgress<double>? progress)
    {
        dynamic app = OfficeCom.GetApplication(ProgId, true, AppName);
        app.Visible = -1;
        var fresh = s.NewDocument || (int)app.Presentations.Count == 0;
        dynamic pres = fresh ? app.Presentations.Add() : app.ActivePresentation;
        double sw = (double)pres.PageSetup.SlideWidth, sh = (double)pres.PageSetup.SlideHeight;
        var stepMode = files.Any(f => f.IsStepCapture);
        var layout = stepMode && s.Layout == SlideLayoutChoice.Blank ? SlideLayoutChoice.TitleOnly : s.Layout;

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            dynamic slide;
            var useCurrent = !stepMode && i == 0 && !s.NewSlide && !fresh && TryCurrentSlide(app, out slide);
            if (!useCurrent)
            {
                var idx = (int)pres.Slides.Count + 1;
                slide = pres.Slides.Add(idx, layout switch { SlideLayoutChoice.TitleOnly => 11, SlideLayoutChoice.TitleAndContent => 16, _ => 12 });
                if (fresh && i == 0 && idx == 2 && IsEmptyTitleSlide(pres.Slides[1])) { pres.Slides[1].Delete(); }
            }
            else TryCurrentSlide(app, out slide);

            var area = ComputeArea(slide, sw, sh, f.Title, stepMode);
            var path = LinkPath(s, f);
            dynamic shape = FormatKeys.Of(f) == "mp4"
                ? slide.Shapes.AddMediaObject2(path, s.Link ? -1 : 0, s.Link ? 0 : -1, area.Left, area.Top, -1, -1)
                : slide.Shapes.AddPicture(path, s.Link ? -1 : 0, s.Link ? 0 : -1, area.Left, area.Top, -1, -1);
            Fit(shape, area);
            progress?.Report((i + 1) / (double)files.Count);
        }
        app.Activate();
        return stepMode ? $"Added {files.Count} step slides to PowerPoint." : "Inserted into PowerPoint.";
    }

    private static bool TryCurrentSlide(dynamic app, out dynamic slide)
    {
        try { slide = app.ActiveWindow.View.Slide; return true; }
        catch (COMException) { slide = null!; return false; }
    }

    private static bool IsEmptyTitleSlide(dynamic slide)
    {
        try
        {
            int n = (int)slide.Shapes.Count;
            for (var i = 1; i <= n; i++)
            {
                dynamic shape = slide.Shapes.Item(i);
                if ((int)shape.HasTextFrame != 0 && (string)shape.TextFrame.TextRange.Text != "") return false;
            }
            return true;
        }
        catch (COMException) { return false; }
    }

    private readonly record struct Area(double Left, double Top, double Width, double Height);

    private static Area ComputeArea(dynamic slide, double sw, double sh, string title, bool setTitle)
    {
        var area = new Area(sw * 0.05, sh * 0.05, sw * 0.9, sh * 0.9);
        var toDelete = new List<dynamic>();
        try
        {
            int count = (int)slide.Shapes.Count;
            for (var si = 1; si <= count; si++)
            {
                dynamic shape = slide.Shapes.Item(si);
                if ((int)shape.Type != 14) continue; // msoPlaceholder
                var pt = (int)shape.PlaceholderFormat.Type;
                if (pt is 1 or 3) // title / center title
                {
                    if (setTitle && !string.IsNullOrEmpty(title)) shape.TextFrame.TextRange.Text = title;
                    var bottom = (double)shape.Top + (double)shape.Height + 8;
                    area = new Area(area.Left, Math.Max(area.Top, bottom), area.Width, Math.Max(40, sh * 0.95 - Math.Max(area.Top, bottom)));
                }
                else if (pt is 2 or 7 or 13 or 18) // body / object / picture
                {
                    area = new Area((double)shape.Left, (double)shape.Top, (double)shape.Width, (double)shape.Height);
                    toDelete.Add(shape);
                }
            }
            foreach (var d in toDelete) d.Delete();
        }
        catch (COMException) { /* layout adornments are best effort */ }
        return area;
    }

    private static void Fit(dynamic shape, Area a)
    {
        double w = (double)shape.Width, h = (double)shape.Height;
        if (w <= 0 || h <= 0) return;
        var scale = Math.Min(1.0, Math.Min(a.Width / w, a.Height / h));
        shape.LockAspectRatio = -1;
        shape.Width = w * scale;
        shape.Left = a.Left + (a.Width - w * scale) / 2;
        shape.Top = a.Top + (a.Height - h * scale) / 2;
    }
}

/// <summary>SHR-07 OneNote: appends the picture to the current page (or a new page) via UpdatePageContent XML.</summary>
public sealed class OneNoteDestination : OfficeDestinationBase<OneNoteSettings>
{
    private const string Ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    public OneNoteDestination(ShareContext ctx) : base(ctx) { }
    public override string Id => "onenote";
    public override string DisplayName => "OneNote";
    public override string IconGlyph => "";
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Raster5;
    protected override string ProgId => "OneNote.Application";
    protected override string[] FallbackProgIds => new[] { "OneNote.Application.15" };
    protected override string AppName => "Microsoft OneNote";
    protected override bool PromptEachTime(OneNoteSettings s) => s.PromptEachTime;
    protected override void SetPrompt(OneNoteSettings s, bool v) => s.PromptEachTime = v;
    protected override void Activate(object app) { dynamic a = app; a.Windows.CurrentWindow.Active = true; }

    protected override void AddFormFields(ShareForm form, OneNoteSettings s)
    {
        form.Add(new ShareFormField { Key = "newpage", Label = "Create a new page", Kind = ShareFieldKind.Checkbox, Value = s.NewPage });
        form.Add(new ShareFormField { Key = "section", Label = "Section for new pages (blank = current)", Value = s.SectionName });
    }

    protected override void ReadForm(ShareForm form, OneNoteSettings s)
    {
        s.NewPage = form.Get<bool>("newpage");
        s.SectionName = (form.Get<string>("section") ?? "").Trim();
    }

    protected override string Insert(OneNoteSettings s, IReadOnlyList<ShareFile> files, IProgress<double>? progress)
    {
        dynamic app = OfficeCom.GetApplication(ProgId, true, AppName, FallbackProgIds);
        string pageId;
        if (s.NewPage)
        {
            string sectionId = string.IsNullOrEmpty(s.SectionName) ? (string)app.Windows.CurrentWindow.CurrentSectionId : FindSectionId(app, s.SectionName);
            string newId = "";
            app.CreateNewPage(sectionId, out newId, 0);
            pageId = newId;
            var title = files.Count == 1 ? files[0].Title : "Shotwise captures";
            var titleXml = $"<one:Page xmlns:one=\"{Ns}\" ID=\"{pageId}\"><one:Title><one:OE><one:T><![CDATA[{title.Replace("]]>", "]]]]><![CDATA[>")}]]></one:T></one:OE></one:Title></one:Page>";
            app.UpdatePageContent(titleXml);
        }
        else pageId = (string)app.Windows.CurrentWindow.CurrentPageId;
        if (string.IsNullOrEmpty(pageId)) throw new ShotwiseException(ErrorCode.ShareFailed, "OneNote has no page open. Open a page or enable 'Create a new page'.");

        string xml = "";
        app.GetPageContent(pageId, out xml, 0 /* piBasic */);
        var y = NextFreeY(xml);
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var data = Convert.ToBase64String(File.ReadAllBytes(f.Path));
            var fmt = FormatKeys.Of(f) switch { "jpg" => "jpg", "gif" => "gif", "bmp" => "bmp", "tiff" => "tif", _ => "png" };
            var wPt = (f.Width * 0.75).ToString("0.##", CultureInfo.InvariantCulture);
            var hPt = (f.Height * 0.75).ToString("0.##", CultureInfo.InvariantCulture);
            var page = $"<one:Page xmlns:one=\"{Ns}\" ID=\"{SecurityElement.Escape(pageId)}\"><one:Outline><one:Position x=\"36\" y=\"{y.ToString("0.##", CultureInfo.InvariantCulture)}\" z=\"0\"/>" +
                       $"<one:OEChildren><one:OE><one:Image format=\"{fmt}\"><one:Size width=\"{wPt}\" height=\"{hPt}\" isSetByUser=\"true\"/><one:Data>{data}</one:Data></one:Image></one:OE></one:OEChildren></one:Outline></one:Page>";
            app.UpdatePageContent(page);
            y += f.Height * 0.75 + 24;
            progress?.Report((i + 1) / (double)files.Count);
        }
        return "Inserted into OneNote.";
    }

    internal static double NextFreeY(string pageXml)
    {
        try
        {
            var doc = XDocument.Parse(pageXml);
            XNamespace one = Ns;
            double max = 36;
            foreach (var o in doc.Descendants(one + "Outline"))
            {
                var pos = o.Element(one + "Position");
                var size = o.Element(one + "Size");
                if (pos is null) continue;
                var py = double.Parse(pos.Attribute("y")?.Value ?? "0", CultureInfo.InvariantCulture);
                var ph = size is null ? 40 : double.Parse(size.Attribute("height")?.Value ?? "40", CultureInfo.InvariantCulture);
                max = Math.Max(max, py + ph + 24);
            }
            return max;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException) { return 36; }
    }

    private static string FindSectionId(dynamic app, string name)
    {
        string xml = "";
        app.GetHierarchy("", 3 /* hsSections */, out xml);
        var doc = XDocument.Parse(xml);
        XNamespace one = Ns;
        var match = doc.Descendants(one + "Section").FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
        return (string?)match?.Attribute("ID") ?? throw new ShotwiseException(ErrorCode.DestinationNotConfigured, $"OneNote section '{name}' was not found.");
    }
}
