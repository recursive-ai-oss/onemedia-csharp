using System.Text;
using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Captions.Teletext;
using RecursiveAI.OneMedia.MpegTs;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Builds teletext packets, PES packets, transport streams and program streams for the teletext tests (ETS 300 706, EN 300 472).</summary>
internal static class TeletextBuilder
{
    public const int Clock = 90_000;

    /// <summary>ETS 300 706 §8.1: bit 8 makes the byte's parity odd.</summary>
    public static byte Parity(int value)
    {
        int v = value & 0x7F;
        return (byte)((System.Numerics.BitOperations.PopCount((uint)v) & 1) == 0 ? v | 0x80 : v);
    }

    /// <summary>ETS 300 706 §8.2.</summary>
    public static byte Hamming84(int d)
    {
        int d1 = d & 1, d2 = (d >> 1) & 1, d3 = (d >> 2) & 1, d4 = (d >> 3) & 1;
        int p1 = 1 ^ d1 ^ d3 ^ d4, p2 = 1 ^ d1 ^ d2 ^ d4, p3 = 1 ^ d1 ^ d2 ^ d3;
        int p4 = 1 ^ p1 ^ d1 ^ p2 ^ d2 ^ p3 ^ d3 ^ d4;
        return (byte)(p1 | (d1 << 1) | (p2 << 2) | (d2 << 3) | (p3 << 4) | (d3 << 5) | (p4 << 6) | (d4 << 7));
    }

    /// <summary>ETS 300 706 §8.3: three bytes carrying 18 data bits.</summary>
    public static byte[] Hamming2418(int data)
    {
        int[] dataPositions = [3, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 22, 23];
        int word = 0;
        for (int i = 0; i < 18; i++)
        {
            if ((data & (1 << i)) != 0)
            {
                word |= 1 << (dataPositions[i] - 1);
            }
        }

        for (int bit = 0; bit < 5; bit++)
        {
            int mask = 0;
            for (int pos = 1; pos <= 23; pos++)
            {
                if ((pos & (1 << bit)) != 0 && (pos & (pos - 1)) != 0)
                {
                    mask |= 1 << (pos - 1);
                }
            }

            if ((System.Numerics.BitOperations.PopCount((uint)(word & mask)) & 1) == 0)
            {
                word |= 1 << ((1 << bit) - 1);
            }
        }

        if ((System.Numerics.BitOperations.PopCount((uint)word) & 1) == 0)
        {
            word |= 1 << 23;
        }

        return [(byte)word, (byte)(word >> 8), (byte)(word >> 16)];
    }

    /// <summary>The 40 data bytes of a page header packet (bytes 6 to 45).</summary>
    public static byte[] Header(int pageNumber, int subcode, bool erase, bool subtitle, int nationalBits = 0, bool serial = false, string text = "")
    {
        byte[] d = new byte[40];
        d[0] = Hamming84(pageNumber & 0xF);
        d[1] = Hamming84(pageNumber >> 4);
        d[2] = Hamming84(subcode & 0xF);
        d[3] = Hamming84(((subcode >> 4) & 7) | (erase ? 8 : 0));
        d[4] = Hamming84((subcode >> 8) & 0xF);
        d[5] = Hamming84(((subcode >> 12) & 3) | (subtitle ? 8 : 0));
        d[6] = Hamming84(0);
        d[7] = Hamming84((serial ? 1 : 0) | (nationalBits << 1));
        for (int i = 0; i < 32; i++)
        {
            d[8 + i] = Parity(i < text.Length ? text[i] : ' ');
        }

        return d;
    }

    /// <summary>The 40 data bytes of a displayable row from a template: control codes as characters below 0x20, text as given.</summary>
    public static byte[] Row(string text)
    {
        byte[] d = new byte[40];
        for (int i = 0; i < 40; i++)
        {
            d[i] = Parity(i < text.Length ? text[i] : ' ');
        }

        return d;
    }

