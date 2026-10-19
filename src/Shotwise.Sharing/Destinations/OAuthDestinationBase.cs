using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Infrastructure;

namespace Shotwise.Sharing.Destinations;

/// <summary>Base for REST destinations that sign in through <see cref="IOAuthHelper"/> (user-supplied client id, tokens in the credential store).</summary>
public abstract class OAuthDestinationBase : ShareDestinationBase
{
    private OAuthSession? _session;

    protected OAuthDestinationBase(ShareContext ctx, HttpClient? http = null) : base(ctx) => Http = http ?? ctx.Http;

    protected HttpClient Http { get; }

    /// <summary>Key in Preferences → Share → Accounts (shared between destinations of one provider, e.g. "google").</summary>
    protected abstract string ProviderId { get; }
    protected abstract OAuthClientConfig? BuildConfig(string clientId, string? clientSecret);

    protected OAuthSession Session => _session ??= new OAuthSession(Ctx, Id, ResolveConfig);

    /// <summary>Provider configuration with the user's client id/secret and (if set) the registered loopback port applied.</summary>
    internal OAuthClientConfig? ResolveConfig()
    {
        var id = Ctx.GetClientId(ProviderId);
        var cfg = id is null ? null : BuildConfig(id, Ctx.GetClientSecret(ProviderId));
        return cfg is null ? null : cfg with { FixedLoopbackPort = Ctx.GetLoopbackPort(ProviderId) ?? cfg.FixedLoopbackPort };
    }

    public override bool RequiresAccount => true;
    public override bool IsConfigured => Ctx.GetClientId(ProviderId) is not null && Session.HasTokens;

    public override Task SignInAsync(IShareUi ui, CancellationToken ct) => Session.SignInAsync(ui, ct);
    public override Task SignOutAsync() { Session.SignOut(); return Task.CompletedTask; }

    /// <summary>Signs in when no session exists yet (used by PrepareAsync).</summary>
    protected async Task EnsureSignedInAsync(IShareUi ui, CancellationToken ct)
    {
        if (Ctx.GetClientId(ProviderId) is null)
        {
            await ui.ShowMessageAsync(DisplayName, $"Enter your {ProviderId} application client id in Preferences → Share → Accounts first.", true).ConfigureAwait(false);
            throw new ShotwiseException(ErrorCode.ShareCancelled, "Not configured.");
        }
        if (!Session.HasTokens) await Session.SignInAsync(ui, ct).ConfigureAwait(false);
    }

    protected Task<string> TokenAsync(CancellationToken ct) => Session.GetAccessTokenAsync(ct);

    protected async Task<System.Text.Json.JsonElement> JsonAsync(Func<string, HttpRequestMessage> build, CancellationToken ct)
    {
        var token = await TokenAsync(ct).ConfigureAwait(false);
        return await HttpHelpers.ReadJsonAsync(await HttpHelpers.SendAsync(Http, () => build(token), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
}
