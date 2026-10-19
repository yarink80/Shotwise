using System.Net;
using System.Security.Cryptography;
using Xunit;
namespace Shotwise.Platform.Tests;

public sealed class SecurityRegressionTests
{
    [Theory]
    [InlineData("PostMessage")][InlineData("RegisterClassEx")][InlineData("CreateWindowEx")][InlineData("GetMessage")][InlineData("RegisterHotKey")]
    public void DiagnosticWin32ImportsPreserveLastError(string method)
    {
        var import = typeof(HotkeyService).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.True(import.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false).Cast<System.Runtime.InteropServices.DllImportAttribute>().Single().SetLastError);
    }
    [Fact] public void PipeFailureBackoffIsBoundedAndExponential()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), SingleInstance.FailureDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), SingleInstance.FailureDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), SingleInstance.FailureDelay(int.MaxValue));
    }
    [Theory]
    [InlineData("10.1.2.3", true)][InlineData("172.16.1.1", true)][InlineData("192.168.1.8", true)]
    [InlineData("172.32.0.1", false)][InlineData("127.0.0.1", false)][InlineData("8.8.8.8", false)][InlineData("::1", false)]
    public void MobileOnlySelectsPrivateIpv4(string address, bool expected) => Assert.Equal(expected, MobileConnectServer.IsPrivateLan(IPAddress.Parse(address)));

    [Fact] public void UriActivationConsumesInjectedArgumentsAndRaisesOnlyValidatedImports()
    {
        var activation = new UriActivation(); var requests = new List<UriActivation.ImportRequest>();
        activation.ImportRequested += (_, request) => requests.Add(request);
        Assert.True(activation.HandleArguments(["shotwise://import/stamps?url=https%3A%2F%2Fexample.org%2Fpack.zip", "--capture", @"\\host\share\evil.shotx"]));
        Assert.Equal("stamps", Assert.Single(requests).Kind);
        requests.Clear();
        Assert.True(activation.HandleArguments(["shotwise:x\"", "--capture"])); Assert.Empty(requests);
        Assert.True(activation.HandleArguments(["--uri", "--capture"]));
        Assert.False(activation.HandleArguments(["--capture"]));
    }
    [Theory]
    [InlineData("shotwise://import/themes?url=file%3A%2F%2Fserver%2Fx")]
    [InlineData("shotwise://import/themes?url=https%3A%2F%2Fu%3Ap%40example.org%2Fx")]
    [InlineData("shotwise://import/programs?url=https%3A%2F%2Fexample.org%2Fx")]
    [InlineData("shotwise://import/templates?url=https%3A%2F%2Flocalhost%2Fx")]
    [InlineData("shotwise://import/stamps?url=https%3A%2F%2Fexample.org%2Fx&capture=true")]
    public void UriRejectsUnsafeSources(string value) => Assert.False(UriActivation.TryParse(value, out _));

}