    /// <summary>A packet X/26 with the given triplets (address, mode, data).</summary>
    public static byte[] Enhancement(int designation, params (int Address, int Mode, int Data)[] triplets)
    {
        byte[] d = new byte[40];
        d[0] = Hamming84(designation);
        for (int i = 0; i < 13; i++)
        {
            (int a, int m, int v) = i < triplets.Length ? triplets[i] : (63, 0x1F, 0); // termination marker
            byte[] t = Hamming2418(a | (m << 6) | (v << 11));
            Array.Copy(t, 0, d, 1 + (i * 3), 3);
        }

        return d;
    }

    /// <summary>A packet X/28/0 or M/29/0 carrying a character set designation (§9.4.2).</summary>
    public static byte[] Designation(int designation)
    {
        byte[] d = new byte[40];
        d[0] = Hamming84(0);
        byte[] t1 = Hamming2418(designation << 7);
        Array.Copy(t1, 0, d, 1, 3);
        for (int i = 1; i < 13; i++)
        {
            Array.Copy(Hamming2418(0), 0, d, 1 + (i * 3), 3);
        }

        return d;
    }

    /// <summary>One EN 300 472 data unit (46 bytes) holding a teletext packet.</summary>
    public static byte[] DataUnit(int magazine, int row, byte[] data, int unitId = 0x03)
    {
        byte[] unit = new byte[46];
        unit[0] = (byte)unitId;
        unit[1] = 0x2C;
        unit[2] = 0xE7;                       // reserved bits, field_parity 1, line_offset 7
        unit[3] = TeletextCodes.ReverseBits(0xE4); // framing code, bits as transmitted
        unit[4] = TeletextCodes.ReverseBits(Hamming84(magazine & 7 | ((row & 1) << 3)));
        unit[5] = TeletextCodes.ReverseBits(Hamming84(row >> 1));
        for (int i = 0; i < 40; i++)
        {
            unit[6 + i] = TeletextCodes.ReverseBits(data[i]);
        }

        return unit;
    }

    /// <summary>A PES payload (data_identifier 0x10 and units), padded with stuffing units to a multiple of four so the PES packet fills whole transport packets.</summary>
    public static byte[] PesPayload(params byte[][] units)
    {
        int count = units.Length;
        while ((count + 1) % 4 != 0)
        {
            count++;
        }

        List<byte> bytes = [0x10];
        foreach (byte[] u in units)
        {
            bytes.AddRange(u);
        }

        for (int i = units.Length; i < count; i++)
        {
            bytes.Add(0xFF);
            bytes.Add(0x2C);
            bytes.AddRange(Enumerable.Repeat((byte)0xFF, 0x2C));
        }

        return [.. bytes];
    }

    /// <summary>A private-stream-1 PES packet with a PTS and the fixed 0x24-byte header data length of EN 300 472 §4.2.</summary>
    public static byte[] Pes(byte[] payload, long pts)
    {
        List<byte> b = [0, 0, 1, 0xBD];
        int length = 3 + 0x24 + payload.Length;
        b.Add((byte)(length >> 8));
        b.Add((byte)length);
        b.Add(0x84);
        b.Add(0x80);
        b.Add(0x24);
        b.Add((byte)(0x21 | (((pts >> 30) & 7) << 1)));
        b.Add((byte)(pts >> 22));
        b.Add((byte)(((pts >> 15) << 1) | 1));
        b.Add((byte)(pts >> 7));
        b.Add((byte)((pts << 1) | 1));
        b.AddRange(Enumerable.Repeat((byte)0xFF, 0x24 - 5));
        b.AddRange(payload);
        return [.. b];
    }

