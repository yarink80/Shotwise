using System.Security.Cryptography;
using System.Text;
using System.Web;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Tests;

public class PkceTests
{
    [Fact]
    public void ChallengeMatchesRfc7636Example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void VerifierIsUrlSafeAndWithinRfcLengthAndUnique()
    {
        var (v1, c1) = Pkce.Generate();
        var (v2, _) = Pkce.Generate();
        Assert.InRange(v1.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", v1);
        Assert.NotEqual(v1, v2);
        Assert.Equal(Pkce.ChallengeFor(v1), c1);
        Assert.Throws<ArgumentOutOfRangeException>(() => Pkce.Generate(8));
    }

    [Fact]
    public void StatesAreRandom()
    {
        var states = Enumerable.Range(0, 50).Select(_ => Pkce.NewState()).ToHashSet();
        Assert.Equal(50, states.Count);
        Assert.All(states, s => Assert.True(s.Length >= 16));
    }

    [Fact]
    public void FixedTimeEqualsComparesStrings()
    {
        Assert.True(OAuthHelper.FixedTimeEquals("abc", "abc"));
        Assert.False(OAuthHelper.FixedTimeEquals("abc", "abd"));
        Assert.False(OAuthHelper.FixedTimeEquals("abc", "abcd"));
    }
}

public class OAuthHelperTests
{
    private static OAuthClientConfig Config(string tokenBase) => new("test", new Uri("https://auth.example.test/authorize?tenant=x"), new Uri(tokenBase + "/token"),
        "client-123", new[] { "read", "write" }, ClientSecret: null, ExtraAuthorizeParameters: new Dictionary<string, string> { ["access_type"] = "offline" });

