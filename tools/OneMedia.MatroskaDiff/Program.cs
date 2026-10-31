// Differential and measurement driver for the Matroska reader: compares packets with ffprobe, prints packet dumps and digests,
// and measures allocations and probe I/O. It runs ffprobe as a black box and reads none of its source.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Matroska;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: matroskadiff <diff|dump|digest|digest-stdin|alloc|probe-io|structure> <file> [--ffprobe <path>] [--limit N]");
    return 2;
}

string command = args[0];
string path = args[1];
string ffprobe = "ffprobe";
int limit = int.MaxValue;
for (int i = 2; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--ffprobe") { ffprobe = args[i + 1]; }
    else if (args[i] == "--limit") { limit = int.Parse(args[i + 1], CultureInfo.InvariantCulture); }
}

switch (command)
{
    case "dump": return Dump(path, limit);
    case "digest": return Digest(path);
    case "digest-stdin": return DigestStdin();
    case "diff": return Diff(path, ffprobe);
    case "alloc": return Alloc(path);
    case "probe-io": return ProbeIo(path);
    case "structure": return Structure(path);
    case "sweep": return Sweep(path, limit == int.MaxValue ? 1 : limit);
    case "mkvtracks": return MkvTracks(path, args.Length > 3 && args[2] == "--mkvinfo" ? args[3] : "mkvinfo");
    case "mkvinfo": return MkvInfoDiff(path, args.Length > 3 && args[2] == "--mkvinfo" ? args[3] : "mkvinfo");
    default: Console.Error.WriteLine("unknown command"); return 2;
}

static List<OurPacket> ReadOurs(IDemuxer d, int limit)
{
    List<OurPacket> list = [];
    while (list.Count < limit && d.TryReadPacket(out PacketView p))
    {
        PacketInfo i = p.Info;
        list.Add(new OurPacket(i.TrackIndex, i.Pts, i.Dts, i.Duration, i.IsKeyframe, i.Flags, i.SourceRange, i.Size, Hash(p.Data), i.DiscardPadding));
    }

    return list;
}

static uint Hash(ReadOnlySpan<byte> data)
{
    uint h = 2166136261;
    foreach (byte b in data) { h = (h ^ b) * 16777619; }
    return h;
}

static int Dump(string path, int limit)
{
    using MatroskaDemuxer d = MatroskaDemuxer.Open(FileMediaSource.Open(path));
    foreach (MediaTrack t in d.Tracks)
    {
        Console.WriteLine($"track {t.Index} id={t.Id} {t.Kind} {t.Codec} tag={t.CodecTag} ts={t.Timescale} lang={t.Language} cfg={t.Configuration.Data.Length}/{t.Configuration.Format} prime={t.Edit.Priming}");
    }

    Console.WriteLine($"duration={d.Duration} doctype={d.Header.DocType} defects={d.Defects.Count}");
    foreach (OurPacket p in ReadOurs(d, limit))
    {
        Console.WriteLine($"{p.Track} pts={p.Pts.Ticks} dts={(p.Dts.IsKnown ? p.Dts.Ticks.ToString(CultureInfo.InvariantCulture) : "N/A")} dur={(p.Duration.IsKnown ? p.Duration.Ticks.ToString(CultureInfo.InvariantCulture) : "N/A")} size={p.Size} pos={p.Range.Offset} key={p.Key} flags={p.Flags} dp={p.DiscardPadding.Ticks}");
    }

    foreach (MediaDefect x in d.Defects) { Console.WriteLine("defect " + x); }
    return 0;
}

static int Digest(string path)
{
    using MatroskaDemuxer d = MatroskaDemuxer.Open(FileMediaSource.Open(path));
    return PrintDigest(d);
}

static int DigestStdin()
{
    using MatroskaDemuxer d = MatroskaDemuxer.Open(new StreamForwardSource(Console.OpenStandardInput()));
    return PrintDigest(d);
}

