using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Platform;
using Shotwise.Platform;
using Xunit;

/// <summary>The real RegisterHotKey probe: two hotkey services in one process behave like two apps (each has its own window).</summary>
public sealed class HotkeyProbeTests
{
    // an unusual combination nobody is likely to hold on a test machine
    private static readonly Hotkey Rare = Hotkey.Parse("Ctrl+Alt+Shift+F19");

    [Fact]
    public void Probe_IsNonDestructive_AndSeesKeysHeldElsewhere()
    {
        using var us = new HotkeyService(NullLogger<HotkeyService>.Instance);
        using var other = new HotkeyService(NullLogger<HotkeyService>.Instance);
        if (us.Probe(Rare) != HotkeyProbeResult.Free) return;   // held on this machine: nothing to assert

        // probing does not keep the key: the other "app" can still register it afterwards
        Assert.Equal(HotkeyProbeResult.Free, us.Probe(Rare));
        Assert.Empty(us.Registered);
        Assert.Equal(HotkeyRegistrationResult.Ok, other.Register("x", Rare, () => { }, false));

        Assert.Equal(HotkeyProbeResult.HeldByOtherApp, us.Probe(Rare));
        Assert.Equal(HotkeyProbeResult.HeldByShotwise, other.Probe(Rare));
        Assert.Equal(HotkeyProbeResult.Invalid, us.Probe(Hotkey.Parse("Ctrl+F12")));

        var advisor = new HotkeyAdvisor(us, null, () => null);
        Assert.Equal(HotkeyAvailabilityStatus.InUseByOtherApp, advisor.CheckAvailability(Rare).Status);
        other.Unregister("x");
        advisor.Invalidate();
        Assert.Equal(HotkeyAvailabilityStatus.Available, advisor.CheckAvailability(Rare).Status);
        Assert.Equal(HotkeyRegistrationResult.Ok, us.Register("mine", Rare, () => { }, false));
        Assert.Equal(HotkeyAvailabilityStatus.Available, advisor.CheckAvailability(Rare, "mine").Status);
        Assert.Equal(HotkeyAvailabilityStatus.UsedByShotwise, advisor.CheckAvailability(Rare, "someone-else").Status);
    }
}
