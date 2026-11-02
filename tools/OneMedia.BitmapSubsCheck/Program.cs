using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Tests.Subtitles;
using static RecursiveAI.OneMedia.Tests.Subtitles.BitmapSubtitleEncoders;

namespace RecursiveAI.OneMedia.BitmapSubsCheck;

// gen <dir>                                  write crafted PGS and VobSub test streams
// cues <file> [track]                        decode with OneMedia and print one line per cue (time, size, position, a pixel hash)
// compare <file> <w> <h> <raw> <showinfo>    compare our pictures with the RGBA frames ffmpeg rendered (see tools/run-bitmapsubs-diff.sh)
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            switch (args.Length > 0 ? args[0] : "")
            {
                case "gen":
                    Gen(args[1]);
                    return 0;
                case "fixtures":
                    Fixtures(args[1]);
                    return 0;
                case "cues":
                    Cues(args[1], args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : -1);
                    return 0;
                case "compare":
                    return Compare(args);
                default:
                    Console.Error.WriteLine("usage: gen <dir> | cues <file> [track] | compare <file> <w> <h> <raw> <showinfo> [track]");
                    return 2;
            }
        }
        catch (MediaException ex)
        {
            Console.Error.WriteLine("media error: " + ex.Message);
            return 1;
        }
    }

    private static Dictionary<int, (byte Y, byte Cr, byte Cb, byte A)> Palette(int seed)
    {
        var rng = new Random(seed);
        var p = new Dictionary<int, (byte, byte, byte, byte)>();
        for (int i = 1; i < 12; i++)
        {
            p[i] = ((byte)rng.Next(16, 236), (byte)rng.Next(16, 241), (byte)rng.Next(16, 241), i % 4 == 0 ? (byte)rng.Next(30, 220) : (byte)255);
        }

        return p;
    }

    private static void Gen(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach ((string name, int w, int h) in new[] { ("pgs-sd", 720, 480), ("pgs-hd", 1920, 1080) })
        {
            var sup = new List<byte>();
            Indexed a = Pattern(300, 40, 12, 1), b = Pattern(180, 26, 12, 2), c = Pattern(60, 14, 12, 3);
            sup.AddRange(PgsDisplaySet(90000, true, w, h, 0, 0x80, 0, [new PgsObjectPlacement(0, 100, h - 120, a), new PgsObjectPlacement(1, 200, 30, b)], Palette(5)));
            sup.AddRange(PgsDisplaySet(90000 * 4, true, w, h, 1, 0, 0, [], null));
            sup.AddRange(PgsDisplaySet(90000 * 6, true, w, h, 2, 0x80, 0, [new PgsObjectPlacement(0, 50, 60, c, Forced: true)], Palette(6), odsFragments: 3));
            sup.AddRange(PgsDisplaySet(90000 * 9, true, w, h, 3, 0, 0, [], null));
            sup.AddRange(PgsDisplaySet(90000 * 11, true, w, h, 4, 0x80, 0, [new PgsObjectPlacement(0, 40, 50, a)], Palette(7)));
            sup.AddRange(PgsDisplaySet(90000 * 13, true, w, h, 5, 0, 0, [], null));
            File.WriteAllBytes(Path.Combine(dir, name + ".sup"), sup.ToArray());
        }

        // VobSub: three pictures, the middle one forced, padded across several packets.
        var sub = new List<byte>();
        var entries = new List<(long Ms, long Pos)>();
        Indexed[] pics = [Pattern(260, 30, 4, 11), Pattern(140, 18, 4, 12), Pattern(300, 41, 4, 13)];
        int[][] colors = [[0, 1, 2, 3], [0, 4, 5, 6], [7, 8, 9, 15]];
        for (int i = 0; i < pics.Length; i++)
        {
            long ms = 1000 + (i * 2500);
            entries.Add((ms, sub.Count));
            sub.AddRange(SpuPackets(Spu(pics[i], 60 + (i * 20), 380 - (i * 10), colors[i], [0, 15, 10, 6], stopDelay: 100 + (i * 20), forced: i == 1), 0x20, ms * 90, maxPayload: 400));
        }

        var idx = new StringBuilder();
        idx.Append("# VobSub index file, v7 (do not modify this line!)\nsize: 720x480\norg: 0, 0\nscale: 100%, 100%\nalpha: 100%\nsmooth: OFF\nfadein/out: 0, 0\nalign: OFF at LEFT TOP\ntime offset: 0\nforced subs: OFF\n");
        idx.Append("palette: 000000, ff0000, 00ff00, 0000ff, ffff00, 00ffff, ff00ff, ffffff, 808080, c0c0c0, 400000, 004000, 000040, 404000, 004040, 7f3fbf\n");
        idx.Append("langidx: 0\n\nid: en, index: 0\n");
        foreach ((long ms, long pos) in entries)
        {
            idx.Append(CultureInfo.InvariantCulture, $"timestamp: {ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}:{ms % 1000:000}, filepos: {pos:x9}\n");
        }

        File.WriteAllBytes(Path.Combine(dir, "vobsub.sub"), sub.ToArray());
        File.WriteAllText(Path.Combine(dir, "vobsub.idx"), idx.ToString(), Encoding.ASCII);
    }

    // Small fixtures for the unit tests' hostile-input seeds and the fuzz targets: one stream of each kind, a few hundred bytes to a few KiB.
    private static void Fixtures(string dir)
    {
        Directory.CreateDirectory(dir);
        Indexed a = Pattern(64, 12, 6, 1), b = Pattern(40, 8, 6, 2);
        var sup = new List<byte>();
        sup.AddRange(PgsDisplaySet(90000, true, 720, 480, 0, 0x80, 0, [new PgsObjectPlacement(0, 20, 400, a), new PgsObjectPlacement(1, 30, 40, b, Forced: true, Crop: [2, 1, 30, 6])], Palette(5), odsFragments: 2));
        sup.AddRange(PgsDisplaySet(90000 * 3, true, 720, 480, 1, 0, 0, [], null));
        File.WriteAllBytes(Path.Combine(dir, "pgs.sup"), sup.ToArray());

        var sub = new List<byte>();
        var idx = new StringBuilder("# VobSub index file, v7 (do not modify this line!)\nsize: 720x480\npalette: 000000, ff0000, 00ff00, 0000ff, ffff00, 00ffff, ff00ff, ffffff, 808080, c0c0c0, 400000, 004000, 000040, 404000, 004040, 7f3fbf\nid: en, index: 0\n");
        for (int i = 0; i < 2; i++)
        {
            long pos = sub.Count;
            sub.AddRange(SpuPackets(Spu(Pattern(48 + (i * 8), 10, 4, 20 + i), 40, 300, [0, 1, 2, 3], [0, 15, 10, 6], 80, i == 1, endsWithRunToEnd: i == 1), 0x20, 90000 + (i * 180000), maxPayload: 150));
            idx.Append(CultureInfo.InvariantCulture, $"timestamp: 00:00:0{1 + (i * 2)}:000, filepos: {pos:x9}\n");
        }

        File.WriteAllBytes(Path.Combine(dir, "vobsub.sub"), sub.ToArray());
        File.WriteAllText(Path.Combine(dir, "vobsub.idx"), idx.ToString(), Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(dir, "sub-picture.spu"), Spu(Pattern(70, 11, 4, 30), 10, 20, [0, 1, 2, 3], [0, 15, 15, 8], 60, false));
        File.WriteAllBytes(Path.Combine(dir, "sub-picture-toend.spu"), Spu(Pattern(70, 11, 4, 31), 10, 20, [0, 1, 2, 3], [0, 15, 15, 8], 60, true, endsWithRunToEnd: true));

        foreach (int depth in new[] { 2, 4, 8 })
        {
            int n = depth == 2 ? 4 : depth == 4 ? 16 : 40;
            var clut = new Dictionary<int, (byte Y, byte Cr, byte Cb, byte T)>();
            for (int i = 1; i < n; i++)
            {
                clut[i] = ((byte)(40 + (i * 11 % 190)), (byte)(100 + (i * 17 % 60)), (byte)(90 + (i * 29 % 80)), (byte)(i == 3 ? 128 : 0));
            }

            File.WriteAllBytes(Path.Combine(dir, $"dvb-{depth}bit.pes"), DvbDisplaySet(1, 720, 576, 4, 2, [new DvbRegion(0, 40, 400, depth, Pattern(70, 9, n, depth))], clut));
        }
    }

    private static SubtitleList<SubtitleCue> Decode(string file, int trackIndex, out string what)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".sup" or ".idx" or ".sub")
        {
            what = ext;
            return BitmapSubtitles.ReadFile(file).Cues;
        }

        using MediaFile media = Media.Open(file);
        MediaTrack? track = media.Tracks.FirstOrDefault(t => (trackIndex < 0 || t.Index == trackIndex) && BitmapSubtitles.CanDecode(t));
        if (track is null)
        {
            throw new InvalidOperationException("no bitmap subtitle track");
        }

        what = track.Codec.Name;
        return BitmapSubtitles.ReadTrack(media, track).Cues;
    }

    private static string Hash(SubtitleCue c)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (SubtitleImage i in c.Images)
        {
            sha.TransformBlock(i.Pixels, 0, i.Pixels.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..12];
    }

    private static void Cues(string file, int track)
    {
        SubtitleList<SubtitleCue> cues = Decode(file, track, out string what);
        Console.WriteLine($"# {file} {what} cues={cues.Count}");
        foreach (SubtitleCue c in cues)
        {
            Console.WriteLine(string.Join('\t', c.Start.ToString(), c.End.ToString(), BitmapSubtitleCues.IsForced(c) ? "forced" : "-", string.Join(';', c.Images.Select(i => $"{i.Width}x{i.Height}@{i.X},{i.Y}")), Hash(c), c.Language ?? "-"));
        }
    }

    private static int Compare(string[] a)
    {
        string file = a[1];
        int w = int.Parse(a[2], CultureInfo.InvariantCulture), h = int.Parse(a[3], CultureInfo.InvariantCulture);
        byte[] raw = File.ReadAllBytes(a[4]);
        var times = new List<double>();
        foreach (string line in File.ReadLines(a[5]))
        {
            int p = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (line.Contains("Parsed_showinfo", StringComparison.Ordinal) && p >= 0)
            {
                string t = line[(p + 9)..].Split(' ')[0];
                times.Add(double.Parse(t, CultureInfo.InvariantCulture));
            }
        }

        int frameBytes = w * h * 4;
        int frames = raw.Length / frameBytes;
        if (frames != times.Count)
        {
            Console.WriteLine($"WARN frames in raw {frames} != showinfo lines {times.Count}");
            frames = Math.Min(frames, times.Count);
        }

        SubtitleList<SubtitleCue> cues = Decode(file, a.Length > 6 ? int.Parse(a[6], CultureInfo.InvariantCulture) : -1, out string what);
        // ffmpeg rebases the timeline of a subtitle file so that its first event is at 0; find that shift from the first picture and apply it.
        int nonBlank = 0, firstFrame = -1;
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<byte> fr = raw.AsSpan(f * frameBytes, frameBytes);
            if (fr.IndexOfAnyExcept((byte)0) >= 0)
            {
                if (firstFrame < 0)
                {
                    firstFrame = f;
                }

                // count a picture once however many times the filter graph repeats it
                if (f == 0 || !fr.SequenceEqual(raw.AsSpan((f - 1) * frameBytes, frameBytes)))
                {
                    nonBlank++;
                }
            }
        }

        double shift = cues.Count > 0 && firstFrame >= 0 ? ((double)cues[0].Start.Ticks / cues[0].Start.Timescale) - times[firstFrame] : 0;
        if (Math.Abs(shift) > 0.0005)
        {
            Console.WriteLine($"  ffmpeg timeline is {shift:F3}s behind the container time stamps");
        }

        int matched = 0, exact = 0, maxDiff = 0, pixelsOver2 = 0;
        long pixelsDiff = 0, pixelsTotal = 0, onlyTheirs = 0, onlyOurs = 0, bothVisibleDiffer = 0;
        double maxDt = 0;
        var used = new HashSet<int>();
        foreach (SubtitleCue c in cues)
        {
            double start = (double)c.Start.Ticks / c.Start.Timescale;
            int best = -1;
            double bestDt = double.MaxValue;
            for (int f = 0; f < frames; f++)
            {
                double dt = Math.Abs(times[f] + shift - start);
                bool blank = raw.AsSpan(f * frameBytes, frameBytes).IndexOfAnyExcept((byte)0) < 0;
                if (!blank && dt < bestDt)
                {
                    best = f;
                    bestDt = dt;
                }
            }

            if (best < 0 || bestDt > 0.002)
            {
                Console.WriteLine($"  cue at {start:F3}s: no ffmpeg frame within 2 ms (nearest {bestDt:F3}s)");
                continue;
            }

            matched++;
            used.Add(best);
            maxDt = Math.Max(maxDt, bestDt);
            byte[] canvas = new byte[frameBytes];
            foreach (SubtitleImage img in c.Images)
            {
                for (int y = 0; y < img.Height; y++)
                {
                    int cy = img.Y + y;
                    if (cy < 0 || cy >= h)
                    {
                        continue;
                    }

                    for (int x = 0; x < img.Width; x++)
                    {
                        int cx = img.X + x;
                        if (cx < 0 || cx >= w)
                        {
                            continue;
                        }

                        Array.Copy(img.Pixels, (y * img.Stride) + (x * 4), canvas, ((cy * w) + cx) * 4, 4);
                    }
                }
            }

            ReadOnlySpan<byte> theirs = raw.AsSpan(best * frameBytes, frameBytes);
            long diffPixels = 0;
            int fileMax = 0;
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4;
                byte aa = canvas[o + 3], ab = theirs[o + 3];
                int d = Math.Abs(aa - ab);
                if (aa != 0 || ab != 0)
                {
                    // Fully transparent pixels have no colour; compare colour only where something shows.
                    if (aa != 0 && ab != 0 && Environment.GetEnvironmentVariable("COMPARE_ALPHA_ONLY") != "1")
                    {
                        d = Math.Max(d, Math.Max(Math.Abs(canvas[o] - theirs[o]), Math.Max(Math.Abs(canvas[o + 1] - theirs[o + 1]), Math.Abs(canvas[o + 2] - theirs[o + 2]))));
                    }
                }
                else
                {
                    d = 0;
                }

                pixelsTotal++;
                if (Environment.GetEnvironmentVariable("COMPARE_CLASSES") == "1")
                {
                    if (aa == 0 && ab != 0)
                    {
                        onlyTheirs++;
                    }
                    else if (aa != 0 && ab == 0)
                    {
                        onlyOurs++;
                    }
                    else if (aa != 0 && d > 2)
                    {
                        bothVisibleDiffer++;
                    }
                }

                if (d > 0)
                {
                    if (d > 2 && pixelsOver2 < 6 && Environment.GetEnvironmentVariable("COMPARE_DEBUG") == "1")
                    {
                        Console.WriteLine($"    px ({i % w},{i / w}) ours={canvas[o]},{canvas[o + 1]},{canvas[o + 2]},{canvas[o + 3]} ffmpeg={theirs[o]},{theirs[o + 1]},{theirs[o + 2]},{theirs[o + 3]}");
                    }

                    diffPixels++;
                    if (d > 2)
                    {
                        pixelsOver2++;
                    }
                }

                fileMax = Math.Max(fileMax, d);
            }

            pixelsDiff += diffPixels;
            maxDiff = Math.Max(maxDiff, fileMax);
            if (diffPixels == 0)
            {
                exact++;
            }
            else
            {
                Console.WriteLine($"  cue at {start:F3}s: {diffPixels} pixels differ, max channel difference {fileMax}");
            }
        }

        Console.WriteLine($"{Path.GetFileName(file)} [{what}] ours={cues.Count} ffmpeg-nonblank-frames={nonBlank} matched={matched} identical={exact} pixels-differing={pixelsDiff} (over 2: {pixelsOver2}) max-channel-diff={maxDiff} max-time-diff={maxDt * 1000:F2}ms");
        if (Environment.GetEnvironmentVariable("COMPARE_CLASSES") == "1")
        {
            Console.WriteLine($"  classes: ours transparent but ffmpeg visible={onlyTheirs}, ours visible but ffmpeg transparent={onlyOurs}, both visible and differing by more than 2={bothVisibleDiffer}");
        }

        return 0;
    }
}