static int PrintDigest(IDemuxer d)
{
    ulong h = 14695981039346656037;
    long count = 0;
    while (d.TryReadPacket(out PacketView p))
    {
        PacketInfo i = p.Info;
        h = (h ^ (ulong)i.TrackIndex) * 1099511628211;
        h = (h ^ (ulong)i.Pts.Ticks) * 1099511628211;
        h = (h ^ (ulong)(i.Duration.IsKnown ? i.Duration.Ticks : -1)) * 1099511628211;
        h = (h ^ (ulong)i.Flags) * 1099511628211;
        h = (h ^ Hash(p.Data)) * 1099511628211;
        count++;
    }

    Console.WriteLine($"packets={count} digest={h:X16} defects={d.Defects.Count}");
    return 0;
}

static int Alloc(string path)
{
    using MatroskaDemuxer d = MatroskaDemuxer.Open(FileMediaSource.Open(path));
    long n = 0;
    long bytes = 0;
    // Warm up so the one-time buffer growth is not counted.
    for (int i = 0; i < 2000 && d.TryReadPacket(out _); i++) { }

    long before = GC.GetAllocatedBytesForCurrentThread();
    while (d.TryReadPacket(out PacketView p))
    {
        n++;
        bytes += p.Data.Length;
    }

    long after = GC.GetAllocatedBytesForCurrentThread();
    Console.WriteLine($"packets={n} payloadBytes={bytes} allocatedBytes={after - before} perPacket={(n == 0 ? 0 : (double)(after - before) / n):F4} peakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64}");
    return 0;
}

static int ProbeIo(string path)
{
    Counter counter = new();
    using ObservedMediaSource source = new(FileMediaSource.Open(path), null, counter);
    using MatroskaDemuxer d = MatroskaDemuxer.Open(source);
    Console.WriteLine($"fileLength={source.Length} tracks={d.Tracks.Count} duration={d.Duration} reads={counter.Reads} bytes={counter.Bytes} defects={d.Defects.Count}");
    return 0;
}

static int Structure(string path)
{
    using FileMediaSource source = FileMediaSource.Open(path);
    EbmlDocument doc = EbmlDocument.Open(source);
    foreach (EbmlElement top in doc.Elements)
    {
        foreach ((IStructureNode node, int depth) in ((IStructureNode)top).Descendants(new MediaLimits { MaxElementCount = 100_000_000 }))
        {
            EbmlElement e = (EbmlElement)node;
            if (e.Id is 0xA3 or 0xA1 or 0xA0 or 0xA2 or 0x9B or 0xFB or 0x75A2 or 0x75A1 or 0xA6 or 0xE7 or 0xA7 or 0xAB or 0xB3 or 0xB7 or 0xF7 or 0xF1 or 0xF0 or 0xEE or 0xA5) { continue; }
            Console.WriteLine($"{new string(' ', depth - 1)}{e.Name ?? $"0x{e.Id:X}"} offset={e.Offset} header={e.HeaderSize} size={e.PayloadSize}{(e.IsSizeInferred ? " inferred" : "")}");
        }
    }

    foreach (MediaDefect x in doc.Defects) { Console.WriteLine("defect " + x); }
    return 0;
}

