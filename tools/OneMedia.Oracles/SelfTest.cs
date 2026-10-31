using System.Globalization;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>How a fake format departs from ffprobe's packets.</summary>
internal enum EchoMode
{
    /// <summary>Exactly ffprobe's packets: the comparison must find nothing.</summary>
    Exact,

    /// <summary>Deliberate, countable mistakes: the comparison must find exactly these.</summary>
    Wrong,
}

/// <summary>
/// A stand-in for a real reader that replays ffprobe's own packet list, reading the payloads from the file at
/// the reported positions. It exercises the whole harness before any real reader exists.
/// </summary>
internal sealed class EchoFormat(IReadOnlyList<ProbeStream> streams, IReadOnlyList<PacketRecord> packets, EchoMode mode) : IMediaFormat
{
    public MediaFormatId Id => new("echo");

    public string DisplayName => "Echo";

    public SniffResult Sniff(ReadOnlySpan<byte> head) => new(SniffConfidence.Certain);

    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        new EchoDemuxer(source, streams, packets, mode);

    /// <summary>Expected difference counts for <see cref="EchoMode.Wrong"/> on the given packets.</summary>
    public static Dictionary<string, long> ExpectedWrong(IReadOnlyList<PacketRecord> packets)
    {
        List<PacketRecord> t0 = packets.Where(p => p.Track == 0).ToList();
        Dictionary<string, long> e = new() { ["count"] = 1 };
        long n = t0.Count - 1;
        for (int i = 0; i < n; i++)
        {
            if (i % 10 == 0 && t0[i].Pts.IsKnown) { e["pts"] = e.GetValueOrDefault("pts") + 1; }
            if (i % 25 == 3) { e["size"] = e.GetValueOrDefault("size") + 1; e["hash"] = e.GetValueOrDefault("hash") + 1; }
            if (i % 7 == 5) { e["key"] = e.GetValueOrDefault("key") + 1; }
        }

        return e;
    }
}

internal sealed class EchoDemuxer : IDemuxer
{
    private readonly IMediaSource _source;
    private readonly List<PacketRecord> _packets;
    private readonly List<long> _track0Index = [];
    private readonly byte[] _buffer;
    private long _lastShift;
    private int _next;

    public EchoDemuxer(IMediaSource source, IReadOnlyList<ProbeStream> streams, IReadOnlyList<PacketRecord> packets, EchoMode mode)
    {
        _source = source;
        _packets = [.. packets];
        if (mode == EchoMode.Wrong)
        {
            int last = _packets.FindLastIndex(p => p.Track == 0);
            _packets.RemoveAt(last);
        }

        Mode = mode;
        _buffer = new byte[Math.Max(1, _packets.Count == 0 ? 1 : (int)_packets.Max(p => p.Size))];
        Tracks = [.. streams.Select(s => new MediaTrack
        {
            Index = s.Index,
            Kind = s.CodecType switch { "video" => TrackKind.Video, "audio" => TrackKind.Audio, "subtitle" => TrackKind.Subtitle, "data" => TrackKind.Data, _ => TrackKind.Unknown },
            Timescale = s.TimeBaseDen,
        })];
    }

    private EchoMode Mode { get; }

    // ffprobe reports the start of the whole block element for some formats (Matroska), not of the payload, and
    // the header length varies with the block. Find the distance by matching the packet's hash.
    private long FindPayloadShift(PacketRecord p)
    {
        if (p.Size == 0 || p.Pos < 0 || p.Hash.Length == 0)
        {
            return 0;
        }

        byte[] data = new byte[p.Size];
        long[] order = [_lastShift, .. Enumerable.Range(0, 33).Select(k => (long)k)];
        foreach (long k in order)
        {
            if (_source.Read(p.Pos + k, data) == data.Length && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)) == p.Hash)
            {
                _lastShift = k;
                return k;
            }
        }

        return 0;
    }

    public MediaFormatId Format => new("echo");

    public IReadOnlyList<MediaTrack> Tracks { get; }

    public MediaTime Duration => MediaTime.Unknown;

    public DefectLog Defects { get; } = new();

    public bool CanSeek => false;

    public void SetTrackEnabled(int trackIndex, bool enabled) { }

    public bool TryReadPacket(out PacketView packet)
    {
        if (_next >= _packets.Count)
        {
            packet = default;
            return false;
        }

        PacketRecord p = _packets[_next++];
        long size = p.Size;
        MediaTime pts = p.Pts;
        bool key = p.Key;
        if (Mode == EchoMode.Wrong && p.Track == 0)
        {
            int i = (int)_track0Index.Count;
            _track0Index.Add(i);
            if (i % 10 == 0 && pts.IsKnown) { pts = new MediaTime(pts.Ticks + 1, pts.Timescale); }
            if (i % 25 == 3) { size = Math.Max(0, size - 1); }
            if (i % 7 == 5) { key = !key; }
        }

        if (p.Pos >= 0 && size > 0)
        {
            _source.ReadExactly(p.Pos + FindPayloadShift(p), _buffer.AsSpan(0, (int)size));
        }

        PacketInfo info = new(p.Track, pts, p.Dts, p.Duration, key ? PacketAttributes.Keyframe : PacketAttributes.None, p.Pos < 0 ? ByteRange.None : new ByteRange(p.Pos, size), (int)size);
        packet = new PacketView(info, _buffer.AsSpan(0, (int)size));
        return true;
    }

    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe) => throw new NotSupportedException();

    public void Dispose() { }
}

/// <summary>The <c>selftest</c> command: proves the harness finds nothing in exact echoes and exactly the planted mistakes in wrong ones.</summary>
internal static class SelfTest
{
    public static int Run(IReadOnlyList<string> files)
    {
        int failures = 0;
        foreach (string file in files)
        {
            List<ProbeStream> streams = FfprobeReader.ReadStreams(file);
            List<PacketRecord> theirs = [.. FfprobeReader.ReadPackets(file, streams)];
            if (theirs.Count(p => p.Track == 0) < 40)
            {
                Console.WriteLine($"skip {file}: fewer than 40 packets on track 0");
                continue;
            }

            foreach (EchoMode mode in new[] { EchoMode.Exact, EchoMode.Wrong })
            {
                MediaFormatRegistry registry = new();
                registry.Add(new EchoFormat(streams, theirs, mode));
                OurResult ours = OurReader.Read(registry, file);
                FileReport report = Comparer.Compare(file, streams, theirs, ours, Exceptions.None);
                Dictionary<string, long> expected = mode == EchoMode.Exact ? [] : EchoFormat.ExpectedWrong(theirs);
                bool ok = report.Differences.Count == expected.Count && expected.All(kv => report.Differences.GetValueOrDefault(kv.Key) == kv.Value);
                Console.WriteLine($"{(ok ? "pass" : "FAIL")} {mode,-5} {Path.GetFileName(file)}: packets {theirs.Count}; found {string.Join(", ", report.Differences.Select(d => $"{d.Key}={d.Value}"))}; expected {string.Join(", ", expected.Select(d => $"{d.Key}={d.Value}"))}");
                if (!ok)
                {
                    failures++;
                }
            }
        }

        Console.WriteLine(failures == 0 ? "selftest passed" : string.Create(CultureInfo.InvariantCulture, $"selftest: {failures} failures"));
        return failures == 0 ? 0 : 1;
    }
}
