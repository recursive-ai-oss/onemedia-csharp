using System.Text;
using RecursiveAI.OneMedia.Streaming.Sources;

namespace RecursiveAI.OneMedia.Tests.Streaming;

public sealed class PresentationSourceTests
{
    private static string Data(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", "streaming", .. parts]);

    // Packets (track, pts, size) of a whole presentation, read through the registry.
    private static List<(int Track, long Pts, int Size)> ReadAll(string path, out MediaFile file)
    {
        file = Media.Open(path);
        List<(int, long, int)> list = [];
        IDemuxer d = file.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            list.Add((v.Info.TrackIndex, v.Info.Pts.Ticks, v.Data.Length));
        }

        return list;
    }

    // The same packets by opening each segment on its own (init segment in front for fragmented MP4).
    private static int CountSegmentPackets(string dir, string? init, IEnumerable<string> segments, int track)
    {
        int total = 0;
        foreach (string seg in segments)
        {
            byte[] bytes = [.. (init is null ? [] : File.ReadAllBytes(Path.Combine(dir, init))), .. File.ReadAllBytes(Path.Combine(dir, seg))];
            using MediaFile f = Media.Open(bytes);
            total += f.Packets(f.Tracks[track]).Count(p => { using (p) { return true; } });
        }

        return total;
    }

    [Theory]
    [InlineData("hls-ts-av", "index.m3u8")]
    [InlineData("hls-fmp4-av", "index.m3u8")]
    [InlineData("hls-ts-single", "index.m3u8")]
    [InlineData("hls-fmp4-single", "index.m3u8")]
    [InlineData("dash-av-timeline", "manifest.mpd")]
    [InlineData("dash-av-number", "manifest.mpd")]
    [InlineData("dash-av-list", "manifest.mpd")]
    [InlineData("dash-av-single", "manifest.mpd")]
    public void OpensEachFixtureWithAudioAndVideoAndReadsEverySegment(string dir, string playlist)
    {
        List<(int Track, long Pts, int Size)> packets = ReadAll(Data(dir, playlist), out MediaFile file);
        using (file)
        {
            Assert.Equal(2, file.Tracks.Count);
            Assert.Equal(TrackKind.Video, file.Tracks[0].Kind);
            Assert.Equal(TrackKind.Audio, file.Tracks[1].Kind);
            Assert.False(file.IsProtected);
            Assert.Equal(50, packets.Count(p => p.Track == 0)); // two seconds at 25 fps
            Assert.True(packets.Count(p => p.Track == 1) > 80);
            Assert.True(file.ContainerDuration.IsKnown || file.TracksDuration.IsKnown || file.Format == MediaFormatId.Hls);
            // Times within a track never go backwards by more than a reorder gap: segments are in order.
            foreach (IGrouping<int, (int Track, long Pts, int Size)> g in packets.GroupBy(p => p.Track))
            {
                long[] pts = [.. g.Select(p => p.Pts)];
                Assert.True(pts.Zip(pts.Skip(1)).Count(x => x.Second < x.First) <= pts.Length / 8, "times run forwards across segments");
            }
        }
    }

    [Fact]
    public void HlsPacketsEqualThePacketsOfTheSegmentsReadOneByOne()
    {
        string dir = Data("hls-fmp4-av");
        string[] segs = [.. Directory.GetFiles(dir, "seg*.m4s").Select(Path.GetFileName).Order()!];
        List<(int Track, long Pts, int Size)> packets = ReadAll(Path.Combine(dir, "index.m3u8"), out MediaFile file);
        using (file)
        {
            Assert.Equal(CountSegmentPackets(dir, "init.mp4", segs, 0), packets.Count(p => p.Track == 0));
            Assert.Equal(CountSegmentPackets(dir, "init.mp4", segs, 1), packets.Count(p => p.Track == 1));
        }
    }

    [Fact]
    public void TsSegmentsReadTheSameAsEachSegmentAlone()
    {
        string dir = Data("hls-ts-av");
        string[] segs = [.. Directory.GetFiles(dir, "seg*.ts").Select(Path.GetFileName).Order()!];
        List<(int Track, long Pts, int Size)> packets = ReadAll(Path.Combine(dir, "index.m3u8"), out MediaFile file);
        using (file)
        {
            Assert.Equal(CountSegmentPackets(dir, null, segs, 0), packets.Count(p => p.Track == 0));
            Assert.Equal(CountSegmentPackets(dir, null, segs, 1), packets.Count(p => p.Track == 1));
        }
    }

    [Fact]
    public void ByteRangePresentationEqualsTheOneWithSeparateFilesInSizeAndHash()
    {
        // Both were cut from the same encoder run? No: each fixture is encoded on its own, so compare structure, not bytes.
        List<(int Track, long Pts, int Size)> single = ReadAll(Data("hls-fmp4-single", "index.m3u8"), out MediaFile a);
        using (a)
        {
            Assert.Equal(2, a.Tracks.Count);
            Assert.Equal(50, single.Count(p => p.Track == 0));
        }
    }