static int Diff(string path, string ffprobe)
{
    using MatroskaDemuxer d = MatroskaDemuxer.Open(FileMediaSource.Open(path));
    List<OurPacket> ours = ReadOurs(d, int.MaxValue);
    string raw = RunFfprobe(ffprobe, path, out string timeBases);
    Dictionary<int, (long Num, long Den)> bases = ParseTimeBases(timeBases);
    List<Dictionary<string, string>> theirs = [];
    foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        Dictionary<string, string> f = [];
        foreach (string part in line.Split('|'))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) { f[part[..eq]] = part[(eq + 1)..]; }
        }

        if (f.ContainsKey("stream_index")) { theirs.Add(f); }
    }

    // Differences are sorted into explained classes (each documented in docs/matroska-differential.md) and unexplained ones.
    //   duration-from-codec : ffprobe fills a duration the file does not store, by parsing the audio frames; ours stays unknown.
    //   lace-shared-pts     : frames of a lace that carries neither BlockDuration nor DefaultDuration; ffprobe spaces them by codec frame length.
    //   inexact-timebase    : the TimestampScale does not divide a second evenly; ffprobe's integer time base is approximate, ours exact.
    //   pos-queue           : ffprobe's pos for packets it held back in its interleaving queue.
    int streams = Math.Max(ours.Count == 0 ? 0 : ours.Max(p => p.Track) + 1, theirs.Count == 0 ? 0 : theirs.Max(t => int.Parse(t["stream_index"], CultureInfo.InvariantCulture)) + 1);
    Dictionary<string, int> explained = [];
    Dictionary<string, int> unexplained = [];
    Dictionary<string, int> posKinds = [];
    long compared = 0;
    StringBuilder examples = new();
    bool inexact = d.Info.TimestampScale != 0 && 1_000_000_000 % d.Info.TimestampScale != 0;
    for (int s = 0; s < streams; s++)
    {
        List<OurPacket> a = ours.Where(p => p.Track == s).ToList();
        List<Dictionary<string, string>> b = theirs.Where(t => int.Parse(t["stream_index"], CultureInfo.InvariantCulture) == s).ToList();
        if (a.Count != b.Count)
        {
            Add(unexplained, $"s{s}:count");
            examples.AppendLine(CultureInfo.InvariantCulture, $"  stream {s}: packet count ours={a.Count} ffprobe={b.Count}");
        }

        (long num, long den) = bases.TryGetValue(s, out var tb) ? tb : (1, 1000);
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            compared++;
            OurPacket p = a[i];
            Dictionary<string, string> t = b[i];
            if (Num(t, "size") != p.Size)
            {
                if (d.Entries[s].HasUnsupportedFrameCompression) { Add(explained, $"s{s}:frame-compression-not-undone"); }
                else if (d.Tracks[s].Codec == CodecId.WebVtt && Num(t, "size") < p.Size) { Add(explained, $"s{s}:webvtt-block-repacked"); }
                else { Bad(s, i, $"size {p.Size}/{Num(t, "size")}"); }
            }

            long? theirPts = NumN(t, "pts");
            long? theirDur = NumN(t, "duration");
            if (theirPts is null) { if (p.Pts.IsKnown) { Bad(s, i, "pts known/N/A"); } }
            else if (!SameTime(p.Pts, theirPts.Value, num, den))
            {
                if (inexact && Near(p.Pts, theirPts.Value, num, den)) { Add(explained, $"s{s}:inexact-timebase"); }
                else if (i > 0 && a[i - 1].Pts.Ticks == p.Pts.Ticks && !p.Duration.IsKnown) { Add(explained, $"s{s}:lace-shared-pts"); }
                else if (i > 0 && a[i - 1].Pts.Ticks == p.Pts.Ticks) { Bad(s, i, $"pts {p.Pts.Ticks}@{p.Pts.Timescale}/{theirPts}@{den / num}"); }
                else { Bad(s, i, $"pts {p.Pts.Ticks}@{p.Pts.Timescale}/{theirPts}@{den / num}"); }
            }

            if (theirDur is { } td)
            {
                if (!p.Duration.IsKnown) { Add(explained, $"s{s}:duration-from-codec"); }
                else if (!SameTime(p.Duration, td, num, den))
                {
                    if (i == a.Count - 1) { Add(explained, $"s{s}:last-block-duration-differs"); }
                    else if (inexact && Near(p.Duration, td, num, den)) { Add(explained, $"s{s}:inexact-timebase"); }
                    else { Bad(s, i, $"duration {p.Duration.Ticks}@{p.Duration.Timescale}/{td}@{den / num}"); }
                }
            }
            else if (p.Duration.IsKnown) { Bad(s, i, $"duration {p.Duration.Ticks}/N/A"); }

            string flags = t.GetValueOrDefault("flags", "");
            if (flags.Contains('K', StringComparison.Ordinal) != p.Key) { Bad(s, i, $"key {p.Key}/{flags}"); }
            if (NumN(t, "pos") is { } theirPos && p.Range.Offset >= 0)
            {
                // For an unlaced block with a one-byte track number, the Block starts 4 bytes before the frame (track, timestamp, flags).
                string kind = p.Range.Offset - 4 == theirPos ? "block" : theirPos < p.Range.Offset - 4 ? "pos-queue" : "later";
                posKinds[$"s{s}:{kind}"] = posKinds.GetValueOrDefault($"s{s}:{kind}") + 1;
                if (kind == "later") { Bad(s, i, $"pos {p.Range.Offset}/{theirPos}"); }
            }
        }
    }

    long unexplainedTotal = unexplained.Values.Sum();
    Console.WriteLine($"{Path.GetFileName(path)}: ours={ours.Count} ffprobe={theirs.Count} compared={compared} unexplained={unexplainedTotal} [{string.Join(", ", unexplained.Select(kv => kv.Key + "=" + kv.Value))}] explained=[{string.Join(", ", explained.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))}] defects={d.Defects.Count} pos:[{string.Join(", ", posKinds.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))}]");
    if (examples.Length > 0) { Console.Write(examples.ToString()); }
    return unexplainedTotal == 0 ? 0 : 1;

    void Bad(int s, int i, string what)
    {
        Add(unexplained, $"s{s}:{what.Split(' ')[0]}");
        if (examples.Length < 2500) { examples.AppendLine(CultureInfo.InvariantCulture, $"  stream {s} packet {i}: {what}"); }
    }

    static void Add(Dictionary<string, int> m, string k) => m[k] = m.GetValueOrDefault(k) + 1;
    static long Num(Dictionary<string, string> t, string k) => long.TryParse(t.GetValueOrDefault(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : -1;
    static long? NumN(Dictionary<string, string> t, string k) => long.TryParse(t.GetValueOrDefault(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
}

// Within 1.5 ticks of ffprobe's (integer) time base: ours is exact, theirs a rounded base.
static bool Near(MediaTime ours, long theirTicks, long num, long den)
{
    System.Numerics.BigInteger l = new System.Numerics.BigInteger(ours.Ticks) * den * 2;
    System.Numerics.BigInteger r = new System.Numerics.BigInteger(theirTicks) * num * ours.Timescale * 2;
    System.Numerics.BigInteger tol = 3 * num * ours.Timescale;
    return System.Numerics.BigInteger.Abs(l - r) <= tol;
}

// Compares every element mkvinfo lists with a position and sizes against the element tree: same offset, same total size, same data size.
static int MkvInfoDiff(string path, string mkvinfo)
{
    string text = Run(mkvinfo, ["-a", "-P", "-z", "-v", "-v", path]);
    using FileMediaSource source = FileMediaSource.Open(path);
    EbmlDocument doc = EbmlDocument.Open(source);
    Dictionary<long, EbmlElement> byOffset = [];
    foreach (EbmlElement top in doc.Elements)
    {
        foreach ((IStructureNode node, int _) in ((IStructureNode)top).Descendants(new MediaLimits { MaxElementCount = 100_000_000 }))
        {
            byOffset[node.Offset] = (EbmlElement)node;
        }
    }

    System.Text.RegularExpressions.Regex rx = new(@"at (\d+) size (\d+) data size (\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    int listed = 0, matched = 0, missing = 0, differing = 0;
    StringBuilder examples = new();
    foreach (string line in text.Split('\n'))
    {
        System.Text.RegularExpressions.Match m = rx.Match(line);
        if (!m.Success) { continue; }
        listed++;
        long at = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        long size = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        long data = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (!byOffset.TryGetValue(at, out EbmlElement? e)) { missing++; if (examples.Length < 1500) { examples.AppendLine("  missing in tree: " + line.Trim()); } continue; }
        if (e.HeaderSize + e.PayloadSize == size && e.PayloadSize == data) { matched++; }
        else { differing++; if (examples.Length < 1500) { examples.AppendLine($"  differs: {line.Trim()} | ours header={e.HeaderSize} payload={e.PayloadSize}"); } }
    }

    Console.WriteLine($"{Path.GetFileName(path)}: mkvinfo elements={listed} matched={matched} missingInTree={missing} differing={differing} treeElements={byOffset.Count}");
    if (examples.Length > 0) { Console.Write(examples.ToString()); }
    return missing + differing == 0 ? 0 : 1;
}

// Cuts the file at every <step>th offset (and corrupts single bytes) and demuxes each variant: only typed exceptions are acceptable, and each
// variant must finish quickly. Prints the first problems found.
static int Sweep(string path, int step)
{
    byte[] file = File.ReadAllBytes(path);
    int cuts = 0, typed = 0, bad = 0, ok = 0;
    for (int cut = 0; cut <= file.Length; cut += step)
    {
        cuts++;
        byte[] part = file.AsSpan(0, cut).ToArray();
        string? outcome = null;
        Thread t = new(() => outcome = RunOne(part)) { IsBackground = true };
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(10))) { outcome = "HANG"; }
        if (outcome == "ok") { ok++; }
        else if (outcome == "typed") { typed++; }
        else { bad++; if (bad <= 5) { Console.WriteLine($"  cut at {cut}: {outcome}"); } if (outcome == "HANG") { break; } }
    }

    Console.WriteLine($"{Path.GetFileName(path)}: step {step}: cut points={cuts} ok={ok} typedException={typed} untypedOrHang={bad}");
    return bad == 0 ? 0 : 1;

    static string RunOne(byte[] data)
    {
        try
        {
            using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(data));
            while (d.TryReadPacket(out _)) { }
            return "ok";
        }
        catch (MediaException) { return "typed"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
}

// Compares the track headers mkvinfo prints with the track entries we read.
static int MkvTracks(string path, string mkvinfo)
{
    string text = Run(mkvinfo, [path]);
    using MatroskaDemuxer d = MatroskaDemuxer.Open(FileMediaSource.Open(path));
    List<Dictionary<string, string>> theirs = [];
    Dictionary<string, string>? cur = null;
    foreach (string raw in text.Split('\n'))
    {
        string line = raw.TrimStart('|', ' ', '+');
        if (raw.StartsWith("| + Track", StringComparison.Ordinal) && line == "Track") { cur = []; theirs.Add(cur); continue; }
        if (cur is null) { continue; }
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) { continue; }
        string key = line[..colon];
        string value = line[(colon + 1)..].Trim();
        if (key is "Track number" or "Track type" or "Codec ID" or "Default duration" or "Language" or "Sampling frequency" or "Channels" or "Pixel width" or "Pixel height" or "Codec's private data" or "Codec delay" or "Seek pre-roll" or "Name")
        {
            cur.TryAdd(key, value);
        }
    }

    int problems = 0;
    if (theirs.Count != d.Entries.Count) { Console.WriteLine($"  track count ours={d.Entries.Count} mkvinfo={theirs.Count}"); problems++; }
    for (int i = 0; i < Math.Min(theirs.Count, d.Entries.Count); i++)
    {
        MatroskaTrackEntry e = d.Entries[i];
        Dictionary<string, string> t = theirs[i];
        void Check(string name, string ours, string? their)
        {
            if (their is not null && ours != their) { Console.WriteLine($"  track {i} {name}: ours={ours} mkvinfo={their}"); problems++; }
        }

        Check("number", e.TrackNumber.ToString(CultureInfo.InvariantCulture), t.GetValueOrDefault("Track number")?.Split(' ')[0]);
        Check("codec", e.CodecId, t.GetValueOrDefault("Codec ID"));
        Check("type", e.TrackType switch { 1 => "video", 2 => "audio", 17 => "subtitles", _ => "?" }, t.GetValueOrDefault("Track type"));
        if (t.TryGetValue("Default duration", out string? dd))
        {
            // 00:00:00.041666665 (24.000 frames/fields per second ...)
            string clock = dd.Split(' ')[0];
            string[] parts = clock.Split(':', '.');
            long ns = ((long.Parse(parts[0], CultureInfo.InvariantCulture) * 3600) + (long.Parse(parts[1], CultureInfo.InvariantCulture) * 60) + long.Parse(parts[2], CultureInfo.InvariantCulture)) * 1_000_000_000 + long.Parse(parts[3], CultureInfo.InvariantCulture);
            Check("defaultDuration", e.DefaultDurationNs.ToString(CultureInfo.InvariantCulture), ns.ToString(CultureInfo.InvariantCulture));
        }
        else if (e.DefaultDurationNs != 0) { Console.WriteLine($"  track {i} defaultDuration ours={e.DefaultDurationNs} mkvinfo=none"); problems++; }

        Check("language", e.Language, t.GetValueOrDefault("Language") ?? "eng");
        if (e.Audio is { } a)
        {
            Check("rate", a.SamplingFrequency.ToString(CultureInfo.InvariantCulture), t.GetValueOrDefault("Sampling frequency")?.Split('.')[0]);
            Check("channels", a.Channels.ToString(CultureInfo.InvariantCulture), t.GetValueOrDefault("Channels"));
        }

        if (e.Video is { } v)
        {
            Check("width", v.PixelWidth.ToString(CultureInfo.InvariantCulture), t.GetValueOrDefault("Pixel width"));
            Check("height", v.PixelHeight.ToString(CultureInfo.InvariantCulture), t.GetValueOrDefault("Pixel height"));
        }

        string? priv = t.GetValueOrDefault("Codec's private data");
        Check("codecPrivate", priv is null ? "none" : "size " + e.CodecPrivate.Length, priv is null && e.CodecPrivate.Length == 0 ? "none" : priv?.Split(' ', 3)[0] + " " + priv?.Split(' ', 3).ElementAtOrDefault(1)?.TrimEnd(',', ' '));
    }

    Console.WriteLine($"{Path.GetFileName(path)}: tracks ours={d.Entries.Count} mkvinfo={theirs.Count} problems={problems}");
    return problems == 0 ? 0 : 1;
}

static bool SameTime(MediaTime ours, long theirTicks, long num, long den)
{
    // ours.Ticks / ours.Timescale == theirTicks * num / den  <=>  ours.Ticks * den == theirTicks * num * ours.Timescale
    System.Numerics.BigInteger l = new System.Numerics.BigInteger(ours.Ticks) * den;
    System.Numerics.BigInteger r = new System.Numerics.BigInteger(theirTicks) * num * ours.Timescale;
    return l == r;
}

static Dictionary<int, (long, long)> ParseTimeBases(string text)
{
    Dictionary<int, (long, long)> map = [];
    foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        int index = -1;
        string? tb = null;
        foreach (string part in line.Split('|'))
        {
            if (part.StartsWith("index=", StringComparison.Ordinal)) { index = int.Parse(part[6..], CultureInfo.InvariantCulture); }
            else if (part.StartsWith("time_base=", StringComparison.Ordinal)) { tb = part[10..]; }
        }

        if (index >= 0 && tb is not null)
        {
            string[] frac = tb.Split('/');
            map[index] = (long.Parse(frac[0], CultureInfo.InvariantCulture), long.Parse(frac[1], CultureInfo.InvariantCulture));
        }
    }

    return map;
}

static string RunFfprobe(string ffprobe, string path, out string streams)
{
    streams = Run(ffprobe, ["-v", "error", "-show_entries", "stream=index,time_base", "-of", "compact=p=0", path]);
    return Run(ffprobe, ["-v", "error", "-show_packets", "-show_entries", "packet=stream_index,pts,dts,duration,size,pos,flags", "-of", "compact=p=0", path]);
}

static string Run(string exe, string[] arguments)
{
    ProcessStartInfo psi = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in arguments) { psi.ArgumentList.Add(a); }
    using Process p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return output;
}

internal sealed record OurPacket(int Track, MediaTime Pts, MediaTime Dts, MediaTime Duration, bool Key, PacketAttributes Flags, ByteRange Range, int Size, uint Hash, MediaTime DiscardPadding);

internal sealed class Counter : IMediaReadObserver
{
    public long Reads;
    public long Bytes;

    public void OnRead(long offset, int requested, int returned)
    {
        Reads++;
        Bytes += returned;
    }
}
