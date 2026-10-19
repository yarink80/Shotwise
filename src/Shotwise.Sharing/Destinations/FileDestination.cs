using System.Diagnostics;
using Shotwise.Core;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;
using Shotwise.Sharing.Naming;

namespace Shotwise.Sharing.Destinations;

public enum FileNameMode { Original, Automatic, Fixed }

public sealed class FileDestinationSettings
{
    /// <summary>Target folder; empty = use Preferences → Save As folder rules, then Documents\Shotwise.</summary>
    public string Folder { get; set; } = "";
    public FileNameMode NameMode { get; set; } = FileNameMode.Automatic;
    public string FixedName { get; set; } = "";
    public bool PromptEachTime { get; set; }
    public bool OverwriteExisting { get; set; }
}

/// <summary>SHR-01: copies exported files into a folder using original / fixed / automatic naming.</summary>
public sealed class FileDestination : ShareDestinationBase
{
    private readonly NamingCounter _counter;
    private FileDestinationSettings? _pending;

    public FileDestination(ShareContext ctx) : base(ctx) => _counter = new NamingCounter(ctx.Settings);

    public override string Id => "file";
    public override string DisplayName => "File";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.BuiltIn;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Everything;
    public override bool CanShowOptions => true;

    public override Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        var s = LoadSettings<FileDestinationSettings>();
        _pending = s;
        if (!s.PromptEachTime) return Task.FromResult(true);
        return AskAsync(ui, s, "Save to file");
    }

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<FileDestinationSettings>();
        if (await AskAsync(ui, s, "File output options", includePrompt: true).ConfigureAwait(false)) SaveSettings(s);
    }

    private static async Task<bool> AskAsync(IShareUi ui, FileDestinationSettings s, string title, bool includePrompt = false)
    {
        var form = new ShareForm { OkText = "Save" };
        form.Add(new ShareFormField { Key = "folder", Label = "Folder", Value = s.Folder, Placeholder = "Default save folder" });
        form.Add(new ShareFormField
        {
            Key = "mode", Label = "File name", Kind = ShareFieldKind.Choice, Value = s.NameMode.ToString(),
            Choices = new[] { ("Original", "Keep original name"), ("Automatic", "Automatic (naming preferences)"), ("Fixed", "Fixed name") },
        });
        form.Add(new ShareFormField { Key = "fixed", Label = "Fixed name", Value = s.FixedName });
        form.Add(new ShareFormField { Key = "overwrite", Label = "Overwrite existing files", Kind = ShareFieldKind.Checkbox, Value = s.OverwriteExisting });
        if (includePrompt)
            form.Add(new ShareFormField { Key = "prompt", Label = "Ask every time", Kind = ShareFieldKind.Checkbox, Value = s.PromptEachTime });
        if (!await ui.ShowFormAsync(title, form).ConfigureAwait(false)) return false;
        s.Folder = (form.Get<string>("folder") ?? "").Trim();
        if (Enum.TryParse<FileNameMode>(form.Get<string>("mode"), out var m)) s.NameMode = m;
        s.FixedName = (form.Get<string>("fixed") ?? "").Trim();
        s.OverwriteExisting = form.Get<bool>("overwrite");
        if (includePrompt) s.PromptEachTime = form.Get<bool>("prompt");
        return true;
    }

    public override Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = _pending ?? LoadSettings<FileDestinationSettings>();
        return Task.Run(() =>
        {
            var folder = ResolveFolder(s);
            Directory.CreateDirectory(folder);
            var share = Ctx.Settings?.Current.Share ?? new ShareSettings();
            var omit = share.DoNotIncludeCaptureSourceInNames || (Ctx.Settings?.Current.Privacy.StripMetadataOnShare ?? false);
            var saved = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = files[i];
                var ext = Path.GetExtension(f.Path);
                var inFolder = string.Equals(Path.GetDirectoryName(Path.GetFullPath(f.Path)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
                string target;
                switch (s.NameMode)
                {
                    case FileNameMode.Original:
                        target = NamingEngine.UniquePath(folder, Path.GetFileNameWithoutExtension(f.Path), ext, s.OverwriteExisting);
                        break;
                    case FileNameMode.Fixed when !string.IsNullOrWhiteSpace(s.FixedName):
                        var fixedBase = NamingEngine.Sanitize(s.FixedName);
                        if (files.Count > 1) fixedBase += $"_{i + 1}";
                        target = NamingEngine.UniquePath(folder, fixedBase, ext, s.OverwriteExisting);
                        break;
                    default:
                        var ctxN = new NamingContext { Title = f.Title, Width = f.Width, Height = f.Height, Counter = _counter.Next(), OriginalName = Path.GetFileNameWithoutExtension(f.Path) };
                        target = NamingEngine.UniquePath(folder, NamingEngine.BuildName(ctxN, share.Naming, omit), ext, s.OverwriteExisting);
                        break;
                }
                if (!inFolder || s.NameMode != FileNameMode.Original) File.Copy(f.Path, target, s.OverwriteExisting);
                else target = f.Path;
                saved.Add(target);
                progress?.Report((i + 1) / (double)files.Count);
            }
            Ctx.Settings?.Update(x => x.Share.SaveAs.LastFolder = folder);
            var first = saved[0];
            return new ShareResult(ShareOutcome.Succeeded, saved.Count == 1 ? "Saved " + Path.GetFileName(first) : $"Saved {saved.Count} files to {folder}",
                null, null, saved, () => RevealInExplorer(first));
        }, ct);
    }

    internal string ResolveFolder(FileDestinationSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.Folder)) return Path.GetFullPath(s.Folder);
        var sa = Ctx.Settings?.Current.Share.SaveAs;
        if (sa is not null)
        {
            if (sa.FolderMode == SaveFolderMode.Fixed && !string.IsNullOrWhiteSpace(sa.FixedFolder)) return sa.FixedFolder;
            if (!string.IsNullOrWhiteSpace(sa.LastFolder)) return sa.LastFolder;
        }
        // Documents\Shotwise, or <data root>\Documents when SHOTWISE_DATA_ROOT redirects the profile (tests, smoke runs).
        return Ctx.Paths.DefaultSaveFolder;
    }

    private static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false }); }
        catch (Exception) { /* best effort */ }
    }
}
