using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Tests.AudioStreams;

/// <summary>Builds small audio elementary streams and tags in memory, byte by byte from the specifications, so the tests need no files.</summary>
internal static class AudioStreamTestData
{
    // ISO/IEC 11172-3 §2.4.1.3: MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, stereo: 144 * 128000 / 44100 = 417 bytes, 418 with the padding bit.
    public const int Mp3FrameBytes = 417;

    public const int Mp3Samples = 1152;

    public static byte[] Mp3Frame(int index, bool padded = false)
    {
        byte[] frame = new byte[padded ? Mp3FrameBytes + 1 : Mp3FrameBytes];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = (byte)(padded ? 0x92 : 0x90);
        frame[3] = 0x00;
        for (int i = 4; i < frame.Length; i++)
        {
            frame[i] = (byte)(1 + ((i + index) % 100)); // never 0xFF, so no false syncword inside a frame
        }

        return frame;
    }

    public static byte[] Mp3(int frames) => Concat(Enumerable.Range(0, frames).Select(i => Mp3Frame(i)).ToArray());

    /// <summary>An Xing/Info header frame; with <paramref name="lame"/> the LAME extension carries the encoder delay and padding.</summary>
    public static byte[] XingFrame(int frameCount, int byteCount, bool lame, int delay = 576, int padding = 1000, bool info = false)
    {
        byte[] frame = new byte[Mp3FrameBytes];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        // The tag sits after the 32-byte side information of an MPEG-1 stereo frame.
        int at = 4 + 32;
        Encoding.ASCII.GetBytes(info ? "Info" : "Xing").CopyTo(frame, at);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(at + 4), 0x0F); // frames, bytes, TOC, quality
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(at + 8), (uint)frameCount);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(at + 12), (uint)byteCount);
        if (lame)
        {
            int l = at + 120;
            Encoding.ASCII.GetBytes("LAME3.100").CopyTo(frame, l);
            int dp = l + 21; // 12 bits of delay, 12 bits of padding
            frame[dp] = (byte)(delay >> 4);
            frame[dp + 1] = (byte)(((delay & 0xF) << 4) | (padding >> 8));
            frame[dp + 2] = (byte)padding;
        }

        return frame;
    }

    /// <summary>ADTS AAC-LC, 44.1 kHz, stereo; <paramref name="length"/> includes the 7-byte header (ISO/IEC 14496-3 §1.A.2.2.1).</summary>
    public static byte[] AdtsFrame(int length)
    {
        byte[] frame = new byte[length];
        frame[0] = 0xFF;
        frame[1] = 0xF1; // MPEG-4, layer 0, protection absent
        frame[2] = 0x50; // profile LC (1), sampling_frequency_index 4 (44100), private 0, channel_configuration high bit 0
        frame[3] = (byte)(0x80 | (length >> 11)); // channel_configuration low bits (2 channels), frame length high bits
        frame[4] = (byte)(length >> 3);
        frame[5] = (byte)(((length & 7) << 5) | 0x1F);
        frame[6] = 0xFC;
        for (int i = 7; i < length; i++)
        {
            frame[i] = 0x11;
        }

        return frame;
    }

    /// <summary>AC-3 at 48 kHz, 192 kbit/s, 2/0 mode: 384 words = 768 bytes per syncframe, 1536 samples (ETSI TS 102 366 §4.3).</summary>
    public static byte[] Ac3Frame()
    {
        byte[] frame = new byte[768];
        frame[0] = 0x0B;
        frame[1] = 0x77;
        frame[4] = 0x14; // fscod 0 (48 kHz), frmsizecod 20 (192 kbit/s)
        frame[5] = 0x40; // bsid 8, bsmod 0
        frame[6] = 0x40; // acmod 2
        for (int i = 7; i < frame.Length; i++)
        {
            frame[i] = 0x22;
        }

        return frame;
    }

    /// <summary>AMR-NB 12.2 kbit/s frame in the storage format: a table-of-contents byte and 31 bytes (RFC 4867 §5.1.3).</summary>
    public static byte[] AmrNbFrame() => AmrFrame(0x3C, 31);

    public static byte[] AmrFrame(byte toc, int payload)
    {
        byte[] frame = new byte[1 + payload];
        frame[0] = toc;
        for (int i = 1; i < frame.Length; i++)
        {
            frame[i] = 0x55;
        }

        return frame;
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // --- ID3 ---

    private static byte[] SynchSafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    public static byte[] Id3Bytes(int major, byte[] frames, byte flags = 0, int padding = 0, byte[]? extendedHeader = null, bool footer = false)
    {
        byte[] body = Concat(extendedHeader ?? [], frames, new byte[padding]);
        byte[] header = Concat("ID3"u8.ToArray(), [(byte)major, 0, (byte)(flags | (footer ? 0x10 : 0))], SynchSafe(body.Length));
        byte[] tag = Concat(header, body);
        return footer ? Concat(tag, "3DI"u8.ToArray(), [(byte)major, 0, flags], SynchSafe(body.Length)) : tag;
    }

    public static byte[] TextFrame(int major, string id, string text, byte encoding = 3)
    {
        byte[] data = Concat([encoding], EncodeText(text, encoding));
        return Frame(major, id, data);
    }

    public static byte[] EncodeText(string text, byte encoding) => encoding switch
    {
        0 => Encoding.Latin1.GetBytes(text),
        1 => Concat([0xFF, 0xFE], Encoding.Unicode.GetBytes(text)),
        2 => Encoding.BigEndianUnicode.GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text),
    };

    public static byte[] Frame(int major, string id, byte[] data, ushort flags = 0)
    {
        if (major == 2)
        {
            return Concat(Encoding.ASCII.GetBytes(id), [(byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length], data);
        }

        byte[] size = major == 4 ? SynchSafe(data.Length) : [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length];
        return Concat(Encoding.ASCII.GetBytes(id), size, [(byte)(flags >> 8), (byte)flags], data);
    }

    /// <summary>ID3v2 unsynchronisation (ID3v2.4 §6.1): a 0x00 after every 0xFF, and after a 0xFF at the end.</summary>
    public static byte[] Unsynchronise(byte[] data)
    {
        List<byte> result = [];
        for (int i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xFF && (i == data.Length - 1 || data[i + 1] == 0x00 || (data[i + 1] & 0xE0) == 0xE0))
            {
                result.Add(0);
            }
        }

        return [.. result];
    }

    public static byte[] Id3v1(string title, string artist, string album, string year, string comment, int track = 0, byte genre = 17)
    {
        byte[] tag = new byte[128];
        "TAG"u8.CopyTo(tag);
        Put(tag, 3, 30, title);
        Put(tag, 33, 30, artist);
        Put(tag, 63, 30, album);
        Put(tag, 93, 4, year);
        if (track > 0)
        {
            Put(tag, 97, 28, comment);
            tag[125] = 0;
            tag[126] = (byte)track;
        }
        else
        {
            Put(tag, 97, 30, comment);
        }

        tag[127] = genre;
        return tag;
    }

    private static void Put(byte[] tag, int offset, int width, string text) => Encoding.Latin1.GetBytes(text.AsSpan(0, Math.Min(text.Length, width)), tag.AsSpan(offset));

    // --- APEv2 ---

    public static byte[] ApeItem(string key, string value, uint flags = 0) => ApeItem(key, Encoding.UTF8.GetBytes(value), flags);

    public static byte[] ApeItem(string key, byte[] value, uint flags = 0)
    {
        byte[] head = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)value.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), flags);
        return Concat(head, Encoding.ASCII.GetBytes(key), [0], value);
    }

    /// <summary>An APEv2 tag with a header and a footer (version 2000, so 2.0 on the wire is 2000).</summary>
    public static byte[] ApeBytes(params byte[][] items) => ApeTagWith(items, true);

    public static byte[] ApeTagWith(byte[][] items, bool header)
    {
        byte[] body = Concat(items);
        byte[] footer = ApeBlock(body.Length + 32, items.Length, header ? 0x80000000u : 0u);
        return header ? Concat(ApeBlock(body.Length + 32, items.Length, 0xA0000000u), body, footer) : Concat(body, footer);
    }

    private static byte[] ApeBlock(int size, int count, uint flags)
    {
        byte[] block = new byte[32];
        "APETAGEX"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 2000);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), flags);
        return block;
    }

    public static List<(PacketInfo Info, byte[] Data)> Packets(IDemuxer demuxer)
    {
        List<(PacketInfo, byte[])> list = [];
        while (demuxer.TryReadPacket(out PacketView v))
        {
            list.Add((v.Info, v.Data.ToArray()));
        }

        return list;
    }
}
