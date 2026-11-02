using RecursiveAI.OneMedia.MpegTs;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>The transport stream probe reads a bounded prefix and never asks the source for the same range twice.</summary>
public class TsProbeBoundTests
{
    private const int PacketSize = 188;
    private const int Window = 256 * 1024;

    private sealed class Reads : IMediaReadObserver
    {
        public readonly List<(long Offset, int Length)> All = [];

        public void OnRead(long offset, int requested, int returned)
        {
            if (returned > 0)
            {
                All.Add((offset, returned));
            }
        }

        public long Bytes => All.Sum(r => (long)r.Length);

        // Bytes requested more than once: the sum of the reads minus the size of their union.
        public long Repeated()
        {
            long union = 0;
            long end = -1;
            foreach ((long offset, int length) in All.OrderBy(r => r.Offset))
            {
                long from = Math.Max(offset, end);
                if (offset + length > from)
                {
                    union += offset + length - from;
                }

                end = Math.Max(end, offset + length);
            }

            return Bytes - union;
        }
    }

    private static byte[] Original() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "mpeg", "h264-aac.ts"));

    private static int Pid(ReadOnlySpan<byte> file, int at) => ((file[at + 1] & 0x1F) << 8) | file[at + 2];

    // PAT (PID 0), SDT (17) and the PMT (0x1000 in this file).
    private static bool IsTable(int pid) => pid is 0 or 17 or 0x1000;

    private static byte[] Padding(long bytes)
    {
        byte[] nulls = new byte[bytes / PacketSize * PacketSize];
        for (int at = 0; at < nulls.Length; at += PacketSize)
        {
            nulls[at] = 0x47;
            nulls[at + 1] = 0x1F;
            nulls[at + 2] = 0xFF;
            nulls[at + 3] = 0x10;
        }

        return nulls;
    }

    // The first 'leading' non-table packets of the file, then the padding, then (optionally) the whole file with its tables.
    private static byte[] Build(long padding, bool tablesAfter)
    {
        byte[] file = Original();
        List<byte> head = [];
        int kept = 0;
        for (int at = 0; at + PacketSize <= file.Length && kept < 300; at += PacketSize)
        {
            if (!IsTable(Pid(file, at)))
            {
                head.AddRange(file.AsSpan(at, PacketSize).ToArray());
                kept++;
            }
        }

        List<byte> all = [.. head, .. Padding(padding)];
        if (tablesAfter)
        {
            all.AddRange(file);
            all.AddRange(Padding(2_000_000)); // the tail read must not overlap the front
        }
        else
        {
            for (int at = 0; at + PacketSize <= file.Length; at += PacketSize)
            {
                if (!IsTable(Pid(file, at)))
                {
                    all.AddRange(file.AsSpan(at, PacketSize).ToArray());
                }
            }
        }

        return [.. all];
    }

    [Fact]
    public void TablesThatComeAfterTheFirstPayloadsAreReadWithoutReadingTheFrontTwice()
    {
        byte[] stream = Build(3_000_000, tablesAfter: true);
        Reads reads = new();
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new ObservedMediaSource(new MemoryMediaSource(stream), null, reads));
        Assert.Equal(2, d.Tracks.Count);
        Assert.Equal(0, reads.Repeated());
        // The front pass ends where the streams are described; a second pass would have read about 3 MB again.
        Assert.InRange(reads.Bytes, 1, 4_000_000 + MpegTsDemuxer.TailBytes);
    }

    [Fact]
    public void ProbeWithoutTablesStopsAtTheBoundAndSaysSo()
    {
        byte[] stream = Build(MpegTsDemuxer.ProbeLimitWithoutTablesBytes + 3_000_000L, tablesAfter: false);
        Reads reads = new();
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new ObservedMediaSource(new MemoryMediaSource(stream), null, reads));
        Assert.Equal(2, d.Tracks.Count);
        Assert.Contains(d.Defects, x => x.Code == "ts.tables-not-found");
        Assert.Equal(0, reads.Repeated());
        Assert.InRange(reads.Bytes, 1, MpegTsDemuxer.ProbeLimitWithoutTablesBytes + (2L * Window) + MpegTsDemuxer.TailBytes + Window);
        Assert.True(reads.Bytes < stream.Length);
    }
}
