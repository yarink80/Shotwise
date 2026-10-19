using System.Collections.Specialized;
using System.Net;
using Shotwise.Core.Annotations;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shotwise-sharing-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string content = "data")
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string FileBytes(string name, int size)
    {
        var p = System.IO.Path.Combine(Path, name);
        var data = new byte[size];
        new Random(42).NextBytes(data);
        System.IO.File.WriteAllBytes(p, data);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FakeShareUi : IShareUi
{
    public Func<ShareForm, bool> OnForm { get; set; } = _ => true;
    public Func<Uri, Task<bool>> OnBrowser { get; set; } = _ => Task.FromResult(true);
    public List<string> Messages { get; } = new();
    public List<Uri> OpenedUrls { get; } = new();
    public IntPtr OwnerHwnd => IntPtr.Zero;

    public Task<bool> ShowFormAsync(string title, ShareForm form) => Task.FromResult(OnForm(form));
    public Task<string?> PickFolderAsync(string title, Func<string?, Task<IReadOnlyList<RemoteFolder>>> loadChildren) => Task.FromResult<string?>(null);
    public Task ShowMessageAsync(string title, string message, bool isError = false) { Messages.Add(message); return Task.CompletedTask; }
    public Task<bool> OpenBrowserForAuthAsync(Uri url) { OpenedUrls.Add(url); return OnBrowser(url); }
}

internal sealed class FakeClipboard : IClipboardWriter
{
    public List<string> Texts { get; } = new();
    public List<ClipboardContent> Contents { get; } = new();
    public List<IReadOnlyList<string>> DropLists { get; } = new();
    public void SetImage(BgraImage image, bool preventTransparency) => Contents.Add(new ClipboardContent { Image = image, PreventTransparency = preventTransparency });
    public void SetText(string text) => Texts.Add(text);
    public void SetHtml(string html, string? plainTextFallback = null) => Texts.Add(html);
    public void SetFileDropList(IReadOnlyList<string> paths) => DropLists.Add(paths);
    public void SetContent(ClipboardContent content) => Contents.Add(content);
}

internal sealed class FakeNotifications : INotificationService
{
    public List<(string Title, string Message, NotificationKind Kind, IReadOnlyList<NotificationAction>? Actions)> Shown { get; } = new();
    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info, IReadOnlyList<NotificationAction>? actions = null) => Shown.Add((title, message, kind, actions));
}

internal sealed class FakePolicy : IPolicyService
{
    public HashSet<PolicyFeature> Denied { get; } = new();
    public bool IsAllowed(PolicyFeature f) => !Denied.Contains(f);
}

internal sealed class FakeLibrary : ILibraryService
{
    public Dictionary<string, LibraryItem> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(Guid Id, ShareHistoryEntry Entry)> History { get; } = new();

    public static LibraryItem Item(string path) => new(Guid.NewGuid(), path, MediaKind.Image, System.IO.Path.GetFileName(path), DateTimeOffset.Now, DateTimeOffset.Now, 1, 1, 1, null, null, null, null, Array.Empty<string>(), false, "");

