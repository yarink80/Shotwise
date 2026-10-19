using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Shotwise.Core.Sharing;

namespace Shotwise.Sharing.Auth;

public static class CredentialKeys
{
    public const string Prefix = "Shotwise";

    /// <summary>"Shotwise:{destinationId}:{account}".</summary>
    public static string Make(string destinationId, string account) => $"{Prefix}:{destinationId}:{account}";
}

/// <summary>Volatile store for tests and for sessions where Credential Manager is unavailable.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Write(string key, string secret) { lock (_gate) _items[key] = secret; }
    public string? Read(string key) { lock (_gate) return _items.GetValueOrDefault(key); }
    public void Delete(string key) { lock (_gate) _items.Remove(key); }
    public IReadOnlyCollection<string> Keys { get { lock (_gate) return _items.Keys.ToArray(); } }
}

/// <summary>
/// Windows Credential Manager (generic credentials, CredWrite/CredRead/CredDelete). Persisted per user (DPAPI-protected by Windows).
/// Secrets longer than one credential blob are split into "key#1", "key#2"… chunks behind a marker entry.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int PersistLocalMachine = 2;
    private const int ChunkChars = 1000; // UTF-16 => 2000 bytes, below CRED_MAX_CREDENTIAL_BLOB_SIZE (2560)
    private const string ChunkMarker = "\u0001CHUNKS:";

    public void Write(string key, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(secret);
        Delete(key); // clears stale chunks
        if (secret.Length <= ChunkChars)
        {
            WriteRaw(key, secret);
            return;
        }
        var count = (secret.Length + ChunkChars - 1) / ChunkChars;
        for (var i = 0; i < count; i++)
            WriteRaw(ChunkKey(key, i + 1), secret.Substring(i * ChunkChars, Math.Min(ChunkChars, secret.Length - i * ChunkChars)));
        WriteRaw(key, ChunkMarker + count);
    }

    public string? Read(string key)
    {
        var first = ReadRaw(key);
        if (first is null || !first.StartsWith(ChunkMarker, StringComparison.Ordinal)) return first;
        if (!int.TryParse(first.AsSpan(ChunkMarker.Length), out var count) || count <= 0 || count > 1000) return null;
        var sb = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            var part = ReadRaw(ChunkKey(key, i));
            if (part is null) return null;
            sb.Append(part);
        }
        return sb.ToString();
    }

    public void Delete(string key)
    {
        var first = ReadRaw(key);
        if (first is not null && first.StartsWith(ChunkMarker, StringComparison.Ordinal) &&
            int.TryParse(first.AsSpan(ChunkMarker.Length), out var count) && count is > 0 and <= 1000)
        {
            for (var i = 1; i <= count; i++) DeleteRaw(ChunkKey(key, i));
        }
        DeleteRaw(key);
    }

    private static string ChunkKey(string key, int i) => key + "#" + i;

    private static void WriteRaw(string key, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = key,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!NativeMethods.CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CredWrite failed for " + key);
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static string? ReadRaw(string key)
    {
        if (!NativeMethods.CredRead(key, CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return string.Empty;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { NativeMethods.CredFree(ptr); }
    }

    private static void DeleteRaw(string key) => NativeMethods.CredDelete(key, CredTypeGeneric, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern void CredFree(IntPtr buffer);
    }
}
