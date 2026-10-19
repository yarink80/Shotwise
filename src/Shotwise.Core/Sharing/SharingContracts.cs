using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;

namespace Shotwise.Core.Sharing;

public sealed record ShareFile(string Path, MediaKind Kind, string Format, string Title, int Width, int Height, TimeSpan? Duration, bool IsStepCapture)
{
    /// <summary>Library item this file was materialized from (share history is recorded against it); null = resolve by path.</summary>
    public Guid? LibraryItemId { get; init; }
}

public enum ShareOutcome { Succeeded, Failed, Cancelled }

public sealed record ShareResult(ShareOutcome Outcome, string? Message = null, string? Link = null, string? ClipboardText = null, IReadOnlyList<string>? Locations = null, Action? OpenDestination = null)
{
    public static ShareResult Success(string? link = null, IReadOnlyList<string>? locations = null) => new(ShareOutcome.Succeeded, null, link, link, locations);
    public static ShareResult Failure(string message) => new(ShareOutcome.Failed, message);
    public static ShareResult Cancel() => new(ShareOutcome.Cancelled);
}

public enum DestinationCategory { BuiltIn, Microsoft, Storage, Social, Uploader, Custom }

public interface IShareDestination
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Segoe Fluent Icons glyph (e.g. "").</summary>
    string IconGlyph { get; }
    DestinationCategory Category { get; }
    /// <summary>Accepted formats: "png","jpg","gif","agif","mp4","pdf","tiff","bmp","webp","shotx".</summary>
    IReadOnlySet<string> SupportedFormats { get; }
    bool SupportsMultiple { get; }
    bool SupportsStepCapture { get; }
    bool CanShowOptions { get; }
    bool RequiresAccount { get; }
    bool IsConfigured { get; }
    Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct);
    Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct);
    Task ShowOptionsAsync(IShareUi ui);
    Task SignInAsync(IShareUi ui, CancellationToken ct);
    Task SignOutAsync();
}

public enum ShareFieldKind { Text, MultilineText, Password, Number, Choice, Checkbox, FolderTree, Label }

/// <summary>One field of a declarative form rendered by IShareUi (values are written back into <see cref="Value"/>).</summary>
public sealed class ShareFormField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public ShareFieldKind Kind { get; init; } = ShareFieldKind.Text;
    public object? Value { get; set; }
    public string? Placeholder { get; init; }
    public string? HelpText { get; init; }
    public bool Required { get; init; }
    /// <summary>Choices for <see cref="ShareFieldKind.Choice"/> (Value = selected key).</summary>
    public IReadOnlyList<(string Key, string Label)> Choices { get; init; } = Array.Empty<(string, string)>();
    /// <summary>Lazy loader for <see cref="ShareFieldKind.FolderTree"/> (null parent = roots).</summary>
    public Func<string?, Task<IReadOnlyList<RemoteFolder>>>? LoadChildren { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
}

/// <summary>Declarative form (title shown by the caller).</summary>
public sealed class ShareForm
{
    public List<ShareFormField> Fields { get; } = new();
    public string? Description { get; init; }
    public string OkText { get; init; } = "OK";

    public ShareForm Add(ShareFormField field) { Fields.Add(field); return this; }
    public T? Get<T>(string key) => Fields.FirstOrDefault(f => f.Key == key)?.Value is T t ? t : default;
}

public sealed record RemoteFolder(string Id, string Name, string? ParentId, bool HasChildren, string? Path = null);

/// <summary>Lets destinations show generic dialogs without referencing WPF (implemented in UI.Dialogs).</summary>
public interface IShareUi
{
    Task<bool> ShowFormAsync(string title, ShareForm form);
    Task<string?> PickFolderAsync(string title, Func<string?, Task<IReadOnlyList<RemoteFolder>>> loadChildren);
    Task ShowMessageAsync(string title, string message, bool isError = false);
    Task<bool> OpenBrowserForAuthAsync(Uri url);
    IntPtr OwnerHwnd { get; }
}

public interface IShareService
{
    IReadOnlyList<IShareDestination> Destinations { get; }
    IEnumerable<IShareDestination> For(IReadOnlyList<ShareFile> files);
    bool IsEnabled(string id);
    void SetEnabled(string id, bool enabled);
    /// <summary>Prepare + share + history + clipboard link + toast.</summary>
    Task<ShareResult> ShareAsync(string destinationId, IReadOnlyList<ShareFile> files, IShareUi ui, IProgress<double>? p, CancellationToken ct);
    /// <summary>
    /// As <see cref="ShareAsync(string, IReadOnlyList{ShareFile}, IShareUi, IProgress{double}?, CancellationToken)"/>, with
    /// per-call destination options (a capture preset's <c>OutputBinding.Options</c>): the JSON object's properties are
    /// overlaid on the destination's saved settings for this share only. Null = saved settings.
    /// </summary>
    Task<ShareResult> ShareAsync(string destinationId, IReadOnlyList<ShareFile> files, IShareUi ui, System.Text.Json.JsonElement? options, IProgress<double>? p, CancellationToken ct);
    /// <summary>Exports the document to temp files per naming preferences.</summary>
    Task<IReadOnlyList<ShareFile>> MaterializeAsync(ShotDocument doc, string format, EncodeOptions? o, CancellationToken ct);
}

