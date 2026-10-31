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

    private sealed class CountingSource(byte[] data) : IMediaSource
    {
        public int Reads { get; private set; }

        public long Length => data.Length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            Reads++;
            int n = (int)Math.Min(destination.Length, Math.Max(0, data.Length - offset));
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        public void Dispose()
        {
        }
    }
}
