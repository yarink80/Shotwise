using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Library;

public enum MediaKind { Image, Video, Gif, Other }

public enum LibrarySort { NewestFirst, OldestFirst, NameAscending, NameDescending, LargestFirst, SmallestFirst }

public enum LibraryGroupBy { Date, Year, Month, App, Website, Tag, Kind }

public sealed record LibraryItem(Guid Id, string Path, MediaKind Kind, string Name, DateTimeOffset Created, DateTimeOffset Modified,
    long SizeBytes, int Width, int Height, TimeSpan? Duration, string? AppName, string? WindowTitle, string? Url, IReadOnlyList<string> Tags, bool IsFavorite, string ThumbnailPath)
{
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? Array.Empty<string>();
    /// <summary>True when the item was produced by step capture (filter "Step Captures").</summary>
    public bool IsStepCapture { get; init; }
    /// <summary>Host part of <see cref="Url"/> (for "Websites" grouping).</summary>
    public string? Website => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : null;
}

public sealed record LibraryQuery(string? Text = null, MediaKind? Kind = null, string? App = null, string? Website = null, string? Tag = null,
    DateOnly? From = null, DateOnly? To = null, bool FavoritesOnly = false, LibrarySort Sort = LibrarySort.NewestFirst, int Skip = 0, int Take = 200);

public sealed record ShareHistoryEntry(DateTimeOffset At, string DestinationId, string DestinationName, string? Link, string? Title);

public enum LibraryChangeKind { Added, Updated, Removed, Reset }

public sealed class LibraryChangedEventArgs : EventArgs
{
    public LibraryChangedEventArgs(LibraryChangeKind kind, IReadOnlyList<Guid> ids)
    {
        Kind = kind;
        Ids = ids;
    }

    public LibraryChangeKind Kind { get; }
    public IReadOnlyList<Guid> Ids { get; }
}

public interface ILibraryService
{
    string LibraryRoot { get; }
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Autosaves a new capture as .shotx in the library and indexes it.</summary>
    Task<LibraryItem> AddCaptureAsync(ShotDocument doc, BgraImage flattenedPreview, CancellationToken ct = default);
    Task<LibraryItem> AddVideoAsync(VideoCaptureResult video, CancellationToken ct = default);
    Task<LibraryItem> ImportFileAsync(string path, CancellationToken ct = default);
    /// <summary>Re-indexes after save (text, metadata, thumbnail).</summary>
    Task UpdateAsync(LibraryItem item, ShotDocument? doc = null, CancellationToken ct = default);
    Task<IReadOnlyList<LibraryItem>> QueryAsync(LibraryQuery q, CancellationToken ct = default);
    Task<LibraryItem?> GetAsync(Guid id, CancellationToken ct = default);
    Task<LibraryItem?> FindByPathAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<(string Key, int Count)>> GroupCountsAsync(LibraryGroupBy by, CancellationToken ct = default);
    Task AddTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags);
    Task RemoveTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags);
    Task<IReadOnlyList<string>> AllTagsAsync();
    Task SetFavoriteAsync(Guid id, bool fav);
    Task DeleteAsync(IEnumerable<Guid> ids, bool toRecycleBin = true);
    Task RenameAsync(Guid id, string newName);
    Task MoveLibraryAsync(string newRoot, IProgress<double>? p, CancellationToken ct);
    Task BackupAsync(string archivePath, IProgress<double>? p, CancellationToken ct);
    Task RestoreAsync(string archivePath, IProgress<double>? p, CancellationToken ct);
    Task SetOcrTextAsync(Guid id, string text);
    Task AddShareHistoryAsync(Guid itemId, ShareHistoryEntry e);
    Task<IReadOnlyList<ShareHistoryEntry>> GetShareHistoryAsync(Guid? itemId = null);
    event EventHandler<LibraryChangedEventArgs>? Changed;
}
