using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using OneAudio;
using OneAudio.Mp4;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Tests.IsoBmffSupport;
using OAPacketView = OneAudio.PacketView;
using PacketView = RecursiveAI.OneMedia.PacketView;
using CodecId = RecursiveAI.OneMedia.CodecId;

// Usage: IsoBmffDiff [--ffprobe <path>] [--mp4box <path>] [--no-audio] <file>...
//        IsoBmffDiff --make-big <path> <gigabytes> [moovlast|faststart]
// For each file: demuxes it with the ISO BMFF demuxer, compares every packet with `ffprobe -show_packets` (size, byte offset, duration,
// key/discard flags, and timestamps once the edit list is applied the way ffprobe applies it), and compares audio packets with OneAudio's
// MP4 packet source. Prints one summary line per file and the first differences.
string ffprobe = "ffprobe";
string? mp4box = null;
bool audio = true;
List<string> files = [];
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--ffprobe")
    {
        ffprobe = args[++i];
    }
    else if (args[i] == "--mp4box")
    {
        mp4box = args[++i];
    }
    else if (args[i] == "--no-audio")
    {
        audio = false;
    }
    else
    {
        files.Add(args[i]);
    }
}

if (args.Length >= 3 && args[0] == "--make-big")
{
    return MakeBig(args[1], double.Parse(args[2], CultureInfo.InvariantCulture), args.Length > 3 ? args[3] : "moovlast");
}

int failures = 0;
long totalPackets = 0, totalAudio = 0;
foreach (string file in files)
{
    try
    {
        (int packets, int diffs, int audioPackets, int audioDiffs) = Compare(file);
        totalPackets += packets;
        totalAudio += audioPackets;
        if (diffs + audioDiffs > 0)
        {
            failures++;
        }
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"{Path.GetFileName(file)}: EXCEPTION {e.GetType().Name}: {e.Message}");
    }
}

Console.WriteLine($"files={files.Count} packets={totalPackets} audioPackets={totalAudio} filesWithDifferences={failures}");
return failures == 0 ? 0 : 1;

