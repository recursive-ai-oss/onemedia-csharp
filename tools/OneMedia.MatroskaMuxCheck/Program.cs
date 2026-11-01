// Copy-muxes a file into Matroska or WebM with MatroskaMuxer and checks the result: ffprobe (a black box) must list the same packets
// (size, payload hash, time, duration, key flag) as our reader found in the input. For a Matroska input, ffprobe on the input and on
// the output are also compared field by field. Usage:
//   matroskamuxcheck check <in> <out> --ffprobe <path> [--webm] [--live] [--crc] [--no-cues] [--no-probe] [--keep-tracks]
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Matroska;

if (args.Length == 2 && args[0] == "describe")
{
    using MediaFile f = MediaFile.Open(FileMediaSource.Open(args[1]));
    foreach (MediaTrack t in f.Tracks)
    {
        RecursiveAI.OneMedia.Codecs.CodecParameters? d = t.DescribeCodec();
        Console.WriteLine($"track {t.Index} {t.Kind} {t.Codec} tag={t.CodecTag} cfg={t.Configuration.Data.Length}/{t.Configuration.Format} ts={t.Timescale} audio={t.Audio} prime={t.Edit.Priming} describe={(d is null ? "null" : $"core={d.CoreSampleRate} rate={d.SampleRate} sbr={d.SbrPresent} dbl={d.ContainerDeclaresDoubleRate} spf={d.SamplesPerFrame}")}");
    }

    return 0;
}

if (args.Length < 3 || args[0] != "check")
{
    Console.Error.WriteLine("usage: matroskamuxcheck check <in> <out> --ffprobe <path> [--webm] [--live] [--crc] [--no-cues] [--no-probe]");
    return 2;
}

string input = args[1];
string output = args[2];
string ffprobe = "ffprobe";
bool webm = false, live = false, crc = false, cues = true, probe = true;
for (int i = 3; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ffprobe": ffprobe = args[++i]; break;
        case "--webm": webm = true; break;
        case "--live": live = true; break;
        case "--crc": crc = true; break;
        case "--no-cues": cues = false; break;
        case "--no-probe": probe = false; break;
        default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
    }
}

MatroskaMuxOptions options = new()
{
    Profile = webm ? MatroskaProfile.WebM : MatroskaProfile.Matroska,
    Live = live,
    Crc32 = crc,
    WriteCues = cues,
    SegmentUuid = Enumerable.Range(1, 16).Select(x => (byte)x).ToArray(),
};

// ---- Copy ----------------------------------------------------------------------------------------------------------------------
MediaTime containerDuration = MediaTime.Unknown;
List<InPacket>[] expected;
List<string> dropped = [];
long packets = 0;
long cuePoints = 0;
Stopwatch clock = Stopwatch.StartNew();
MediaTrack[] keptTracks;
bool[] inFlag;
MediaFile? opened = null;
try
{
    opened = MediaFile.Open(FileMediaSource.Open(input));
}
catch (MediaFormatException e)
{
    Console.WriteLine($"SKIP {Path.GetFileName(input)}: cannot be read as a stand-alone file ({e.Message})");
    return 0;
}

