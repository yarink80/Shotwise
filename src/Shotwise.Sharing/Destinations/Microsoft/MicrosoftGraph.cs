using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations.Graph;

public sealed class MicrosoftAccountSettings
{
    /// <summary>"common", "organizations", "consumers" or a tenant id/domain.</summary>
    public string Tenant { get; set; } = "common";
    public string Username { get; set; } = "";
}

/// <summary>Token source for Microsoft Graph (abstracted so destinations are testable without MSAL).</summary>
public interface IMicrosoftTokenProvider
{
    bool IsSignedIn { get; }
    string? AccountName { get; }
    Task<string> GetTokenAsync(IReadOnlyCollection<string> scopes, IShareUi? ui, CancellationToken ct);
    Task SignInAsync(IReadOnlyCollection<string> scopes, IShareUi ui, CancellationToken ct);
    Task SignOutAsync();
}

/// <summary>
/// MSAL public client with the user's own app registration (client id from Preferences → Share → Accounts, key "microsoft").
/// Token cache: DPAPI-protected file via Microsoft.Identity.Client.Extensions.Msal; redirect http://localhost (loopback).
/// </summary>
public sealed class MsalTokenProvider : IMicrosoftTokenProvider
{
    private readonly ShareContext _ctx;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _app;
    private string? _appKey;

    public MsalTokenProvider(ShareContext ctx) => _ctx = ctx;

    private MicrosoftAccountSettings Account => _ctx.Store.Load<MicrosoftAccountSettings>("microsoft");

