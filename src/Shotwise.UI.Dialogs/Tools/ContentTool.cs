using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;

namespace Shotwise.UI.Dialogs.Tools;

// Opt-in content-authoring entrypoint, excluded from the production library.
internal static class ContentTool
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke")) { UiSmoke.Run(); return 0; }
        if (args.FirstOrDefault(a => a.StartsWith("--shots=", StringComparison.Ordinal)) is { } shots) { DialogShots.Dark = args.Contains("--dark"); DialogShots.Only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?["--only=".Length..]; DialogShots.Run(shots["--shots=".Length..]); return 0; }
        var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        var content = Path.Combine(root, "src", "Shotwise.App", "Content");
        var folder = Path.Combine(content, "templates"); Directory.CreateDirectory(folder);
        if (!args.Contains("--verify-only"))
        {
            foreach (var (id, name, count, columns) in new[] { ("step-guide", "Step guide", 3, 1), ("comparison", "Comparison", 2, 2), ("before-after", "Before and after", 2, 2), ("grid-2", "Two image grid", 2, 1), ("grid-4", "Four image grid", 4, 2), ("grid-6", "Six image grid", 6, 3), ("process", "Process", 4, 1), ("checklist", "Checklist", 5, 1) })
            {
                var template = Make(id, name, count, columns);
                AtomicFile.WriteAllText(Path.Combine(folder, id + ".shottemplate"), JsonSerializer.Serialize(template, ShotwiseJson.Indented));
            }
        }
        var templates = Directory.GetFiles(folder, "*.shottemplate");
        if (templates.Length < 8) throw new InvalidDataException("Expected at least eight templates.");
        foreach (var file in templates)
        {
            var t = JsonSerializer.Deserialize<TemplateDefinition>(File.ReadAllText(file), ShotwiseJson.Compact)!;
            var roundtrip = JsonSerializer.Deserialize<TemplateDefinition>(JsonSerializer.Serialize(t, ShotwiseJson.Compact), ShotwiseJson.Compact)!;
            var document = roundtrip.Instantiate();
            var page = document.Pages.Single();
            var sections = page.Sections.Select(s => s.Id).ToHashSet();
            var zones = page.AllAnnotations().OfType<DropZoneAnnotation>().ToArray();
            if (zones.Length != t.MaxImages || zones.Any(z => !sections.Contains(z.SectionId)) || page.CanvasSize.Width <= 0 || page.CanvasSize.Height <= 0) throw new InvalidDataException(file);
            if (page.AllAnnotations().Select(a => a.Id).Distinct().Count() != page.AllAnnotations().Count()) throw new InvalidDataException("Duplicate graphic ids.");
        }
        int stamps = 0;
        foreach (var manifest in Directory.GetFiles(Path.Combine(content, "stamps"), "packageinfo.json", SearchOption.AllDirectories))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (var stamp in json.RootElement.GetProperty("stamps").EnumerateArray())
            {
                foreach (var culture in new[] { "en", "de", "es", "fr", "ja", "pt-BR" })
                    if (stamp.GetProperty("keywords").GetProperty(culture).GetArrayLength() == 0) throw new InvalidDataException("Missing keywords.");
                XDocument.Load(Path.Combine(Path.GetDirectoryName(manifest)!, stamp.GetProperty("file").GetString()!)); stamps++;
            }
        }
        if (stamps < 120) throw new InvalidDataException("Expected at least 120 stamps.");
        foreach (var name in new[] { "arrow", "hand", "ibeam", "wait", "cross", "move" }) XDocument.Load(Path.Combine(content, "cursors", name + ".svg"));
        Console.WriteLine($"PASS: {templates.Length} Core template round-trips/instantiations; {stamps} SVGs and six-language keywords; six cursor SVGs.");
        return 0;
    }
    private static TemplateDefinition Make(string id, string name, int count, int columns)
    {
        const double width = 1200; int rows = (int)Math.Ceiling((double)count / columns); double cellHeight = id is "step-guide" or "process" ? 420 : 340;
        var t = new TemplateDefinition { Id = "shotwise." + id, Name = name, Category = "Getting started", MaxImages = count, IsBuiltIn = true, Keywords = new() { id, "layout", "images" } };
        t.Prototype = new Page { CanvasSize = new Size2(width, 180 + rows * (cellHeight + 40) + 80), Background = new CanvasBackground(new Argb(0xFFF8FAFC)) };
        var header = new TemplateSection { Type = SectionType.Header, Bounds = new Rect2(0, 0, width, 140), Background = new Argb(0xFF172033) };
        header.Graphics.Add(new TextAnnotation { Start = new Point2(48, 36), End = new Point2(1152, 108), Text = RichText.FromPlain(name, fontSize: 44, color: Argb.White, bold: true), IgnoresUndoAll = true }); t.Prototype.Sections.Add(header);
        for (int row = 0, index = 0; row < rows; row++)
        {
            double y = 180 + row * (cellHeight + 40);
            var section = new TemplateSection { Type = id is "step-guide" or "process" ? SectionType.Repeatable : SectionType.Body, Bounds = new Rect2(32, y, width - 64, cellHeight), Background = Argb.White };
            for (int col = 0; col < columns && index < count; col++, index++)
            {
                double cellWidth = (width - 96 - (columns - 1) * 24) / columns; double x = 48 + col * (cellWidth + 24);
                string caption = id == "before-after" ? (index == 0 ? "Before" : "After") : id == "checklist" ? "Check " + (index + 1) : "Step " + (index + 1);
                section.Graphics.Add(new TextAnnotation { Start = new Point2(x, y + 12), End = new Point2(x + cellWidth, y + 52), Text = RichText.FromPlain(caption, fontSize: 24, color: new Argb(0xFF0F766E), bold: true), IgnoresUndoAll = true });
                section.Graphics.Add(new DropZoneAnnotation { SectionId = section.Id, Start = new Point2(x, y + 68), End = new Point2(x + cellWidth, y + cellHeight - 20), CornerRadius = 12 });
            }
            t.Prototype.Sections.Add(section);
        }
        var footer = new TemplateSection { Type = SectionType.Footer, Bounds = new Rect2(32, t.Prototype.CanvasSize.Height - 60, width - 64, 40) };
        footer.Graphics.Add(new TextAnnotation { Start = new Point2(48, t.Prototype.CanvasSize.Height - 56), End = new Point2(1152, t.Prototype.CanvasSize.Height - 20), Text = RichText.FromPlain("Created with Shotwise", fontSize: 16, color: new Argb(0xFF475569)), IgnoresUndoAll = true }); t.Prototype.Sections.Add(footer);
        return t;
    }
}

