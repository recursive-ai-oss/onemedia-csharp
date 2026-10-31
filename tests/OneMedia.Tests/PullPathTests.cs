using System.Buffers;
using RecursiveAI.OneMedia.Smoke;

namespace RecursiveAI.OneMedia.Tests;

public class PullPathTests
{
    private const int PacketSize = 64;
    private const int PacketCount = 20_000;

    private static byte[] Data()
    {
        byte[] data = new byte[PacketSize * PacketCount];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        return data;
    }

    [Fact]
    public void PullPathAllocatesNothingPerPacket()
    {
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(Data()), PacketSize);

        // Warm up: the first calls may JIT and grow buffers.
        for (int i = 0; i < 100; i++)
        {
            Assert.True(demuxer.TryReadPacket(out _));
        }

        long checksum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (demuxer.TryReadPacket(out PacketView view))
        {
            checksum += view.Data[0] + view.Info.Pts.Ticks;
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.NotEqual(0, checksum);
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void PullPathAfterSeekAllocatesNothing()
    {
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(Data()), PacketSize);
        Assert.True(demuxer.TryReadPacket(out _));
        demuxer.Seek(new MediaTime(100, 1000));
        Assert.True(demuxer.TryReadPacket(out _));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = 0;
        while (demuxer.TryReadPacket(out _))
        {
            count++;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(PacketCount - 1, count); // the seek went back to the first keyframe
    }

    [Fact]
    public void PullPacketsMatchTheSource()
    {
        byte[] data = Data();
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(data), PacketSize);
        int index = 0;
        while (demuxer.TryReadPacket(out PacketView view))
        {
            Assert.Equal(data.AsSpan(index * PacketSize, PacketSize).ToArray(), view.Data.ToArray());
            Assert.Equal(new MediaTime(index * 40L, 1000), view.Info.Pts);
            Assert.Equal(index % 4 == 0, view.Info.IsKeyframe);
            Assert.Equal(new ByteRange(index * PacketSize, PacketSize), view.Info.SourceRange);
            index++;
        }

        Assert.Equal(PacketCount, index);
    }

    [Fact]
    public void OwnedPacketsOutliveTheNextRead()
    {
        byte[] data = Data();
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(data), PacketSize);
        Assert.True(demuxer.TryReadOwnedPacket(null, out OwnedPacket first));
        Assert.True(demuxer.TryReadOwnedPacket(null, out OwnedPacket second));
        using (first)
        using (second)
        {
            Assert.Equal(data.AsMemory(0, PacketSize).ToArray(), first.Data.ToArray());
            Assert.Equal(data.AsMemory(PacketSize, PacketSize).ToArray(), second.Data.ToArray());
            Assert.Equal(PacketSize, first.Info.Size);
        }
    }

    [Fact]
    public async Task AsyncEnumerableSitsOnThePullPath()
    {
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(Data()), PacketSize);
        int count = 0;
        long bytes = 0;
        await foreach (OwnedPacket packet in demuxer.ReadPacketsAsync(MemoryPool<byte>.Shared))
        {
            using (packet)
            {
                count++;
                bytes += packet.Data.Length;
            }
        }

        Assert.Equal(PacketCount, count);
        Assert.Equal(PacketSize * PacketCount, bytes);
    }

    [Fact]
    public async Task AsyncEnumerableStopsWhenCancelled()
    {
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(Data()), PacketSize);
        using CancellationTokenSource cts = new();
        int count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (OwnedPacket packet in demuxer.ReadPacketsAsync(null, cts.Token))
            {
                using (packet)
                {
                    if (++count == 5)
                    {
                        cts.Cancel();
                    }
                }
            }
        });
        Assert.Equal(5, count);
    }

    [Fact]
    public void DisabledTracksDeliverNothing()
    {
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(Data()), PacketSize);
        demuxer.SetTrackEnabled(0, false);
        Assert.False(demuxer.TryReadPacket(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.SetTrackEnabled(1, true));
    }

    [Fact]
    public void SeekLandsOnKeyframes()
    {
        using FixedPacketDemuxer demuxer = new(new MemoryMediaSource(Data()), PacketSize);
        // Packets are 40 ms apart and every fourth is a keyframe, so keyframes sit at 0, 160, 320 ms.
        Assert.Equal(new MediaTime(160, 1000), demuxer.Seek(new MediaTime(250, 1000)));
        Assert.True(demuxer.TryReadPacket(out PacketView previous));
        Assert.Equal(new MediaTime(160, 1000), previous.Info.Pts);
        Assert.Equal(new MediaTime(320, 1000), demuxer.Seek(new MediaTime(250, 1000), SeekMode.NextKeyframe));
        Assert.Equal(MediaTime.Unknown, demuxer.Seek(new MediaTime(100_000, 1), SeekMode.NextKeyframe));
        Assert.False(demuxer.TryReadPacket(out _));
    }

    [Fact]
    public void TruncatedSourceSurfacesAsTypedException()
    {
        // The demuxer counts whole packets, so cut the source from under it.
        byte[] data = Data();
        using MemoryMediaSource full = new(data);
        using FixedPacketDemuxer demuxer = new(new ShrinkingSource(data), PacketSize);
        MediaFormatException ex = Assert.Throws<MediaFormatException>(() =>
        {
            while (demuxer.TryReadPacket(out _))
            {
            }
        });
        Assert.Equal(DefectKind.Truncated, ex.Kind);
    }

    private sealed class ShrinkingSource(byte[] data) : IMediaSource
    {
        public long Length => data.Length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset >= 1000)
            {
                return 0;
            }

            int n = (int)Math.Min(destination.Length, 1000 - offset);
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        public void Dispose()
        {
        }
    }
}