    public string LibraryRoot => "";
    public Task<LibraryItem?> FindByPathAsync(string path, CancellationToken ct = default) => Task.FromResult(ByPath.GetValueOrDefault(path));
    public Task AddShareHistoryAsync(Guid itemId, ShareHistoryEntry e) { History.Add((itemId, e)); return Task.CompletedTask; }
    public Task<IReadOnlyList<ShareHistoryEntry>> GetShareHistoryAsync(Guid? itemId = null) => Task.FromResult<IReadOnlyList<ShareHistoryEntry>>(History.Select(h => h.Entry).ToList());

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<LibraryItem> AddCaptureAsync(ShotDocument doc, BgraImage flattenedPreview, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<LibraryItem> AddVideoAsync(VideoCaptureResult video, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<LibraryItem> ImportFileAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
    public Task UpdateAsync(LibraryItem item, ShotDocument? doc = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<LibraryItem>> QueryAsync(LibraryQuery q, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<LibraryItem?> GetAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<(string Key, int Count)>> GroupCountsAsync(LibraryGroupBy by, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AddTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags) => Task.CompletedTask;
    public Task RemoveTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> AllTagsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task SetFavoriteAsync(Guid id, bool fav) => Task.CompletedTask;
    public Task DeleteAsync(IEnumerable<Guid> ids, bool toRecycleBin = true) => Task.CompletedTask;
    public Task RenameAsync(Guid id, string newName) => Task.CompletedTask;
    public Task MoveLibraryAsync(string newRoot, IProgress<double>? p, CancellationToken ct) => Task.CompletedTask;
    public Task BackupAsync(string archivePath, IProgress<double>? p, CancellationToken ct) => Task.CompletedTask;
    public Task RestoreAsync(string archivePath, IProgress<double>? p, CancellationToken ct) => Task.CompletedTask;
    public Task SetOcrTextAsync(Guid id, string text) => Task.CompletedTask;
    public event EventHandler<LibraryChangedEventArgs>? Changed { add { } remove { } }
}

internal sealed class FakeCodec : IImageCodec
{
    public List<(string Path, ImageFormat Format)> Saved { get; } = new();
    public List<(string Path, ImageFormat Format, int Pages)> SavedMulti { get; } = new();

    public BgraImage Decode(ReadOnlySpan<byte> data) => new(4, 4);
    public BgraImage Load(string path) => new(4, 4);
    public IReadOnlyList<BgraImage> LoadAllPages(string path) => new[] { new BgraImage(4, 4) };
    public byte[] Encode(BgraImage img, ImageFormat fmt, EncodeOptions? o = null) => new byte[] { 1, 2, 3 };

    public void Save(BgraImage img, string path, ImageFormat fmt, EncodeOptions? o = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "FAKE:" + fmt);
        Saved.Add((path, fmt));
    }

    public void SaveMultipage(IReadOnlyList<BgraImage> pages, string path, ImageFormat fmt, EncodeOptions? o = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "FAKEMULTI:" + fmt);
        SavedMulti.Add((path, fmt, pages.Count));
    }

    public IReadOnlyList<ImageFormatInfo> SupportedFormats => Array.Empty<ImageFormatInfo>();
}

internal sealed class FakeRenderer : IDocumentRenderer
{
    public BgraImage RenderPage(ShotDocument doc, Page page, RenderOptions o) => new((int)page.CanvasSize.Width, (int)page.CanvasSize.Height);
    public void RenderAnnotation(object skCanvas, ShotDocument doc, Page page, Annotation a) { }
    public BgraImage RenderThumbnail(ShotDocument doc, int maxSide) => new(2, 2);
    public Rect2 MeasureBounds(Annotation a) => default;
    public TextLayoutInfo LayoutText(RichText text, TextLayoutStyle style, double maxWidth) => TextLayoutInfo.Empty;
    public Geometry2 GetOutline(Annotation a) => throw new NotSupportedException();
}

internal sealed class FakeEffects : IEffectProcessor
{
    public int Applied { get; private set; }
    public EffectResult Apply(BgraImage src, EffectSpec effect, DocumentMetadata? md) { Applied++; return new EffectResult(src, default); }
    public EffectResult ApplyAll(BgraImage src, IEnumerable<EffectSpec> effects, DocumentMetadata? md) { Applied += effects.Count(); return new EffectResult(src, default); }
    public BgraImage Preview(BgraImage src, EffectSpec e, int maxSide) => src;
}

/// <summary>Configurable destination for registry / service tests.</summary>
internal sealed class FakeDestination : IShareDestination, IPolicyGatedDestination
{
    public FakeDestination(string id, params string[] formats)
    {
        Id = id;
        DisplayName = id.ToUpperInvariant();
        SupportedFormats = new HashSet<string>(formats, StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string IconGlyph => "";
    public DestinationCategory Category { get; init; } = DestinationCategory.BuiltIn;
    public IReadOnlySet<string> SupportedFormats { get; }
    public bool SupportsMultiple { get; init; } = true;
    public bool SupportsStepCapture { get; init; }
    public bool CanShowOptions => false;
    public bool RequiresAccount => false;
    public bool IsConfigured => true;
    public IReadOnlyList<PolicyFeature> RequiredPolicies { get; init; } = Array.Empty<PolicyFeature>();

    public bool PrepareResult { get; set; } = true;
    public Func<IReadOnlyList<ShareFile>, CancellationToken, Task<ShareResult>> OnShare { get; set; } = (_, _) => Task.FromResult(ShareResult.Success());
    public int ShareCalls { get; private set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct) => Task.FromResult(PrepareResult);

    public async Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        ShareCalls++;
        Started.TrySetResult();
        return await OnShare(files, ct);
    }

    public Task ShowOptionsAsync(IShareUi ui) => Task.CompletedTask;
    public Task SignInAsync(IShareUi ui, CancellationToken ct) => Task.CompletedTask;
    public Task SignOutAsync() => Task.CompletedTask;
}

internal sealed record Recorded(string Method, string Path, string Query, NameValueCollection Headers, byte[] Body)
{
    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);
    public string Url => Path + Query;
}

internal sealed record StubResponse(int Status, string Body, string ContentType = "application/json", Dictionary<string, string>? Headers = null);

/// <summary>Local HTTP stub (127.0.0.1, random port) so tests never touch the network.</summary>
internal sealed class StubServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public StubServer(Func<Recorded, StubResponse> handler)
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        Handler = handler;
        _loop = Task.Run(Loop);
    }

