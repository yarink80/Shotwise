using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Xunit;
namespace Shotwise.Platform.Tests;

public sealed class MobileSecurityRegressionTests
{
    private sealed class Settings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public void Update(Action<AppSettings> mutate) => mutate(Current);
        public Task SaveAsync() => Task.CompletedTask;
        public Task ResetAsync() => Task.CompletedTask;
    }
    private sealed class Policy : IPolicyService { public bool Allowed = true; public bool IsAllowed(PolicyFeature f) => Allowed; }
    private static PairingTokenStore Tokens(MobileConnectServer server) => (PairingTokenStore)typeof(MobileConnectServer).GetField("_tokens", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
    private static JsonElement Info(MobileConnectServer server, string? auth = null)
    {
        var context = new DefaultHttpContext(); if (auth is not null) context.Request.Headers.Authorization = auth;
        return JsonSerializer.SerializeToElement(((IValueHttpResult)server.Info(context)).Value);
    }
    [Fact] public async Task InfoDisclosesOnlyVersionWithoutValidSessionOrPairingGrant()
    {
        var settings = new Settings(); settings.Current.Mobile.AllowSharing = true; settings.Current.Mobile.ConnectionName = "Private desktop";
        await using var server = new MobileConnectServer(settings, new Policy(), null!, NullLogger<MobileConnectServer>.Instance);
        var anonymous = Info(server); Assert.Single(anonymous.EnumerateObject()); Assert.True(anonymous.TryGetProperty("protocolVersion", out _));
        Assert.Single(Info(server, "Bearer invalid").EnumerateObject());
        var grant = Tokens(server).Create(TimeSpan.FromMinutes(1));
        Assert.Equal("Private desktop", Info(server, "Pairing " + grant.Token).GetProperty("name").GetString());
        var session = Tokens(server).Exchange(grant.Token, "phone"); Assert.NotNull(session);
        Assert.Single(Info(server, "Pairing " + grant.Token).EnumerateObject());
        Assert.True(Info(server, "Bearer " + session).TryGetProperty("fingerprint", out _));
    }
    [Fact] public async Task PolicyOrPreferenceDisablePermanentlyRevokesSessionsAndPendingPairings()
    {
        var settings = new Settings(); settings.Current.Mobile.AllowSharing = true; var policy = new Policy();
        await using var server = new MobileConnectServer(settings, policy, null!, NullLogger<MobileConnectServer>.Instance);
        foreach (var preference in new[] { false, true })
        {
            var grant = Tokens(server).Create(TimeSpan.FromMinutes(1)); var session = Tokens(server).Exchange(grant.Token, "phone");
            var pending = Tokens(server).Create(TimeSpan.FromMinutes(1));
            if (preference) settings.Current.Mobile.AllowSharing = false; else policy.Allowed = false;
            Assert.False(server.CheckMobileAllowed());
            settings.Current.Mobile.AllowSharing = true; policy.Allowed = true;
            Assert.True(server.CheckMobileAllowed()); Assert.False(Tokens(server).Authorize(session, out _)); Assert.False(Tokens(server).CanPair(pending.Token));
        }
    }
    [Fact] public async Task StartCannotEnableSharingWhenPreferenceIsDisabled()
    {
        await using var server = new MobileConnectServer(new Settings(), new Policy(), null!, NullLogger<MobileConnectServer>.Instance);
        var error = await Assert.ThrowsAsync<ShotwiseException>(() => server.StartAsync(default));
        Assert.Equal(ErrorCode.PolicyDisabled, error.Code); Assert.False(server.IsRunning);
    }
    [Fact] public async Task PairingQrContainsActualCertificateFingerprintInFragment()
    {
        var settings = new Settings(); settings.Current.Mobile.AllowSharing = true;
        await using var server = new MobileConnectServer(settings, new Policy(), null!, NullLogger<MobileConnectServer>.Instance);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=FX3 fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        void Set(string field, object value) => typeof(MobileConnectServer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, value);
        Set("_certificate", cert); Set("_running", true); Set("_boundAddress", IPAddress.Parse("192.168.1.2"));
        var pairing = server.CreatePairing(TimeSpan.FromMinutes(1));
        Assert.Empty(pairing.Url.Query); Assert.Contains("t=" + pairing.Token, pairing.Url.Fragment);
        Assert.Contains("fp=" + cert.GetCertHashString(HashAlgorithmName.SHA256), pairing.Url.Fragment);
        Assert.NotEmpty(pairing.QrCodePng!);
        Assert.Equal(server.CertificateFingerprint, Info(server, "Pairing " + pairing.Token).GetProperty("fingerprint").GetString());
    }
}
