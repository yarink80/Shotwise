using Shotwise.Core.Platform;
using Xunit;

namespace Shotwise.Core.Tests;

public class CommandLineTests
{
    [Fact]
    public void Parses_FilesSwitchesAndUris()
    {
        var o = CommandLineOptions.Parse(new[] { @"C:\pics\a.png", "--capture=builtin.video", "--editor", "--background", "shotwise://import?x=1", "--bogus" });
        Assert.Equal(new[] { @"C:\pics\a.png" }, o.Files);
        Assert.True(o.Capture);
        Assert.Equal("builtin.video", o.CapturePreset);
        Assert.True(o.OpenEditor);
        Assert.True(o.Background);
        Assert.Single(o.Uris);
        Assert.Equal(new[] { "--bogus" }, o.Unknown);
        Assert.True(o.HasWork);
    }

    [Fact]
    public void Capture_WithoutPreset_AndSlashSyntax()
    {
        var o = CommandLineOptions.Parse(new[] { "/capture" });
        Assert.True(o.Capture);
        Assert.Null(o.CapturePreset);
        Assert.False(CommandLineOptions.Parse(Array.Empty<string>()).HasWork);
    }

    [Fact]
    public void Exit_IsRecognised_AndIsNotAFile()
    {
        var o = CommandLineOptions.Parse(new[] { "--exit" });
        Assert.True(o.Exit);
        Assert.Empty(o.Files);
        Assert.Empty(o.Unknown);
        Assert.True(CommandLineOptions.Parse(new[] { "/quit" }).Exit);
        Assert.False(CommandLineOptions.Parse(new[] { "--editor" }).Exit);
    }
}
