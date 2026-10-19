using System.Buffers.Binary;
using System.Text;
using Shotwise.Core.Media;

namespace Shotwise.Media;

internal static class Mp4Metadata
{
    private sealed record Box(string Type, byte[] Payload);
    private static readonly Encoding FourCc = Encoding.Latin1;
    private static IEnumerable<Box> Parse(byte[] bytes, int offset = 0)
    {
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) throw new InvalidDataException("Truncated MP4 box.");
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)); int header = 8;
            string type = FourCc.GetString(bytes, offset + 4, 4);
            if (size == 1) { if (bytes.Length - offset < 16) throw new InvalidDataException(); size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8)); header = 16; }
            if (size == 0) size = (ulong)(bytes.Length - offset);
            if (size < (ulong)header || size > (ulong)(bytes.Length - offset)) throw new InvalidDataException("Invalid MP4 box size.");
            yield return new(type, bytes.AsSpan(offset + header, checked((int)size - header)).ToArray()); offset += (int)size;
        }
    }
    private static byte[] BoxBytes(string type, params byte[][] payloads)
    {
        int size = checked(8 + payloads.Sum(p => p.Length)); var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)size); FourCc.GetBytes(type).CopyTo(bytes, 4);
        int offset = 8; foreach (var p in payloads) { p.CopyTo(bytes, offset); offset += p.Length; } return bytes;
    }
    private static byte[] Text(string type, string? value)
    {
        byte[] flags = new byte[8]; flags[3] = 1;
        return BoxBytes(type, BoxBytes("data", flags, Encoding.UTF8.GetBytes(value ?? "")));
    }
    private static byte[] Rewrite(byte[] moov, VideoMetadata md)
    {
        var children = Parse(moov).ToList();
        var udta = children.Where(b => b.Type == "udta").SelectMany(b => Parse(b.Payload)).ToList();
        children.RemoveAll(b => b.Type == "udta");
        var existing = new List<Box>();
        foreach (var meta in udta.Where(b => b.Type == "meta"))
        {
            if (meta.Payload.Length < 4) throw new InvalidDataException("Truncated metadata.");
            var parts = Parse(meta.Payload, 4).ToList();
            // Preserve non-iTunes metadata handlers intact.
            if (!parts.Any(b => b.Type == "hdlr" && b.Payload.Length >= 12 && FourCc.GetString(b.Payload, 8, 4) == "mdir")) continue;
            existing.AddRange(parts.Where(b => b.Type == "ilst").SelectMany(b => Parse(b.Payload)));
        }
        udta.RemoveAll(b => b.Type == "meta" && b.Payload.Length >= 4 && Parse(b.Payload, 4).Any(p => p.Type == "hdlr" && p.Payload.Length >= 12 && FourCc.GetString(p.Payload, 8, 4) == "mdir"));
        var owned = new HashSet<string> { "©nam", "©cmt", "keyw", "©day", "©too" };
        var items = existing.Where(b => !owned.Contains(b.Type)).Select(b => BoxBytes(b.Type, b.Payload)).ToList();
        items.Add(Text("©nam", md.Title)); items.Add(Text("©cmt", md.Comments)); items.Add(Text("keyw", string.Join(';', md.Tags)));
        items.Add(Text("©day", md.CaptureDate?.ToString("O"))); items.Add(Text("©too", md.SourceApp));
        byte[] handler = new byte[25]; FourCc.GetBytes("mdir").CopyTo(handler, 8); FourCc.GetBytes("appl").CopyTo(handler, 12);
        udta.Add(new("meta", new byte[4].Concat(BoxBytes("hdlr", handler)).Concat(BoxBytes("ilst", items.ToArray())).ToArray()));
        children.Add(new("udta", udta.SelectMany(b => BoxBytes(b.Type, b.Payload)).ToArray()));
        return BoxBytes("moov", children.Select(b => BoxBytes(b.Type, b.Payload)).ToArray());
    }
    public static void Write(string path, VideoMetadata metadata, CancellationToken ct)
    {
        string temp = OutputFile.Temporary(path, ".mp4");
        try
        {
            using (var source = File.OpenRead(path))
            using (var destination = File.Create(temp))
            {
                byte[]? replacement = null; var header = new byte[16]; var buffer = new byte[1024 * 1024];
                while (source.Position < source.Length)
                {
                    ct.ThrowIfCancellationRequested(); long start = source.Position; source.ReadExactly(header.AsSpan(0, 8));
                    ulong size = BinaryPrimitives.ReadUInt32BigEndian(header); string type = FourCc.GetString(header, 4, 4); int headerSize = 8;
                    if (size == 1) { source.ReadExactly(header.AsSpan(8, 8)); size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); headerSize = 16; }
                    if (size == 0) { size = (ulong)(source.Length - start); if (size > uint.MaxValue) throw new InvalidDataException("Unbounded MP4 box exceeds 4 GiB."); BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size); }
                    if (size < (ulong)headerSize || size > (ulong)(source.Length - start)) throw new InvalidDataException("Invalid MP4 box.");
                    if (type == "moof") throw new InvalidDataException("Fragmented MP4 metadata rewriting is unsupported.");
                    long remaining = (long)size - headerSize;
                    if (type == "moov")
                    {
                        if (replacement is not null || remaining > 64 * 1024 * 1024) throw new InvalidDataException("Invalid or oversized movie box.");
                        var payload = new byte[(int)remaining]; source.ReadExactly(payload); replacement = Rewrite(payload, metadata);
                        FourCc.GetBytes("free").CopyTo(header, 4); destination.Write(header, 0, headerSize); destination.Write(payload);
                    }
                    else
                    {
                        destination.Write(header, 0, headerSize);
                        while (remaining > 0) { ct.ThrowIfCancellationRequested(); int n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); if (n == 0) throw new EndOfStreamException(); destination.Write(buffer, 0, n); remaining -= n; }
                    }
                }
                if (replacement is null) throw new InvalidDataException("MP4 has no movie box.");
                destination.Write(replacement); destination.Flush(true);
            }
            ct.ThrowIfCancellationRequested(); OutputFile.Replace(temp, path);
        }
        finally { OutputFile.Delete(temp); }
    }
}