    public string BaseUrl { get; }
    public Func<Recorded, StubResponse> Handler { get; set; }
    public List<Recorded> Requests { get; } = new();

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch (Exception) { return; }
            try
            {
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                var rec = new Recorded(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.Url.Query, new NameValueCollection(ctx.Request.Headers), ms.ToArray());
                lock (Requests) Requests.Add(rec);
                var resp = Handler(rec);
                var bytes = System.Text.Encoding.UTF8.GetBytes(resp.Body);
                ctx.Response.StatusCode = resp.Status;
                ctx.Response.ContentType = resp.ContentType;
                if (resp.Headers is not null) foreach (var h in resp.Headers) ctx.Response.AddHeader(h.Key, h.Value);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
        }
    }

    public List<Recorded> Snapshot() { lock (Requests) return Requests.ToList(); }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch (Exception) { }
    }
}

internal sealed record Seen(HttpMethod Method, Uri Url, System.Net.Http.Headers.HttpRequestHeaders Headers, System.Net.Http.Headers.HttpContentHeaders? ContentHeaders, byte[] Body)
{
    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);
    public string? Auth => Headers.Authorization?.ToString();
    public string? Header(string name) => Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;
}

/// <summary>In-process HttpMessageHandler: lets tests drive destinations whose endpoints are hard-coded.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<Seen, HttpResponseMessage> _respond;
    public List<Seen> Requests { get; } = new();

    public FakeHandler(Func<Seen, HttpResponseMessage> respond) => _respond = respond;

    public static HttpResponseMessage Json(string json, int status = 200) => new((HttpStatusCode)status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
        var seen = new Seen(request.Method, request.RequestUri!, request.Headers, request.Content?.Headers, body);
        lock (Requests) Requests.Add(seen);
        return _respond(seen);
    }

    public HttpClient Client() => new(this);
}

internal static class Ctx
{
    public static void SignedIn(ShareContext ctx, string destinationId, string provider, string clientId = "cid")
    {
        ctx.SetClientId(provider, clientId);
        ctx.Credentials.Write(Shotwise.Sharing.Auth.CredentialKeys.Make(destinationId, "tokens"),
            System.Text.Json.JsonSerializer.Serialize(new OAuthTokens("ACCESS", "REFRESH", DateTimeOffset.UtcNow.AddHours(1))));
    }

    public static ShareContext Make(TempDir dir, ICredentialStore? creds = null, IClipboardWriter? clipboard = null, ISettingsService? settings = null, IImageCodec? codec = null, HttpClient? http = null)
        => new(new AppPaths(dir.Path), creds ?? new InMemoryCredentialStore(), new OAuthHelper(), settings, clipboard, http, codec: codec);

    public static ShareFile PngFile(string path, string title = "T") => new(path, MediaKind.Image, "png", title, 10, 10, null, false);
}