    public bool IsSignedIn => !string.IsNullOrEmpty(Account.Username);
    public string? AccountName => string.IsNullOrEmpty(Account.Username) ? null : Account.Username;

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        var clientId = _ctx.GetClientId("microsoft")
            ?? throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Enter your Microsoft (Entra) application client id in Preferences → Share → Accounts.");
        var tenant = string.IsNullOrWhiteSpace(Account.Tenant) ? "common" : Account.Tenant;
        var key = clientId + "|" + tenant;
        if (_app is not null && _appKey == key) return _app;
        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenant}")
            .WithRedirectUri("http://localhost")
            .WithClientName("Shotwise")
            .Build();
        var cacheDir = Path.Combine(_ctx.Paths.LocalAppData, "msal");
        Directory.CreateDirectory(cacheDir);
        var props = new StorageCreationPropertiesBuilder("msal.cache.bin", cacheDir).Build();
        var helper = await MsalCacheHelper.CreateAsync(props).ConfigureAwait(false);
        helper.RegisterCache(app.UserTokenCache);
        _app = app;
        _appKey = key;
        return app;
    }

    public async Task<string> GetTokenAsync(IReadOnlyCollection<string> scopes, IShareUi? ui, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = await GetAppAsync().ConfigureAwait(false);
            var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault(a => string.Equals(a.Username, Account.Username, StringComparison.OrdinalIgnoreCase)) ?? accounts.FirstOrDefault();
            if (account is not null)
            {
                try { return (await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false)).AccessToken; }
                catch (MsalUiRequiredException) { /* fall through to interactive */ }
            }
            if (ui is null) throw new ShotwiseException(ErrorCode.AuthorizationRequired, "Sign in to Microsoft required.");
            return await InteractiveAsync(app, scopes, ui, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SignInAsync(IReadOnlyCollection<string> scopes, IShareUi ui, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = await GetAppAsync().ConfigureAwait(false);
            await InteractiveAsync(app, scopes, ui, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> InteractiveAsync(IPublicClientApplication app, IReadOnlyCollection<string> scopes, IShareUi ui, CancellationToken ct)
    {
        try
        {
            var result = await app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(new SystemWebViewOptions { OpenBrowserAsync = async uri => { await ui.OpenBrowserForAuthAsync(uri).ConfigureAwait(false); } })
                .ExecuteAsync(ct).ConfigureAwait(false);
            var acc = Account;
            acc.Username = result.Account?.Username ?? acc.Username;
            _ctx.Store.Save("microsoft", acc);
            return result.AccessToken;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            throw new ShotwiseException(ErrorCode.ShareCancelled, "Sign-in was cancelled.", ex);
        }
        catch (MsalException ex)
        {
            throw new ShotwiseException(ErrorCode.AuthenticationFailed, "Microsoft sign-in failed: " + ex.Message, ex);
        }
    }

    public async Task SignOutAsync()
    {
        try
        {
            var app = await GetAppAsync().ConfigureAwait(false);
            foreach (var a in await app.GetAccountsAsync().ConfigureAwait(false)) await app.RemoveAsync(a).ConfigureAwait(false);
        }
        catch (ShotwiseException) { /* not configured: nothing cached */ }
        var acc = Account;
        acc.Username = "";
        _ctx.Store.Save("microsoft", acc);
    }
}

public sealed record DriveItemRef(string Id, string? WebUrl, string? DriveId, string Name, string? ETag = null)
{
    /// <summary>GUID inside the eTag ("{GUID},n"), used as the Teams attachment id.</summary>
    public string? ETagGuid
    {
        get
        {
            if (ETag is null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(ETag, @"\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }
    }
}

/// <summary>Minimal Microsoft Graph v1.0 REST client (files, sharing links, Teams) over HttpClient.</summary>
public sealed class GraphApi
{
    public const string Root = "https://graph.microsoft.com/v1.0";
    public const int SmallUploadLimit = 4 * 1024 * 1024;
    /// <summary>Resumable chunk size; must be a multiple of 320 KiB.</summary>
    public const int ChunkSize = 320 * 1024 * 32;

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly string _root;

    public GraphApi(HttpClient http, Func<CancellationToken, Task<string>> token, string? root = null)
    {
        _http = http;
        _token = token;
        _root = root ?? Root;
    }

    private string Abs(string pathOrUrl) => pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : _root + pathOrUrl;

    public async Task<JsonElement> GetAsync(string pathOrUrl, CancellationToken ct)
    {
        var t = await _token(ct).ConfigureAwait(false);
        return await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(_http, () => HttpHelpers.Bearer(HttpMethod.Get, Abs(pathOrUrl), t), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> PostAsync(string pathOrUrl, object body, CancellationToken ct)
    {
        var t = await _token(ct).ConfigureAwait(false);
        return await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(_http, () => HttpHelpers.Bearer(HttpMethod.Post, Abs(pathOrUrl), t, HttpHelpers.Json(body)), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>All pages of a collection (follows @odata.nextLink, capped).</summary>
    public async Task<List<JsonElement>> ListAsync(string path, CancellationToken ct, int maxPages = 10)
    {
        var list = new List<JsonElement>();
        string? next = path;
        for (var i = 0; i < maxPages && next is not null; i++)
        {
            var page = await GetAsync(next, ct).ConfigureAwait(false);
            list.AddRange(page.Items("value"));
            next = page.Str("@odata.nextLink");
        }
        return list;
    }

    public async Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(string driveBase, string? parentId, CancellationToken ct)
    {
        var path = parentId is null ? $"{driveBase}/root/children" : $"{driveBase}/items/{Uri.EscapeDataString(parentId)}/children";
        var items = await ListAsync(path + "?$select=id,name,folder,parentReference&$top=200", ct).ConfigureAwait(false);
        return items.Where(i => i.Obj("folder") is not null)
            .Select(i => new RemoteFolder(i.Str("id")!, i.Str("name") ?? "(unnamed)", parentId, (i.Obj("folder")!.Value.TryGetProperty("childCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1) > 0))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Uploads a file (simple PUT up to 4 MB, resumable upload session above) and returns the created item.</summary>
    public async Task<DriveItemRef> UploadAsync(string driveBase, string? folderId, string name, string localPath, IProgress<double>? progress, CancellationToken ct, string? folderPath = null)
    {
        var rootPath = string.IsNullOrWhiteSpace(folderPath) ? "" : string.Join('/', folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + "/";
        var parent = folderId is null ? $"{driveBase}/root:/{rootPath}{Uri.EscapeDataString(name)}:" : $"{driveBase}/items/{Uri.EscapeDataString(folderId)}:/{Uri.EscapeDataString(name)}:";
        var length = new FileInfo(localPath).Length;
        JsonElement item;
        if (length <= SmallUploadLimit)
        {
            var t = await _token(ct).ConfigureAwait(false);
            item = await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(_http,
                () => HttpHelpers.Bearer(HttpMethod.Put, Abs(parent + "/content?@microsoft.graph.conflictBehavior=rename"), t,
                    new ProgressFileContent(localPath, "application/octet-stream", v => progress?.Report(v))), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        else
        {
            var session = await PostAsync(parent + "/createUploadSession",
                new Dictionary<string, object> { ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "rename", ["name"] = name } }, ct).ConfigureAwait(false);
            var url = session.Str("uploadUrl") ?? throw new ShotwiseException(ErrorCode.UploadRejected, "Upload session could not be created.");
            item = await UploadChunksAsync(url, localPath, length, progress, ct).ConfigureAwait(false);
        }
        return new DriveItemRef(item.Str("id") ?? "", item.Str("webUrl"), item.Obj("parentReference")?.Str("driveId"), item.Str("name") ?? name, item.Str("eTag"));
    }

    private async Task<JsonElement> UploadChunksAsync(string uploadUrl, string path, long length, IProgress<double>? progress, CancellationToken ct)
    {
        long offset = 0;
        while (offset < length)
        {
            var size = (int)Math.Min(ChunkSize, length - offset);
            var start = offset;
            using var resp = await HttpHelpers.SendAsync(_http, () =>
            {
                // The upload URL is pre-authenticated: never send the bearer token to it.
                var r = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ProgressFileContent(path, "application/octet-stream", null, start, size) };
                r.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + size - 1, length);
                return r;
            }, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw HttpHelpers.ToException(resp.StatusCode, body);
            offset += size;
            progress?.Report(offset / (double)length);
            if (offset >= length)
            {
                try { return JsonDocument.Parse(body).RootElement.Clone(); }
                catch (JsonException ex) { throw new ShotwiseException(ErrorCode.UploadRejected, "Upload finished but the service reply was unreadable.", ex); }
            }
        }
        throw new ShotwiseException(ErrorCode.UploadRejected, "Empty file.");
    }

    /// <summary>Creates a sharing link; falls back from "anonymous" to "organization" when tenant policy forbids it.</summary>
    public async Task<string?> CreateLinkAsync(string driveBase, string itemId, string scope, CancellationToken ct)
    {
        foreach (var s in scope == "anonymous" ? new[] { "anonymous", "organization" } : new[] { scope })
        {
            try
            {
                var r = await PostAsync($"{driveBase}/items/{Uri.EscapeDataString(itemId)}/createLink", new { type = "view", scope = s }, ct).ConfigureAwait(false);
                var url = r.Obj("link")?.Str("webUrl");
                if (url is not null) return url;
            }
            catch (ShotwiseException) when (s == "anonymous") { /* policy: try organization */ }
        }
        return null;
    }
}

/// <summary>Shared behaviour for OneDrive / SharePoint / Teams.</summary>
public abstract class GraphDestinationBase : ShareDestinationBase
{
    protected GraphDestinationBase(ShareContext ctx, IMicrosoftTokenProvider? tokens, HttpClient? http) : base(ctx)
    {
        Tokens = tokens ?? new MsalTokenProvider(ctx);
        Http = http ?? ctx.Http;
    }

    protected IMicrosoftTokenProvider Tokens { get; }
    protected HttpClient Http { get; }
    protected abstract string[] Scopes { get; }

    protected GraphApi Api(IShareUi? ui = null, string? root = null) => new(Http, ct => Tokens.GetTokenAsync(Scopes, ui, ct), root);

    public override DestinationCategory Category => DestinationCategory.Microsoft;
    public override bool RequiresAccount => true;
    public override bool CanShowOptions => true;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Cloud;
    public override bool IsConfigured => Ctx.GetClientId("microsoft") is not null && Tokens.IsSignedIn;

    public override Task SignInAsync(IShareUi ui, CancellationToken ct) => Tokens.SignInAsync(Scopes, ui, ct);
    public override Task SignOutAsync() => Tokens.SignOutAsync();

    protected async Task EnsureSignedInAsync(IShareUi ui, CancellationToken ct)
    {
        if (Ctx.GetClientId("microsoft") is null)
        {
            await ui.ShowMessageAsync(DisplayName, "Enter your Microsoft application (client) id in Preferences → Share → Accounts first.", true).ConfigureAwait(false);
            throw new ShotwiseException(ErrorCode.ShareCancelled, "Not configured.");
        }
        await Tokens.GetTokenAsync(Scopes, ui, ct).ConfigureAwait(false);
    }
}