using (MediaFile file = opened)
{
    IDemuxer demux = file.Demuxer ?? throw new InvalidOperationException("needs a synchronous demuxer");
    containerDuration = file.ContainerDuration;
    List<MediaTrack> kept = [];
    int[] map = new int[file.Tracks.Count];
    for (int t = 0; t < file.Tracks.Count; t++)
    {
        map[t] = -1;
        try
        {
            using MemoryStream probeOut = new();
            _ = new MatroskaMuxer(probeOut, [file.Tracks[t]], options with { Live = true });
            map[t] = kept.Count;
            kept.Add(file.Tracks[t]);
        }
        catch (MuxRefusedException e)
        {
            dropped.Add($"track {t} {file.Tracks[t].Codec}: {e.Reason}");
            demux.SetTrackEnabled(t, false);
        }
    }

    if (kept.Count == 0)
    {
        Console.WriteLine($"SKIP {Path.GetFileName(input)}: no track can be written ({string.Join("; ", dropped)})");
        return 0;
    }

    keptTracks = [.. kept];
    inFlag = [.. kept.Select(k => k.Edit.PacketTimesIncludePriming)];
    expected = [.. kept.Select(_ => new List<InPacket>())];
    using FileStream fs = new(output, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
    Stream sink = live ? new NonSeekable(fs) : fs;
    MatroskaMuxer mux;
    try
    {
        mux = new MatroskaMuxer(sink, keptTracks, options with { Duration = containerDuration });
    }
    catch (MuxRefusedException e)
    {
        Console.WriteLine($"SKIP {Path.GetFileName(input)}: {e.Reason}: {e.Message}");
        return 0;
    }

    while (demux.TryReadPacket(out PacketView p))
    {
        PacketInfo info = p.Info;
        int m = map[info.TrackIndex];
        if (m < 0)
        {
            continue;
        }

        expected[m].Add(new InPacket(info.Pts, info.Duration, info.IsKeyframe, (info.Flags & PacketAttributes.Discard) != 0, p.Data.Length, Adler(p.Data), info.Dts));
        mux.WritePacket(info with { TrackIndex = m }, p.Data);
        packets++;
    }

    mux.Finish();
    cuePoints = mux.CuePointCount;
}

if (packets == 0)
{
    Console.WriteLine($"SKIP {Path.GetFileName(input)}: the file holds no packets (an initialisation segment)");
    return 0;
}

long rss = Process.GetCurrentProcess().PeakWorkingSet64;
Console.WriteLine($"{Path.GetFileName(input)}: muxed {packets} packets, {keptTracks.Length} tracks, {new FileInfo(output).Length} bytes, {cuePoints} cues, {clock.Elapsed.TotalSeconds:F1}s, peakRss={rss >> 20} MiB{(dropped.Count > 0 ? ", dropped: " + string.Join("; ", dropped) : string.Empty)}");
if (!probe)
{
    return 0;
}

// ---- Compare with ffprobe ------------------------------------------------------------------------------------------------------
List<ProbePacket>[] got = Probe(ffprobe, output, keptTracks.Length);
int[] outDelayUnits = new int[keptTracks.Length];
long[] outScale = new long[keptTracks.Length];
Dictionary<string, int> diffs = [];
Dictionary<string, int> explained = [];
StringBuilder examples = new();
long compared = 0;
using (MediaFile again = MediaFile.Open(FileMediaSource.Open(output)))
{
    for (int t = 0; t < keptTracks.Length; t++)
    {
        MediaTime pr = again.Tracks[t].Edit.Priming;
        outScale[t] = again.Tracks[t].Timescale;
        outDelayUnits[t] = pr.IsKnown ? (int)pr.Rescale(outScale[t], MediaRounding.NearestAwayFromZero).Ticks : 0;
    }

    // Our own reader on the output: the packets, flags included, are the ones that went in.
    int[] seen = new int[keptTracks.Length];
    IDemuxer rd = again.Demuxer!;
    while (rd.TryReadPacket(out PacketView p))
    {
        int t = p.Info.TrackIndex;
        if (seen[t] >= expected[t].Count) { Add(diffs, "reader: more packets than went in"); break; }
        InPacket w = expected[t][seen[t]++];
        compared++;
        if (p.Data.Length != w.Size || Adler(p.Data) != w.Hash) { Note(diffs, examples, "reader-payload", t, seen[t], w.Size, p.Data.Length); }
        if (p.Info.IsKeyframe != w.Key) { Note(diffs, examples, "reader-key", t, seen[t], w.Key, p.Info.IsKeyframe); }
        if (((p.Info.Flags & PacketAttributes.Discard) != 0) != w.Discard) { Note(diffs, examples, "reader-discard", t, seen[t], w.Discard, p.Info.Flags); }
    }

    for (int t = 0; t < keptTracks.Length; t++)
    {
        if (seen[t] != expected[t].Count) { Add(diffs, $"reader: track {t} has {seen[t]} of {expected[t].Count} packets"); }
    }
}

// Frames of one laced block that state no duration share a timestamp in our reader and in the file; ffprobe spaces them by the codec's
// frame length, so their times cannot be compared (RFC 9559 §10.3: a lace has one timestamp).
// The muxer completes a packet that states no duration with the distance to the next decode time of its track (the mux contract).
static bool DerivedFromNextDts(List<InPacket> list, int k, long scale, long outDuration)
{
    if (list[k].Duration.IsKnown || k + 1 >= list.Count) { return false; }
    MediaTime a = list[k].Dts.IsKnown ? list[k].Dts : list[k].Pts, b = list[k + 1].Dts.IsKnown ? list[k + 1].Dts : list[k + 1].Pts;
    if (!a.IsKnown || !b.IsKnown) { return false; }
    long gap = b.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks - a.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks;
    return gap > 0 && Math.Abs(gap - outDuration) <= 1;
}

bool[][] laceShared = new bool[keptTracks.Length][];
for (int t = 0; t < keptTracks.Length; t++)
{
    List<InPacket> w = expected[t];
    laceShared[t] = new bool[w.Count];
    for (int k = 0; k < w.Count; k++)
    {
        laceShared[t][k] = !w[k].Duration.IsKnown && ((k > 0 && w[k].Pts == w[k - 1].Pts) || (k + 1 < w.Count && w[k].Pts == w[k + 1].Pts));
    }
}

// Matroska in, Matroska out: everything ffprobe says about the input (decode times included) must survive, and where it does not
// survive the reason has to be one of the explained ones.
HashSet<(int, int, string)> sameAsInput = [];
int[]? beforeCounts = null;
if (IsMatroska(input) && dropped.Count == 0)
{
    List<ProbePacket>[] before = Probe(ffprobe, input, keptTracks.Length);
    beforeCounts = [.. before.Select(l => l.Count)];
    (long Num, long Den)[] tbIn = TimeBases(ffprobe, input, keptTracks.Length);
    (long Num, long Den)[] tbOut = TimeBases(ffprobe, output, keptTracks.Length);
    for (int t = 0; t < keptTracks.Length; t++)
    {
        if (before[t].Count != got[t].Count) { Add(diffs, $"ffprobe-vs-ffprobe count track {t}"); continue; }
        if (before[t].Count != expected[t].Count) { Add(explained, "ffprobe-splits-blocks"); }
        for (int k = 0; k < before[t].Count; k++)
        {
            ProbePacket a = before[t][k], b = got[t][k];
            compared++;
            if (a.Size != b.Size || a.Hash != b.Hash) { Note(diffs, examples, "ff-size/hash", t, k, a.Hash, b.Hash); }
            bool pts = SameTime(a.Pts, b.Pts, tbIn[t], tbOut[t]);
            bool dts = SameTime(a.Dts, b.Dts, tbIn[t], tbOut[t]);
            bool dur = SameTime(a.Duration, b.Duration, tbIn[t], tbOut[t]);
            if (pts && dts) { sameAsInput.Add((t, k, "time")); }
            if (dur) { sameAsInput.Add((t, k, "duration")); }
            bool rebased = tbIn[t] != tbOut[t];
            if (k < laceShared[t].Length && laceShared[t][k] && (!pts || !dts)) { Add(explained, "lace-shared-pts"); }
            else if (!pts && a.Pts == long.MinValue) { Add(explained, "pts-not-stated-by-ffprobe-for-the-input"); }
            else if (!dts && a.Dts == long.MinValue) { Add(explained, "dts-not-stated-by-ffprobe-for-the-input"); }
            else if (!pts) { Note(diffs, examples, "ff-pts", t, k, $"{a.Pts}@{tbIn[t].Num}/{tbIn[t].Den}", $"{b.Pts}@{tbOut[t].Num}/{tbOut[t].Den}"); }
            else if (!dts) { Note(diffs, examples, "ff-dts", t, k, a.Dts, b.Dts); }
            else if (rebased && (a.Pts != b.Pts || a.Dts != b.Dts)) { Add(explained, "rounded-to-the-output-time-base"); }
            if (!dur)
            {
                if (k < laceShared[t].Length && laceShared[t][k]) { Add(explained, "lace-shared-pts"); }
                else if (DerivedFromNextDts(expected[t], k, outScale[t], b.Duration)) { Add(explained, "duration-derived-from-next-dts"); }
                else if (a.Duration == long.MinValue) { Add(explained, "duration-from-default-duration"); }
                else { Note(diffs, examples, "ff-duration", t, k, a.Duration, b.Duration); }
            }
            else if (rebased && a.Duration != b.Duration) { Add(explained, "rounded-to-the-output-time-base"); }

            if (a.Flags != b.Flags) { Note(diffs, examples, "ff-flags", t, k, a.Flags, b.Flags); }
        }
    }
}

List<ProbePacket>[]? inputProbe = null;
for (int t = 0; t < keptTracks.Length; t++)
{
    List<InPacket> want = expected[t];
    List<ProbePacket> have = got[t];
    if (want.Count != have.Count)
    {
        // ffprobe splits some packed audio (RealAudio) into more packets than the file has blocks; then it does the same on the input.
        inputProbe ??= dropped.Count == 0 && beforeCounts is null ? Probe(ffprobe, input, keptTracks.Length) : null;
        if (beforeCounts is not null && beforeCounts[t] == have.Count) { Add(explained, "ffprobe-splits-blocks-of-the-input-too"); }
        else if (inputProbe is not null && inputProbe[t].Count == have.Count) { Add(explained, "ffprobe-reframes-the-input-the-same-way"); }
        else { Add(diffs, $"count track {t}: input {want.Count}, ffprobe {have.Count}"); }
        continue;
    }

    for (int k = 0; k < want.Count; k++)
    {
        InPacket w = want[k];
        ProbePacket h = have[k];
        compared++;
        long pts = w.Pts.Rescale(outScale[t], MediaRounding.NearestAwayFromZero).Ticks - (inFlag[t] ? 0 : outDelayUnits[t]);
        if (w.Size != h.Size) { Note(diffs, examples, "size", t, k, w.Size, h.Size); }
        if (h.Hash != w.Hash) { Note(diffs, examples, "hash", t, k, w.Hash, h.Hash); }
        if (h.Pts != pts)
        {
            if (laceShared[t][k]) { Add(explained, "lace-shared-pts"); }
            else if (sameAsInput.Contains((t, k, "time"))) { Add(explained, "pts-as-ffprobe-gives-for-the-input"); }
            else { Note(diffs, examples, "pts", t, k, pts, h.Pts); }
        }

        if (w.Duration.IsKnown)
        {
            long dur = w.Duration.Rescale(outScale[t], MediaRounding.NearestAwayFromZero).Ticks;
            if (h.Duration != dur && dur == 0 && h.Duration > 0 && keptTracks[t].Kind is TrackKind.Video or TrackKind.Audio)
            {
                Add(explained, "zero-duration-treated-as-unknown");
            }
            else if (h.Duration != dur)
            {
                if (sameAsInput.Contains((t, k, "duration"))) { Add(explained, "duration-as-ffprobe-gives-for-the-input"); }
                else if (Math.Abs(h.Duration - dur) <= 1) { Add(explained, "duration-truncated-from-default-duration-by-ffprobe"); }
                else if (k == want.Count - 1 && h.Duration > dur) { Add(explained, "ffprobe-gives-the-default-duration-for-a-shorter-last-block"); }
                else { Note(diffs, examples, "duration", t, k, dur, h.Duration); }
            }
        }

        if (w.Key != h.Flags.Contains('K', StringComparison.Ordinal))
        {
            // ffmpeg's parsers mark some key frames of the bitstream itself (MPEG-4 part 2, for one); then it does the same for the input.
            inputProbe ??= dropped.Count == 0 ? Probe(ffprobe, input, keptTracks.Length) : null;
            if (inputProbe is not null && k < inputProbe[t].Count && inputProbe[t][k].Flags.Contains('K', StringComparison.Ordinal) == h.Flags.Contains('K', StringComparison.Ordinal)) { Add(explained, "key-as-ffprobe-gives-for-the-input"); }
            else { Note(diffs, examples, "key", t, k, w.Key, h.Flags); }
        }

        // ffprobe does not map a block's invisible flag to its discard flag, so only a discard it shows that we did not send is a difference.
        if (!w.Discard && h.Flags.Contains('D', StringComparison.Ordinal)) { Note(diffs, examples, "discard", t, k, w.Discard, h.Flags); }
    }
}

if (live)
{
    // Live output read back with the forward-only reader: same packets as the seekable reader finds in the same file.
    long fwd = 0;
    using FileStream rs = File.OpenRead(output);
    using MediaFile f1 = MediaFile.Open(new StreamForwardSource(rs));
    using MediaFile f2 = MediaFile.Open(FileMediaSource.Open(output));
    IDemuxer d1 = f1.Demuxer!, d2 = f2.Demuxer!;
    while (true)
    {
        bool a = d1.TryReadPacket(out PacketView p1);
        PacketInfo i1 = p1.Info;
        uint h1 = a ? Adler(p1.Data) : 0;
        bool b = d2.TryReadPacket(out PacketView p2);
        if (a != b) { Add(diffs, "forward-only reader and seekable reader disagree on the packet count"); break; }
        if (!a) { break; }
        fwd++;
        PacketInfo i2 = p2.Info;
        if (i1.TrackIndex != i2.TrackIndex || i1.Pts != i2.Pts || i1.Duration != i2.Duration || i1.Flags != i2.Flags || h1 != Adler(p2.Data)) { Add(diffs, "forward-only reader differs from the seekable reader"); }
    }

    Console.WriteLine($"  forward-only reader read {fwd} packets");
}

Console.WriteLine($"  compared {compared} packet fields-sets against ffprobe; differences: {(diffs.Count == 0 ? "none" : string.Join(", ", diffs.Select(d => $"{d.Key} x{d.Value}")))}{(explained.Count == 0 ? string.Empty : "; explained: " + string.Join(", ", explained.Select(d => $"{d.Key} x{d.Value}")))}");
if (examples.Length > 0)
{
    Console.WriteLine(examples.ToString().TrimEnd());
}

return diffs.Count == 0 ? 0 : 1;

static void Add(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

static void Note<TA, TB>(Dictionary<string, int> d, StringBuilder examples, string kind, int track, int index, TA want, TB got)
{
    Add(d, kind);
    if (d[kind] <= 3)
    {
        examples.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    {kind}: track {track} packet {index}: expected {want}, ffprobe {got}"));
    }
}

static bool IsMatroska(string path)
{
    Span<byte> head = stackalloc byte[4];
    using FileStream s = File.OpenRead(path);
    return s.Read(head) == 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
}

static uint Adler(ReadOnlySpan<byte> data)
{
    uint a = 1, b = 0;
    int i = 0;
    while (i < data.Length)
    {
        int end = Math.Min(data.Length, i + 5552);
        for (; i < end; i++) { a += data[i]; b += a; }
        a %= 65521;
        b %= 65521;
    }

    return (b << 16) | a;
}

static List<ProbePacket>[] Probe(string ffprobe, string path, int streams, bool onlyKept = true)
{
    _ = onlyKept;
    ProcessStartInfo psi = new(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in new[] { "-v", "error", "-show_packets", "-show_entries", "packet=stream_index,pts,dts,duration,size,flags,data_hash", "-show_data_hash", "adler32", "-of", "compact=p=0", path })
    {
        psi.ArgumentList.Add(a);
    }

    using Process p = Process.Start(psi)!;
    Task<string> err = p.StandardError.ReadToEndAsync();
    List<ProbePacket>[] result = [.. Enumerable.Range(0, streams).Select(_ => new List<ProbePacket>())];
    string? line;
    while ((line = p.StandardOutput.ReadLine()) is not null)
    {
        Dictionary<string, string> f = [];
        foreach (string part in line.Split('|'))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) { f[part[..eq]] = part[(eq + 1)..]; }
        }

        if (!f.TryGetValue("stream_index", out string? si))
        {
            continue;
        }

        int s = int.Parse(si, CultureInfo.InvariantCulture);
        if (s >= streams)
        {
            continue;
        }

        result[s].Add(new ProbePacket(Num(f, "pts"), Num(f, "dts"), Num(f, "duration"), (int)Num(f, "size"), f.GetValueOrDefault("flags", string.Empty), Convert.ToUInt32(f.GetValueOrDefault("data_hash", "adler32:0")[8..], 16)));
    }

    p.WaitForExit();
    string e = err.Result;
    if (e.Length > 0) { Console.WriteLine("  ffprobe stderr: " + e.Trim().Split('\n')[0]); }
    return result;
}

