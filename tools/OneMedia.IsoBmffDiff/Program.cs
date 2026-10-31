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

if (args.Length >= 2 && args[0] == "--io")
{
    return ProbeIo(args[1]);
}

if (args.Length >= 2 && args[0] == "--stream")
{
    return StreamFile(args[1]);
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
    catch (MediaFormatException e) when (e.Kind == DefectKind.MissingRequired)
    {
        Console.WriteLine($"{Path.GetFileName(file)}: skipped, no movie box (a media segment needs its initialization segment)");
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
    bool isFragmented;
    long[] tableCounts;
    using (FileMediaSource src = FileMediaSource.Open(file))
    using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(src, new MediaOpenOptions { LeaveOpen = true }))
    {
        tracks = d.Tracks;
        defects = d.Defects;
        isFragmented = d.Movie.IsFragmented;
        tableCounts = d.Movie.Tracks.Select(x => x.SampleCount).ToArray();
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
        int nonEmptyEdits = (t.Edit.Edits ?? []).Count(e => !e.IsEmpty);
        if (nonEmptyEdits >= 2 || (nonEmptyEdits == 1 && mine.Count != other.Count))
        {
            // ffprobe applies the edit list to the sample index: it drops samples outside the segments, repeats samples that several segments use and
            // cuts packets at segment boundaries, so its packet list cannot be lined up with the table. We expose the edits in TrackEditInfo instead.
            // Check that every packet it lists lies inside bytes we list contiguously (our packets merged where one ends where the next begins).
            List<(long Start, long End)> merged = [];
            foreach (Pkt x in mine.OrderBy(x => x.Pos))
            {
                if (merged.Count > 0 && merged[^1].End >= x.Pos)
                {
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, x.Pos + x.Size));
                }
                else
                {
                    merged.Add((x.Pos, x.Pos + x.Size));
                }
            }

            int unknown = 0;
            foreach (string[] x in other)
            {
                long pos = long.Parse(x[5], CultureInfo.InvariantCulture), size = long.Parse(x[4], CultureInfo.InvariantCulture);
                int lo = merged.BinarySearch((pos, 0), Comparer<(long Start, long End)>.Create((l, r) => l.Start.CompareTo(r.Start)));
                int at = lo >= 0 ? lo : ~lo - 1;
                if (at < 0 || merged[at].End < pos + size)
                {
                    unknown++;
                }
            }

            notes.Append($" track{t.Index}:edit-list-applied-by-ffprobe({mine.Count} vs {other.Count}, {unknown} of its packets outside ours)");
            diffs += unknown;
            continue;
        }

        if (mine.Count != other.Count)
        {
            // A file cut off inside its media data: ffprobe still lists the sample that straddles the cut, we stop before it (typed Truncated defect).
            bool cutOff = mine.Count < other.Count && defects.Any(x => x.Kind == DefectKind.Truncated);
            notes.Append(cutOff ? $" track{t.Index}:samples-past-eof-not-returned({mine.Count} vs {other.Count})" : $" track{t.Index}:count {mine.Count} vs {other.Count}");
            diffs += cutOff ? 0 : 1;
        }

        int n = Math.Min(mine.Count, other.Count);
        int[] bad = new int[6]; // size pos dur pts dts flags
        string?[] first = new string?[6];
        int naCount = 0, naLast = -1;
        long? constPts = null, constDts = null;
        int gapDurations = 0, hybridSkipped = 0, zeroStated = 0, dupDiscard = 0;
        long? ratioK = null;
        bool ratioOk = true;
        string modalDuration = other.GroupBy(x => x[3]).OrderByDescending(g => g.Count()).First().Key;
        bool ptsConst = true, dtsConst = true;
        for (int i = 0; i < n; i++)
        {
            Pkt m = mine[i];
            string[] o = other[i];
            total++;
            bool zeroDur = isFragmented && m.Duration == 0 && long.TryParse(o[3], out long zd) && zd > 0;
            bool edgeDiscard = !m.Discard && o[6].Contains('D', StringComparison.Ordinal) && o[6].Contains('K', StringComparison.Ordinal) == m.Key && OutsideOrStraddlesEdit(t, m);
            if (!long.TryParse(o[4], out long osize) || osize != m.Size) { bad[0]++; first[0] ??= $"@{i} {m.Size}->{o[4]}"; }
            if (!long.TryParse(o[5], out long opos) || opos != m.Pos) { bad[1]++; first[1] ??= $"@{i} {m.Pos}->{o[5]}"; }
            if (o[3] == "N/A")
            {
                // ffprobe leaves the duration of the first fragment's samples unset when it comes from a default in the track fragment header; the box states it.
                naCount++;
                naLast = i;
            }
            else if (isFragmented && i == 0 && t.Kind == TrackKind.Audio && long.TryParse(o[3], out long firstDur) && firstDur != m.Duration)
            {
                // The first audio sample of these fragmented files states a longer duration in its run than the codec frame.
                notes.Append($" track{t.Index}:first-fragment-duration({m.Duration}->{o[3]})");
            }
            else if (zeroDur)
            {
                // The fragment states a duration of 0 (a muxer that did not know the next timestamp yet); ffprobe works one out from the following sample.
                zeroStated++;
            }
            else if (!long.TryParse(o[3], out long odur) || odur != m.Duration)
            {
                if (i == n - 1)
                {
                    // ffprobe replaces the last packet's duration (from the edit list end or the audio parser); the table value is what the file stores.
                    notes.Append($" track{t.Index}:last-duration({m.Duration}->{o[3]})");
                }
                else if (o[3] == modalDuration && m.Duration > odur)
                {
                    // The table states a longer delta (a gap in the decode times); ffprobe keeps reporting the usual frame duration while the next DTS still moves by the delta.
                    gapDurations++;
                }
                else
                {
                    bad[2]++;
                    first[2] ??= $"@{i} {m.Duration}->{o[3]}";
                    long k = m.Duration > 0 && long.TryParse(o[3], out long od2) && od2 % m.Duration == 0 ? od2 / m.Duration : -1;
                    ratioK ??= k;
                    ratioOk &= k == ratioK;
                }
            }
            if (edgeDiscard)
            {
                // The sample straddles the first edit's start; ffprobe moves its timestamps and flags it, we report the table values.
            }
            else if (isFragmented && i < tableCounts[t.Index])
            {
                // A file with samples in the movie box and in fragments: ffprobe places the movie box's samples on the edited timeline in its own way.
                hybridSkipped++;
            }
            else
            {
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
            }
            string fl = o[6];
            bool discardOnlyOurs = isFragmented && m.Discard && !fl.Contains('D', StringComparison.Ordinal) && fl.Contains('K', StringComparison.Ordinal) == m.Key;
            if (discardOnlyOurs)
            {
                // ffprobe applies the edit list's discard only to samples of the movie box's own table, not to fragment samples.
                notes.Append($" track{t.Index}:fragment-discard-flag-only-ours@{i}");
            }
            else if (!m.Key && fl.Contains('K', StringComparison.Ordinal) && !m.Discard && t.Kind is TrackKind.Data or TrackKind.Unknown)
            {
                // ffprobe marks every packet of a data track (RTP hint, MPEG-4 systems) as a key frame; the sync sample table of the track is what we report.
                notes.Append($" track{t.Index}:data-track-keyframe@{i}");
            }
            else if ((zeroDur || (i > 0 && other[i - 1][2] == o[2])) && fl.Contains('D', StringComparison.Ordinal) && fl.Contains('K', StringComparison.Ordinal) == m.Key)
            {
                // ffprobe flags a packet as discardable when its own timeline repeats a decode time (it guesses durations the box does not state, so
                // two packets can land on the same time); the box states no such thing and our times are exact.
                dupDiscard++;
            }
            else if (edgeDiscard)
            {
                // ffprobe flags a sample as discarded when it starts before the first edit or lies outside every edit segment; we flag only samples that end before it.
                notes.Append($" track{t.Index}:edit-discard-flag-only-theirs@{i}");
            }
            else if (!m.Key && fl.Contains('K', StringComparison.Ordinal) && !m.Discard && t.Kind == TrackKind.Video)
            {
                // ffprobe marks a sample as a keyframe when the H.264/HEVC parser finds an IDR picture, even if the sync sample table omits it.
                notes.Append($" track{t.Index}:parser-keyframe@{i}");
            }
            else if (fl.Contains('K', StringComparison.Ordinal) != m.Key || fl.Contains('D', StringComparison.Ordinal) != m.Discard) { bad[5]++; first[5] ??= $"@{i} K{(m.Key ? 1 : 0)}D{(m.Discard ? 1 : 0)}->{fl}"; }
        }

        if (dupDiscard > 0)
        {
            notes.Append($" track{t.Index}:discard-flag-on-repeated-time-only-theirs x{dupDiscard}");
        }

        if (zeroStated > 0)
        {
            notes.Append($" track{t.Index}:zero-duration-stated x{zeroStated}");
        }

        if (hybridSkipped > 0)
        {
            notes.Append($" track{t.Index}:movie-box-samples-timestamps-not-compared x{hybridSkipped}");
        }

        if (gapDurations > 0)
        {
            notes.Append($" track{t.Index}:stts-gap-duration x{gapDurations}");
        }

        if (naCount > 0)
        {
            notes.Append($" track{t.Index}:ffprobe-N/A-duration x{naCount} (last at index {naLast})");
        }

        if (bad[2] > 0 && ratioOk && ratioK > 1 && t.Kind == TrackKind.Video)
        {
            // Every differing video duration is the same multiple of the table's: ffprobe takes the duration from the video parser's frame timing, not the table.
            notes.Append($" track{t.Index}:parser-duration-x{ratioK} x{bad[2]}");
            bad[2] = 0;
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

        if (negativeOffsets && ptsConst && dtsConst && bad[3] > 0 && bad[4] > 0 && constPts - constDts == mine.Max(x => x.Dts - x.Pts) && bad[3] == bad[4])
        {
            // Both of the above at once: a constant shift of every timestamp (ffprobe aligns the first sample with the edit list) and the negative-offset shift of DTS.
            notes.Append($" track{t.Index}:negative-cts-and-edit-shift(pts {constPts}, dts {constDts})");
            bad[3] = bad[4] = 0;
        }

        string[] names = ["size", "pos", "dur", "pts", "dts", "flags"];
        for (int k = 0; k < bad.Length; k++)
        {
            if (bad[k] > 0)
            {
                diffs += bad[k];
                string extra = k == 3 && ptsConst ? $" (constant {constPts})" : k == 4 && dtsConst ? $" (constant {constDts})" : string.Empty;
                notes.Append(CultureInfo.InvariantCulture, $" track{t.Index}:{names[k]}x{bad[k]}{extra}{(first[k] is null ? "" : " (" + first[k] + ")")}");
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

        bool rejected = info.Contains("Error opening file", StringComparison.Ordinal) || theirTracks.Count == 0;
        bool same = theirTracks.Count == tracks.Count;
        for (int k = 0; same && k < tracks.Count; k++)
        {
            same &= theirTracks[k].Id == tracks[k].Id && theirTracks[k].Scale == tracks[k].Timescale && (fragmented || theirTracks[k].Samples == tableCounts[k]);
        }

        notes.Append(same ? " mp4box:agree" : rejected ? " mp4box:cannot-open-file" : " mp4box:DIFFER");
        if (!same && !rejected)
        {
            diffs++;
            notes.Append(" [" + string.Join(';', theirTracks) + "]");
        }
    }

    string defectText = defects.Count > 0 ? $" defects={defects.Count} [{string.Join("; ", defects.Take(2))}]" : string.Empty;
    Console.WriteLine($"{Path.GetFileName(file)}: tracks={tracks.Count} packets={total} diffs={diffs} audio={audioPackets}/{audioDiffs}{defectText}{notes}");
    return (total, diffs, audioPackets, audioDiffs);
}

// Probe = open, list tracks and duration, read no packet. Counts what the source is asked for.
static int ProbeIo(string path)
{
    CountingObserver counter = new();
    using FileMediaSource file = FileMediaSource.Open(path);
    using ObservedMediaSource observed = new(file, null, counter);
    using IsoBmffDemuxer d = IsoBmffDemuxer.Open(observed, new MediaOpenOptions { LeaveOpen = true });
    long tracks = d.Tracks.Count;
    MediaTime duration = d.Duration;
    Console.WriteLine($"{Path.GetFileName(path)}: size={file.Length} tracks={tracks} duration={duration} reads={counter.Reads} bytesReturned={counter.Bytes} uniqueBytes={counter.UniqueBytes()} largestRead={counter.Largest}");
    return 0;
}

// Full demux twice (the second after a seek to the start) reporting allocation and memory use.
static int StreamFile(string path)
{
    using FileMediaSource file = FileMediaSource.Open(path);
    using IsoBmffDemuxer d = IsoBmffDemuxer.Open(file, new MediaOpenOptions { LeaveOpen = true });
    long before = GC.GetAllocatedBytesForCurrentThread();
    long n1 = 0, bytes1 = 0;
    while (d.TryReadPacket(out PacketView v))
    {
        n1++;
        bytes1 += v.Data.Length;
    }

    long pass1 = GC.GetAllocatedBytesForCurrentThread() - before;
    d.Seek(MediaTime.Zero);
    before = GC.GetAllocatedBytesForCurrentThread();
    long n2 = 0;
    while (d.TryReadPacket(out PacketView v))
    {
        n2++;
    }

    long pass2 = GC.GetAllocatedBytesForCurrentThread() - before;
    using Process me = Process.GetCurrentProcess();
    Console.WriteLine($"{Path.GetFileName(path)}: size={file.Length} packets={n1}/{n2} payloadBytes={bytes1} allocatedFirstPass={pass1} allocatedSecondPass={pass2} (per packet {(double)pass2 / Math.Max(1, n2):F3}) peakWorkingSetMB={me.PeakWorkingSet64 >> 20} gcHeapMB={GC.GetTotalMemory(false) >> 20}");
    return 0;
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

// True when the sample starts before the first non-empty edit or lies outside every non-empty edit segment (all in the track's media timescale).
static bool OutsideOrStraddlesEdit(MediaTrack t, Pkt m)
{
    long first = -1;
    bool inside = false;
    foreach (EditSegment e in t.Edit.Edits ?? [])
    {
        if (e.IsEmpty)
        {
            continue;
        }

        long start = e.MediaStart.Ticks;
        long length = e.Duration.Rescale(t.Timescale, MediaRounding.NearestAwayFromZero).Ticks;
        first = first < 0 ? start : first;
        inside |= m.Pts < start + length && m.Pts + m.Duration > start;
    }

    return first >= 0 && (!inside || m.Pts < first);
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

internal sealed class CountingObserver : IMediaReadObserver
{
    private readonly List<(long Start, long End)> _ranges = [];

    public long Reads { get; private set; }

    public long Bytes { get; private set; }

    public long Largest { get; private set; }

    public void OnRead(long offset, int requested, int returned)
    {
        Reads++;
        Bytes += returned;
        Largest = Math.Max(Largest, returned);
        _ranges.Add((offset, offset + returned));
    }

    public long UniqueBytes()
    {
        long total = 0, end = long.MinValue;
        foreach ((long start, long stop) in _ranges.OrderBy(r => r.Start))
        {
            long from = Math.Max(start, end);
            if (stop > from)
            {
                total += stop - from;
            }

            end = Math.Max(end, stop);
        }

        return total;
    }
}

internal sealed record Pkt(long Pts, long Dts, long Duration, int Size, long Pos, bool Key, bool Discard, byte[] Data);
