using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>
/// Writes small ASF files for tests and fuzz seeds, byte by byte from the layout the reader documents (docs/asf.md). It writes only what the tests
/// need: it is a fixture generator, not a general ASF writer.
/// </summary>
internal sealed class AsfBuilder
{
    public static readonly Guid HeaderGuid = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid DataGuid = new("75B22636-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid FilePropertiesGuid = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    public static readonly Guid StreamPropertiesGuid = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
    public static readonly Guid HeaderExtensionGuid = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
    public static readonly Guid ContentDescriptionGuid = new("75B22633-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid ExtendedContentDescriptionGuid = new("D2D0A440-E307-11D2-97F0-00A0C95EA850");
    public static readonly Guid MetadataLibraryGuid = new("44231C94-9498-49D1-A141-1D134E457054");
    public static readonly Guid ContentEncryptionGuid = new("2211B3FB-BD23-11D2-B4B7-00A0C955FC6E");
    public static readonly Guid SimpleIndexGuid = new("33000890-E5B1-11CF-89F4-00A0C90349CB");
    public static readonly Guid AudioStreamGuid = new("F8699E40-5B4D-11CF-A8FD-00805F5C442B");
    public static readonly Guid VideoStreamGuid = new("BC19EFC0-5B4D-11CF-A8FD-00805F5C442B");
    private static readonly Guid FileId = new("11111111-2222-3333-4444-555555555555");

    public const int PacketSize = 512;

    public readonly List<byte[]> HeaderObjects = [];
    public readonly List<byte[]> Packets = [];
    public readonly List<byte[]> TrailingObjects = [];
    public long PrerollMs = 1000;
    public long PlayDuration100ns = 100_000_000;

    /// <summary>One payload to place in a packet.</summary>
    public sealed record Payload(int Stream, bool Key, uint ObjectNumber, uint Offset, uint ObjectSize, uint TimeMs, byte[] Data, bool Compressed = false, byte Delta = 0);

    public static byte[] Obj(Guid guid, byte[] body)
    {
        byte[] o = new byte[24 + body.Length];
        guid.TryWriteBytes(o);
        BinaryPrimitives.WriteUInt64LittleEndian(o.AsSpan(16), (ulong)o.Length);
        body.CopyTo(o, 24);
        return o;
    }

    public static byte[] Utf16(string s, bool nul = true) => Encoding.Unicode.GetBytes(nul ? s + "\0" : s);

    public AsfBuilder AddFileProperties(uint flags = 2, uint? minPacket = null, uint? maxPacket = null)
    {
        byte[] b = new byte[80];
        FileId.TryWriteBytes(b);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), 0x01D0_0000_0000_0000); // creation date
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), (ulong)Packets.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), (ulong)PlayDuration100ns);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(48), (ulong)PlayDuration100ns);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(56), (ulong)PrerollMs);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(64), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(68), minPacket ?? PacketSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(72), maxPacket ?? PacketSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(76), 128_000);
        HeaderObjects.Add(Obj(FilePropertiesGuid, b));
        return this;
    }

    private static byte[] StreamProps(Guid type, int number, byte[] typeSpecific, bool encrypted = false)
    {
        byte[] b = new byte[54 + typeSpecific.Length];
        type.TryWriteBytes(b);
        new Guid("20FB5700-5B55-11CF-A8FD-00805F5C442B").TryWriteBytes(b.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)typeSpecific.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(48), (ushort)(number | (encrypted ? 0x8000 : 0)));
        typeSpecific.CopyTo(b, 54);
        return Obj(StreamPropertiesGuid, b);
    }

    public AsfBuilder AddAudioStream(int number, int formatTag = 0x0161, int channels = 2, int rate = 44100, byte[]? extra = null)
    {
        extra ??= [1, 2, 3, 4];
        byte[] d = new byte[18 + extra.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(12), 1486);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(14), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(16), (ushort)extra.Length);
        extra.CopyTo(d, 18);
        HeaderObjects.Add(StreamProps(AudioStreamGuid, number, d));
        return this;
    }

    public AsfBuilder AddVideoStream(int number, string fourcc = "WMV3", int width = 320, int height = 240, bool encrypted = false)
    {
        byte[] d = new byte[11 + 40];
        BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), (uint)height);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(9), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(11), 40);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(15), width);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(19), height);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(23), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(25), 24);
        Encoding.ASCII.GetBytes(fourcc).CopyTo(d, 27);
        HeaderObjects.Add(StreamProps(VideoStreamGuid, number, d, encrypted));
        return this;
    }

    public AsfBuilder AddContentDescription(string title, string author, string copyright = "", string description = "", string rating = "")
    {
        byte[][] s = [Utf16(title), Utf16(author), Utf16(copyright), Utf16(description), Utf16(rating)];
        s = [.. s.Select((x, i) => (i is 2 or 3 or 4) && x.Length == 2 ? [] : x)];
        byte[] b = new byte[10 + s.Sum(x => x.Length)];
        for (int i = 0; i < 5; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)s[i].Length);
        }

        int at = 10;
        foreach (byte[] x in s)
        {
            x.CopyTo(b, at);
            at += x.Length;
        }

        HeaderObjects.Add(Obj(ContentDescriptionGuid, b));
        return this;
    }

    /// <summary>An attribute for the Extended Content Description: type 0 string, 1 bytes, 2 bool, 3 dword, 4 qword, 5 word.</summary>
    public sealed record Attr(string Name, int Type, byte[] Value);

    public static Attr Text(string name, string value) => new(name, 0, Utf16(value));

    public static Attr Dword(string name, uint value) => new(name, 3, BitConverter.GetBytes(value));

    public static byte[] PictureValue(int pictureType, string mime, byte[] image)
    {
        List<byte> v = [(byte)pictureType];
        v.AddRange(BitConverter.GetBytes((uint)image.Length));
        v.AddRange(Utf16(mime));
        v.AddRange(Utf16("cover"));
        v.AddRange(image);
        return [.. v];
    }

    public AsfBuilder AddExtendedContentDescription(params Attr[] attributes)
    {
        List<byte> b = [.. BitConverter.GetBytes((ushort)attributes.Length)];
        foreach (Attr a in attributes)
        {
            byte[] name = Utf16(a.Name);
            b.AddRange(BitConverter.GetBytes((ushort)name.Length));
            b.AddRange(name);
            b.AddRange(BitConverter.GetBytes((ushort)a.Type));
            b.AddRange(BitConverter.GetBytes((ushort)a.Value.Length));
            b.AddRange(a.Value);
        }

        HeaderObjects.Add(Obj(ExtendedContentDescriptionGuid, [.. b]));
        return this;
    }

    /// <summary>A Header Extension holding a Metadata Library object with the given attributes (file level, stream 0).</summary>
    public AsfBuilder AddMetadataLibrary(params Attr[] attributes)
    {
        List<byte> b = [.. BitConverter.GetBytes((ushort)attributes.Length)];
        foreach (Attr a in attributes)
        {
            byte[] name = Utf16(a.Name);
            b.AddRange(BitConverter.GetBytes((ushort)0));
            b.AddRange(BitConverter.GetBytes((ushort)0));
            b.AddRange(BitConverter.GetBytes((ushort)name.Length));
            b.AddRange(BitConverter.GetBytes((ushort)a.Type));
            b.AddRange(BitConverter.GetBytes((uint)a.Value.Length));
            b.AddRange(name);
            b.AddRange(a.Value);
        }

        byte[] library = Obj(MetadataLibraryGuid, [.. b]);
        byte[] ext = new byte[22 + library.Length];
        new Guid("ABD3D211-A9BA-11CF-8EE6-00C00C205365").TryWriteBytes(ext);
        BinaryPrimitives.WriteUInt16LittleEndian(ext.AsSpan(16), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(ext.AsSpan(18), (uint)library.Length);
        library.CopyTo(ext, 22);
        HeaderObjects.Add(Obj(HeaderExtensionGuid, ext));
        return this;
    }

    public AsfBuilder AddEncryptionObject()
    {
        HeaderObjects.Add(Obj(ContentEncryptionGuid, new byte[24]));
        return this;
    }

    /// <summary>Adds a data packet (multiple-payload form, 16-bit payload lengths, 16-bit padding length, no error correction unless asked).</summary>
    public AsfBuilder AddPacket(bool errorCorrection, params Payload[] payloads)
    {
        List<byte> p = [];
        if (errorCorrection)
        {
            p.AddRange([0x82, 0x00, 0x00]);
        }

        p.Add(0x11); // length flags: multiple payloads, padding length is a WORD, no packet length, no sequence
        p.Add(0x5D); // property flags: replicated data length BYTE, offset DWORD, object number BYTE, stream number BYTE
        int paddingAt = p.Count;
        p.AddRange([0, 0]);          // padding length, patched below
        p.AddRange(new byte[4]);     // send time
        p.AddRange(new byte[2]);     // duration
        p.Add((byte)(0x80 | payloads.Length));
        foreach (Payload pl in payloads)
        {
            p.Add((byte)(pl.Stream | (pl.Key ? 0x80 : 0)));
            p.Add((byte)pl.ObjectNumber);
            if (pl.Compressed)
            {
                p.AddRange(BitConverter.GetBytes(pl.TimeMs)); // the offset field carries the presentation time
                p.Add(1);
                p.Add(pl.Delta);
            }
            else
            {
                p.AddRange(BitConverter.GetBytes(pl.Offset));
                p.Add(8);
                p.AddRange(BitConverter.GetBytes(pl.ObjectSize));
                p.AddRange(BitConverter.GetBytes(pl.TimeMs));
            }

            p.AddRange(BitConverter.GetBytes((ushort)pl.Data.Length));
            p.AddRange(pl.Data);
        }

        int padding = PacketSize - p.Count;
        if (padding < 0)
        {
            throw new InvalidOperationException("payloads do not fit one packet");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(CollectionSpan(p, paddingAt), (ushort)padding);
        p.AddRange(new byte[padding]);
        Packets.Add([.. p]);
        return this;
    }

    private static Span<byte> CollectionSpan(List<byte> list, int at) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[at..];

    /// <summary>A Simple Index covering the video stream: entry i gives the packet to start from for time i * interval.</summary>
    public AsfBuilder AddSimpleIndex(long interval100ns, params int[] packetNumbers)
    {
        byte[] b = new byte[32 + packetNumbers.Length * 6];
        FileId.TryWriteBytes(b);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), (ulong)interval100ns);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), (uint)packetNumbers.Length);
        for (int i = 0; i < packetNumbers.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32 + i * 6), (uint)packetNumbers[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(36 + i * 6), 1);
        }

        TrailingObjects.Add(Obj(SimpleIndexGuid, b));
        return this;
    }

    public byte[] Build()
    {
        int headerChildren = HeaderObjects.Sum(o => o.Length);
        using MemoryStream ms = new();
        byte[] head = new byte[30];
        HeaderGuid.TryWriteBytes(head);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(16), (ulong)(30 + headerChildren));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(24), (uint)HeaderObjects.Count);
        head[28] = 1;
        head[29] = 2;
        ms.Write(head);
        foreach (byte[] o in HeaderObjects)
        {
            ms.Write(o);
        }

        byte[] data = new byte[50];
        DataGuid.TryWriteBytes(data);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), (ulong)(50 + Packets.Count * PacketSize));
        FileId.TryWriteBytes(data.AsSpan(24));
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(40), (ulong)Packets.Count);
        data[48] = 1;
        data[49] = 1;
        ms.Write(data);
        foreach (byte[] p in Packets)
        {
            ms.Write(p);
        }

        foreach (byte[] t in TrailingObjects)
        {
            ms.Write(t);
        }

        return ms.ToArray();
    }
}