    [Fact]
    public void AuthorizationUrlContainsPkceStateAndScopes()
    {
        var url = OAuthHelper.BuildAuthorizationUrl(Config("http://x"), "http://127.0.0.1:5555/", "st4te", "chal");
        var q = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("x", q["tenant"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("client-123", q["client_id"]);
        Assert.Equal("http://127.0.0.1:5555/", q["redirect_uri"]);
        Assert.Equal("st4te", q["state"]);
        Assert.Equal("chal", q["code_challenge"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("read write", q["scope"]);
        Assert.Equal("offline", q["access_type"]);
    }

    [Fact]
    public void ParsesTokenResponse()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t = OAuthHelper.ParseTokenResponse("""{"access_token":"AT","refresh_token":"RT","expires_in":"120","token_type":"bearer","scope":"a b"}""", now);
        Assert.Equal("AT", t.AccessToken);
        Assert.Equal("RT", t.RefreshToken);
        Assert.Equal(now.AddSeconds(120), t.ExpiresAt);
        Assert.Equal(new[] { "a", "b" }, t.Scopes);
        Assert.True(t.IsExpired(now.AddSeconds(100))); // within the 2 minute skew
        Assert.Throws<ShotwiseException>(() => OAuthHelper.ParseTokenResponse("""{"error":"invalid_grant"}""", now));
        Assert.Throws<ShotwiseException>(() => OAuthHelper.ParseTokenResponse("not json", now));
    }

    [Fact]
    public async Task FullFlowOverLoopbackSendsVerifierAndReturnsTokens()
    {
        using var stub = new StubServer(r => new StubResponse(200, """{"access_token":"AT","refresh_token":"RT","expires_in":3600}"""));
        var ui = new FakeShareUi();
        ui.OnBrowser = url =>
        {
            var q = HttpUtility.ParseQueryString(url.Query);
            var redirect = q["redirect_uri"]!;
            Assert.StartsWith("http://127.0.0.1:", redirect);
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetAsync(redirect + "?code=the-code&state=" + Uri.EscapeDataString(q["state"]!));
            });
            return Task.FromResult(true);
        };

        var tokens = await new OAuthHelper().AuthorizeAsync(Config(stub.BaseUrl), ui, CancellationToken.None);

        Assert.Equal("AT", tokens.AccessToken);
        Assert.Equal("RT", tokens.RefreshToken);
        var tokenReq = Assert.Single(stub.Snapshot());
        var form = HttpUtility.ParseQueryString(tokenReq.BodyText);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("client-123", form["client_id"]);
        var authQuery = HttpUtility.ParseQueryString(ui.OpenedUrls.Single().Query);
        Assert.Equal(authQuery["redirect_uri"], form["redirect_uri"]);
        // The verifier sent to the token endpoint must hash to the challenge sent to the browser.
        Assert.Equal(authQuery["code_challenge"], Pkce.ChallengeFor(form["code_verifier"]!));
    }

    [Fact]
    public async Task StateMismatchIsIgnoredUntilValidCallbackArrives()
    {
        using var stub = new StubServer(_ => new StubResponse(200, """{"access_token":"AT"}"""));
        var ui = new FakeShareUi
        {
            OnBrowser = url =>
            {
                var redirect = HttpUtility.ParseQueryString(url.Query)["redirect_uri"]!;
                _ = Task.Run(async () => { using var http = new HttpClient();
                    using var rejected = await http.GetAsync(redirect + "?code=evil&state=WRONG");
                    Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
                    var state = HttpUtility.ParseQueryString(url.Query)["state"]!;
                    await http.GetAsync(redirect + "?code=good&state=" + Uri.EscapeDataString(state)); });
                return Task.FromResult(true);
            },
        };
        var tokens = await new OAuthHelper { CallbackTimeout = TimeSpan.FromSeconds(10) }.AuthorizeAsync(Config(stub.BaseUrl), ui, CancellationToken.None);
        Assert.Equal("AT", tokens.AccessToken);
        Assert.Contains("code=good", Assert.Single(stub.Snapshot()).BodyText);
    }

    [Fact]
    public async Task AccessDeniedMapsToCancelledAndMissingClientIdIsConfigError()
    {
        var ui = new FakeShareUi
        {
            OnBrowser = url =>
            {
                var q = HttpUtility.ParseQueryString(url.Query);
                _ = Task.Run(async () => { using var http = new HttpClient(); await http.GetAsync(q["redirect_uri"] + "?error=access_denied&state=" + Uri.EscapeDataString(q["state"]!)); });
                return Task.FromResult(true);
            },
        };
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => new OAuthHelper().AuthorizeAsync(Config("http://127.0.0.1:1"), ui, CancellationToken.None));
        Assert.Equal(ErrorCode.ShareCancelled, ex.Code);

        var cfg = Config("http://x") with { ClientId = "" };
        var ex2 = await Assert.ThrowsAsync<ShotwiseException>(() => new OAuthHelper().AuthorizeAsync(cfg, ui, CancellationToken.None));
        Assert.Equal(ErrorCode.DestinationNotConfigured, ex2.Code);
    }

    [Fact]
    public async Task BrowserDeclinedMeansCancelled()
    {
        var ui = new FakeShareUi { OnBrowser = _ => Task.FromResult(false) };
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => new OAuthHelper().AuthorizeAsync(Config("http://x"), ui, CancellationToken.None));
        Assert.Equal(ErrorCode.ShareCancelled, ex.Code);
    }

    [Fact]
    public async Task RefreshKeepsOldRefreshTokenWhenProviderOmitsIt()
    {
        using var stub = new StubServer(_ => new StubResponse(200, """{"access_token":"AT2","expires_in":60}"""));
        var old = new OAuthTokens("AT1", "RT1", DateTimeOffset.UtcNow.AddMinutes(-1));
        var fresh = await new OAuthHelper().RefreshAsync(Config(stub.BaseUrl), old, CancellationToken.None);
        Assert.Equal("AT2", fresh.AccessToken);
        Assert.Equal("RT1", fresh.RefreshToken);
        var form = HttpUtility.ParseQueryString(stub.Snapshot().Single().BodyText);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("RT1", form["refresh_token"]);
        await Assert.ThrowsAsync<ShotwiseException>(() => new OAuthHelper().RefreshAsync(Config(stub.BaseUrl), old with { RefreshToken = null }, CancellationToken.None));
    }

