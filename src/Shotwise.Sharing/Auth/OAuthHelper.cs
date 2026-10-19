using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Auth;

/// <summary>RFC 7636 PKCE (S256) and state generation.</summary>
public static class Pkce
{
    public static (string Verifier, string Challenge) Generate(int verifierBytes = 48)
    {
        if (verifierBytes is < 32 or > 96) throw new ArgumentOutOfRangeException(nameof(verifierBytes)); // 43..128 chars after base64url
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(verifierBytes));
        return (verifier, ChallengeFor(verifier));
    }

    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// OAuth 2.0 authorization-code flow with PKCE (S256) and a loopback redirect on http://127.0.0.1:{random port}/.
/// The system browser is opened through <see cref="IShareUi.OpenBrowserForAuthAsync"/>. No client ids are shipped.
/// </summary>
public sealed class OAuthHelper : IOAuthHelper
{
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    public OAuthHelper(HttpClient? http = null, ILogger<OAuthHelper>? log = null, TimeProvider? time = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _log = (ILogger?)log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long to wait for the user to complete sign-in in the browser.</summary>
    public TimeSpan CallbackTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public async Task<OAuthTokens> AuthorizeAsync(OAuthClientConfig cfg, IShareUi ui, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(ui);
        if (string.IsNullOrWhiteSpace(cfg.ClientId))
            throw new ShotwiseException(ErrorCode.DestinationNotConfigured, $"No OAuth client id configured for {cfg.ProviderId}.");

        var port = cfg.FixedLoopbackPort ?? FreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            throw new ShotwiseException(ErrorCode.AuthenticationFailed, "Could not start the local sign-in listener: " + ex.Message, ex);
        }

        var (verifier, challenge) = Pkce.Generate();
        var state = Pkce.NewState();
        var url = BuildAuthorizationUrl(cfg, redirectUri, state, challenge);

        if (!await ui.OpenBrowserForAuthAsync(url).ConfigureAwait(false))
            throw new ShotwiseException(ErrorCode.ShareCancelled, "Sign-in was cancelled.");

        var code = await WaitForCallbackAsync(listener, state, ct).ConfigureAwait(false);
        return await ExchangeCodeAsync(cfg, code, redirectUri, verifier, ct).ConfigureAwait(false);
    }

    public async Task<OAuthTokens> RefreshAsync(OAuthClientConfig cfg, OAuthTokens t, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(t.RefreshToken))
            throw new ShotwiseException(ErrorCode.AuthorizationRequired, "No refresh token; sign in again.");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = t.RefreshToken,
            ["client_id"] = cfg.ClientId,
        };
        if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;
        var fresh = await PostTokenAsync(cfg.TokenEndpoint, form, ct).ConfigureAwait(false);
        // Providers may omit a new refresh token: keep the old one.
        return fresh with { RefreshToken = fresh.RefreshToken ?? t.RefreshToken, Scopes = fresh.Scopes ?? t.Scopes };
    }

    internal static Uri BuildAuthorizationUrl(OAuthClientConfig cfg, string redirectUri, string state, string challenge)
    {
        var q = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", cfg.ClientId),
            new("redirect_uri", redirectUri),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        };
        if (cfg.Scopes.Count > 0) q.Add(new("scope", string.Join(' ', cfg.Scopes)));
        if (cfg.ExtraAuthorizeParameters is not null)
            foreach (var kv in cfg.ExtraAuthorizeParameters) q.Add(new(kv.Key, kv.Value));
        var ub = new UriBuilder(cfg.AuthorizationEndpoint);
        var extra = string.Join('&', q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        ub.Query = string.IsNullOrEmpty(ub.Query) ? extra : ub.Query.TrimStart('?') + "&" + extra;
        return ub.Uri;
    }

    private async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallbackTimeout);
        using var reg = timeout.Token.Register(() => { try { listener.Abort(); } catch (ObjectDisposedException) { } });

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                throw new ShotwiseException(ErrorCode.AuthenticationFailed, "Sign-in timed out.", ex);
            }

            var query = ctx.Request.QueryString;
            var hasResult = query["code"] is not null || query["error"] is not null;
            if (!hasResult)
            {
                await RespondAsync(ctx, 404, "Not found").ConfigureAwait(false); // favicon etc.
                continue;
            }

            var gotState = query["state"] ?? "";
            if (!FixedTimeEquals(gotState, expectedState))
            {
                await RespondAsync(ctx, 400, "Sign-in failed: state mismatch. You can close this window.").ConfigureAwait(false);
                continue;
            }
            if (query["error"] is { } err)
            {
                await RespondAsync(ctx, 400, "Sign-in was not completed. You can close this window.").ConfigureAwait(false);
                throw new ShotwiseException(err == "access_denied" ? ErrorCode.ShareCancelled : ErrorCode.AuthenticationFailed,
                    "Authorization was not completed.");
            }
            await RespondAsync(ctx, 200, "Signed in. You can close this window and return to Shotwise.").ConfigureAwait(false);
            return query["code"]!;
        }
    }

    internal static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task RespondAsync(HttpListenerContext ctx, int status, string message)
    {
        try
        {
            var html = "<!doctype html><meta charset=utf-8><title>Shotwise</title><body style=\"font-family:Segoe UI,sans-serif;margin:3em\"><h3>"
                       + WebUtility.HtmlEncode(message) + "</h3></body>";
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException) { }
    }

    private Task<OAuthTokens> ExchangeCodeAsync(OAuthClientConfig cfg, string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = cfg.ClientId,
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;
        return PostTokenAsync(cfg.TokenEndpoint, form, ct);
    }

    private async Task<OAuthTokens> PostTokenAsync(Uri endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await HttpHelpers.ReadBoundedAsync(resp.Content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Token endpoint {Endpoint} returned {Status}", endpoint.Host, (int)resp.StatusCode);
            throw new ShotwiseException(ErrorCode.AuthenticationFailed, $"Token request failed ({(int)resp.StatusCode}).");
        }
        return ParseTokenResponse(body, _time.GetUtcNow());
    }

    internal static OAuthTokens ParseTokenResponse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                throw new ShotwiseException(ErrorCode.AuthenticationFailed, "The token endpoint rejected authentication.");
            var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) throw new ShotwiseException(ErrorCode.AuthenticationFailed, "Token response contained no access_token.");
            var expiresIn = 3600.0;
            if (r.TryGetProperty("expires_in", out var ex))
                expiresIn = ex.ValueKind == JsonValueKind.Number ? ex.GetDouble() : double.TryParse(ex.GetString(), out var d) ? d : 3600;
            string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var scopes = Str("scope")?.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return new OAuthTokens(access, Str("refresh_token"), now.AddSeconds(expiresIn), Str("token_type") ?? "Bearer", Str("id_token"), scopes);
        }
        catch (JsonException ex)
        {
            throw new ShotwiseException(ErrorCode.AuthenticationFailed, "Token endpoint returned invalid JSON.", ex);
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}

