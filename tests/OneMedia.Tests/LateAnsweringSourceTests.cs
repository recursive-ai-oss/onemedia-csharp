namespace RecursiveAI.OneMedia.Tests;

/// <summary>
/// Reading through a source that answers later than the call, the way a browser Blob does. Formats with an asynchronous reader must
/// work on every host, a single-threaded one included; formats read by synchronous code must either work (a thread is available to wait on)
/// or say clearly that they cannot (browser WebAssembly), never hang.
/// </summary>
public class LateAnsweringSourceTests
{
    private static string Data(string dir, string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", dir, name);

    private static ValueTask<MediaFile> OpenLate(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Media.OpenAsync(bytes.Length, async (offset, destination, ct) =>
        {
            await Task.Yield(); // the answer arrives on a later turn of the event loop
            ct.ThrowIfCancellationRequested();
            if (offset >= bytes.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(destination.Length, bytes.Length - offset);
            bytes.AsMemory((int)offset, n).CopyTo(destination);
            return n;
        });
    }

    [Theory]
    [InlineData("audio", "aac.m4a")]
    [InlineData("audio", "opus.webm")]
    [InlineData("audio", "opus.ogg")]
    [InlineData("asf", "wmav2-tagged.wma")]
    [InlineData("pcm", "bwf-v0.wav")]
    [InlineData("pcm", "aiff-odd-rates.aiff")]
    [InlineData("pcm", "caf-info-chan.caf")]
    [InlineData("audio-es", "plain.mp3")]
    [InlineData("audio-es", "lc.aac")]
    [InlineData("audio-es", "ac3.ac3")]
    [InlineData("audio-es", "eac3.eac3")]
    [InlineData("audio-es", "nb.amr")]
    public async Task FormatsWithAnAsynchronousReaderOpenOnASourceThatAnswersLater(string dir, string name)
    {
        await using MediaFile media = await OpenLate(Data(dir, name));
        Assert.NotEmpty(media.Tracks);
        Assert.True(await ReadAFewPackets(media) > 0);
    }

    private static async Task<int> ReadAFewPackets(MediaFile media)
    {
        int count = 0;
        while (count < 10 && await media.AsyncDemuxer!.ReadPacketAsync())
        {
            count++;
        }

        return count;
    }

    [Theory]
    [InlineData("mpeg", "h264-aac.ts")]
    [InlineData("avi", "mjpeg-pcm.avi")]
    [InlineData("xiph-flv", "tiny.flv")]
    public async Task FormatsReadBySynchronousCodeEitherOpenOrRefuseClearly(string dir, string name)
    {
        string path = Data(dir, name);
        if (OperatingSystem.IsBrowser())
        {
            // One thread: nothing can finish the read while the reader waits for it.
            NotSupportedException e = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await using MediaFile media = await OpenLate(path);
            });
            Assert.Contains("single-threaded", e.Message, StringComparison.Ordinal);
        }
        else
        {
            await using MediaFile media = await OpenLate(path);
            Assert.NotEmpty(media.Tracks);
        }
    }
}
