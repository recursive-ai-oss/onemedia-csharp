using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>
/// Builds small inputs for the timed-event smoke apps and fuzz seeds: a transport stream with an ID3 metadata PID and a SCTE 35 PID, an
/// ID3 tag, an <c>emsg</c> box payload and a <c>tmcd</c> sample entry. The checked-in fixtures come from tools/gen-events-testdata.py.
/// </summary>
public static class EventBuilder
{
    /// <summary>The 50-byte splice_insert (event 0x12, out of network, 60 s break) that a published satellite capture carries.</summary>
    public static readonly byte[] SpliceInsert = Convert.FromHexString("fc302f000000000000fffff01405000000127feffe75c14b4a7e0066ff30000112ff000a00084355454900000012eaa8473f");

    /// <summary>An ID3v2.4 tag with one TXXX frame.</summary>
    public static byte[] Id3Tag(string description, string value)
    {
        byte[] text = [3, .. Encoding.UTF8.GetBytes(description), 0, .. Encoding.UTF8.GetBytes(value)];
        byte[] frame = [.. "TXXX"u8.ToArray(), .. Synchsafe(text.Length), 0, 0, .. text];
        return [.. "ID3"u8.ToArray(), 4, 0, 0, .. Synchsafe(frame.Length), .. frame];
    }

    private static byte[] Synchsafe(int n) => [(byte)((n >> 21) & 0x7F), (byte)((n >> 14) & 0x7F), (byte)((n >> 7) & 0x7F), (byte)(n & 0x7F)];

    /// <summary>The payload of a version 0 <c>emsg</c> box (after its header): scheme, value, timescale, delta, duration, id and the message.</summary>
    public static byte[] EmsgV0(string scheme, string value, uint timescale, uint delta, uint duration, uint id, byte[] message)
    {
        byte[] fixedFields = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(fixedFields, timescale);
        BinaryPrimitives.WriteUInt32BigEndian(fixedFields.AsSpan(4), delta);
        BinaryPrimitives.WriteUInt32BigEndian(fixedFields.AsSpan(8), duration);
        BinaryPrimitives.WriteUInt32BigEndian(fixedFields.AsSpan(12), id);
        return [0, 0, 0, 0, .. Encoding.UTF8.GetBytes(scheme), 0, .. Encoding.UTF8.GetBytes(value), 0, .. fixedFields, .. message];
    }

    /// <summary>A <c>tmcd</c> sample entry body (after the data reference index) for the given counting rate.</summary>
    public static byte[] TimecodeEntry(bool dropFrame, int framesPerSecond, uint timescale, uint frameDuration, string? name = null)
    {
        byte[] b = new byte[18];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), dropFrame ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), frameDuration);
        b[16] = (byte)framesPerSecond;
        if (name is null)
        {
            return b;
        }

        byte[] text = Encoding.UTF8.GetBytes(name);
        byte[] atom = new byte[12 + text.Length];
        BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)atom.Length);
        "name"u8.CopyTo(atom.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(atom.AsSpan(8), (ushort)text.Length);
        text.CopyTo(atom.AsSpan(12));
        return [.. b, .. atom];
    }

    /// <summary>
    /// A transport stream with one program holding an ID3 metadata PID (stream type 0x15, PID 0x101, one PES at <paramref name="id3Pts"/>) and a
    /// SCTE 35 PID (stream type 0x86, PID 0x102) that carries <paramref name="splice"/> after the ID3 packet.
    /// </summary>
    public static byte[] TransportStream(byte[] splice, byte[] id3, long id3Pts)
    {
        using MemoryStream ms = new();
        int[] cc = new int[4];
        byte[] pat = Section(0x00, 1, [0x00, 0x01, 0xE1, 0x00]);
        // PMT: no PCR PID, no program info; 0x15 with an 'ID3 ' registration descriptor, 0x86 with a 'CUEI' registration descriptor.
        byte[] id3Desc = [0x05, 4, .. "ID3 "u8.ToArray()];
        byte[] cueDesc = [0x05, 4, .. "CUEI"u8.ToArray()];
        byte[] pmt = Section(0x02, 1, [0xFF, 0xFF, 0xF0, 0x00, 0x15, 0xE1, 0x01, 0xF0, (byte)id3Desc.Length, .. id3Desc, 0x86, 0xE1, 0x02, 0xF0, (byte)cueDesc.Length, .. cueDesc]);
        WriteSection(ms, 0, pat, ref cc[0]);
        WriteSection(ms, 0x100, pmt, ref cc[1]);
        byte[] pts = [(byte)(0x21 | ((id3Pts >> 29) & 0x0E)), (byte)(id3Pts >> 22), (byte)(0x01 | ((id3Pts >> 14) & 0xFE)), (byte)(id3Pts >> 7), (byte)(0x01 | ((id3Pts << 1) & 0xFE))];
        byte[] header = [0x80, 0x80, 5, .. pts];
        byte[] pes = [0, 0, 1, 0xBD, (byte)((header.Length + id3.Length) >> 8), (byte)(header.Length + id3.Length), .. header, .. id3];
        for (int i = 0; i < pes.Length; i += 184)
        {
            int n = Math.Min(184, pes.Length - i);
            byte[] packet = new byte[188];
            Array.Fill(packet, (byte)0xFF);
            packet[0] = 0x47;
            packet[1] = (byte)((i == 0 ? 0x40 : 0) | 0x01);
            packet[2] = 0x01;
            if (n == 184)
            {
                packet[3] = (byte)(0x10 | (cc[2]++ & 15));
                Array.Copy(pes, i, packet, 4, n);
            }
            else
            {
                // Pad the short last packet with an adaptation field of stuffing bytes.
                int room = 183 - n;
                packet[3] = (byte)(0x30 | (cc[2]++ & 15));
                packet[4] = (byte)room;
                if (room > 0)
                {
                    packet[5] = 0;
                }

                Array.Copy(pes, i, packet, 188 - n, n);
            }

            ms.Write(packet);
        }

        WriteSection(ms, 0x102, splice, ref cc[3]);
        WriteSection(ms, 0, pat, ref cc[0]);
        WriteSection(ms, 0x100, pmt, ref cc[1]);
        return ms.ToArray();
    }

    private static byte[] Section(int tableId, int idExtension, byte[] body)
    {
        List<byte> s = [(byte)tableId, 0xB0, 0, (byte)(idExtension >> 8), (byte)idExtension, 0xC1, 0, 0, .. body];
        int length = s.Count - 3 + 4;
        s[1] = (byte)(0xB0 | (length >> 8));
        s[2] = (byte)length;
        uint crc = 0xFFFFFFFF;
        foreach (byte b in s)
        {
            crc ^= (uint)b << 24;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }

        s.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        return [.. s];
    }

    // A splice section is carried like a PSI section: after a pointer_field, in packets padded with 0xFF.
    private static void WriteSection(Stream output, int pid, byte[] section, ref int cc)
    {
        byte[] data = [0, .. section];
        for (int i = 0; i < data.Length; i += 184)
        {
            byte[] packet = new byte[188];
            Array.Fill(packet, (byte)0xFF);
            packet[0] = 0x47;
            packet[1] = (byte)((i == 0 ? 0x40 : 0) | (pid >> 8));
            packet[2] = (byte)pid;
            packet[3] = (byte)(0x10 | (cc++ & 15));
            Array.Copy(data, i, packet, 4, Math.Min(184, data.Length - i));
            output.Write(packet);
        }
    }
}
