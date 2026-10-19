using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Platform;

namespace Shotwise.Platform;

/// <summary>
/// Per-user Start menu shortcut (IShellLinkW + IPersistFile) with <c>System.AppUserModel.ID</c> set through the link's IPropertyStore
/// to <see cref="AppIdentity.AppUserModelId"/>, the same id the process uses (<see cref="ProcessIdentity.Apply"/>), so the Start menu
/// tile, taskbar grouping and toast notifications all show the Shotwise name and icon.
/// </summary>
public sealed class StartMenuShortcut : IStartMenuShortcut
{
    private readonly ILogger _log;

    public StartMenuShortcut(ILogger<StartMenuShortcut> log) : this(DefaultPath, log) { }

    /// <summary>Test constructor: a shortcut at <paramref name="shortcutPath"/>.</summary>
    public StartMenuShortcut(string shortcutPath, ILogger<StartMenuShortcut> log)
    {
        ShortcutPath = shortcutPath;
        _log = LogRedaction.Wrap(log);
    }

    /// <summary><c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Shotwise.lnk</c>.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppIdentity.DisplayName + ".lnk");

    public string ShortcutPath { get; }

    public bool IsCurrent(string exePath)
    {
        var info = Read(ShortcutPath);
        return info is not null
            && string.Equals(Path.GetFullPath(info.Target), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(info.AppUserModelId, AppIdentity.AppUserModelId, StringComparison.Ordinal)
            && string.Equals(info.IconPath, exePath, StringComparison.OrdinalIgnoreCase);
    }

    public bool Ensure(string exePath, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (!force && IsCurrent(exePath)) return false;
        Write(ShortcutPath, exePath);
        _log.LogInformation("Shortcut written: {Path} -> {Target} (AUMID {Aumid})", ShortcutPath, exePath, AppIdentity.AppUserModelId);
        return true;
    }

    public void Remove()
    {
        try
        {
            if (File.Exists(ShortcutPath)) { File.Delete(ShortcutPath); _log.LogInformation("Shortcut removed: {Path}", ShortcutPath); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.LogWarning(e, "Shortcut could not be removed"); }
    }

    /// <summary>What a .lnk file points at (null when the file is missing or unreadable).</summary>
    public sealed record LinkInfo(string Target, string Arguments, string WorkingDirectory, string IconPath, int IconIndex, string Description, string? AppUserModelId);

    public static LinkInfo? Read(string path)
    {
        if (!File.Exists(path)) return null;
        object? obj = null;
        try
        {
            obj = CreateShellLink();
            ((IPersistFile)obj).Load(path, 0 /* STGM_READ */);
            var link = (IShellLinkW)obj;
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var target = sb.ToString();
            sb.Clear(); link.GetArguments(sb, sb.Capacity); var args = sb.ToString();
            sb.Clear(); link.GetWorkingDirectory(sb, sb.Capacity); var dir = sb.ToString();
            sb.Clear(); link.GetIconLocation(sb, sb.Capacity, out var iconIndex); var icon = sb.ToString();
            sb.Clear(); link.GetDescription(sb, sb.Capacity); var desc = sb.ToString();
            string? aumid = null;
            var store = (IPropertyStore)obj;
            var key = PKEY_AppUserModel_ID;
            if (store.GetValue(ref key, out var pv) == 0)
            {
                try { if (pv.vt == 31 /* VT_LPWSTR */) aumid = Marshal.PtrToStringUni(pv.pointer); }
                finally { PropVariantClear(ref pv); }
            }
            return new LinkInfo(target, args, dir, icon, iconIndex, desc, aumid);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidCastException) { return null; }
        finally { if (obj is not null) Marshal.FinalReleaseComObject(obj); }
    }

    private static void Write(string path, string exePath)
    {
        object obj = CreateShellLink();
        try
        {
            var link = (IShellLinkW)obj;
            link.SetPath(exePath);
            link.SetArguments("");
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
            link.SetIconLocation(exePath, 0);
            link.SetDescription(AppIdentity.Description);
            var store = (IPropertyStore)obj;
            var key = PKEY_AppUserModel_ID;
            var pv = new PropVariant { vt = 31, pointer = Marshal.StringToCoTaskMemUni(AppIdentity.AppUserModelId) };
            try
            {
                Check(store.SetValue(ref key, ref pv));
                Check(store.Commit());
            }
            finally { PropVariantClear(ref pv); }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ((IPersistFile)obj).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(obj); }
        SHChangeNotify(0x00000002 /* SHCNE_CREATE */, 0x0005 /* SHCNF_PATHW */, path, IntPtr.Zero);
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    private static object CreateShellLink()
        => Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;

    private static PropertyKey PKEY_AppUserModel_ID => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId = fmtid;
        public uint PropertyId = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public IntPtr pointer;
        public IntPtr pointer2;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pvar);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
}

/// <summary>Process-wide app identity (call first thing in Main, before any window, tray icon or toast).</summary>
public static class ProcessIdentity
{
    /// <summary>Sets the explicit AppUserModelID (<see cref="AppIdentity.AppUserModelId"/>) of the current process.</summary>
    public static bool Apply()
    {
        try { return SetCurrentProcessExplicitAppUserModelID(AppIdentity.AppUserModelId) >= 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