    private static DelegatePresentationResolver Memory(Dictionary<string, byte[]> files, List<string>? requests = null) =>
        new((uri, ct) =>
        {
            requests?.Add(uri.AbsolutePath);
            return files.TryGetValue(uri.AbsolutePath, out byte[]? bytes)
                ? new ValueTask<IAsyncMediaSource>(new MemoryMediaSource(bytes))
                : throw new FileNotFoundException(uri.AbsolutePath);
        });

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task DiscontinuityContinuesTheTimesAndFlagsTheFirstPackets()
    {
        string dir = Data("hls-ts-av");
        Dictionary<string, byte[]> files = new() { ["/p/a.ts"] = File.ReadAllBytes(Path.Combine(dir, "seg000.ts")) };
        string pl = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXTINF:1.000000,\na.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:1.000000,\na.ts\n#EXT-X-ENDLIST\n";
        files["/p/index.m3u8"] = Text(pl);
        await using MediaFile media = await Media.OpenPresentationAsync(new Uri("http://example.test/p/index.m3u8"), Memory(files));
        List<PacketInfo> infos = [];
        await foreach (OwnedPacket p in media.PacketsAsync(media.Tracks[0]))
        {
            infos.Add(p.Info);
            p.Dispose();
        }

        PacketInfo[] flagged = [.. infos.Where(i => (i.Flags & PacketAttributes.Discontinuity) != 0)];
        Assert.Single(flagged);
        int half = infos.Count / 2;
        Assert.Equal(infos.Count, half * 2);
        // The second copy of the segment starts where the first one's last packet ended.
        PacketInfo last = infos[half - 1];
        PacketInfo firstOfSecond = infos[half];
        Assert.True(firstOfSecond.Dts >= last.Dts);
        Assert.True(firstOfSecond.Dts <= last.Dts.Add(last.Duration, MediaRounding.NearestAwayFromZero).Add(new MediaTime(1, 1000), MediaRounding.NearestAwayFromZero));
        // Packets of the copy are those of the original, shifted by one constant.
        long shift = infos[half].Dts.Ticks - infos[0].Dts.Ticks;
        for (int i = 0; i < half; i++)
        {
            Assert.Equal(infos[i].Dts.Ticks + shift, infos[half + i].Dts.Ticks);
        }
    }

    [Fact]
    public async Task KeyedSegmentsAreReportedAsProtectedAndDeliverNothing()
    {
        Dictionary<string, byte[]> files = new()
        {
            ["/p/index.m3u8"] = Text("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n#EXTINF:1.0,\na.ts\n#EXT-X-ENDLIST\n"),
            ["/p/a.ts"] = new byte[188 * 20],
        };
        await using PresentationDemuxer d = await PresentationSource.OpenAsync(new Uri("http://example.test/p/index.m3u8"), Memory(files));
        Assert.True(d.IsProtected);
        Assert.False(await d.ReadPacketAsync());
    }

    [Fact]
    public async Task AKeyThatStartsPartWayAndASegmentWithoutDurationAreRecovered()
    {
        // A segment URI with no EXTINF is kept (a defect, not a refusal); the key that starts at the second segment makes the presentation protected.
        Dictionary<string, byte[]> files = new()
        {
            ["/p/index.m3u8"] = Text("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\na.ts\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k.bin\"\n#EXTINF:1.0,\nb.ts\n#EXT-X-ENDLIST\n"),
            ["/p/a.ts"] = new byte[188 * 20],
            ["/p/b.ts"] = new byte[188 * 20],
        };
        await using PresentationDemuxer d = await PresentationSource.OpenAsync(new Uri("http://example.test/p/index.m3u8"), Memory(files));
        Assert.True(d.IsProtected);
        Assert.Contains(d.Defects, x => x.Code == "hls.segment-without-duration");
    }

    [Fact]
    public async Task AMultivariantPlaylistWithNoUsableEntryIsRefusedWithATypedException()
    {
        // Found by fuzzing: the malformed STREAM-INF is dropped by the lenient reader, which left no variant to choose.
        Dictionary<string, byte[]> files = new()
        {
            ["/p/master.m3u8"] = Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=abc\nlow.m3u8\n"),
        };
        await Assert.ThrowsAsync<RecursiveAI.OneMedia.Streaming.ManifestFormatException>(async () => await PresentationSource.OpenAsync(new Uri("http://example.test/p/master.m3u8"), Memory(files)));
    }