(int, int, int, int) Compare(string file)
{
    // Ours.
    List<Pkt>[] ours;
    IReadOnlyList<MediaTrack> tracks;
    DefectLog defects;
    using (FileMediaSource src = FileMediaSource.Open(file))
    using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(src, new MediaOpenOptions { LeaveOpen = true }))
    {
        tracks = d.Tracks;
        defects = d.Defects;
        ours = new List<Pkt>[tracks.Count];
        for (int i = 0; i < ours.Length; i++)
        {
            ours[i] = [];
        }

        while (d.TryReadPacket(out PacketView v))
        {
            PacketInfo p = v.Info;
            ours[p.TrackIndex].Add(new Pkt(p.Pts.Ticks, p.Dts.Ticks, p.Duration.Ticks, p.Size, p.SourceRange.Offset, p.IsKeyframe, (p.Flags & PacketAttributes.Discard) != 0, audio && tracks[p.TrackIndex].Kind == TrackKind.Audio ? v.Data.ToArray() : []));
        }
    }

    // ffprobe.
    Dictionary<int, List<string[]>> theirs = [];
    foreach (string line in Run(ffprobe, "-v", "error", "-show_packets", "-show_entries", "packet=stream_index,pts,dts,duration,size,pos,flags", "-of", "csv=p=0", file).Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] f = line.Trim().Split(',');
        if (f.Length < 7 || !int.TryParse(f[0], out int si))
        {
            continue;
        }

        if (!theirs.TryGetValue(si, out List<string[]>? list))
        {
            theirs[si] = list = [];
        }

        list.Add(f);
    }

    int total = 0, diffs = 0;
    StringBuilder notes = new();
    int streamIdx = -1;
    foreach (MediaTrack t in tracks)
    {
        List<Pkt> mine = ours[t.Index];
        // ffprobe numbers streams in track order; tracks it does not expose leave gaps, so match by the next index that has packets.
        streamIdx++;
        if (!theirs.TryGetValue(streamIdx, out List<string[]>? other))
        {
            if (mine.Count > 0)
            {
                notes.Append($" track{t.Index}:ffprobe-has-no-stream");
                diffs++;
            }

            continue;
        }

        long editStart = 0, start = 0;
        foreach (EditSegment s in t.Edit.Edits ?? [])
        {
            if (!s.IsEmpty)
            {
                editStart = s.MediaStart.Ticks;
                break;
            }
        }

        start = t.Edit.StartTime.Rescale(t.Timescale, MediaRounding.NearestAwayFromZero).Ticks;
        long shift = start - editStart;
        if (t.Codec == CodecId.Pcm && mine.Count != other.Count)
        {
            // ffprobe regroups one-frame PCM samples into larger packets; compare the bytes they cover instead.
            long mineBytes = mine.Sum(x => (long)x.Size), theirBytes = other.Sum(x => long.Parse(x[4], CultureInfo.InvariantCulture));
            bool contiguous = true;
            for (int i = 1; i < mine.Count; i++)
            {
                contiguous &= mine[i].Pos == mine[i - 1].Pos + mine[i - 1].Size;
            }

            if (mineBytes == theirBytes && contiguous && mine[0].Pos == long.Parse(other[0][5], CultureInfo.InvariantCulture))
            {
                notes.Append($" track{t.Index}:pcm-repacketised({mine.Count}->{other.Count})");
                continue;
            }
        }

        if (mine.Count != other.Count)
        {
            notes.Append($" track{t.Index}:count {mine.Count} vs {other.Count}");
            diffs++;
        }

        int n = Math.Min(mine.Count, other.Count);
        int[] bad = new int[6]; // size pos dur pts dts flags
        long? constPts = null, constDts = null;
        bool ptsConst = true, dtsConst = true;
        for (int i = 0; i < n; i++)
        {
            Pkt m = mine[i];
            string[] o = other[i];
            total++;
            if (!long.TryParse(o[4], out long osize) || osize != m.Size) { bad[0]++; }
            if (!long.TryParse(o[5], out long opos) || opos != m.Pos) { bad[1]++; }
            if (!long.TryParse(o[3], out long odur) || odur != m.Duration)
            {
                if (i == n - 1 && t.Codec.Name is "mp3" or "ac3" or "eac3")
                {
                    notes.Append($" track{t.Index}:last-duration-from-parser({m.Duration}->{odur})");
                }
                else
                {
                    bad[2]++;
                }
            }
            if (long.TryParse(o[1], out long opts))
            {
                long d = opts - (m.Pts + shift);
                if (d != 0) { bad[3]++; constPts ??= d; ptsConst &= d == constPts; }
            }
            else { bad[3]++; ptsConst = false; }
            if (long.TryParse(o[2], out long odts))
            {
                long d = odts - (m.Dts + shift);
                if (d != 0) { bad[4]++; constDts ??= d; dtsConst &= d == constDts; }
            }
            else { bad[4]++; dtsConst = false; }
            string fl = o[6];
            if (fl.Contains('K', StringComparison.Ordinal) != m.Key || fl.Contains('D', StringComparison.Ordinal) != m.Discard) { bad[5]++; }
        }

        bool negativeOffsets = mine.Any(x => x.Pts < x.Dts);
        if (negativeOffsets && ptsConst && bad[3] > 0 && bad[4] == 0)
        {
            notes.Append($" track{t.Index}:negative-cts-pts-shift({constPts})");
            bad[3] = 0;
        }

        if (negativeOffsets && dtsConst && bad[4] > 0 && bad[3] == 0)
        {
            notes.Append($" track{t.Index}:negative-cts-dts-shift({constDts})");
            bad[4] = 0;
        }

        string[] names = ["size", "pos", "dur", "pts", "dts", "flags"];
        for (int k = 0; k < bad.Length; k++)
        {
            if (bad[k] > 0)
            {
                diffs += bad[k];
                string extra = k == 3 && ptsConst ? $" (constant {constPts})" : k == 4 && dtsConst ? $" (constant {constDts})" : string.Empty;
                notes.Append(CultureInfo.InvariantCulture, $" track{t.Index}:{names[k]}x{bad[k]}{extra}");
            }
        }
    }

    // OneAudio.
    int audioPackets = 0, audioDiffs = 0;
    if (audio)
    {
        try
        {
            using FileStream fs = File.OpenRead(file);
            using Mp4PacketSource oa = Mp4PacketSource.Open(fs, null, leaveOpen: true);
            foreach (Mp4TrackInfo ti in oa.Tracks)
            {
                if (ti.Codec is null)
                {
                    continue;
                }

                MediaTrack? t = tracks.FirstOrDefault(x => x.Id == ti.TrackId);
                if (t is null)
                {
                    audioDiffs++;
                    notes.Append($" oneaudio-track{ti.TrackId}:missing");
                    continue;
                }

                oa.SelectTrack(ti.Index);
                List<Pkt> mine = ours[t.Index];
                int idx = 0;
                int bytesBad = 0, tsBad = 0, durBad = 0;
                while (oa.TryReadPacket(out OAPacketView pv))
                {
                    if (idx >= mine.Count)
                    {
                        idx++;
                        break;
                    }

                    Pkt m = mine[idx++];
                    if (!pv.Data.SequenceEqual(m.Data)) { bytesBad++; }
                    if (pv.Timestamp != m.Dts) { tsBad++; }
                    if (pv.Duration != m.Duration) { durBad++; }
                    audioPackets++;
                }

                long ourPrimingFrames = t.Audio is { SampleRate: > 0 } a ? t.Edit.Priming.Rescale(a.SampleRate, MediaRounding.NearestAwayFromZero).Ticks : 0;
                bool countBad = idx != mine.Count;
                bool primingBad = ourPrimingFrames != oa.PrimingFrames;
                audioDiffs += bytesBad + tsBad + durBad + (countBad ? 1 : 0) + (primingBad ? 1 : 0);
                if (bytesBad + tsBad + durBad > 0 || countBad || primingBad)
                {
                    notes.Append($" oneaudio-track{ti.TrackId}:bytes{bytesBad} ts{tsBad} dur{durBad} count{(countBad ? $"({idx} vs {mine.Count})" : "ok")} priming({ourPrimingFrames} vs {oa.PrimingFrames}){(primingBad ? "!" : "ok")}");
                }
            }
        }
        catch (Exception e)
        {
            notes.Append($" oneaudio-threw:{e.GetType().Name}");
        }
    }

    // MP4Box -info: track ids, timescales and sample counts.
    if (mp4box is not null)
    {
        string info = Run(mp4box, "-info", file);
        bool fragmented = info.Contains("Fragmented: yes", StringComparison.Ordinal);
        List<(long Id, long Scale, long Samples)> theirTracks = [];
        long id = 0, scale = 0;
        foreach (string raw in info.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("# Track ", StringComparison.Ordinal) && line.Contains(" - ID ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ');
                id = long.Parse(parts[parts.ToList().IndexOf("ID") + 1], CultureInfo.InvariantCulture);
                scale = long.Parse(parts[^1], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("Media Samples: ", StringComparison.Ordinal))
            {
                string num = new(line["Media Samples: ".Length..].TakeWhile(char.IsDigit).ToArray());
                theirTracks.Add((id, scale, long.Parse(num, CultureInfo.InvariantCulture)));
            }
            else if (line.StartsWith("Media Type: ", StringComparison.Ordinal) && theirTracks.All(x => x.Id != id))
            {
                theirTracks.Add((id, scale, 0));
            }
        }

        bool same = theirTracks.Count == tracks.Count;
        for (int k = 0; same && k < tracks.Count; k++)
        {
            same &= theirTracks[k].Id == tracks[k].Id && theirTracks[k].Scale == tracks[k].Timescale && (fragmented || theirTracks[k].Samples == ours[k].Count);
        }

        notes.Append(same ? " mp4box:agree" : " mp4box:DIFFER");
        if (!same)
        {
            diffs++;
            notes.Append(" [" + string.Join(';', theirTracks) + "]");
        }
    }

    string defectText = defects.Count > 0 ? $" defects={defects.Count}" : string.Empty;
    Console.WriteLine($"{Path.GetFileName(file)}: tracks={tracks.Count} packets={total} diffs={diffs} audio={audioPackets}/{audioDiffs}{defectText}{notes}");
    return (total, diffs, audioPackets, audioDiffs);
}

