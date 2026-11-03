using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Repair.Reconstruction;

namespace RecursiveAI.OneMedia.ReconstructCheck;

/// <summary>
/// reconstruct IN OUT [--ref FILE] [--no-aac] [--json]  : reconstructs and prints one line per track.
/// compare ORIGINAL RECONSTRUCTED                      : packets of the reconstruction against the original's, per track.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "reconstruct")
        {
            return Reconstruct(args);
        }

        if (args.Length == 2 && args[0] == "note")
        {
            return MissingSetupNote(args[1]);
        }

        if (args.Length == 3 && args[0] == "compare")
        {
            return Compare(args[1], args[2]);
        }

        if (args.Length == 2 && args[0] == "config")
        {
            using IsoBmffDemuxer d = IsoBmffDemuxer.Open(FileMediaSource.Open(args[1]));
            foreach (MediaTrack t in d.Tracks)
            {
                Console.WriteLine($"{t.Index} {t.Kind} {t.Codec} tag={t.CodecTag} fmt={t.Configuration.Format} cfg={Convert.ToHexString(t.Configuration.Data.Span[..Math.Min(8, t.Configuration.Data.Length)])} ts={t.Timescale} priming={t.Edit.Priming}");
            }

            // For every AAC track: does the probe frame each packet of the healthy file exactly as the file's own sample table does?
            foreach (MediaTrack t in d.Tracks.Where(x => x.Codec == CodecId.Aac))
            {
                ReadOnlyMemory<byte> asc = t.Configuration.Data;
                if (RecursiveAI.OneMedia.Codecs.Aac.EsDescriptor.TryParse(asc, out RecursiveAI.OneMedia.Codecs.Aac.EsDescriptor? es) && es is not null && !es.DecoderSpecificInfo.IsEmpty)
                {
                    asc = es.DecoderSpecificInfo;
                }

                IAudioFramer? framer = AacReconstructionProbe.Instance.CreateFramer(asc.Span);
                Console.WriteLine($"aac track {t.Index} asc={Convert.ToHexString(asc.Span)} framer={(framer is null ? "null" : "ok")}");
                if (framer is null)
                {
                    continue;
                }

                using FileMediaSource src = FileMediaSource.Open(args[1]);
                using IsoBmffDemuxer d2 = IsoBmffDemuxer.Open(FileMediaSource.Open(args[1]));
                int total = 0, frame = 0, same = 0, incomplete = 0, notAac = 0;
                while (d2.TryReadPacket(out PacketView p) && total < 2000)
                {
                    if (p.Info.TrackIndex != t.Index)
                    {
                        continue;
                    }

                    total++;
                    // The packet followed by zeros, as it would be inside the media data.
                    // The packet in its place in the file, followed by whatever really follows it (the next packet or another track).
                    byte[] buf = new byte[Math.Min(1 << 16, src.Length - p.Info.SourceRange.Offset)];
                    src.Read(p.Info.SourceRange.Offset, buf);
                    AudioFrameResult r = framer.Probe(buf, out int len);
                    frame += r == AudioFrameResult.Frame ? 1 : 0;
                    same += r == AudioFrameResult.Frame && len == p.Data.Length ? 1 : 0;
                    incomplete += r == AudioFrameResult.Incomplete ? 1 : 0;
                    notAac += r == AudioFrameResult.NotFrame ? 1 : 0;
                    if (r != AudioFrameResult.Frame || len != p.Data.Length)
                    {
                        if (total - same < 4)
                        {
                            Console.WriteLine($"  packet {total - 1}: size={p.Data.Length} result={r} len={len}");
                        }
                    }
                }

                Console.WriteLine($"  probed {total}: frame={frame} exact={same} incomplete={incomplete} notAac={notAac}");
            }

            return 0;
        }

        if (args.Length == 4 && args[0] == "aacat")
        {
            // aacat FILE REFERENCE OFFSET: what the AAC probe built from the reference's setup says about the bytes at OFFSET of FILE.
            using IsoBmffDemuxer rd = IsoBmffDemuxer.Open(FileMediaSource.Open(args[2]));
            MediaTrack at = rd.Tracks.First(x => x.Codec == CodecId.Aac);
            ReadOnlyMemory<byte> asc = at.Configuration.Data;
            if (RecursiveAI.OneMedia.Codecs.Aac.EsDescriptor.TryParse(asc, out RecursiveAI.OneMedia.Codecs.Aac.EsDescriptor? e2) && e2 is not null)
            {
                asc = e2.DecoderSpecificInfo;
            }

            IAudioFramer f = AacReconstructionProbe.Instance.CreateFramer(asc.Span)!;
            using FileMediaSource fs = FileMediaSource.Open(args[1]);
            long off = long.Parse(args[3], CultureInfo.InvariantCulture);
            byte[] b = new byte[(int)Math.Min(1 << 16, fs.Length - off)];
            fs.Read(off, b);
            AudioFrameResult res = f.Probe(b, out int n);
            Console.WriteLine($"{res} length={n} first={Convert.ToHexString(b.AsSpan(0, 16))}");
            return 0;
        }

        if (args.Length == 3 && args[0] == "inband")
        {
            return InBand(args[1], args[2]);
        }

        Console.Error.WriteLine("usage: reconstruct IN OUT [--ref FILE] [--no-aac] | compare ORIGINAL RECONSTRUCTED | inband IN OUT");
        return 2;
    }

    // note IN: a cut recording whose video setup is lost must lead with the missing parameter sets through both library entry points.
    private static int MissingSetupNote(string input)
    {
        int bad = 0;
        foreach ((string name, Func<string, string, RecursiveAI.OneMedia.Repair.RepairReport> repair) in new (string, Func<string, string, RecursiveAI.OneMedia.Repair.RepairReport>)[]
        {
            ("Media.Repair", (i, o) => Media.Repair(i, o)),
            ("AacReconstructionProbe.Repair", (i, o) => AacReconstructionProbe.Repair(i, o)),
        })
        {
            string output = Path.Combine(Path.GetTempPath(), "note-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                RecursiveAI.OneMedia.Repair.RepairReport r = repair(input, output);
                bool ok = r.Status == RecursiveAI.OneMedia.Repair.RepairStatus.Unrepairable && r.Warnings.Count > 0
                    && r.Warnings[0].Contains("video parameter sets", StringComparison.Ordinal)
                    && r.Warnings[0].Contains("reference file from the same device", StringComparison.Ordinal);
                Console.WriteLine($"{name}: {r.Status}, first warning: {(r.Warnings.Count > 0 ? r.Warnings[0] : "(none)")} => {(ok ? "ok" : "MISSING NOTE")}");
                bad |= ok ? 0 : 1;
            }
            finally
            {
                File.Delete(output);
            }
        }

        return bad;
    }

    private static int Reconstruct(string[] args)
    {
        string? reference = null;
        bool aac = true;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--ref" && i + 1 < args.Length)
            {
                reference = args[++i];
            }
            else if (args[i] == "--no-aac")
            {
                aac = false;
            }
        }

        ReconstructionOptions options = aac ? AacReconstructionProbe.Options() : new ReconstructionOptions();
        long rssBefore = Process.GetCurrentProcess().WorkingSet64;
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            ReconstructionReport r = IsoReconstructor.Reconstruct(args[1], args[2], reference, options);
            sw.Stop();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"report confidence={r.Confidence} scan={r.ScanStart}-{r.ScanEnd} used={r.UsedBytes} fraction={r.RecoveredFraction:F4} skipped={r.SkippedRangeCount} out={r.OutputLength} ms={sw.ElapsedMilliseconds}"));
            foreach (ReconstructedTrack t in r.Tracks)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"track {t.Index} {t.Kind} {t.Codec} samples={t.Samples} bytes={t.PayloadBytes} dur={t.Duration} conf={t.Confidence} setup={t.Setup}"));
                foreach (string n in t.Notes)
                {
                    Console.WriteLine("  note: " + n);
                }
            }

            foreach (SkippedRange s in r.SkippedRanges.Take(Environment.GetEnvironmentVariable("RECON_VERBOSE") == "1" ? 60 : 5))
            {
                Console.WriteLine($"skip {s.Range.Offset}+{s.Range.Length} {s.Reason} track={s.TrackIndex}");
            }

            foreach (string n in r.Notes)
            {
                Console.WriteLine("note: " + n);
            }

            Console.WriteLine($"peak-rss-mb={Process.GetCurrentProcess().PeakWorkingSet64 >> 20} start-rss-mb={rssBefore >> 20}");
            return 0;
        }
        catch (ReconstructionException e)
        {
            Console.WriteLine($"failed {e.Failure}: {e.Message}");
            return 3;
        }
    }

    // Writes a copy of an ISO file whose key frames carry their parameter sets in the stream, as some cameras do, so the media data alone describes the codec.
    private static int InBand(string input, string output)
    {
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(FileMediaSource.Open(input));
        using FileStream fs = File.Create(output);
        using IsoMuxer mux = new(fs, new IsoMuxOptions { Layout = IsoLayout.MoovLast });
        byte[][] prefix = new byte[d.Tracks.Count][];
        foreach (MediaTrack t in d.Tracks)
        {
            mux.AddTrack(t);
            using MemoryStream ms = new();
            if (t.Codec == CodecId.H264 && RecursiveAI.OneMedia.Codecs.H264.AvcDecoderConfigurationRecord.TryParse(t.Configuration.Data, out var avc) && avc is not null)
            {
                foreach (ReadOnlyMemory<byte> u in avc.SequenceParameterSets.Concat(avc.PictureParameterSets))
                {
                    ms.Write([0, 0, (byte)(u.Length >> 8), (byte)u.Length]);
                    ms.Write(u.Span);
                }
            }
            else if (t.Codec == CodecId.Hevc && RecursiveAI.OneMedia.Codecs.Hevc.HevcDecoderConfigurationRecord.TryParse(t.Configuration.Data, out var hevc) && hevc is not null)
            {
                foreach (ReadOnlyMemory<byte> u in hevc.Arrays.SelectMany(a => a.Units))
                {
                    ms.Write([0, 0, (byte)(u.Length >> 8), (byte)u.Length]);
                    ms.Write(u.Span);
                }
            }

            prefix[t.Index] = ms.ToArray();
        }

        while (d.TryReadPacket(out PacketView p))
        {
            byte[] pre = p.Info.IsKeyframe ? prefix[p.Info.TrackIndex] : [];
            if (pre.Length == 0)
            {
                mux.WritePacket(p.Info, p.Data);
            }
            else
            {
                byte[] joined = new byte[pre.Length + p.Data.Length];
                pre.CopyTo(joined, 0);
                p.Data.CopyTo(joined.AsSpan(pre.Length));
                mux.WritePacket(p.Info with { Size = joined.Length }, joined);
            }
        }

        mux.Finish();
        return 0;
    }

    private sealed record Pkt(long Size, ulong Hash, bool Key, double Dts, double Pts, double Dur);

    private static (List<MediaTrack> Tracks, List<List<Pkt>> Packets) Read(string path)
    {
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(FileMediaSource.Open(path));
        List<List<Pkt>> all = [];
        foreach (MediaTrack _ in d.Tracks)
        {
            all.Add([]);
        }

        while (d.TryReadPacket(out PacketView p))
        {
            MediaTrack t = d.Tracks[p.Info.TrackIndex];
            double scale = t.Timescale;
            // Presentation times are compared after the edit list's start (the track's priming): a rebuilt file writes the video reorder delay
            // as an edit, while a device may have put the same delay into its composition offsets.
            double priming = t.Edit.Priming.IsKnown ? t.Edit.Priming.Rescale(t.Timescale, MediaRounding.NearestAwayFromZero).Ticks : 0;
            all[p.Info.TrackIndex].Add(new Pkt(
                p.Info.Size,
                BitConverter.ToUInt64(SHA256.HashData(p.Data)),
                p.Info.IsKeyframe,
                p.Info.Dts.IsKnown ? p.Info.Dts.Ticks / scale : double.NaN,
                p.Info.Pts.IsKnown ? (p.Info.Pts.Ticks - priming) / scale : double.NaN,
                p.Info.Duration.IsKnown ? p.Info.Duration.Ticks / scale : 0));
        }

        return (new List<MediaTrack>(d.Tracks), all);
    }

    private static int Compare(string originalPath, string reconPath)
    {
        (List<MediaTrack> ot, List<List<Pkt>> op) = Read(originalPath);
        (List<MediaTrack> rt, List<List<Pkt>> rp) = Read(reconPath);
        bool[] used = new bool[ot.Count];
        int exit = 0;
        for (int i = 0; i < rt.Count; i++)
        {
            int j = -1;
            for (int k = 0; k < ot.Count; k++)
            {
                if (!used[k] && ot[k].Kind == rt[i].Kind && ot[k].Codec == rt[i].Codec && (ot[k].Kind != TrackKind.Data || ot[k].CodecTag == rt[i].CodecTag))
                {
                    j = k;
                    break;
                }
            }

            if (j < 0)
            {
                Console.WriteLine($"cmp track={i} {rt[i].Kind} {rt[i].Codec} no-original");
                exit = 1;
                continue;
            }

            used[j] = true;
            List<Pkt> o = op[j];
            List<Pkt> r = rp[i];
            int bytesEq = 0, keyEq = 0, timeEq = 0, all = 0;
            double frame = o.Count > 1 ? Median(o.Select(x => x.Dur).Where(x => x > 0).ToList()) : 0;
            string firstBad = string.Empty;
            for (int n = 0; n < r.Count; n++)
            {
                if (n >= o.Count)
                {
                    break;
                }

                Pkt a = o[n], b = r[n];
                bool be = a.Size == b.Size && a.Hash == b.Hash;
                bool ke = a.Key == b.Key;
                // Times within one frame (the larger of the original's and the rebuilt duration).
                double tol = Math.Max(frame, a.Dur) + 1e-9;
                bool te = Math.Abs(a.Dts - b.Dts) <= tol && Math.Abs(a.Pts - b.Pts) <= tol;
                all++;
                bytesEq += be ? 1 : 0;
                keyEq += ke ? 1 : 0;
                timeEq += te ? 1 : 0;
                if (!(be && ke && te) && firstBad.Length == 0)
                {
                    firstBad = string.Create(CultureInfo.InvariantCulture, $" first-diff=#{n}(bytes={be} key={ke} time={te} odts={a.Dts:F4} rdts={b.Dts:F4} opts={a.Pts:F4} rpts={b.Pts:F4})");
                }
            }

            if (Environment.GetEnvironmentVariable("RECON_VERBOSE") == "1")
            {
                int shown = 0;
                for (int n = 0; n < Math.Min(r.Count, o.Count) && shown < 8; n++)
                {
                    if (o[n].Hash == r[n].Hash && o[n].Size == r[n].Size)
                    {
                        continue;
                    }

                    shown++;
                    Console.WriteLine($"  #{n} orig size={o[n].Size} key={o[n].Key}  recon size={r[n].Size} key={r[n].Key}");
                }
            }

            bool full = bytesEq == r.Count && keyEq == r.Count && timeEq == r.Count;
            if (!full || r.Count > o.Count)
            {
                exit = 1;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"cmp track={i} {rt[i].Kind} {rt[i].Codec} recon={r.Count} original={o.Count} compared={all} bytes={bytesEq} keys={keyEq} times={timeEq} recovered={(o.Count == 0 ? 0 : 100.0 * r.Count / o.Count):F1}%{firstBad}"));
        }

        return exit;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0)
        {
            return 0;
        }

        v.Sort();
        return v[v.Count / 2];
    }
}
