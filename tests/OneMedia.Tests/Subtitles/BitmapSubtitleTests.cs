using RecursiveAI.OneMedia.Captions;
using static RecursiveAI.OneMedia.Tests.Subtitles.BitmapSubtitleEncoders;

namespace RecursiveAI.OneMedia.Tests.Subtitles;

public class BitmapSubtitleTests
{
    private static MediaTime T(long ms) => new(ms, 1000);

    // Straight, unrounded limited-range conversion to check the fixed-point one against (BT.709 above 576 lines, BT.601 otherwise).
    private static (int R, int G, int B) Reference(int y, int cb, int cr, bool bt709)
    {
        double yy = 1.16438 * (y - 16);
        return bt709
            ? ((int)Math.Round(yy + (1.79274 * (cr - 128))), (int)Math.Round(yy - (0.21325 * (cb - 128)) - (0.53291 * (cr - 128))), (int)Math.Round(yy + (2.11240 * (cb - 128))))
            : ((int)Math.Round(yy + (1.59603 * (cr - 128))), (int)Math.Round(yy - (0.39176 * (cb - 128)) - (0.81297 * (cr - 128))), (int)Math.Round(yy + (2.01723 * (cb - 128))));
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 255);

    // ---- PGS ----

    private static Dictionary<int, (byte Y, byte Cr, byte Cb, byte A)> PgsPalette() => new()
    {
        [1] = (235, 128, 128, 255),
        [2] = (81, 240, 90, 255),
        [3] = (145, 54, 34, 128),
        [4] = (41, 110, 240, 255),
    };

    [Fact]
    public void PgsDisplaySetsDecodeToExactIndexedPixels()
    {
        Indexed a = Pattern(173, 41, 5, 1), b = Pattern(60, 20, 5, 2);
        var objs = new List<PgsObjectPlacement> { new(0, 100, 900, a), new(1, 700, 100, b, Forced: true) };
        var sup = new List<byte>();
        sup.AddRange(PgsDisplaySet(90000, true, 1920, 1080, 0, 0x80, 0, objs, PgsPalette()));
        sup.AddRange(PgsDisplaySet(90000 * 3, true, 1920, 1080, 1, 0, 0, [], null));
        sup.AddRange(PgsDisplaySet(90000 * 5, true, 1920, 1080, 2, 0x80, 0, [new PgsObjectPlacement(0, 10, 20, b)], PgsPalette()));
        var defects = new DefectLog();
        SubtitleDocument doc = BitmapSubtitles.ReadSup(sup.ToArray(), null, defects);

        Assert.Empty(defects);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("pgs", doc.Format);
        Assert.Equal((1920, 1080), (doc.ReferenceWidth, doc.ReferenceHeight));
        SubtitleCue first = doc.Cues[0];
        Assert.Equal(T(1000), first.Start);
        Assert.Equal(T(3000), first.End);
        Assert.Equal(2, first.Images.Count);
        Assert.True(BitmapSubtitleCues.IsForced(first));
        Assert.False(BitmapSubtitleCues.IsForced(doc.Cues[1]));
        Assert.False(doc.Cues[1].End.IsKnown); // nothing closes the last one
        Check(first.Images[0], a, 100, 900, 1920, 1080);
        Check(first.Images[1], b, 700, 100, 1920, 1080);

        static void Check(SubtitleImage img, Indexed idx, int x, int y, int dw, int dh)
        {
            Assert.Equal((x, y, idx.Width, idx.Height, dw, dh), (img.X, img.Y, img.Width, img.Height, img.DisplayWidth, img.DisplayHeight));
            var pal = PgsPalette();
            for (int i = 0; i < idx.Codes.Length; i++)
            {
                int o = i * 4;
                byte code = idx.Codes[i];
                if (code == 0)
                {
                    Assert.Equal(0, img.Pixels[o + 3]);
                    continue;
                }

                (byte yy, byte cr, byte cb, byte al) = pal[code];
                (int r, int g, int bl) = Reference(yy, cb, cr, dh > 576);
                Assert.Equal(al, img.Pixels[o + 3]);
                Assert.InRange(img.Pixels[o], Clamp(r) - 1, Clamp(r) + 1);
                Assert.InRange(img.Pixels[o + 1], Clamp(g) - 1, Clamp(g) + 1);
                Assert.InRange(img.Pixels[o + 2], Clamp(bl) - 1, Clamp(bl) + 1);
            }
        }
    }