/// <summary>
/// Token lifecycle for one OAuth destination: tokens live only in <see cref="ICredentialStore"/> (key "Shotwise:{dest}:tokens"),
/// refreshed transparently when (nearly) expired.
/// </summary>
public sealed class OAuthSession
{
    private readonly ShareContext _ctx;
    private readonly string _destinationId;
    private readonly Func<OAuthClientConfig?> _config;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OAuthSession(ShareContext ctx, string destinationId, Func<OAuthClientConfig?> config)
    {
        _ctx = ctx;
        _destinationId = destinationId;
        _config = config;
    }

    private string Key => CredentialKeys.Make(_destinationId, "tokens");

    public bool HasTokens => _ctx.Credentials.Read(Key) is not null;

    public OAuthClientConfig Config => _config() ?? throw new ShotwiseException(ErrorCode.DestinationNotConfigured,
        $"{_destinationId}: client id not configured (Preferences → Share → Accounts).");

    public async Task SignInAsync(IShareUi ui, CancellationToken ct)
    {
        var tokens = await _ctx.OAuth.AuthorizeAsync(Config, ui, ct).ConfigureAwait(false);
        Save(tokens);
    }

    public void SignOut() => _ctx.Credentials.Delete(Key);

    /// <summary>Returns a valid access token, refreshing when needed. Throws <see cref="ErrorCode.AuthorizationRequired"/> when sign-in is needed.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tokens = Load() ?? throw new ShotwiseException(ErrorCode.AuthorizationRequired, "Sign in required.");
            if (tokens.IsExpired(DateTimeOffset.UtcNow))
            {
                try { tokens = await _ctx.OAuth.RefreshAsync(Config, tokens, ct).ConfigureAwait(false); }
                catch (ShotwiseException) { throw new ShotwiseException(ErrorCode.AuthorizationRequired, "Session expired; sign in again."); }
                Save(tokens);
            }
            return tokens.AccessToken;
        }
        finally { _gate.Release(); }
    }

    private OAuthTokens? Load()
    {
        var json = _ctx.Credentials.Read(Key);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<OAuthTokens>(json); }
        catch (JsonException) { return null; }
    }

    private void Save(OAuthTokens t) => _ctx.Credentials.Write(Key, JsonSerializer.Serialize(t));
}
