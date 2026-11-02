using System.Collections.Generic;
using System.IO;

namespace RecursiveAI.OneMedia.Tests.Subtitles;

// Writers for the three bitmap subtitle systems, used to make test streams with known pixels. They write what the public descriptions of
// each format say; the decoders under test are checked against these round trips and, separately, against another decoder's output.
internal static class BitmapSubtitleEncoders
{
    internal sealed record Indexed(int Width, int Height, byte[] Codes);

    // A repeatable picture with runs of every length class: a few colour bands, a diagonal, some speckle.
    internal static Indexed Pattern(int w, int h, int colors, int seed)
    {
        var codes = new byte[w * h];
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                int run = rng.Next(5) switch { 0 => 1 + rng.Next(3), 1 => 3 + rng.Next(13), 2 => 16 + rng.Next(48), 3 => 60 + rng.Next(200), _ => 1 + rng.Next(w) };
                int c = rng.Next(colors);
                if (rng.Next(3) == 0)
                {
                    c = 0;
                }

                run = Math.Min(run, w - x);
                for (int i = 0; i < run; i++)
                {
                    codes[(y * w) + x + i] = (byte)c;
                }

                x += run;
            }
        }

        return new Indexed(w, h, codes);
    }

    // ---- PGS ----

    internal readonly record struct PgsObjectPlacement(int Id, int X, int Y, Indexed Bitmap, bool Forced = false, int[]? Crop = null);

    internal static byte[] PgsRle(Indexed b)
    {
        var o = new List<byte>();
        for (int y = 0; y < b.Height; y++)
        {
            int x = 0;
            while (x < b.Width)
            {
                byte c = b.Codes[(y * b.Width) + x];
                int run = 1;
                while (x + run < b.Width && b.Codes[(y * b.Width) + x + run] == c && run < 16383)
                {
                    run++;
                }

                x += run;
                if (c == 0)
                {
                    if (run < 64)
                    {
                        o.Add(0);
                        o.Add((byte)run);
                    }
                    else
                    {
                        o.Add(0);
                        o.Add((byte)(0x40 | (run >> 8)));
                        o.Add((byte)run);
                    }
                }
                else if (run < 3)
                {
                    for (int i = 0; i < run; i++)
                    {
                        o.Add(c);
                    }
                }
                else if (run < 64)
                {
                    o.Add(0);
                    o.Add((byte)(0x80 | run));
                    o.Add(c);
                }
                else
                {
                    o.Add(0);
                    o.Add((byte)(0xC0 | (run >> 8)));
                    o.Add((byte)run);
                    o.Add(c);
                }
            }

            o.Add(0);
            o.Add(0);
        }

        return o.ToArray();
    }

    private static void Be16(List<byte> o, int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }

    private static void Segment(List<byte> o, bool header, long pts, int type, byte[] payload)
    {
        if (header)
        {
            o.Add((byte)'P');
            o.Add((byte)'G');
            o.Add((byte)(pts >> 24)); o.Add((byte)(pts >> 16)); o.Add((byte)(pts >> 8)); o.Add((byte)pts);
            o.AddRange(new byte[4]);
        }

        o.Add((byte)type);
        Be16(o, payload.Length);
        o.AddRange(payload);
    }

    // palette: entry -> (Y, Cr, Cb, A). Pass null objects (or empty) for a clearing display set.
    internal static byte[] PgsDisplaySet(long pts, bool header, int width, int height, int compositionNumber, int state, int paletteId,
        IReadOnlyList<PgsObjectPlacement> objects, IReadOnlyDictionary<int, (byte Y, byte Cr, byte Cb, byte A)>? palette, bool paletteOnly = false, int odsFragments = 1)
    {
        var o = new List<byte>();
        var pcs = new List<byte>();
        Be16(pcs, width); Be16(pcs, height);
        pcs.Add(0x10);
        Be16(pcs, compositionNumber);
        pcs.Add((byte)state);
        pcs.Add((byte)(paletteOnly ? 0x80 : 0));
        pcs.Add((byte)paletteId);
        pcs.Add((byte)objects.Count);
        foreach (PgsObjectPlacement ob in objects)
        {
            Be16(pcs, ob.Id);
            pcs.Add(0); // window id
            pcs.Add((byte)((ob.Crop is not null ? 0x80 : 0) | (ob.Forced ? 0x40 : 0)));
            Be16(pcs, ob.X); Be16(pcs, ob.Y);
            if (ob.Crop is { } c)
            {
                Be16(pcs, c[0]); Be16(pcs, c[1]); Be16(pcs, c[2]); Be16(pcs, c[3]);
            }
        }

        Segment(o, header, pts, 0x16, pcs.ToArray());
        var wds = new List<byte> { (byte)Math.Max(1, objects.Count) };
        if (objects.Count == 0)
        {
            wds.Add(0); Be16(wds, 0); Be16(wds, 0); Be16(wds, 1); Be16(wds, 1);
        }

        for (int i = 0; i < objects.Count; i++)
        {
            wds.Add((byte)i); Be16(wds, objects[i].X); Be16(wds, objects[i].Y); Be16(wds, objects[i].Bitmap.Width); Be16(wds, objects[i].Bitmap.Height);
        }

        Segment(o, header, pts, 0x17, wds.ToArray());
        if (palette is not null)
        {
            var pds = new List<byte> { (byte)paletteId, 0 };
            foreach (KeyValuePair<int, (byte Y, byte Cr, byte Cb, byte A)> e in palette)
            {
                pds.Add((byte)e.Key); pds.Add(e.Value.Y); pds.Add(e.Value.Cr); pds.Add(e.Value.Cb); pds.Add(e.Value.A);
            }

            Segment(o, header, pts, 0x14, pds.ToArray());
        }

        if (!paletteOnly)
        {
            foreach (PgsObjectPlacement ob in objects)
            {
                byte[] rle = PgsRle(ob.Bitmap);
                int total = rle.Length + 4;
                int first = odsFragments > 1 ? rle.Length / odsFragments : rle.Length;
                for (int part = 0, at = 0; at < rle.Length || part == 0; part++)
                {
                    int take = part == 0 ? first : (part == odsFragments - 1 ? rle.Length - at : first);
                    var ods = new List<byte>();
                    Be16(ods, ob.Id);
                    ods.Add(0);
                    bool last = at + take >= rle.Length;
                    ods.Add((byte)((part == 0 ? 0x80 : 0) | (last ? 0x40 : 0)));
                    if (part == 0)
                    {
                        ods.Add((byte)(total >> 16)); ods.Add((byte)(total >> 8)); ods.Add((byte)total);
                        Be16(ods, ob.Bitmap.Width); Be16(ods, ob.Bitmap.Height);
                    }

                    ods.AddRange(rle.AsSpan(at, take).ToArray());
                    Segment(o, header, pts, 0x15, ods.ToArray());
                    at += take;
                    if (last)
                    {
                        break;
                    }
                }
            }
        }

        Segment(o, header, pts, 0x80, []);
        return o.ToArray();
    }

    // ---- DVD sub-picture units ----

    private sealed class NibbleWriter
    {
        internal readonly List<byte> Bytes = [];
        private int _half = -1;

        internal void Put(int nibble)
        {
            if (_half < 0) { _half = nibble & 0xF; }
            else { Bytes.Add((byte)((_half << 4) | (nibble & 0xF))); _half = -1; }
        }

        internal void Align() { if (_half >= 0) { Put(0); } }
    }

    private static void SpuRun(NibbleWriter w, int count, int code, bool toEnd)
    {
        if (toEnd) { w.Put(0); w.Put(0); w.Put(0); w.Put(code); return; }
        int v;
        int nibbles;
        if (count <= 3) { v = (count << 2) | code; nibbles = 1; }
        else if (count <= 15) { v = (count << 2) | code; nibbles = 2; }
        else if (count <= 63) { v = (count << 2) | code; nibbles = 3; }
        else { v = (count << 2) | code; nibbles = 4; }
        for (int i = nibbles - 1; i >= 0; i--) { w.Put(v >> (4 * i)); }
    }

    private static byte[] SpuField(Indexed b, int first, bool runToEnd)
    {
        var w = new NibbleWriter();
        for (int y = first; y < b.Height; y += 2)
        {
            int x = 0;
            while (x < b.Width)
            {
                byte c = b.Codes[(y * b.Width) + x];
                int run = 1;
                while (x + run < b.Width && b.Codes[(y * b.Width) + x + run] == c && run < 255) { run++; }
                x += run;
                SpuRun(w, run, c, runToEnd && x >= b.Width);
            }

            w.Align();
        }

        return w.Bytes.ToArray();
    }

    // colors/contrasts are the four nibbles for pixel codes 0..3 (index 0 = code 0).
    internal static byte[] Spu(Indexed b, int x1, int y1, int[] colors, int[] contrasts, int stopDelay, bool forced, bool endsWithRunToEnd = false)
    {
        byte[] top = SpuField(b, 0, endsWithRunToEnd), bottom = SpuField(b, 1, endsWithRunToEnd);
        var o = new List<byte> { 0, 0, 0, 0 };
        int topOffset = o.Count; o.AddRange(top);
        int bottomOffset = o.Count; o.AddRange(bottom);
        int table = o.Count;
        // first sequence: delay 0
        int seq2 = table + 4 + 1 + 3 + 3 + 7 + 5 + 1;
        o.Add(0); o.Add(0); Be16(o, seq2);
        o.Add((byte)(forced ? 0x00 : 0x01));
        o.Add(0x03); o.Add((byte)((colors[3] << 4) | colors[2])); o.Add((byte)((colors[1] << 4) | colors[0]));
        o.Add(0x04); o.Add((byte)((contrasts[3] << 4) | contrasts[2])); o.Add((byte)((contrasts[1] << 4) | contrasts[0]));
        int x2 = x1 + b.Width - 1, y2 = y1 + b.Height - 1;
        o.Add(0x05); o.Add((byte)(x1 >> 4)); o.Add((byte)(((x1 & 0xF) << 4) | (x2 >> 8))); o.Add((byte)x2); o.Add((byte)(y1 >> 4)); o.Add((byte)(((y1 & 0xF) << 4) | (y2 >> 8))); o.Add((byte)y2);
        o.Add(0x06); Be16(o, topOffset); Be16(o, bottomOffset);
        o.Add(0xFF);
        // second sequence: stop
        Be16(o, stopDelay); Be16(o, o.Count - 2);
        o.Add(0x02); o.Add(0xFF);
        o[0] = (byte)(o.Count >> 8); o[1] = (byte)o.Count; o[2] = (byte)(table >> 8); o[3] = (byte)table;
        return o.ToArray();
    }

    // MPEG-2 program stream packets for one sub-picture unit: pack header, then PES packets of private stream 1.
    internal static byte[] SpuPackets(byte[] spu, int subStream, long pts90k, int maxPayload = 2000)
    {
        var o = new List<byte>();
        for (int at = 0, n = 0; at < spu.Length; n++)
        {
            int take = Math.Min(maxPayload, spu.Length - at);
            o.AddRange(new byte[] { 0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 0x01, 0x89, 0xC3, 0xF8 });
            bool withPts = n == 0;
            int hdl = withPts ? 5 : 0;
            int len = 3 + hdl + 1 + take;
            o.AddRange(new byte[] { 0, 0, 1, 0xBD, (byte)(len >> 8), (byte)len, 0x81, (byte)(withPts ? 0x80 : 0), (byte)hdl });
            if (withPts)
            {
                o.Add((byte)(0x21 | (((pts90k >> 30) & 7) << 1)));
                o.Add((byte)(pts90k >> 22));
                o.Add((byte)(0x01 | (((pts90k >> 15) & 0x7F) << 1)));
                o.Add((byte)(pts90k >> 7));
                o.Add((byte)(0x01 | ((pts90k & 0x7F) << 1)));
            }

            o.Add((byte)subStream);
            o.AddRange(spu.AsSpan(at, take).ToArray());
            at += take;
        }

        return o.ToArray();
    }

    // ---- DVB subtitles ----

    private sealed class BitWriter
    {
        internal readonly List<byte> Bytes = [];
        private int _acc, _n;

        internal void Put(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((value >> i) & 1);
                if (++_n == 8) { Bytes.Add((byte)_acc); _acc = 0; _n = 0; }
            }
        }

        internal void Align() { while (_n != 0) { Put(0, 1); } }
    }

    // Pixel code string of `bits` bits per pixel for one line, using every run form the syntax has.
    private static void DvbLine(BitWriter w, byte[] codes, int offset, int width, int bits)
    {
        int x = 0;
        while (x < width)
        {
            int c = codes[offset + x];
            int run = 1;
            while (x + run < width && codes[offset + x + run] == c) { run++; }
            x += run;
            while (run > 0)
            {
                if (bits == 2)
                {
                    if (c != 0 && run <= 2) { for (int i = 0; i < run; i++) { w.Put(c, 2); } run = 0; }
                    else if (c == 0 && run == 1) { w.Put(0, 2); w.Put(0, 1); w.Put(1, 1); run = 0; }
                    else if (c == 0 && run == 2) { w.Put(0, 2); w.Put(0, 1); w.Put(0, 1); w.Put(1, 2); run = 0; }
                    else if (run <= 10) { w.Put(0, 2); w.Put(1, 1); w.Put(run - 3, 3); w.Put(c, 2); run = 0; }
                    else if (run == 11) { w.Put(0, 2); w.Put(1, 1); w.Put(7, 3); w.Put(c, 2); run = 1; } // 11 has no code of its own
                    else if (run <= 27) { w.Put(0, 2); w.Put(0, 1); w.Put(0, 1); w.Put(2, 2); w.Put(run - 12, 4); w.Put(c, 2); run = 0; }
                    else if (run == 28) { w.Put(0, 2); w.Put(0, 1); w.Put(0, 1); w.Put(2, 2); w.Put(27 - 12, 4); w.Put(c, 2); run = 1; } // 28 has no code of its own
                    else { int t = Math.Min(run, 284); w.Put(0, 2); w.Put(0, 1); w.Put(0, 1); w.Put(3, 2); w.Put(t - 29, 8); w.Put(c, 2); run -= t; }
                }
                else if (bits == 4)
                {
                    if (c != 0 && run <= 3) { for (int i = 0; i < run; i++) { w.Put(c, 4); } run = 0; }
                    else if (c == 0 && run == 1) { w.Put(0, 4); w.Put(1, 1); w.Put(1, 1); w.Put(0, 2); run = 0; }
                    else if (c == 0 && run == 2) { w.Put(0, 4); w.Put(1, 1); w.Put(1, 1); w.Put(1, 2); run = 0; }
                    else if (c == 0 && run <= 9) { w.Put(0, 4); w.Put(0, 1); w.Put(run - 2, 3); run = 0; }
                    else if (run <= 8) { int t = Math.Min(run, 7); w.Put(0, 4); w.Put(1, 1); w.Put(0, 1); w.Put(t - 4, 2); w.Put(c, 4); run -= t; } // 8 has no code of its own: 7 then 1
                    else if (run <= 24) { w.Put(0, 4); w.Put(1, 1); w.Put(1, 1); w.Put(2, 2); w.Put(run - 9, 4); w.Put(c, 4); run = 0; }
                    else { int t = Math.Min(run, 280); w.Put(0, 4); w.Put(1, 1); w.Put(1, 1); w.Put(3, 2); w.Put(t - 25, 8); w.Put(c, 4); run -= t; }
                }
                else
                {
                    if (c != 0 && run <= 2) { for (int i = 0; i < run; i++) { w.Put(c, 8); } run = 0; }
                    else if (c == 0 && run <= 127) { w.Put(0, 8); w.Put(0, 1); w.Put(run, 7); run = 0; }
                    else if (c == 0) { w.Put(0, 8); w.Put(0, 1); w.Put(127, 7); run -= 127; }
                    else { int t = Math.Min(run, 127); w.Put(0, 8); w.Put(1, 1); w.Put(t, 7); w.Put(c, 8); run -= t; }
                }
            }
        }

        // end of string
        if (bits == 2) { w.Put(0, 6); } // 2-bit zero, switch_1, switch_2, switch_3 = 00
        else if (bits == 4) { w.Put(0, 8); }
        else { w.Put(0, 16); }
        w.Align();
    }

    private static byte[] DvbField(Indexed b, int first, int bits)
    {
        var all = new List<byte>();
        for (int y = first; y < b.Height; y += 2)
        {
            var w = new BitWriter();
            DvbLine(w, b.Codes, y * b.Width, b.Width, bits);
            all.Add((byte)(bits == 2 ? 0x10 : bits == 4 ? 0x11 : 0x12));
            all.AddRange(w.Bytes);
            all.Add(0xF0);
        }

        return all.ToArray();
    }

    private static void DvbSegment(List<byte> o, int type, int page, byte[] data)
    {
        o.Add(0x0F); o.Add((byte)type); Be16(o, page); Be16(o, data.Length); o.AddRange(data);
    }

    internal readonly record struct DvbRegion(int Id, int X, int Y, int Depth, Indexed Bitmap, int ClutId = 0, bool Fill = false, int FillCode = 0);

    // clut: entry -> (Y, Cr, Cb, T) full range, for the depth's table. Returns one PES payload.
    internal static byte[] DvbDisplaySet(int page, int displayW, int displayH, int timeOut, int pageState, IReadOnlyList<DvbRegion> regions,
        IReadOnlyDictionary<int, (byte Y, byte Cr, byte Cb, byte T)>? clut, bool emitDds = true, bool reducedClut = false)
    {
        var o = new List<byte> { 0x20, 0x00 };
        if (emitDds)
        {
            var dds = new List<byte> { 0x00 };
            Be16(dds, displayW - 1); Be16(dds, displayH - 1);
            DvbSegment(o, 0x14, page, dds.ToArray());
        }

        var pcs = new List<byte> { (byte)timeOut, (byte)(pageState << 2) };
        foreach (DvbRegion r in regions) { pcs.Add((byte)r.Id); pcs.Add(0); Be16(pcs, r.X); Be16(pcs, r.Y); }
        DvbSegment(o, 0x10, page, pcs.ToArray());
        foreach (DvbRegion r in regions)
        {
            var rcs = new List<byte> { (byte)r.Id, (byte)(r.Fill ? 0x08 : 0x00) };
            Be16(rcs, r.Bitmap.Width); Be16(rcs, r.Bitmap.Height);
            int depthCode = r.Depth == 2 ? 1 : r.Depth == 4 ? 2 : 3;
            rcs.Add((byte)((depthCode << 5) | (depthCode << 2)));
            rcs.Add((byte)r.ClutId);
            rcs.Add((byte)(r.Depth == 8 ? r.FillCode : 0));
            rcs.Add((byte)(((r.Depth == 4 ? r.FillCode : 0) << 4) | ((r.Depth == 2 ? r.FillCode : 0) << 2)));
            Be16(rcs, r.Id); // object id = region id
            rcs.Add(0); rcs.Add(0); rcs.Add(0); rcs.Add(0);
            rcs[^4] = 0x00; // object type bitmap, provider stream, x = 0
            DvbSegment(o, 0x11, page, rcs.ToArray());
        }

        if (clut is not null)
        {
            foreach (IGrouping<int, DvbRegion> g in regions.GroupBy(r => r.ClutId))
            {
                var cds = new List<byte> { (byte)g.Key, 0 };
                int depth = g.First().Depth;
                foreach (KeyValuePair<int, (byte Y, byte Cr, byte Cb, byte T)> e in clut)
                {
                    cds.Add((byte)e.Key);
                    int flag = depth == 2 ? 0x80 : depth == 4 ? 0x40 : 0x20;
                    if (reducedClut)
                    {
                        cds.Add((byte)flag);
                        cds.Add((byte)((e.Value.Y & 0xFC) | (e.Value.Cr >> 6)));
                        cds.Add((byte)(((e.Value.Cr >> 4) & 3) << 6 | ((e.Value.Cb >> 4) << 2) | (e.Value.T >> 6)));
                    }
                    else
                    {
                        cds.Add((byte)(flag | 1));
                        cds.Add(e.Value.Y); cds.Add(e.Value.Cr); cds.Add(e.Value.Cb); cds.Add(e.Value.T);
                    }
                }

                DvbSegment(o, 0x12, page, cds.ToArray());
            }
        }

        foreach (DvbRegion r in regions)
        {
            byte[] top = DvbField(r.Bitmap, 0, r.Depth), bottom = DvbField(r.Bitmap, 1, r.Depth);
            var ods = new List<byte>();
            Be16(ods, r.Id);
            ods.Add(0x00); // version 0, method 0
            Be16(ods, top.Length); Be16(ods, bottom.Length);
            ods.AddRange(top); ods.AddRange(bottom);
            DvbSegment(o, 0x13, page, ods.ToArray());
        }

        DvbSegment(o, 0x80, page, []);
        o.Add(0xFF);
        return o.ToArray();
    }

    // The same, with no regions: clears the page.
    internal static byte[] DvbEmptyPage(int page, int timeOut = 5)
    {
        var o = new List<byte> { 0x20, 0x00 };
        DvbSegment(o, 0x10, page, [(byte)timeOut, 0x00]);
        DvbSegment(o, 0x80, page, []);
        o.Add(0xFF);
        return o.ToArray();
    }
}
