using System.Diagnostics.Tracing;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using static RecursiveAI.OneMedia.Tests.Ebl;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>
/// The steady-state pull path must not touch <see cref="System.Buffers.ArrayPool{T}"/> at all: no rent, and so no allocation of a pooled
/// array either. Unlike the allocation counter this does not depend on the runtime's GC settings, the platform or the pool's trimming, so
/// it catches a rent per element or per chunk deterministically. The listener only counts events written on the measuring thread.
/// </summary>
public class PoolEventTests
{
    private sealed class PoolWatch : EventListener
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private int _rented;
        private int _allocated;

        public int Rented => Volatile.Read(ref _rented);

        public int Allocated => Volatile.Read(ref _allocated);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (Environment.CurrentManagedThreadId != _thread || e.EventSource.Name != "System.Buffers.ArrayPoolEventSource")
            {
                return;
            }

            // BufferRented (1) and BufferAllocated (2); returns and trims are not acquisitions.
            if (e.EventName == "BufferRented")
            {
                Interlocked.Increment(ref _rented);
            }
            else if (e.EventName == "BufferAllocated")
            {
                Interlocked.Increment(ref _allocated);
            }
        }
    }

    // Counts the pool events of the loop, then stops listening before anything is asserted: building an assertion message rents from the pool itself.
    private static void AssertNoPoolUse(Func<int> loop, string what, int minPackets = 30)
    {
        int packets;
        int rented;
        int allocated;
        using (PoolWatch watch = new())
        {
            packets = loop();
            rented = watch.Rented;
            allocated = watch.Allocated;
        }

        Assert.True(packets > minPackets, $"{what}: only {packets} packets");
        Assert.True(rented == 0 && allocated == 0, $"{what}: {rented} rents and {allocated} pooled allocations over {packets} packets");
    }

    private static int Drain(Func<bool> next)
    {
        int n = 0;
        while (next())
        {
            n++;
        }

        return n;
    }

    // ---- Matroska ----

    private static byte[] Mkv(bool laced)
    {
        List<byte[]> blocks = [];
        for (int i = 0; i < 3000; i++)
        {
            if (laced)
            {
                blocks.Add(Simple(2, (short)(i % 100), 0x82, Cat([3], [10, 11, 12], Fill(10, 1), Fill(11, 2), Fill(12, 3), Fill(13, 4)))); // Xiph lace of 4
                blocks.Add(El(BlockGroup, BlockBytes(1, (short)(i % 100), 0, Fill(50, 7)), U(0x9B, 40), El(0xFB, [0x81])));
            }
            else
            {
                blocks.Add(Simple(1, (short)(i % 100), (byte)(i % 25 == 0 ? 0x80 : 0), Fill(200 + (i % 7), (byte)i)));
                blocks.Add(Simple(2, (short)(i % 100), 0x80, Fill(100 + (i % 5), (byte)i)));
            }
        }

        return Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 1, "V_VP8"), Track(2, 2, "A_AAC", U(0x23E383, 21_000_000))), ClusterElement(0, blocks.ToArray())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatroskaPullRentsNothing(bool laced)
    {
        byte[] file = Mkv(laced);
        using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(file), new MediaOpenOptions());
        Assert.True(Drain(() => d.TryReadPacket(out _)) > 30);
        d.Seek(MediaTime.Zero);
        AssertNoPoolUse(() => Drain(() => d.TryReadPacket(out _)), laced ? "mkv laced" : "mkv");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatroskaForwardPullRentsNothing(bool laced)
    {
        byte[] file = Mkv(laced);
        using MatroskaDemuxer d = MatroskaDemuxer.Open(new StreamForwardSource(new MemoryStream(file)));
        for (int i = 0; i < 300; i++)
        {
            Assert.True(d.TryReadPacket(out _));
        }

        AssertNoPoolUse(() => Drain(() => d.TryReadPacket(out _)), laced ? "mkv forward laced" : "mkv forward");
    }

    // ---- ISO BMFF ----

    [Theory]
    [InlineData("bframes_aac.mp4")]
    [InlineData("faststart.mp4")]
    [InlineData("frag.mp4")]
    public void IsoPullRentsNothing(string name)
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path(name));
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        Assert.True(Drain(() => d.TryReadPacket(out _)) > 30);
        d.Seek(MediaTime.Zero);
        AssertNoPoolUse(() => Drain(() => d.TryReadPacket(out _)), name);
    }

    [Fact]
    public void IsoAudioOnlyPullOfInterleavedFileRentsNothing()
    {
        // With the video track off, every audio sample is a jump over the video between them: the read-ahead window must serve them.
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path("bframes_aac.mp4"));
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        for (int i = 0; i < d.Tracks.Count; i++)
        {
            if (d.Tracks[i].Kind != TrackKind.Audio)
            {
                d.SetTrackEnabled(i, false);
            }
        }

        Assert.True(Drain(() => d.TryReadPacket(out _)) > 10);
        d.Seek(MediaTime.Zero);
        AssertNoPoolUse(() => Drain(() => d.TryReadPacket(out _)), "audio-only", 10);
    }

    [Theory]
    [InlineData("faststart.mp4")]
    [InlineData("frag.mp4")]
    public void IsoForwardPullRentsNothing(string name)
    {
        byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path(name));
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new StreamForwardSource(new MemoryStream(file)));
        for (int i = 0; i < 5; i++)
        {
            Assert.True(d.TryReadPacket(out _));
        }

        AssertNoPoolUse(() => Drain(() => d.TryReadPacket(out _)), name + " forward");
    }
}
