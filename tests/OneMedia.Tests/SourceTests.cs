namespace RecursiveAI.OneMedia.Tests;

public class SourceTests
{
    private static byte[] Pattern(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 7 + 3);
        }

        return data;
    }

    private sealed class Recorder : IMediaReadObserver
    {
        public List<(long Offset, int Requested, int Returned)> Reads { get; } = [];

        public void OnRead(long offset, int requested, int returned) => Reads.Add((offset, requested, returned));
    }

    [Fact]
    public void MemorySourceReadsAtOffsets()
    {
        byte[] data = Pattern(100);
        using MemoryMediaSource source = new(data);
        Assert.Equal(100, source.Length);
        byte[] buffer = new byte[10];
        Assert.Equal(10, source.Read(5, buffer));
        Assert.Equal(data[5..15], buffer);
        Assert.Equal(4, source.Read(96, buffer));
        Assert.Equal(data[96..], buffer[..4]);
        Assert.Equal(0, source.Read(100, buffer));
        Assert.Equal(0, source.Read(long.MaxValue, buffer));
        Assert.Equal(0, source.Read(0, Span<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(-1, buffer));
    }

    [Fact]
    public void ReadExactlyReportsTruncationWithTheOffset()
    {
        using MemoryMediaSource source = new(Pattern(10));
        byte[] buffer = new byte[8];
        source.ReadExactly(2, buffer);
        MediaFormatException ex = Assert.Throws<MediaFormatException>(() => source.ReadExactly(5, buffer));
        Assert.Equal(5, ex.Offset);
        Assert.Equal(DefectKind.Truncated, ex.Kind);
        Assert.False(source.TryReadExactly(5, buffer));
    }

    [Fact]
    public void CancelledTokenStopsSyncAndAsyncReads()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        using MemoryMediaSource source = new(Pattern(10));
        Assert.Throws<OperationCanceledException>(() => source.Read(0, new byte[2], cts.Token));
        Assert.True(source.ReadAsync(0, new byte[2], cts.Token).AsTask().IsCanceled);
    }

    [Fact]
    public async Task StreamSourceSupportsBothStylesAndStaysOpenOnRequest()
    {
        byte[] data = Pattern(1000);
        using MemoryStream stream = new(data);
        await using (StreamMediaSource source = new(stream, leaveOpen: true))
        {
            byte[] a = new byte[16];
            Assert.Equal(16, source.Read(500, a));
            Assert.Equal(data[500..516], a);
            byte[] b = new byte[16];
            Assert.Equal(16, await source.ReadAsync(10, b));
            Assert.Equal(data[10..26], b);
            Assert.Equal(1000, source.Length);
            Assert.Equal(0, source.Read(1000, a));
        }

        Assert.True(stream.CanRead);
    }

    [Fact]
    public void StreamSourceRejectsUnseekableStreams()
    {
        using NonSeekable stream = new();
        Assert.Throws<ArgumentException>(() => new StreamMediaSource(stream));
    }

    [Fact]
    public async Task FileSourceReadsPositionally()
    {
        string path = Path.Combine(Path.GetTempPath(), "onemedia-test-" + Guid.NewGuid().ToString("N"));
        byte[] data = Pattern(5000);
        await File.WriteAllBytesAsync(path, data);
        try
        {
            await using FileMediaSource source = FileMediaSource.Open(path);
            Assert.Equal(5000, source.Length);
            byte[] a = new byte[100];
            Assert.Equal(100, source.Read(4000, a));
            Assert.Equal(data[4000..4100], a);
            Assert.Equal(40, source.Read(4960, a));
            byte[] b = new byte[100];
            Assert.Equal(100, await source.ReadAsync(1, b));
            Assert.Equal(data[1..101], b);
            Assert.Equal(0, await source.ReadAsync(5000, b));

            // A file that is still being written is read at its current size.
            await File.AppendAllTextAsync(path, "more");
            Assert.Equal(5004, source.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileSourceMissingFileIsAnIOException()
    {
        Assert.ThrowsAny<IOException>(() => FileMediaSource.Open(Path.Combine(Path.GetTempPath(), "onemedia-missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task RangedSourceLoopsOnShortReadsAndClampsToLength()
    {
        byte[] data = Pattern(100);
        int calls = 0;
        await using RangedMediaSource source = new(100, (offset, buffer, _) =>
        {
            calls++;
            int n = Math.Min(7, Math.Min(buffer.Length, (int)(100 - offset)));
            data.AsMemory((int)offset, n).CopyTo(buffer);
            return new ValueTask<int>(n);
        });
        byte[] buf = new byte[20];
        Assert.Equal(20, await source.ReadAsync(10, buf));
        Assert.Equal(data[10..30], buf);
        Assert.Equal(3, calls); // 7 + 7 + 6
        Assert.Equal(5, await source.ReadAsync(95, buf));
        Assert.Equal(0, await source.ReadAsync(100, buf));
        Assert.Equal(0, await source.ReadAsync(500, buf));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await source.ReadAsync(-1, buf));
    }

    [Fact]
    public async Task RangedSourceHonoursCancellation()
    {
        using CancellationTokenSource cts = new();
        await using RangedMediaSource source = new(100, async (offset, buffer, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        });
        Task<int> pending = source.ReadAsync(0, new byte[4], cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task RangedSourceDisposeRunsTheCallback()
    {
        bool disposed = false;
        RangedMediaSource source = new(0, (_, _, _) => new ValueTask<int>(0), () =>
        {
            disposed = true;
            return default;
        });
        await source.DisposeAsync();
        Assert.True(disposed);
    }

    [Fact]
    public void ForwardSourceCountsPositionAndSkips()
    {
        byte[] data = Pattern(30_000);
        using StreamForwardSource source = new(new NonSeekable(data));
        byte[] head = new byte[10];
        Assert.True(source.TryReadExactly(head));
        Assert.Equal(data[..10], head);
        Assert.Equal(10, source.Position);
        Assert.Equal(20_000, source.Skip(20_000));
        Assert.Equal(20_010, source.Position);
        byte[] rest = new byte[9990];
        Assert.True(source.TryReadExactly(rest));
        Assert.Equal(data[20_010..], rest);
        Assert.Equal(0, source.Read(rest));
        Assert.False(source.TryReadExactly(new byte[1]));
        Assert.Equal(0, source.Skip(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Skip(-1));
    }

    [Fact]
    public async Task ForwardSourceAsyncAndCancellation()
    {
        byte[] data = Pattern(1000);
        await using StreamForwardSource source = new(new NonSeekable(data));
        byte[] buf = new byte[100];
        Assert.True(await source.TryReadExactlyAsync(buf));
        Assert.Equal(900, await source.SkipAsync(5000));
        Assert.Equal(1000, source.Position);
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.Read(buf, cts.Token));
    }

    [Fact]
    public async Task ObserverSeesEveryReadAndRangeRequest()
    {
        byte[] data = Pattern(100);
        Recorder recorder = new();
        MemoryMediaSource inner = new(data);
        using ObservedMediaSource source = new(inner, inner, recorder);
        byte[] buf = new byte[30];
        source.Read(90, buf);
        await source.ReadAsync(0, buf);
        Assert.Equal([(90L, 30, 10), (0L, 30, 30)], recorder.Reads);
        Assert.Equal(100, source.Length);

        Recorder asyncOnly = new();
        await using RangedMediaSource ranged = new(100, (o, b, _) => new ValueTask<int>(0));
        ObservedMediaSource observed = new(null, ranged, asyncOnly);
        Assert.Throws<NotSupportedException>(() => observed.Read(0, buf));
        await observed.ReadAsync(0, buf);
        Assert.Single(asyncOnly.Reads);
        Assert.Throws<ArgumentNullException>(() => new ObservedMediaSource(null, null, recorder));
    }

    [Fact]
    public void ObserverSeesForwardReads()
    {
        Recorder recorder = new();
        using ObservedForwardSource source = new(new StreamForwardSource(new NonSeekable(Pattern(50))), recorder);
        byte[] buf = new byte[20];
        source.Read(buf);
        source.Read(buf);
        Assert.Equal([(0L, 20, 20), (20L, 20, 20)], recorder.Reads);
    }

    private sealed class NonSeekable : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekable() => _inner = new MemoryStream();

        public NonSeekable(byte[] data) => _inner = new MemoryStream(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        // Hands out at most 4096 bytes per call, like a pipe.
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 4096));
        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, 4096)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(_inner.Read(buffer.Span[..Math.Min(buffer.Length, 4096)]));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