// Equal times, allowing a rounding to the other file's time base when the two differ (half an output unit).
static bool SameTime(long a, long b, (long Num, long Den) tbA, (long Num, long Den) tbB)
{
    if (a == long.MinValue || b == long.MinValue) { return a == b; }
    if (tbA == tbB) { return a == b; }
    Int128 nsA = (Int128)a * tbA.Num * 1_000_000_000 / tbA.Den;
    Int128 nsB = (Int128)b * tbB.Num * 1_000_000_000 / tbB.Den;
    // Half an output unit, plus the 1e-4 by which ffprobe's integer time base can miss a TimestampScale that does not divide a second.
    Int128 half = (Int128)tbB.Num * 1_000_000_000 / tbB.Den / 2 + 1 + (nsA < 0 ? -nsA : nsA) / 10_000;
    Int128 d = nsA - nsB;
    return (d < 0 ? -d : d) <= half;
}

static (long Num, long Den)[] TimeBases(string ffprobe, string path, int streams)
{
    ProcessStartInfo psi = new(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in new[] { "-v", "error", "-show_entries", "stream=index,time_base", "-of", "compact=p=0", path }) { psi.ArgumentList.Add(a); }
    using Process p = Process.Start(psi)!;
    string text = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    (long, long)[] result = new (long, long)[streams];
    foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        int index = -1;
        string? tb = null;
        foreach (string part in line.Split('|'))
        {
            if (part.StartsWith("index=", StringComparison.Ordinal)) { index = int.Parse(part[6..], CultureInfo.InvariantCulture); }
            else if (part.StartsWith("time_base=", StringComparison.Ordinal)) { tb = part[10..]; }
        }

        if (index >= 0 && index < streams && tb is not null)
        {
            string[] frac = tb.Split('/');
            result[index] = (long.Parse(frac[0], CultureInfo.InvariantCulture), long.Parse(frac[1], CultureInfo.InvariantCulture));
        }
    }

    return result;
}

static long Num(Dictionary<string, string> f, string key) => f.TryGetValue(key, out string? v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : long.MinValue;

internal sealed record InPacket(MediaTime Pts, MediaTime Duration, bool Key, bool Discard, int Size, uint Hash, MediaTime Dts);

internal sealed record ProbePacket(long Pts, long Dts, long Duration, int Size, string Flags, uint Hash);

// A stream that refuses to seek or report a length, as a pipe would.
internal sealed class NonSeekable(Stream inner) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
}
