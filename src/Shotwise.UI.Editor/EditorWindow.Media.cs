using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Media;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor;

// Video from Images (REC-12), video metadata editor (MED-06) and full application exit (File menu → Exit).
internal sealed partial class EditorWindow
{
    private async Task VideoFromImagesAsync()
    {
        if (ImageDoc is not { } d) return;
        _canvas.CommitTextEditing();
        var result = await _vfi.RecordAsync(d, this);
        if (result is null) return;
        var path = result.Mp4Path; // the recorder may publish to an alternate path: always use the returned one
        if (_ctx.Video is { } video)
        {
            var md = new VideoMetadata(d.Title, null, d.LibraryItem?.Tags ?? Array.Empty<string>(), DateTimeOffset.Now, "Shotwise Editor");
            try { await video.WriteMetadataAsync(path, md); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "Writing video metadata failed for {Path}", path); }
        }
        if (_ctx.Library is { } lib && _ctx.Settings.Current.Library.AutosaveVideos)
        {
            try { path = (await lib.AddVideoAsync(result)).Path; }
            catch (Exception ex) when (ex is ShotwiseException or IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Adding the video to the library failed"); }
        }
        await OpenVideoAsync(path);
        _toasts.Show(ES.F("Msg_VideoRecorded", "Video saved: {0}", Path.GetFileName(path)), MessageKind.Success);
    }

    /// <summary>MED-06 metadata editor: title, comments and tags written into the MP4 (and the library index).</summary>
    private async Task EditVideoPropertiesAsync()
    {
        if (VideoDoc is not { IsGif: false, FilePath: { } path } v || _ctx.Video is not { } video) return;
        if (v.IsDirty)
        {
            await _ctx.InfoAsync(ES.T("Cmd_VideoProperties", "Video Properties…"), ES.T("Msg_SaveVideoFirst", "Save the video first, then edit its properties."));
            return;
        }
        var li = v.LibraryItem;
        var edited = await VideoPropertiesDialog.AskAsync(this, li?.Name ?? v.Title, li?.Url ?? "", li?.Tags ?? Array.Empty<string>());
        if (edited is null) return;
        var md = new VideoMetadata(edited.Value.Title, string.IsNullOrWhiteSpace(edited.Value.Comments) ? null : edited.Value.Comments, edited.Value.Tags, li?.Created ?? File.GetCreationTime(path), li?.AppName);
        // The player keeps the file open: release it while the metadata is rewritten, then reload.
        _video.Pause();
        await _video.LoadAsync(null);
        try
        {
            await _overlay.RunAsync(ES.T("Msg_WritingMetadata", "Saving video properties…"), Path.GetFileName(path), false, (_, ct) => video.WriteMetadataAsync(path, md, ct));
            if (_ctx.Library is { } lib && li is not null)
            {
                var added = edited.Value.Tags.Except(li.Tags, StringComparer.CurrentCultureIgnoreCase).ToList();
                var removed = li.Tags.Except(edited.Value.Tags, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (added.Count > 0) await lib.AddTagsAsync(new[] { li.Id }, added);
                if (removed.Count > 0) await lib.RemoveTagsAsync(new[] { li.Id }, removed);
                await lib.UpdateAsync(li);
            }
        }
        finally
        {
            await ReplaceVideoDocumentAsync(v, path);
        }
    }

    /// <summary>File ▸ Exit Shotwise: closes the editor's documents (prompting for unsaved ones), then exits the app.</summary>
    private async Task ExitApplicationAsync()
    {
        if (!await CloseForRealAsync()) return;
        Application.Current?.Shutdown(0);
    }
}

/// <summary>Title / comments / tags editor for MP4 metadata.</summary>
internal sealed class VideoPropertiesDialog : DialogWindow
{
    private readonly TextBox _title;
    private readonly TextBox _comments;
    private readonly TextBox _tags;

    private VideoPropertiesDialog(string title, string comments, IReadOnlyList<string> tags)
    {
        Title = ES.T("Cmd_VideoProperties", "Video Properties…").TrimEnd('…');
        MinWidth = 460;
        _title = new TextBox { Text = title, MinWidth = 320 };
        _comments = new TextBox { Text = comments, MinWidth = 320, MinHeight = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        _tags = new TextBox { Text = string.Join(", ", tags), MinWidth = 320 };
        var panel = new StackPanel { Margin = new Thickness(20, 48, 20, 16) };
        panel.Children.Add(UiKit.Row(ES.T("VideoProps_Title", "Title"), _title, 100));
        panel.Children.Add(UiKit.Row(ES.T("VideoProps_Comments", "Comments"), _comments, 100));
        panel.Children.Add(UiKit.Row(ES.T("VideoProps_Tags", "Tags"), _tags, 100));
        panel.Children.Add(UiKit.Label(ES.T("VideoProps_TagsHint", "Separate tags with commas.")));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = ES.T("Common_OK", "OK"), IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = ES.T("Common_Cancel", "Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => CancelDialog();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }

    public static async Task<(string Title, string Comments, IReadOnlyList<string> Tags)?> AskAsync(object? owner, string title, string comments, IReadOnlyList<string> tags)
    {
        var d = new VideoPropertiesDialog(title, comments, tags);
        if (await d.ShowDialogAsync(owner) != true) return null;
        var parsed = d._tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        return (d._title.Text.Trim(), d._comments.Text.Trim(), parsed);
    }
}
