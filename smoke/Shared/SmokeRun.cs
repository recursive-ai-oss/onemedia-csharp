using System.Buffers;
using System.Globalization;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Exercises the shared contracts end to end (time, sources, sniffing, limits, defects, the pull and owned packet paths). The same
/// code runs under NativeAOT and in the browser WebAssembly runtime, where any use of reflection or a missing trim root would show up.
/// </summary>
internal static class SmokeRun
{
    public static async Task<int> RunAsync(Action<string> log)
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            log((ok ? "ok   " : "FAIL ") + name);
            if (!ok)
            {
                failures++;
            }
        }

        // Time: exact rescaling and cross-timescale comparison.
        MediaTime t = new(1, 3);
        Check("rescale floor", t.Rescale(1000, MediaRounding.Floor).Ticks == 333);
        Check("rescale ceiling", t.Rescale(1000, MediaRounding.Ceiling).Ticks == 334);
        Check("compare across timescales", new MediaTime(90, 90000) == new MediaTime(1, 1000));
        Check("huge rescale", new MediaTime(long.MaxValue, 1_000_000_000).Rescale(90000, MediaRounding.Floor).Ticks > 0);

        // Sniffing through the explicit registry.
        MediaFormatRegistry registry = new();
        registry.Add(new IsoBmffFormat());
        registry.Add(new EbmlSniffer());
        byte[] mp4 = [0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];
        byte[] mkv = [0x1A, 0x45, 0xDF, 0xA3, 0x88, 0x42, 0x82, 0x84, (byte)'w', (byte)'e', (byte)'b', (byte)'m'];
        Check("sniff mp4", registry.TrySniff(mp4, out FormatMatch m1) && m1.Format.Id == MediaFormatId.IsoBmff && m1.Result.Detail == "isom");
        Check("sniff webm", registry.TrySniff(mkv, out FormatMatch m2) && m2.Format.Id == MediaFormatId.Matroska && m2.Result.Detail == "webm");
        Check("sniff nothing", !registry.TrySniff(new byte[16], out _));

        // Sources: memory, stream and a ranged delegate agree.
        byte[] data = new byte[40 * 10];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        using MemoryStream stream = new(data);
        using StreamMediaSource streamSource = new(stream, leaveOpen: true);
        Check("stream source read", streamSource.Read(395, new byte[10]) == 5);
        await using RangedMediaSource ranged = new(data.Length, (offset, buffer, _) =>
        {
            int n = (int)Math.Min(buffer.Length, data.Length - offset);
            data.AsMemory((int)offset, n).CopyTo(buffer);
            return new ValueTask<int>(n);
        });
        byte[] viaRange = new byte[16];
        Check("ranged source read", await ranged.ReadAsync(100, viaRange) == 16 && viaRange[0] == 100);

        // Pull path and owned path over a trivial demuxer.
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(data), 40);
        int pulled = 0;
        long bytes = 0;
        while (demuxer.TryReadPacket(out PacketView view))
        {
            pulled++;
            bytes += view.Data.Length;
        }

        Check("pull path", pulled == 10 && bytes == data.Length);
        Check("seek", demuxer.Seek(new MediaTime(250, 1000)) == new MediaTime(160, 1000));
        int owned = 0;
        await foreach (OwnedPacket packet in demuxer.ReadPacketsAsync(MemoryPool<byte>.Shared))
        {
            using (packet)
            {
                owned++;
            }
        }

        Check("owned packets", owned == 6);

        // Limits and defects.
        bool threw = false;
        try
        {
            MediaLimits.Default.CheckAllocation(long.MaxValue, 12);
        }
        catch (MediaLimitExceededException ex)
        {
            threw = ex.Offset == 12 && ex.LimitName == nameof(MediaLimits.MaxAllocationBytes);
        }

        Check("limit exception", threw);
        DefectLog log2 = new();
        log2.Add(DefectKind.Truncated, DefectSeverity.Error, 7, "cut off");
        Check("defect log", log2.Count == 1 && log2.HasErrors && log2[0].Offset == 7);

        log(string.Create(CultureInfo.InvariantCulture, $"failures: {failures}"));
        return failures;
    }
}