    /// <summary>A transport stream with one program holding one teletext stream (PID 0x101) with a teletext descriptor.</summary>
    public static byte[] TransportStream(IReadOnlyList<(long Pts, byte[][] Units)> pes, string language = "ita", int page = 0x77, int magazine = 7, int type = 2)
    {
        using MemoryStream ms = new();
        int[] cc = new int[3];
        byte[] pat = Section(0x00, 1, [0x00, 0x01, 0xE1, 0x00]);
        // PMT: PCR PID 0x101, no program info, one stream: type 0x06, PID 0x101, descriptors 0x56.
        byte[] desc = [0x56, 5, .. Encoding.ASCII.GetBytes(language), (byte)((type << 3) | (magazine & 7)), (byte)page];
        byte[] pmt = Section(0x02, 1, [0xE1, 0x01, 0xF0, 0x00, 0x06, 0xE1, 0x01, 0xF0, (byte)desc.Length, .. desc]);
        WriteSection(ms, 0, pat, ref cc[0]);
        WriteSection(ms, 0x100, pmt, ref cc[1]);
        foreach ((long pts, byte[][] units) in pes)
        {
            byte[] p = Pes(PesPayload(units), pts);
            for (int i = 0; i < p.Length; i += 184)
            {
                byte[] packet = new byte[188];
                packet[0] = 0x47;
                packet[1] = (byte)((i == 0 ? 0x40 : 0) | 0x01);
                packet[2] = 0x01;
                packet[3] = (byte)(0x10 | (cc[2]++ & 15));
                Array.Copy(p, i, packet, 4, Math.Min(184, p.Length - i));
                if (p.Length - i < 184)
                {
                    Array.Fill(packet, (byte)0xFF, 4 + (p.Length - i), 184 - (p.Length - i));
                }

                ms.Write(packet);
            }

            WriteSection(ms, 0, pat, ref cc[0]);
            WriteSection(ms, 0x100, pmt, ref cc[1]);
        }

        return ms.ToArray();
    }

    /// <summary>A program stream carrying the same PES packets in private stream 1.</summary>
    public static byte[] ProgramStream(IReadOnlyList<(long Pts, byte[][] Units)> pes)
    {
        using MemoryStream ms = new();
        long scr = 0;
        foreach ((long pts, byte[][] units) in pes)
        {
            // MPEG-2 pack header (ISO/IEC 13818-1 §2.5.3.3): SCR 33 bits, mux rate 25 Mbit/s, no stuffing.
            ms.Write([0, 0, 1, 0xBA, (byte)(0x44 | ((scr >> 27) & 0x38) | ((scr >> 28) & 3)), (byte)(scr >> 20), (byte)(0x04 | ((scr >> 12) & 0xF8) | ((scr >> 13) & 3)), (byte)(scr >> 5), (byte)(0x04 | ((scr << 3) & 0xF8)), 0x01, 0x01, 0x89, 0xC3, 0xF8]);
            ms.Write(Pes(PesPayload(units), pts));
            scr += 3600;
        }

        ms.Write([0, 0, 1, 0xB9]);
        return ms.ToArray();
    }

    private static byte[] Section(int tableId, int idExtension, byte[] body)
    {
        List<byte> s = [(byte)tableId, 0xB0, 0, (byte)(idExtension >> 8), (byte)idExtension, 0xC1, 0, 0, .. body];
        int length = s.Count - 3 + 4;
        s[1] = (byte)(0xB0 | (length >> 8));
        s[2] = (byte)length;
        uint crc = Crc32Mpeg(s);
        s.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        return [.. s];
    }

    private static void WriteSection(Stream output, int pid, byte[] section, ref int cc)
    {
        byte[] packet = new byte[188];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | (pid >> 8));
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | (cc++ & 15));
        packet[4] = 0; // pointer_field
        Array.Copy(section, 0, packet, 5, section.Length);
        output.Write(packet);
    }

    private static uint Crc32Mpeg(List<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }

        return crc;
    }

    /// <summary>The units of one subtitle page transmission: header and rows (row number, 40 bytes) in one list.</summary>
    public static byte[][] Page(int magazine, int pageNumber, bool subtitle, bool erase, int nationalBits, params (int Row, byte[] Data)[] rows)
    {
        List<byte[]> units = [DataUnit(magazine, 0, Header(pageNumber, 0, erase, subtitle, nationalBits))];
        foreach ((int row, byte[] data) in rows)
        {
            units.Add(DataUnit(magazine, row, data));
        }

        return [.. units];
    }

    /// <summary>A subtitle row: start box twice, then the text, then end box (§12.2 codes 0/B and 0/A).</summary>
    public static byte[] BoxedRow(string text, int colourCode = 7) => Row($"{(char)colourCode}\u000B\u000B{text}\u000A\u000A");
}
