using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
namespace Shotwise.Platform;
public sealed class AutostartService(ILogger<AutostartService> log) : IAutostartService
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Command => $"\"{Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.")}\" --background";
    public bool IsEnabled { get { using var key = Registry.CurrentUser.OpenSubKey(Run); return string.Equals(key?.GetValue("Shotwise") as string, Command, StringComparison.OrdinalIgnoreCase); } }
    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Run);
            if (enabled) key.SetValue("Shotwise", Command); else key.DeleteValue("Shotwise", false);
            log.LogInformation("Autostart enabled: {Enabled}", enabled);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { LogRedaction.Wrap(log).LogWarning(e, "Autostart registration failed"); throw new ShotwiseException(ErrorCode.AccessDenied, "Autostart registration failed.", e); }
    }
}
public sealed class PolicyService(ISettingsService settings, ILogger<PolicyService> log) : IPolicyService
{
    public bool IsAllowed(PolicyFeature f)
    {
        var s = settings.Current;
        var permitted = f switch
        {
            PolicyFeature.MobileSharing => s.Mobile.AllowSharing,
            PolicyFeature.Telemetry => s.Privacy.UsageData != UsageDataMode.Off,
            PolicyFeature.Experiments => s.Experiments.Enabled && s.Experiments.AgreedToTerms,
            _ => true
        };
        var name = f switch { PolicyFeature.MobileSharing => "AllowMobile", _ => "Allow" + f };
        // Local adapter for feature flags preserved by Core's JSON extension data.
        if (s.Extra?.TryGetValue("featureSwitches", out var switches) == true && switches.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var jsonName = char.ToLowerInvariant(name[0]) + name[1..];
            if (switches.TryGetProperty(jsonName, out var flag)) permitted &= flag.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Policies\Shotwise");
            return permitted && (key?.GetValue(name) is not int value || value != 0);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { LogRedaction.Wrap(log).LogWarning(e, "Policy registry inaccessible; denying {Feature}", f); return false; }
    }
}
public sealed class ShellRegistration(ILogger<ShellRegistration> log) : IShellRegistration
{
    public const string HandlerClsid = "{73603584-28A9-4D1B-A08D-6C5B50710837}";
    private const string Classes = @"Software\Classes\";
    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
    private static string Dll => Path.Combine(AppContext.BaseDirectory, "Shotwise.ShellExt.dll");
    private static void Set(string path, string value, string name = "") { using var key = Registry.CurrentUser.CreateSubKey(Classes + path); key.SetValue(name, value); }
    private void Register(Action operation)
    {
        try { operation(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { LogRedaction.Wrap(log).LogWarning(e, "Shell registration failed"); throw new ShotwiseException(ErrorCode.ShellRegistrationFailed, "Shell registration failed.", e); }
    }
    public void RegisterFileAssociations() => Register(() =>
    {
        Set(".shotx", "Shotwise.Document"); Set("Shotwise.Document", "Shotwise Document");
        Set(@"Shotwise.Document\DefaultIcon", $"\"{Exe}\",0");
        Set(@"Shotwise.Document\shell\open\command", $"\"{Exe}\" \"%1\"");
        Set(@"SystemFileAssociations\image\shell\Shotwise", "Edit in Shotwise");
        Set(@"SystemFileAssociations\image\shell\Shotwise\command", $"\"{Exe}\" \"%1\"");
        if (File.Exists(Dll))
        {
            Set($@"CLSID\{HandlerClsid}\InprocServer32", Dll);
            Set($@"CLSID\{HandlerClsid}\InprocServer32", "Apartment", "ThreadingModel");
            Set(@".shotx\shellex\{E357FCCD-A995-4576-B01F-234630154E96}", HandlerClsid);
            Set(@".shotx\shellex\{9B174B34-40FF-11D2-A27E-00C04FC30871}", HandlerClsid);
            using var propertyHandler = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\.shotx");
            propertyHandler.SetValue("", HandlerClsid);
        }
        Notify(); log.LogInformation("Registered per-user document associations");
    });
    public void RegisterUriScheme() => Register(() =>
    {
        Set("shotwise", "URL:Shotwise Protocol"); Set("shotwise", "", "URL Protocol");
        Set(@"shotwise\shell\open\command", $"\"{Exe}\" --uri \"%1\""); Notify();
    });
    public bool IsShellExtensionRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Classes + @".shotx\shellex\{E357FCCD-A995-4576-B01F-234630154E96}");
            using var server = Registry.CurrentUser.OpenSubKey(Classes + $@"CLSID\{HandlerClsid}\InprocServer32");
            return File.Exists(Dll) && (key?.GetValue("") as string) == HandlerClsid && string.Equals(server?.GetValue("") as string, Dll, StringComparison.OrdinalIgnoreCase);
        }
    }
    /// <summary>
    /// Uninstall: removes the per-user keys written by <see cref="RegisterFileAssociations"/> / <see cref="RegisterUriScheme"/> (and by the
    /// native DllRegisterServer, which writes the same keys). Keys are only deleted when they still belong to Shotwise. Never touches HKLM.
    /// </summary>
    public void UnregisterAll() => Register(() =>
    {
        var classes = Registry.CurrentUser.OpenSubKey(Classes, writable: true);
        if (classes is null) return;
        using (classes)
        {
            var extProgId = ReadDefault(classes, ".shotx", out var extExists);
            if (extExists && (extProgId is null || extProgId == "Shotwise.Document")) classes.DeleteSubKeyTree(".shotx", false);
            classes.DeleteSubKeyTree("Shotwise.Document", false);
            classes.DeleteSubKeyTree(@"SystemFileAssociations\image\shell\Shotwise", false);
            classes.DeleteSubKeyTree($@"CLSID\{HandlerClsid}", false);
            if (ReadDefault(classes, @"shotwise\shell\open\command", out _) is { } cmd && cmd.Contains("Shotwise.exe", StringComparison.OrdinalIgnoreCase))
                classes.DeleteSubKeyTree("shotwise", false);
        }
        const string handlerKey = @"Software\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\.shotx";
        if (string.Equals(ReadDefault(Registry.CurrentUser, handlerKey, out _), HandlerClsid, StringComparison.OrdinalIgnoreCase))
            Registry.CurrentUser.DeleteSubKeyTree(handlerKey, false);
        Notify(); log.LogInformation("Removed per-user document associations, URI scheme and shell extension registration");
    });

    private static string? ReadDefault(RegistryKey parent, string path, out bool exists)
    {
        using var key = parent.OpenSubKey(path);
        exists = key is not null;
        return key?.GetValue("") as string;
    }

    private static void Notify() => SHChangeNotify(0x08000000, 0, 0, 0);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint id, uint flags, nint item1, nint item2);
}