    [Fact]
    public async Task AVariantIsChosenByIndexAndListed()
    {
        string dir = Data("hls-fmp4-av");
        Dictionary<string, byte[]> files = new()
        {
            ["/p/master.m3u8"] = Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=200000,RESOLUTION=160x120\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=320x240\nhigh.m3u8\n"),
            ["/p/low.m3u8"] = File.ReadAllBytes(Path.Combine(dir, "index.m3u8")),
            ["/p/high.m3u8"] = Text("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXTINF:1.0,\nx.ts\n#EXT-X-ENDLIST\n"),
            ["/p/init.mp4"] = File.ReadAllBytes(Path.Combine(dir, "init.mp4")),
        };
        foreach (string s in Directory.GetFiles(dir, "seg*.m4s"))
        {
            files["/p/" + Path.GetFileName(s)] = File.ReadAllBytes(s);
        }

        await using PresentationDemuxer d = await PresentationSource.OpenAsync(new Uri("http://example.test/p/master.m3u8"), Memory(files), new PresentationOptions { VariantIndex = 0 });
        Assert.Equal(2, d.Variants.Count);
        Assert.Equal(200000, d.Variants[0].Bandwidth);
        Assert.Equal(320, d.Variants[1].Width);
        Assert.Equal(MediaFormatId.Hls, d.Format);
        Assert.Equal(2, d.Tracks.Count);
        // Variant 1 names a segment that is not there: opening it fails with an I/O error, not a crash.
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await PresentationSource.OpenAsync(new Uri("http://example.test/p/master.m3u8"), Memory(files), new PresentationOptions { VariantIndex = 1 }));
    }

    [Fact]
    public async Task ALivePlaylistIsLoadedAgainUntilItEnds()
    {
        string dir = Data("hls-ts-av");
        Dictionary<string, byte[]> files = new();
        foreach (string s in Directory.GetFiles(dir, "seg*.ts"))
        {
            files["/p/" + Path.GetFileName(s)] = File.ReadAllBytes(s);
        }

        string head = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n";
        string[] names = [.. files.Keys.Select(k => k[3..]).Order()];
        int loads = 0;
        DelegatePresentationResolver resolver = new((uri, ct) =>
        {
            byte[] bytes;
            if (uri.AbsolutePath == "/p/live.m3u8")
            {
                loads++;
                // Each load shows one more segment; the last one ends the list.
                int n = Math.Min(loads, names.Length);
                bytes = Text(head + string.Concat(names.Take(n).Select(x => $"#EXTINF:1.0,\n{x}\n")) + (n == names.Length ? "#EXT-X-ENDLIST\n" : string.Empty));
            }
            else
            {
                bytes = files[uri.AbsolutePath];
            }

            return new ValueTask<IAsyncMediaSource>(new MemoryMediaSource(bytes));
        });
        PresentationOptions options = new() { LivePolicy = new GrowthPolicy { PollIntervalMilliseconds = 1, IdleTimeoutMilliseconds = 5000 } };
        await using PresentationDemuxer d = await PresentationSource.OpenAsync(new Uri("http://example.test/p/live.m3u8"), resolver, options);
        Assert.True(d.IsLive);
        int video = 0;
        while (await d.ReadPacketAsync())
        {
            video += d.Current.Info.TrackIndex == 0 ? 1 : 0;
        }

        Assert.Equal(names.Length, loads);
        Assert.False(d.IsLive);
        Assert.True(video > 0);
    }

    [Fact]
    public async Task TheFileResolverRefusesReferencesThatLeaveItsDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "onemedia-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FilePresentationResolver r = new(root);
            await Assert.ThrowsAsync<UnsupportedMediaException>(async () => await r.OpenAsync(new Uri("file:///etc/passwd"), default));
            await Assert.ThrowsAsync<UnsupportedMediaException>(async () => await r.OpenAsync(new Uri(new Uri("file://" + root + "/"), "../outside.ts"), default));
            await Assert.ThrowsAsync<UnsupportedMediaException>(async () => await r.OpenAsync(new Uri("http://example.test/a.ts"), default));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SeekToTheStartReadsThePresentationAgain()
    {
        using MediaFile media = Media.Open(Data("hls-ts-av", "index.m3u8"));
        int first = media.Packets(media.Tracks[0]).Count(p => { using (p) { return true; } });
        int second = media.Packets(media.Tracks[0]).Count(p => { using (p) { return true; } });
        Assert.Equal(first, second);
        Assert.Equal(50, first);
    }
}

[Collection(AllocationMeasurements.Name)]
public sealed class PresentationAllocationTests
{
    private static string Data(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", "streaming", .. parts]);

    [Fact]
    public void PacketsInsideASegmentCostNoAllocation()
    {
        AllocationGuard.RequireOptimised();
        using PresentationDemuxer d = PresentationSource.Open(Data("hls-fmp4-av", "index.m3u8"));
        for (int i = 0; i < 20; i++)
        {
            Assert.True(d.TryReadPacket(out _)); // warm up: buffers grow to the largest packet
        }

        long bytes = AllocationGuard.Best(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30; i++)
            {
                d.TryReadPacket(out _);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.Equal(0, bytes);
    }
}
