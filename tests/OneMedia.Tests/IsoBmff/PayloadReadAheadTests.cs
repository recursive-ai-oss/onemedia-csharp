using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Tests;

public class PayloadReadAheadTests
{
    private static byte[] Data(int length)
    {
        byte[] data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    // Whatever mix of sequential, jumping, large and past-the-end reads, the window returns what a direct read returns.
    [Fact]
    public void ReadsEqualDirectReads()
    {
        byte[] data = Data(300_000);
        MemoryMediaSource source = new(data);
        PayloadReadAhead ahead = new();
        Random random = new(11);
        long position = 0;
        for (int i = 0; i < 5000; i++)
        {
            int size = random.Next(5) switch { 0 => random.Next(0, 40), 1 => random.Next(0, 5000), 2 => random.Next(30_000, 70_000), _ => random.Next(0, 400) };
            position = random.Next(6) switch { 0 => random.Next(0, 310_000), 1 => position + random.Next(0, 3000), _ => position };
            byte[] expected = new byte[size];
            byte[] actual = new byte[size];
            int e = source.Read(position, expected);
            int a = ahead.Read(source, position, actual, CancellationToken.None);
            Assert.Equal(e, a);
            Assert.True(expected.AsSpan(0, e).SequenceEqual(actual.AsSpan(0, a)), $"read {i} at {position} size {size}");
            position += size;
        }
    }

    [Fact]
    public void ReadsAfterReleaseGoToTheSource()
    {
        byte[] data = Data(10_000);
        MemoryMediaSource source = new(data);
        PayloadReadAhead ahead = new();
        byte[] buffer = new byte[100];
        Assert.Equal(100, ahead.Read(source, 50, buffer, CancellationToken.None));
        ahead.Release();
        ahead.Release();
        Assert.Equal(100, ahead.Read(source, 5000, buffer, CancellationToken.None));
        Assert.True(data.AsSpan(5000, 100).SequenceEqual(buffer));
    }

    // Samples of one chunk are contiguous: the number of reads on the file must be far below the number of samples.
    [Fact]
    public void ContiguousSamplesShareReads()
    {
        CountingSource source = new(Data(200_000));
        PayloadReadAhead ahead = new();
        byte[] buffer = new byte[200];
        for (int i = 0; i < 900; i++)
        {
            Assert.Equal(200, ahead.Read(source, i * 200L, buffer, CancellationToken.None));
        }

        Assert.True(source.Reads < 30, $"{source.Reads} reads");
    }

    // Reading one track of an interleaved file jumps from chunk to chunk. After the first chunk the window starts as long as the chunk
    // just read, so each further chunk costs one read rather than a climb from the smallest window.
    [Fact]
    public void EachChunkOfAnInterleavedTrackCostsAboutOneRead()
    {
        const int chunks = 40, samplesPerChunk = 140, sampleSize = 200, stride = 150_000;
        byte[] data = Data(chunks * stride + 100_000);
        CountingSource source = new(data);
        PayloadReadAhead ahead = new();
        byte[] buffer = new byte[sampleSize];
        for (int c = 0; c < chunks; c++)
        {
            for (int i = 0; i < samplesPerChunk; i++)
            {
                long offset = ((long)c * stride) + ((long)i * sampleSize);
                Assert.Equal(sampleSize, ahead.Read(source, offset, buffer, CancellationToken.None));
                Assert.True(data.AsSpan((int)offset, sampleSize).SequenceEqual(buffer));
            }
        }

        Assert.True(source.Reads <= chunks + 8, $"{source.Reads} reads for {chunks} chunks");
    }

    // Opening a small file reads the head once (the registry's look at it is handed on), the movie box once, and the payload in a few windows.
    [Fact]
    public void OpeningAndReadingASmallFileTakesFewReads()
    {
        foreach (string name in new[] { "bframes_aac.mp4", "faststart.mp4" })
        {
            byte[] file = File.ReadAllBytes(IsoBmffTestHelpers.Path(name));
            CountingSource source = new(file);
            using IDemuxer d = MediaFormats.Default.Open(source);
            int headReads = source.Reads;
            while (d.TryReadPacket(out _))
            {
            }

            Assert.True(source.ReadsAt0 == 1, $"{name}: {source.ReadsAt0} reads at offset 0");
            Assert.True(headReads <= 5, $"{name}: {headReads} reads to open");
            Assert.True(source.Reads <= 12, $"{name}: {source.Reads} reads in all");
        }
    }

    private sealed class CountingSource(byte[] data) : IMediaSource
    {
        public int Reads { get; private set; }

        public int ReadsAt0 { get; private set; }

        public long Length => data.Length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (offset == 0)
            {
                ReadsAt0++;
            }

            int n = (int)Math.Min(destination.Length, Math.Max(0, data.Length - offset));
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        public void Dispose()
        {
        }
    }
}
