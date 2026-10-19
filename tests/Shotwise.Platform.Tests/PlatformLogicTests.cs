using System.Security.Cryptography;
using Shotwise.Core.Platform;
using Xunit;
namespace Shotwise.Platform.Tests;
public sealed class PlatformLogicTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact] public void PairingExpiresAtDeadline()
    {
        var clock = new Clock(); var store = new PairingTokenStore(clock); var grant = store.Create(TimeSpan.FromMinutes(1));
        clock.Now = grant.Expires; Assert.Null(store.Exchange(grant.Token, "phone"));
    }
    [Fact] public void PairingIsOneTimeAndSessionsExpire()
    {
        var clock = new Clock(); var store = new PairingTokenStore(clock); var grant = store.Create(TimeSpan.FromMinutes(1));
        var token = store.Exchange(grant.Token, "phone"); Assert.NotNull(token); Assert.Null(store.Exchange(grant.Token, "second"));
        Assert.True(store.Authorize(token, out var device)); Assert.Equal("phone", device);
        clock.Now = clock.Now.AddHours(12); Assert.False(store.Authorize(token, out _));
    }
    [Fact] public async Task PairingHasExactlyOneConcurrentWinner()
    {
        var store = new PairingTokenStore(); var grant = store.Create(TimeSpan.FromMinutes(1));
        var winners = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => store.Exchange(grant.Token, null))));
        Assert.Single(winners, w => w is not null);
    }
    [Fact] public void ClearingRevokesAllTokens()
    {
        var store = new PairingTokenStore(); var grant = store.Create(TimeSpan.FromMinutes(1)); var session = store.Exchange(grant.Token, null); store.Clear(); Assert.False(store.Authorize(session, out _));
    }
    [Theory]
    [InlineData("photo.JPG", "image/jpeg")][InlineData("photo.jpeg", "image/jpeg")][InlineData("a.png", "image/png")][InlineData("a.heic", "image/heic")][InlineData("a.webp", "image/webp")][InlineData("a.gif", "image/gif")][InlineData("a.mp4", "video/mp4")][InlineData("a.MOV", "video/quicktime")]
    public void AllowedFiles(string filename, string mediaType) => Assert.Equal(mediaType, MobileFileRules.MediaType(filename));
    [Theory][InlineData("photo.jpg.exe")][InlineData("photo.svg")][InlineData("a.html")][InlineData("noextension")][InlineData("photo.jpg:evil")]
    public void RejectsOtherFiles(string filename) => Assert.Null(MobileFileRules.MediaType(filename));
    [Theory][InlineData("Ctrl+Shift+F9", VirtualKey.F9, ModifierKeys.Control | ModifierKeys.Shift)][InlineData("PrintScreen", VirtualKey.PrintScreen, ModifierKeys.None)][InlineData("Control + Alt + PrtSc", VirtualKey.PrintScreen, ModifierKeys.Control | ModifierKeys.Alt)][InlineData("Ctrl++", VirtualKey.OemPlus, ModifierKeys.Control)]
    public void HotkeyMappingUsesNativeValues(string text, VirtualKey key, ModifierKeys modifiers)
    {
        var parsed = Hotkey.Parse(text); Assert.Equal(key, parsed.Key); Assert.Equal(modifiers, parsed.Modifiers); Assert.Equal(parsed, Hotkey.Parse(parsed.ToDisplayString()));
    }
    [Fact] public void SemVerPrecedenceMatchesSpecification()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.1.0", "2.0.0"];
        for (int i = 1; i < ordered.Length; ++i) Assert.True(SemanticVersion.Parse(ordered[i - 1]).CompareTo(SemanticVersion.Parse(ordered[i])) < 0);
        Assert.Equal(0, SemanticVersion.Parse("1.0.0+one").CompareTo(SemanticVersion.Parse("1.0.0+two")));
    }
    [Theory][InlineData("01.2.3")][InlineData("1.2")][InlineData("1.2.3-01")][InlineData("1.2.3-")][InlineData("1.2.3+a..b")]
    public void InvalidSemVerRejected(string text) => Assert.Throws<FormatException>(() => SemanticVersion.Parse(text));
}