    [Fact]
    public async Task TokenEndpointErrorsBecomeAuthenticationFailures()
    {
        using var stub = new StubServer(_ => new StubResponse(400, """{"error":"invalid_grant"}"""));
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() =>
            new OAuthHelper().RefreshAsync(Config(stub.BaseUrl), new OAuthTokens("a", "r", DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Equal(ErrorCode.AuthenticationFailed, ex.Code);
    }
}

public class OAuthSessionTests
{
    private sealed class CountingOAuth : IOAuthHelper
    {
        public int Authorizations, Refreshes;
        public Task<OAuthTokens> AuthorizeAsync(OAuthClientConfig cfg, IShareUi ui, CancellationToken ct)
        {
            Authorizations++;
            return Task.FromResult(new OAuthTokens("first", "refresh", DateTimeOffset.UtcNow.AddSeconds(-10)));
        }

        public Task<OAuthTokens> RefreshAsync(OAuthClientConfig cfg, OAuthTokens t, CancellationToken ct)
        {
            Refreshes++;
            return Task.FromResult(new OAuthTokens("renewed", t.RefreshToken, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    [Fact]
    public async Task ExpiredTokensAreRefreshedAndPersistedOnlyInCredentialStore()
    {
        using var dir = new TempDir();
        var creds = new InMemoryCredentialStore();
        var oauth = new CountingOAuth();
        var ctx = new ShareContext(new Shotwise.Core.Platform.AppPaths(dir.Path), creds, oauth);
        ctx.SetClientId("prov", "cid");
        var session = new OAuthSession(ctx, "dest", () => new OAuthClientConfig("dest", new Uri("https://a/auth"), new Uri("https://a/token"), "cid", Array.Empty<string>()));

        await Assert.ThrowsAsync<ShotwiseException>(() => session.GetAccessTokenAsync(CancellationToken.None)); // not signed in
        await session.SignInAsync(new FakeShareUi(), CancellationToken.None);
        Assert.True(session.HasTokens);
        Assert.Contains("Shotwise:dest:tokens", creds.Keys);

        Assert.Equal("renewed", await session.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("renewed", await session.GetAccessTokenAsync(CancellationToken.None)); // second call uses the cached fresh token
        Assert.Equal(1, oauth.Refreshes);

        session.SignOut();
        Assert.False(session.HasTokens);
        // No file under the share settings folder may contain a token.
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "Roaming", "share")) && Directory.EnumerateFiles(Path.Combine(dir.Path, "Roaming", "share")).Any());
    }

    [Fact]
    public async Task MissingClientIdIsAConfigurationError()
    {
        using var dir = new TempDir();
        var ctx = new ShareContext(new Shotwise.Core.Platform.AppPaths(dir.Path), new InMemoryCredentialStore(), new CountingOAuth());
        var session = new OAuthSession(ctx, "dest", () => null);
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => session.SignInAsync(new FakeShareUi(), CancellationToken.None));
        Assert.Equal(ErrorCode.DestinationNotConfigured, ex.Code);
    }
}

public class CredentialStoreTests
{
    [Fact]
    public void KeyFormatIsShotwiseDestinationAccount()
        => Assert.Equal("Shotwise:ftp:me@host", CredentialKeys.Make("ftp", "me@host"));

    [Fact]
    public void InMemoryStoreRoundTrips()
    {
        var s = new InMemoryCredentialStore();
        s.Write("k", "v");
        Assert.Equal("v", s.Read("k"));
        s.Write("k", "v2");
        Assert.Equal("v2", s.Read("k"));
        s.Delete("k");
        Assert.Null(s.Read("k"));
        s.Delete("k"); // idempotent
    }

    /// <summary>Touches the real Windows Credential Manager; opt in with SHOTWISE_TEST_CREDMAN=1.</summary>
    [Fact]
    public void WindowsCredentialManagerRoundTripIncludingChunkedSecrets()
    {
        if (Environment.GetEnvironmentVariable("SHOTWISE_TEST_CREDMAN") != "1") return;
        var store = new WindowsCredentialStore();
        var key = CredentialKeys.Make("unit-test", Guid.NewGuid().ToString("N"));
        try
        {
            store.Write(key, "short secret");
            Assert.Equal("short secret", store.Read(key));
            var big = new string('x', 3500) + "end";
            store.Write(key, big);
            Assert.Equal(big, store.Read(key));
            store.Write(key, "again");
            Assert.Equal("again", store.Read(key));
        }
        finally { store.Delete(key); }
        Assert.Null(store.Read(key));
    }
}
