using System.Runtime.InteropServices;
using System.Text;

namespace Shotwise.Library.Catalog;

/// <summary>Moves files to the Windows Recycle Bin with SHFileOperation (FOF_ALLOWUNDO), silently and in one undo step.</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Sends existing files to the recycle bin. Returns the Win32/shell error code (0 = success).</summary>
    public static int Delete(IReadOnlyCollection<string> paths)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).ToList();
        if (existing.Count == 0) return 0;
        // pFrom is a double-NUL terminated list of NUL separated paths.
        var sb = new StringBuilder();
        foreach (var p in existing) sb.Append(p).Append('\0');
        sb.Append('\0');
        var buf = Marshal.StringToHGlobalUni(sb.ToString());
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = buf,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
            };
            var rc = SHFileOperation(ref op);
            if (rc == 0 && op.fAnyOperationsAborted != 0) return 1223; // ERROR_CANCELLED
            return rc;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
