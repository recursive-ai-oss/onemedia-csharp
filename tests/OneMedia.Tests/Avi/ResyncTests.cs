using RecursiveAI.OneMedia.Avi;

namespace RecursiveAI.OneMedia.Tests.Avi;

/// <summary>Files whose chunks are not padded to even lengths, and the frame rate of DV type 1 streams.</summary>
public class ResyncTests
{
    [Fact]
    public void OddChunksWithoutPaddingAreFollowed()
    {
        AviBuilder b = new() { WriteIdx1 = false };
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "MJPG", 1, 25, 0, AviBuilder.Bitmap(8, 8, "MJPG")));
        for (int i = 0; i < 6; i++)
        {
            b.Chunks.Add(new AviBuilder.ChunkSpec(0, [.. Enumerable.Repeat((byte)(i + 1), 5)]));
        }

        List<byte> file = [.. b.Build()];
        // Remove the pad byte after every five-byte payload, as writers that ignore the rule do.
        int movi = file.ToArray().AsSpan().IndexOf("movi"u8) + 4;
        for (int p = movi, removed = 0; removed < 6; p++)
        {
            if (file[p] == (byte)'0' && file[p + 1] == (byte)'0' && file[p + 2] == (byte)'d' && file[p + 3] == (byte)'c')
            {
                file.RemoveAt(p + 8 + 5);
                removed++;
            }
        }

        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file.ToArray()));
        int n = 0;
        while (d.TryReadPacket(out PacketView v))
        {
            Assert.Equal(5, v.Data.Length);
            Assert.All(v.Data.ToArray(), x => Assert.Equal((byte)(n + 1), x));
            n++;
        }

        Assert.Equal(6, n);
        Assert.Contains(d.Defects, x => x.Code == "avi.resync");
    }

    [Theory]
    [InlineData(333666u, 10_000_000u, 30000, 1001)]
    [InlineData(1001u, 30000u, 30000, 1001)]
    [InlineData(40u, 1000u, 25, 1)]
    [InlineData(1u, 15u, 15, 1)]
    public void DvType1UsesTheFrameRateOfTheDvSystem(uint scale, uint rate, int expectedRate, int expectedScale)
    {
        AviBuilder b = new() { WriteIdx1 = false };
        b.Streams.Add(new AviBuilder.StreamSpec("iavs", "dvsd", scale, rate, 0, new byte[32]));
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        MediaTrack t = d.Tracks[0];
        Assert.Equal(expectedRate, t.Video!.Value.FrameRateNumerator);
        Assert.Equal(expectedScale, t.Video!.Value.FrameRateDenominator);
    }
}
