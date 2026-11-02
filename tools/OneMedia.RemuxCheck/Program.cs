using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Remuxing;

// Usage:
//   RemuxCheck remux <in> <out> [--format mp4|m4a|mov|3gp|3g2|fmp4|cmaf|mkv|webm] [--no-faststart] [--drop-video] [--drop-audio] [--report]
//       Remuxes with Remuxer, writes <out>.map ("<output index> <input index> <repackaging>") and <out>.pkts (every packet as written:
//       "<out> <dts> <pts> <duration> <size> <key> <disposable> <sha256>"), checks that the NAL units of re-framed tracks are unchanged apart
//       from parameter sets, and prints one summary line.
//   RemuxCheck faststart <in> <out>
//       Moves the movie box to the front and prints one summary line.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: RemuxCheck remux <in> <out> [options] | RemuxCheck faststart <in> <out>");
    return 2;
}

string mode = args[0], input = args[1], output = args[2];
RemuxFormat format = RemuxFormat.Mp4;
bool faststart = true, dropVideo = false, dropAudio = false, full = false;
for (int i = 3; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--format":
            format = args[++i] switch
            {
                "m4a" => RemuxFormat.M4a,
                "mov" => RemuxFormat.Mov,
                "3gp" => RemuxFormat.ThreeGp,
                "3g2" => RemuxFormat.ThreeG2,
                "fmp4" => RemuxFormat.FragmentedMp4,
                "cmaf" => RemuxFormat.Cmaf,
                "mkv" => RemuxFormat.Matroska,
                "webm" => RemuxFormat.WebM,
                _ => RemuxFormat.Mp4,
            };
            break;
        case "--no-faststart": faststart = false; break;
        case "--drop-video": dropVideo = true; break;
        case "--drop-audio": dropAudio = true; break;
        case "--report": full = true; break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

string name = Path.GetFileName(input);
try
{
    return mode == "faststart" ? Faststart() : Remux();
}
catch (MediaException e)
{
    Console.WriteLine($"{name}\tREFUSED\t{e.GetType().Name}: {e.Message.Replace('\n', ' ')}");
    return 0;
}

int Faststart()
{
    FaststartReport r = Remuxer.Faststart(input, output, overwrite: true);
    Console.WriteLine($"{name}\tok\trelocated={r.Relocated}\tmoov={r.MovieBoxBytes}\tshifted={r.OffsetsShifted}\tpromoted={r.PromotedTables}\tpeakRssMB={PeakRss() >> 20}");
    return 0;
}

int Remux()
{
    using StreamWriter pkts = new(output + ".pkts");
    Dictionary<(int Track, NalCodec Codec), IncrementalHash> outNal = [];
    Dictionary<int, NalCodec> nalCodec = [];
    HashSet<NalCodec> candidates = [];
    long packets = 0;
    RemuxOptions options = new()
    {
        Faststart = faststart,
        Overwrite = true,
        Select = t => !(dropVideo && t.Kind == TrackKind.Video) && !(dropAudio && t.Kind == TrackKind.Audio),
        Observer = (in PacketInfo p, ReadOnlySpan<byte> data) =>
        {
            pkts.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{p.TrackIndex} {p.Dts.Ticks} {p.Pts.Ticks} {(p.Duration.IsKnown ? p.Duration.Ticks : -1)} {data.Length} {(p.IsKeyframe ? 1 : 0)} {((p.Flags & PacketAttributes.Disposable) != 0 ? 1 : 0)} {Convert.ToHexStringLower(SHA256.HashData(data))}"));
            packets++;
            foreach (NalCodec codec in candidates)
            {
                if (!outNal.TryGetValue((p.TrackIndex, codec), out IncrementalHash? h))
                {
                    outNal[(p.TrackIndex, codec)] = h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }

                HashLengthPrefixed(h, codec, data);
            }
        },
    };

    // The tracks that will be re-framed are known from the input: Annex B video without a stored record.
    using (MediaFile probe = Media.Open(input))
    {
        foreach (MediaTrack t in probe.Tracks)
        {
            if (t.Kind == TrackKind.Video && t.Codec.Name is "h264" or "hevc" && t.Configuration.Format is not ("avcC" or "hvcC"))
            {
                nalCodec[t.Index] = t.Codec == CodecId.H264 ? NalCodec.H264 : NalCodec.Hevc;
                candidates.Add(nalCodec[t.Index]);
            }
        }
    }

    RemuxReport report = Remuxer.Remux(input, output, format, options);
    // nalCodec is keyed by input index but the observer sees output indexes: remap once the map is known.
    Dictionary<int, int> outToIn = report.Tracks.Where(t => t.OutputTrack >= 0).ToDictionary(t => t.OutputTrack, t => t.InputTrack);
    File.WriteAllLines(output + ".map", report.Tracks.Where(t => t.OutputTrack >= 0).Select(t => $"{t.OutputTrack} {t.InputTrack} {t.Repackaging}"));
    string nalCheck = "n/a";
    if (report.Tracks.Any(t => t.Repackaging == "annexb-to-length-prefixed"))
    {
        nalCheck = VerifyNals(report) ? "same" : "DIFFERENT";
    }

    Console.WriteLine($"{name}\tok\tpackets={packets}\tbytes={report.Mux.BytesWritten}\ttracks={report.Tracks.Count(t => t.OutputTrack >= 0)}/{report.Tracks.Count}\tlosses={report.Losses.Count(l => l.Reason != LossReason.Deselected)}\tchanges={report.Changes.Count}\tnals={nalCheck}\tdefects={report.InputDefects}\tpeakRssMB={PeakRss() >> 20}");
    if (full)
    {
        foreach (RemuxLoss l in report.Losses)
        {
            Console.WriteLine($"\tloss: {l.Kind}/{l.Reason} track={l.InputTrack} {l.Subject}: {l.Detail}");
        }

        foreach (string c in report.Changes)
        {
            Console.WriteLine("\tchange: " + c);
        }

        foreach (string n in report.Mux.Notes)
        {
            Console.WriteLine("\tmux note: " + n);
        }
    }

    return 0;

    bool VerifyNals(RemuxReport r)
    {
        // Reads the input again and hashes its NAL units (parameter sets excluded) per track; the output side was hashed as packets went by.
        Dictionary<int, IncrementalHash> inHash = [];
        using MediaFile again = Media.Open(input);
        IDemuxer d = again.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            if (!nalCodec.TryGetValue(v.Info.TrackIndex, out NalCodec codec))
            {
                continue;
            }

            if (!inHash.TryGetValue(v.Info.TrackIndex, out IncrementalHash? h))
            {
                inHash[v.Info.TrackIndex] = h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }

            AnnexBReader reader = new(v.Data);
            while (reader.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
            {
                if (nal.Length > 0 && !NalUnit.IsParameterSet(codec, NalUnit.GetType(codec, nal)))
                {
                    h.AppendData(nal);
                }
            }
        }

        foreach ((int o, int i) in outToIn)
        {
            if (nalCodec.TryGetValue(i, out NalCodec c) && inHash.TryGetValue(i, out IncrementalHash? ih) && outNal.TryGetValue((o, c), out IncrementalHash? oh)
                && !ih.GetHashAndReset().AsSpan().SequenceEqual(oh.GetHashAndReset()))
            {
                return false;
            }
        }

        return true;
    }
}

void HashLengthPrefixed(IncrementalHash h, NalCodec codec, ReadOnlySpan<byte> sample)
{
    int pos = 0;
    while (pos + 4 <= sample.Length)
    {
        int len = (sample[pos] << 24) | (sample[pos + 1] << 16) | (sample[pos + 2] << 8) | sample[pos + 3];
        pos += 4;
        if (len < 0 || len > sample.Length - pos)
        {
            return;
        }

        ReadOnlySpan<byte> nal = sample.Slice(pos, len);
        pos += len;
        if (nal.Length > 0 && !NalUnit.IsParameterSet(codec, NalUnit.GetType(codec, nal)))
        {
            h.AppendData(nal);
        }
    }
}

static long PeakRss()
{
    foreach (string line in File.ReadLines("/proc/self/status"))
    {
        if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
        {
            return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
        }
    }

    return 0;
}
