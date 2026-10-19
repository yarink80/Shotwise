using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Shotwise.Core.Imaging;
using Xunit;
namespace Shotwise.Platform.Tests;
public sealed class NativeShellTests
{
    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory { void CreateInstance(nint outer, ref Guid iid, out nint result); void LockServer([MarshalAs(UnmanagedType.Bool)] bool value); }
    [ComImport, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }
    [ComImport, Guid("E357FCCD-A995-4576-B01F-234630154E96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailProvider { void GetThumbnail(uint cx, out nint bitmap, out uint alpha); }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Value; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore { void GetCount(out uint count); void GetAt(uint index, out PropertyKey key); void GetValue(ref PropertyKey key, out PropVariant value); void SetValue(ref PropertyKey key, ref PropVariant value); void Commit(); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetClassObject(ref Guid clsid, ref Guid iid, out nint factory);
    [DllImport("shlwapi.dll")] private static extern nint SHCreateMemStream(byte[] bytes, uint length);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint bitmap);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant variant);
    private static string NativePath
    {
        get
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at is not null && !File.Exists(Path.Combine(at.FullName, "docs", "SPEC.md"))) at = at.Parent;
            // Prefer the Debug build; fall back to Release (tools/build.ps1 -Native -Configuration Release).
            var debug = Path.Combine(at!.FullName, "native", "Shotwise.ShellExt", "bin", "x64", "Debug", "Shotwise.ShellExt.dll");
            var release = Path.Combine(at.FullName, "native", "Shotwise.ShellExt", "bin", "x64", "Release", "Shotwise.ShellExt.dll");
            return File.Exists(debug) || !File.Exists(release) ? debug : release;
        }
    }
    private sealed class Provider : IDisposable
    {
        private readonly nint _module; private readonly object _provider; private readonly object _stream;
        public IThumbnailProvider Thumbnail => (IThumbnailProvider)_provider;
        public IPropertyStore Properties => (IPropertyStore)_provider;
        public Provider(byte[] zip)
        {
            _module = NativeLibrary.Load(NativePath);
            var get = Marshal.GetDelegateForFunctionPointer<GetClassObject>(NativeLibrary.GetExport(_module, "DllGetClassObject"));
            var cls = Guid.Parse(ShellRegistration.HandlerClsid); var factoryId = typeof(IClassFactory).GUID;
            Marshal.ThrowExceptionForHR(get(ref cls, ref factoryId, out var pointer));
            var factory = (IClassFactory)Marshal.GetObjectForIUnknown(pointer); Marshal.Release(pointer);
            var iid = typeof(IInitializeWithStream).GUID;
            try { factory.CreateInstance(0, ref iid, out pointer); _provider = Marshal.GetObjectForIUnknown(pointer); Marshal.Release(pointer); }
            finally { Marshal.FinalReleaseComObject(factory); }
            pointer = SHCreateMemStream(zip, (uint)zip.Length); Assert.NotEqual(0, pointer);
            _stream = Marshal.GetObjectForIUnknown(pointer); Marshal.Release(pointer);
            ((IInitializeWithStream)_provider).Initialize((IStream)_stream, 0);
        }
        public void Dispose() { Marshal.FinalReleaseComObject(_provider); Marshal.FinalReleaseComObject(_stream); NativeLibrary.Free(_module); }
    }
    private static byte[] Fixture(CompressionLevel compression, bool thumbnailFirst = true, string? metadata = null, byte[]? thumbnail = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes, CompressionLevel level) { using var stream = zip.CreateEntry(name, level).Open(); stream.Write(bytes); }
            if (!thumbnailFirst) Add("manifest.json", "{}"u8.ToArray(), compression);
            var image = new BgraImage(20, 10); Array.Fill(image.Pixels, (byte)255);
            Add("thumbnail.png", thumbnail ?? PngCodec.Encode(image), CompressionLevel.NoCompression);
            Add("metadata.json", Encoding.UTF8.GetBytes(metadata ?? "{\"windowTitle\":\"Window \\u2713\",\"appName\":\"Fixture\",\"tags\":[\"one\",\"two\"],\"ocrText\":\"" + new string('x', 10000) + "\",\"ignored\":{\"number\":-1.5e3,\"array\":[true,false,null]}}"), compression);
        }
        return output.ToArray();
    }
    [Fact] public void NativeRejectsNonPngThumbnailEvenWhenWicCouldDecodeIt()
    {
        // A valid 1x1 GIF must never be routed to the GIF or an installed third-party decoder.
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        using var provider = new Provider(Fixture(CompressionLevel.NoCompression, thumbnail: gif));
        Assert.Throws<COMException>(() => provider.Thumbnail.GetThumbnail(32, out _, out _));
    }
    [Fact] public void NativeRejectsEmptyFixedBlockBombQuicklyAndCachesFailure()
    {
        const int blocks = 1000000; var compressed = new byte[(blocks * 10 + 7) / 8];
        for (int i = 0; i < blocks; ++i)
        {
            // BTYPE=01 followed by fixed EOB=0000000; last block sets BFINAL.
            int bit = i * 10; compressed[(bit + 1) / 8] |= (byte)(1 << ((bit + 1) % 8));
            if (i == blocks - 1) compressed[bit / 8] |= (byte)(1 << (bit % 8));
        }
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
        var name = Encoding.ASCII.GetBytes("metadata.json");
        writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)8); writer.Write(0u); writer.Write(0u); writer.Write(compressed.Length); writer.Write(0u); writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write(name); writer.Write(compressed);
        var directory = (uint)buffer.Position;
        writer.Write(0x02014b50u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)8); writer.Write(0u); writer.Write(0u); writer.Write(compressed.Length); writer.Write(0u); writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(0u); writer.Write(name);
        var directorySize = (uint)buffer.Position - directory;
        writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(directorySize); writer.Write(directory); writer.Write((ushort)0); writer.Flush();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 3; ++i)
        {
            using var provider = new Provider(buffer.ToArray());
            for (int j = 0; j < 20; ++j)
            {
                Assert.Throws<COMException>(() => provider.Properties.GetCount(out _));
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Block bomb took {clock.Elapsed}.");
            }
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Block bomb took {clock.Elapsed}.");
    }
    [Theory][InlineData(CompressionLevel.NoCompression)][InlineData(CompressionLevel.Fastest)][InlineData(CompressionLevel.Optimal)][InlineData(CompressionLevel.SmallestSize)]
    public void NativeProviderReadsThumbnailAndStoredOrDeflateMetadata(CompressionLevel compression)
    {
        using var provider = new Provider(Fixture(compression));
        provider.Thumbnail.GetThumbnail(16, out var bitmap, out var alpha);
        try { Assert.NotEqual(0, bitmap); Assert.Equal(2u, alpha); } finally { DeleteObject(bitmap); }
        provider.Properties.GetCount(out var count); Assert.True(count >= 4);
        var key = new PropertyKey { Format = Guid.Parse("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), Id = 2 };
        provider.Properties.GetValue(ref key, out var value);
        try { Assert.Equal(31, value.Type); Assert.Equal("Window \u2713", Marshal.PtrToStringUni(value.Value)); } finally { PropVariantClear(ref value); }
    }
    [Fact] public void NativeRejectsThumbnailOutsideFirstEntry()
    {
        using var provider = new Provider(Fixture(CompressionLevel.Optimal, false));
        Assert.Throws<COMException>(() => provider.Thumbnail.GetThumbnail(32, out _, out _));
    }
    [Theory][InlineData("{\"a\":1,\"a\":2}")][InlineData("{\"a\":garbage}")][InlineData("{\"a\":01}")][InlineData("{\"a\":\"\\uZZZZ\"}")]
    public void NativeRejectsMalformedMetadata(string json)
    {
        using var provider = new Provider(Fixture(CompressionLevel.Optimal, metadata: json));
        Assert.Throws<COMException>(() => provider.Properties.GetCount(out _));
    }
    [Fact] public void NativeRejectsTruncatedArchive()
    {
        var bytes = Fixture(CompressionLevel.Optimal); using var provider = new Provider(bytes[..^12]);
        Assert.Throws<COMException>(() => provider.Properties.GetCount(out _));
    }
    [Fact] public void NativeInflatesVariedLiteralAndDistancePatterns()
    {
        var random = new Random(8303);
        for (int i = 0; i < 20; ++i)
        {
            var text = new string(Enumerable.Range(0, 500 + i * 997).Select(_ => (char)('a' + random.Next(i % 3 == 0 ? 2 : 26))).ToArray());
            var json = System.Text.Json.JsonSerializer.Serialize(new { windowTitle = text, tags = new[] { "fixture" } });
            using var provider = new Provider(Fixture((CompressionLevel)(i % 4), metadata: json));
            var key = new PropertyKey { Format = Guid.Parse("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), Id = 2 };
            provider.Properties.GetValue(ref key, out var value);
            try { Assert.Equal(text, Marshal.PtrToStringUni(value.Value)); } finally { PropVariantClear(ref value); }
        }
    }
}