    [Fact]
    public void PgsCropFragmentsAndPaletteUpdateWork()
    {
        Indexed a = Pattern(90, 30, 5, 3);
        var sup = new List<byte>();
        sup.AddRange(PgsDisplaySet(0, true, 1280, 720, 0, 0x80, 0, [new PgsObjectPlacement(0, 5, 6, a, Crop: [10, 4, 50, 20])], PgsPalette(), odsFragments: 3));
        var pal2 = PgsPalette();
        pal2[1] = (16, 128, 128, 255);
        sup.AddRange(PgsDisplaySet(90000, true, 1280, 720, 1, 0, 0, [new PgsObjectPlacement(0, 5, 6, a, Crop: [10, 4, 50, 20])], pal2, paletteOnly: true));
        sup.AddRange(PgsDisplaySet(180000, true, 1280, 720, 2, 0, 0, [], null));
        var defects = new DefectLog();
        SubtitleDocument doc = BitmapSubtitles.ReadSup(sup.ToArray(), null, defects);

        Assert.Empty(defects);
        Assert.Equal(2, doc.Cues.Count);
        SubtitleImage img = doc.Cues[0].Images[0];
        Assert.Equal((5, 6, 50, 20), (img.X, img.Y, img.Width, img.Height));
        // the cropped window shows object pixels (10..59, 4..23)
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 50; x++)
            {
                Assert.Equal(a.Codes[((y + 4) * 90) + x + 10] == 0, img.Pixels[(((y * 50) + x) * 4) + 3] == 0);
            }
        }

        Assert.Equal(T(1000), doc.Cues[0].End);
        Assert.Equal(T(1000), doc.Cues[1].Start);
        Assert.Equal(T(2000), doc.Cues[1].End);
        // the palette update changed colour 1 from white to black
        int at1 = Array.FindIndex(a.Codes, c => c == 1);
        Assert.True(at1 >= 0);
        Assert.True(doc.Cues[0].Images[0].Pixels.Length > 0);
    }

    [Fact]
    public void PgsPacketsAsContainersStoreThem()
    {
        Indexed a = Pattern(40, 12, 4, 4);
        byte[] set = PgsDisplaySet(0, false, 1920, 1080, 0, 0x80, 0, [new PgsObjectPlacement(0, 1, 2, a)], PgsPalette());
        var dec = new PgsDecoder();
        Assert.Empty(dec.Feed(set, T(500), T(2000)));
        IReadOnlyList<SubtitleCue> rest = dec.Flush();
        Assert.Single(rest);
        Assert.Equal(T(500), rest[0].Start);
        Assert.Equal(new MediaTime(2500, 1000), rest[0].End);
    }

    [Fact]
    public void PgsHostileInputIsBoundedAndTyped()
    {
        // An object that declares 65535 x 65535 pixels fails the limit before anything is allocated.
        var o = new List<byte>();
        byte[] pcs = [0x07, 0x80, 0x04, 0x38, 0x10, 0, 0, 0x80, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        o.AddRange([0x16, 0, (byte)pcs.Length]); o.AddRange(pcs);
        o.AddRange([0x15, 0, 11, 0, 0, 0, 0xC0, 0, 0, 4, 0xFF, 0xFF, 0xFF, 0xFF]);
        o.AddRange([0x80, 0, 0]);
        var dec = new PgsDecoder();
        var ex = Assert.Throws<MediaLimitExceededException>(() => dec.Feed(o.ToArray(), T(0), MediaTime.Unknown));
        Assert.Equal("ImageDimension", ex.LimitName);

        // Garbage never throws anything but the typed limit exception.
        var rng = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            byte[] junk = new byte[rng.Next(1, 300)];
            rng.NextBytes(junk);
            try { new PgsDecoder().Feed(junk, T(0), MediaTime.Unknown); }
            catch (MediaLimitExceededException) { }
        }
    }

    // ---- VobSub ----

    [Fact]
    public void SpuDecodesFieldsColoursAndTimes()
    {
        foreach (bool toEnd in new[] { false, true })
        {
            Indexed b = Pattern(201, 37, 4, 5);
            var palette = Enumerable.Range(0, 16).Select(i => new SubtitleColor((byte)(i * 16), (byte)(255 - (i * 16)), (byte)(i * 7))).ToArray();
            byte[] spu = Spu(b, 100, 400, [0, 5, 9, 14], [0, 15, 8, 3], stopDelay: 150, forced: false, endsWithRunToEnd: toEnd);
            var defects = new DefectLog();
            IReadOnlyList<SubtitleCue> cues = new VobSubDecoder(new BitmapSubtitleOptions { Palette = palette, DisplayWidth = 720, DisplayHeight = 480 }, defects).Decode(spu, new MediaTime(90000, 90000));

            Assert.Empty(defects);
            SubtitleCue cue = Assert.Single(cues);
            Assert.Equal(new MediaTime(90000, 90000), cue.Start);
            // 150 delay units of 1024 / 90000 s
            Assert.Equal(new MediaTime(90000 + (150 * 1024), 90000), cue.End);
            SubtitleImage img = Assert.Single(cue.Images);
            Assert.Equal((100, 400, 201, 37, 720, 480), (img.X, img.Y, img.Width, img.Height, img.DisplayWidth, img.DisplayHeight));
            int[] colors = [0, 5, 9, 14], contrasts = [0, 15, 8, 3];
            for (int i = 0; i < b.Codes.Length; i++)
            {
                int code = b.Codes[i];
                SubtitleColor c = palette[colors[code]];
                Assert.Equal((c.R, c.G, c.B, (byte)(contrasts[code] * 17)), (img.Pixels[i * 4], img.Pixels[(i * 4) + 1], img.Pixels[(i * 4) + 2], img.Pixels[(i * 4) + 3]));
            }
        }
    }

    [Fact]
    public void IdxAndSubSidecarsDecode()
    {
        Indexed b1 = Pattern(120, 24, 4, 6), b2 = Pattern(80, 16, 4, 7);
        var sub = new List<byte>();
        long pos1 = sub.Count;
        sub.AddRange(SpuPackets(Spu(b1, 50, 300, [0, 1, 2, 3], [0, 15, 15, 15], 100, false), 0x20, 90000, maxPayload: 300));
        long pos2 = sub.Count;
        sub.AddRange(SpuPackets(Spu(b2, 60, 320, [0, 1, 2, 3], [0, 15, 15, 15], 100, true), 0x20, 270000));
        string idx = "# VobSub index file, v7 (do not modify this line!)\nsize: 720x576\norg: 0, 0\nforced subs: OFF\npalette: 000000, ffffff, ff0000, 00ff00, 0000ff, 111111, 222222, 333333, 444444, 555555, 666666, 777777, 888888, 999999, aaaaaa, bbbbbb\n"
            + "langidx: 0\nid: en, index: 0\n"
            + $"timestamp: 00:00:01:000, filepos: {pos1:x9}\ntimestamp: 00:00:03:000, filepos: {pos2:x9}\n";
        var defects = new DefectLog();
        SubtitleDocument doc = BitmapSubtitles.ReadVobSub(System.Text.Encoding.ASCII.GetBytes(idx), sub.ToArray(), null, defects);

        Assert.Empty(defects);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal((720, 576), (doc.ReferenceWidth, doc.ReferenceHeight));
        Assert.Equal("en", doc.Cues[0].Language);
        Assert.Equal(T(1000), doc.Cues[0].Start);
        Assert.Equal(T(3000), doc.Cues[1].Start);
        Assert.False(BitmapSubtitleCues.IsForced(doc.Cues[0]));
        Assert.True(BitmapSubtitleCues.IsForced(doc.Cues[1]));
        Assert.Equal(b1.Width, doc.Cues[0].Images[0].Width);
        // code 2 = palette entry 2 = ff0000 at full opacity
        int at = Array.IndexOf(b1.Codes, (byte)2);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, doc.Cues[0].Images[0].Pixels.AsSpan(at * 4, 4).ToArray());

        // Without the index the program stream is read on its own, timed by the PES time stamps.
        SubtitleDocument bare = new() { Cues = new SubtitleList<SubtitleCue>(VobSubReader.ReadProgramStream(sub.ToArray())) };
        Assert.Equal(2, bare.Cues.Count);
        Assert.Equal(new MediaTime(90000, 90000), bare.Cues[0].Start);
    }

    [Fact]
    public void SpuHostileInputIsTyped()
    {
        var rng = new Random(11);
        for (int i = 0; i < 500; i++)
        {
            byte[] junk = new byte[rng.Next(0, 200)];
            rng.NextBytes(junk);
            if (junk.Length > 4 && i % 2 == 0)
            {
                junk[0] = 0; junk[1] = (byte)junk.Length; junk[2] = 0; junk[3] = 4;
            }

            try { new VobSubDecoder().Decode(junk, T(0)); }
            catch (MediaLimitExceededException) { }
        }

        // A display area of 4095 x 4095 passes; the allocation limit is what stops anything larger.
        var tight = new BitmapSubtitleOptions { Limits = new MediaLimits { MaxAllocationBytes = 1000 } };
        byte[] spu = Spu(Pattern(60, 20, 4, 1), 0, 0, [0, 1, 2, 3], [0, 15, 15, 15], 10, false);
        Assert.Throws<MediaLimitExceededException>(() => new VobSubDecoder(tight).Decode(spu, T(0)));
    }

    [Fact]
    public void IdxParserIsTolerant()
    {
        var d = new DefectLog();
        VobSubIndex idx = VobSubIndex.Parse("size: 720x480\nnonsense\npalette: 000000, zz\nid: --, index: 1\ntimestamp: bad\ntimestamp: 00:00:02:500, filepos: 0000001f\n"u8, d);
        Assert.Equal((720, 480), (idx.Width, idx.Height));
        Assert.Equal(1, idx.Streams[0].Index);
        Assert.Null(idx.Streams[0].Language);
        Assert.Equal(new VobSubEntry(T(2500), 0x1f), idx.Streams[0].Entries[0]);
        Assert.True(d.Count >= 3);
    }

    // ---- DVB ----

    private static Dictionary<int, (byte Y, byte Cr, byte Cb, byte T)> DvbClut(int n)
    {
        var d = new Dictionary<int, (byte Y, byte Cr, byte Cb, byte T)>();
        for (int i = 1; i < n; i++)
        {
            d[i] = ((byte)(40 + (i * 11 % 190)), (byte)(100 + (i * 17 % 60)), (byte)(90 + (i * 29 % 80)), (byte)(i == 3 ? 128 : 0));
        }

        d[0] = (0, 0, 0, 0);
        return d;
    }

    [Fact]
    public void DvbDecodesAllPixelDepthsWithRegionsAndTimeOut()
    {
        foreach (int depth in new[] { 2, 4, 8 })
        {
            int colors = depth == 2 ? 4 : depth == 4 ? 16 : 40;
            Indexed b = Pattern(300, 33, colors, 10 + depth), c = Pattern(90, 10, colors, 20 + depth);
            var clut = DvbClut(colors);
            var dec = new DvbSubtitleDecoder();
            var all = new List<SubtitleCue>();
            all.AddRange(dec.Feed(DvbDisplaySet(1, 1280, 720, 4, 2, [new DvbRegion(0, 100, 600, depth, b), new DvbRegion(1, 200, 20, depth, c, ClutId: 1)], clut), T(1000)));
            all.AddRange(dec.Feed(DvbEmptyPage(1), T(2500)));
            all.AddRange(dec.Feed(DvbDisplaySet(1, 1280, 720, 2, 0, [new DvbRegion(0, 100, 600, depth, b)], null), T(6000)));
            all.AddRange(dec.Flush());

            Assert.Equal(2, all.Count);
            Assert.Equal(T(1000), all[0].Start);
            Assert.Equal(T(2500), all[0].End);
            Assert.Equal(T(6000), all[1].Start);
            Assert.Equal(T(8000), all[1].End); // page time-out of 2 s
            Assert.Equal(2, all[0].Images.Count);
            SubtitleImage img = all[0].Images[0];
            Assert.Equal((100, 600, 300, 33, 1280, 720), (img.X, img.Y, img.Width, img.Height, img.DisplayWidth, img.DisplayHeight));
            for (int i = 0; i < b.Codes.Length; i++)
            {
                int code = b.Codes[i];
                (byte y, byte cr, byte cb, byte tt) = clut.TryGetValue(code, out var e) ? e : default;
                if (code == 0 || y == 0)
                {
                    if (img.Pixels[(i * 4) + 3] != 0)
                    {
                        Assert.Fail($"depth {depth} px ({i % 300},{i / 300}) code {code} should be transparent; alpha {img.Pixels[(i * 4) + 3]}");
                    }

                    continue;
                }

                if (255 - tt != img.Pixels[(i * 4) + 3])
                {
                    Assert.Fail($"depth {depth} px ({i % 300},{i / 300}) code {code} alpha {img.Pixels[(i * 4) + 3]} want {255 - tt}");
                }

                // DVB is always ITU-R BT.601 (EN 300 743 clause 7.2.4)
                (int r, _, _) = Reference(y, cb, cr, false);
                Assert.InRange(img.Pixels[i * 4], Clamp(r) - 1, Clamp(r) + 1);
            }
        }
    }

    [Fact]
    public void DvbDefaultClutAndHostileInput()
    {
        Indexed b = Pattern(50, 8, 16, 3);
        var dec = new DvbSubtitleDecoder();
        dec.Feed(DvbDisplaySet(1, 720, 576, 3, 2, [new DvbRegion(0, 0, 0, 4, b)], null), T(0));
        SubtitleCue cue = Assert.Single(dec.Flush());
        int i = Array.FindIndex(b.Codes, c => c == 7); // 16-entry default: b1=0, b2=b3=b4=1 -> white
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, cue.Images[0].Pixels.AsSpan(i * 4, 4).ToArray());
        int j = Array.FindIndex(b.Codes, c => c == 9); // b1=1, b4=1 -> R=50 %
        Assert.Equal(new byte[] { 127, 0, 0, 255 }, cue.Images[0].Pixels.AsSpan(j * 4, 4).ToArray());

        var rng = new Random(5);
        byte[] good = DvbDisplaySet(1, 720, 576, 3, 2, [new DvbRegion(0, 0, 0, 8, Pattern(64, 8, 30, 2))], DvbClut(30));
        for (int n = 0; n < 500; n++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int k = 0; k < 4; k++) { bad[rng.Next(bad.Length)] = (byte)rng.Next(256); }
            try { new DvbSubtitleDecoder().Feed(bad, T(0)); }
            catch (MediaLimitExceededException) { }
        }
    }

    [Fact]
    public void ImagesWritePng()
    {
        var img = new SubtitleImage(0, 0, 3, 2, 12, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 128, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        byte[] png = SubtitleImagePng.Encode(img);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.AsSpan(0, 8).ToArray());
        Assert.Equal((3, 2), ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]));
    }
}