/// <summary>Windows Credential Manager wrapper (keys like "Shotwise:{destinationId}:{account}").</summary>
public interface ICredentialStore
{
    void Write(string key, string secret);
    string? Read(string key);
    void Delete(string key);
}

/// <summary>
/// Credential-store key scheme shared by the Accounts UI (UI.Dialogs) and the share destinations (Sharing), so both
/// read/write the same entries (CONTRACT A15).
/// </summary>
public static class ShareCredentialKeys
{
    public const string Prefix = "Shotwise";

    /// <summary>"Shotwise:{id}:{account}" — per destination/provider secret.</summary>
    public static string Make(string destinationOrProviderId, string account) => $"{Prefix}:{destinationOrProviderId}:{account}";

    /// <summary>Optional OAuth client secret of a provider ("google", "box", "imgur", …): "Shotwise:{provider}:client-secret".</summary>
    public static string ClientSecret(string providerId) => Make(providerId, "client-secret");

    /// <summary>OAuth providers whose consoles only accept pre-registered redirect URIs, so they need a fixed loopback port.</summary>
    public static IReadOnlySet<string> FixedPortProviders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dropbox", "box", "imgur", "x" };

    /// <summary>Suggested fixed loopback port (register http://127.0.0.1:53682/ in the provider console).</summary>
    public const int DefaultLoopbackPort = 53682;
}

/// <summary>Print job description for <see cref="IPrintService"/> (SPEC SHR-03).</summary>
/// <param name="Files">Image/PDF files to print, in order.</param>
/// <param name="PrinterName">Printer queue name; null = default printer (or ask, unless <paramref name="Quick"/>).</param>
/// <param name="Page">Paper, orientation, layout, anchor and margins.</param>
/// <param name="ImagesPerPage">1 = one image per page; &gt;1 = grid layout.</param>
/// <param name="Preview">Show the print preview first.</param>
/// <param name="Quick">Print immediately with the given settings, without the print dialog.</param>
public sealed record PrintRequest(IReadOnlyList<string> Files, string? PrinterName, PdfPageOptions Page, int ImagesPerPage = 1, bool Preview = false, bool Quick = false)
{
    /// <summary>Owner window handle for dialogs (0 = none).</summary>
    public IntPtr OwnerHwnd { get; init; }
}

/// <summary>Printing with page setup / preview / printer choice; implemented in UI.Dialogs (WPF / System.Printing).</summary>
public interface IPrintService
{
    /// <summary>Returns true when the job was submitted, false when the user cancelled.</summary>
    Task<bool> PrintAsync(PrintRequest request, CancellationToken ct);
}

public sealed record OAuthClientConfig(
    string ProviderId,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    string ClientId,
    IReadOnlyList<string> Scopes,
    string? ClientSecret = null,
    IReadOnlyDictionary<string, string>? ExtraAuthorizeParameters = null,
    int? FixedLoopbackPort = null);

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string TokenType = "Bearer", string? IdToken = null, IReadOnlyList<string>? Scopes = null)
{
    public bool IsExpired(DateTimeOffset now, TimeSpan? skew = null) => now >= ExpiresAt - (skew ?? TimeSpan.FromMinutes(2));
}

/// <summary>OAuth 2.0 authorization-code + PKCE with loopback redirect http://127.0.0.1:{port}/.</summary>
public interface IOAuthHelper
{
    Task<OAuthTokens> AuthorizeAsync(OAuthClientConfig cfg, IShareUi ui, CancellationToken ct);
    Task<OAuthTokens> RefreshAsync(OAuthClientConfig cfg, OAuthTokens t, CancellationToken ct);
}

/// <summary>Multi-format clipboard content written in one operation (formats would otherwise replace each other).</summary>
public sealed record ClipboardContent
{
    public BgraImage? Image { get; init; }
    /// <summary>Composite the image on white before writing (pref "prevent clipboard transparency").</summary>
    public bool PreventTransparency { get; init; }
    public string? Text { get; init; }
    /// <summary>HTML fragment (the writer wraps it in the CF_HTML envelope).</summary>
    public string? Html { get; init; }
    public IReadOnlyList<string>? FilePaths { get; init; }
}

/// <summary>
/// Clipboard abstraction so engine code (Sharing) never references WPF. Implemented in UI.Common on the STA/UI thread;
/// methods may be called from any thread and block until the clipboard has been written.
/// </summary>
public interface IClipboardWriter
{
    /// <summary>Writes bitmap (DIB/DIBV5) + PNG formats.</summary>
    void SetImage(BgraImage image, bool preventTransparency);
    void SetText(string text);
    void SetHtml(string html, string? plainTextFallback = null);
    void SetFileDropList(IReadOnlyList<string> paths);
    /// <summary>Writes several formats at once.</summary>
    void SetContent(ClipboardContent content);
}