// Writes a sparse MP4 whose media data is a run of zeros: only the structure is real, so the file can be gigabytes on a filesystem with sparse support.
static int MakeBig(string path, double gigabytes, string layout)
{
    int samples = (int)(gigabytes * 1024 * 1024 * 1024 / 1100);
    Mp4Builder.TrackSpec t = new()
    {
        Id = 1,
        Handler = "soun",
        Timescale = 44100,
        SampleCount = samples,
        SizeOf = i => 1000 + (i % 201),
        DurationOf = _ => 1024,
        SamplesPerChunk = 50,
        Co64 = gigabytes >= 3.9,
    };
    Mp4Builder.Layout l = Mp4Builder.Build([t], layout == "faststart");
    using FileStream fs = File.Create(path);
    fs.Write(l.Head);
    fs.SetLength(l.Head.Length + l.MdatPayloadSize + l.Tail.Length);
    fs.Seek(l.Head.Length + l.MdatPayloadSize, SeekOrigin.Begin);
    fs.Write(l.Tail);
    Console.WriteLine($"{path}: {fs.Length} bytes, {samples} samples, head {l.Head.Length}, tail {l.Tail.Length}");
    return 0;
}

static string Run(string exe, params string[] arguments)
{
    ProcessStartInfo psi = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in arguments)
    {
        psi.ArgumentList.Add(a);
    }

    using Process p = Process.Start(psi)!;
    // MP4Box -info writes to standard error; ffprobe only writes there when something is wrong.
    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return output;
}

internal sealed record Pkt(long Pts, long Dts, long Duration, int Size, long Pos, bool Key, bool Discard, byte[] Data);
