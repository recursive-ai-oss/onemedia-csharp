using RecursiveAI.OneMedia.Streaming.Sources;

namespace RecursiveAI.OneMedia.Tests.Streaming;

public sealed class GrowingFileTests
{
    private static string Data(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", "streaming", .. parts]);

    // init segment followed by the media segments is a fragmented MP4 as a recorder writes it.
    private static byte[] FragmentedMp4()
    {
        string dir = Data("hls-fmp4-av");
        List<byte> all = [.. File.ReadAllBytes(Path.Combine(dir, "init.mp4"))];
        foreach (string s in Directory.GetFiles(dir, "seg*.m4s").Order())
        {
            all.AddRange(File.ReadAllBytes(s));
        }

        return [.. all];
    }

    private static List<(int, long, int)> Read(IDemuxer d)
    {
        List<(int, long, int)> list = [];
        while (d.TryReadPacket(out PacketView v))
        {
            list.Add((v.Info.TrackIndex, v.Info.Pts.Ticks, v.Data.Length));
        }

        return list;
    }

    [Fact]
    public async Task AFileWrittenInOddSizedPiecesReadsAsTheFinishedFileDoes()
    {
        byte[] whole = FragmentedMp4();
        string path = Path.Combine(Path.GetTempPath(), "onemedia-grow-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            List<(int, long, int)> expected;
            using (MediaFile finished = Media.Open(whole))
            {
                expected = Read(finished.Demuxer!);
            }

            await File.WriteAllBytesAsync(path, []);
            bool done = false;
            Task writer = Task.Run(() =>
            {
                using FileStream fs = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                Random rng = new(7);
                int at = 0;
                while (at < whole.Length)
                {
                    int n = Math.Min(rng.Next(1, 9000), whole.Length - at); // cuts fall inside boxes
                    fs.Write(whole, at, n);
                    fs.Flush();
                    at += n;
                    Thread.Sleep(1);
                }

                Volatile.Write(ref done, true);
            });
            GrowthPolicy policy = new() { PollIntervalMilliseconds = 2, IdleTimeoutMilliseconds = 10_000, IsComplete = () => Volatile.Read(ref done) };
            List<(int, long, int)> actual;
            using (MediaFile growing = Media.OpenGrowing(path, policy))
            {
                actual = Read(growing.Demuxer!);
            }

            await writer;
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheIdleTimeoutEndsTheData()
    {
        string path = Path.Combine(Path.GetTempPath(), "onemedia-grow-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            byte[] whole = FragmentedMp4();
            File.WriteAllBytes(path, whole[..(whole.Length - 100)]);
            using GrowingFileSource source = GrowingFileSource.Open(path, new GrowthPolicy { PollIntervalMilliseconds = 5, IdleTimeoutMilliseconds = 60 });
            byte[] buf = new byte[whole.Length];
            int total = 0;
            int n;
            while ((n = source.Read(buf.AsSpan(total))) > 0)
            {
                total += n;
            }

            Assert.Equal(whole.Length - 100, total);
            Assert.Equal(total, source.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CancellationStopsAWait()
    {
        string path = Path.Combine(Path.GetTempPath(), "onemedia-grow-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, []);
            using GrowingFileSource source = GrowingFileSource.Open(path, new GrowthPolicy { PollIntervalMilliseconds = 5, IdleTimeoutMilliseconds = -1 });
            using CancellationTokenSource cts = new(50);
            Assert.Throws<OperationCanceledException>(() => source.Read(new byte[16], cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileThatDoesNotExistYetIsWaitedFor()
    {
        string path = Path.Combine(Path.GetTempPath(), "onemedia-grow-" + Guid.NewGuid().ToString("N") + ".bin");
        Task.Run(() =>
        {
            Thread.Sleep(80);
            File.WriteAllBytes(path, [1, 2, 3]);
        });
        try
        {
            using GrowingFileSource source = GrowingFileSource.Open(path, new GrowthPolicy { PollIntervalMilliseconds = 10, IdleTimeoutMilliseconds = 5000 });
            byte[] buf = new byte[3];
            Assert.True(source.TryReadExactly(buf));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Throws<FileNotFoundException>(() => GrowingFileSource.Open(path + ".none", GrowthPolicy.NoWait));
    }

    [Fact]
    public void AProgressiveMp4WithoutItsMovieBoxNeedsTheFinishedFile()
    {
        // ftyp, then mdat of undefined length (size 0: to the end of the file), no moov: what a recorder has written before it finishes.
        byte[] bytes = [.. new byte[] { 0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0 }, .. new byte[] { 0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'a', (byte)'t' }, .. new byte[400]];
        string path = Path.Combine(Path.GetTempPath(), "onemedia-grow-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            File.WriteAllBytes(path, bytes);
            MediaFormatException e = Assert.Throws<MediaFormatException>(() => Media.OpenGrowing(path, GrowthPolicy.NoWait));
            Assert.Contains("finished", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
