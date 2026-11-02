using System.Security.Cryptography;
using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Streaming;

namespace RecursiveAI.OneMedia.Tests.Streaming;

public sealed class PackagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packager-" + Guid.NewGuid().ToString("N"));

    public PackagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "package", name);

    private static List<(string Hash, bool Key)> Packets(MediaFile file, TrackKind kind)
    {
        int index = file.Tracks.First(t => t.Kind == kind).Index;
        IDemuxer demuxer = file.Demuxer!;
        List<(string, bool)> list = [];
        while (demuxer.TryReadPacket(out PacketView v))
        {
            if (v.Info.TrackIndex == index)
            {
                list.Add((Convert.ToHexString(SHA256.HashData(v.Data)), v.Info.IsKeyframe));
            }
        }

        return list;
    }

    private static List<(string Hash, bool Key)> Packets(string path, TrackKind kind)
    {
        using MediaFile f = Media.Open(path);
        return Packets(f, kind);
    }

    private string Out(string name) => Path.Combine(_dir, name);

    // The init segment and the media segments of a track, laid end to end, are a fragmented MP4 the reader opens.
    private string Concatenate(string output, string track)
    {
        string joined = Path.Combine(_dir, track + ".joined.mp4");
        using (FileStream f = File.Create(joined))
        {
            f.Write(File.ReadAllBytes(Path.Combine(output, track, "init.mp4")));
            foreach (string s in Directory.GetFiles(Path.Combine(output, track), "seg-*.m4s").Order())
            {
                f.Write(File.ReadAllBytes(s));
            }
        }

        return joined;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransportStreamSegmentsAreStandaloneAndConcatenateToTheSourcePackets(bool singleFile)
    {
        string output = Out(singleFile ? "ts-single" : "ts");
        PackagingReport report = Packager.Package(Fixture("gop1s-bframes-aac.mp4"), output, new PackagingOptions { SegmentFormat = SegmentFormat.MpegTs, SegmentDuration = new MediaTime(1, 1), SingleFile = singleFile });
        Assert.Contains(report.Notes, n => n.Contains("DASH", StringComparison.Ordinal));
        Assert.DoesNotContain("manifest.mpd", report.Manifests);
        PackagedTrack stream = Assert.Single(report.Tracks);
        Assert.True(stream.Segments > 2);
        HlsMediaPlaylist list = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(File.ReadAllText(Path.Combine(output, "stream0", "index.m3u8"))));
        Assert.Equal(stream.Segments, list.Segments.Count);
        Assert.All(list.Segments, s => Assert.Null(s.Map));
        HlsMultivariantPlaylist master = Assert.IsType<HlsMultivariantPlaylist>(HlsPlaylist.Parse(File.ReadAllText(Path.Combine(output, "master.m3u8"))));
        Assert.Contains("avc1", master.Variants[0].Codecs);
        Assert.Contains("mp4a", master.Variants[0].Codecs);
        string joined = Path.Combine(_dir, "joined" + singleFile + ".ts");
        using (FileStream f = File.Create(joined))
        {
            if (singleFile)
            {
                Assert.All(list.Segments, s => Assert.NotNull(s.ByteRange));
                byte[] all = File.ReadAllBytes(Path.Combine(output, "stream0", "media.ts"));
                foreach (HlsSegment s in list.Segments)
                {
                    HlsByteRange r = s.ByteRange!.Value;
                    Assert.Equal(0x47, all[r.Offset]); // each range starts at a transport packet
                    Assert.Equal(0x47, all[r.Offset + 188]);
                    f.Write(all, (int)r.Offset, (int)r.Length);
                }
            }
            else
            {
                foreach (HlsSegment s in list.Segments)
                {
                    byte[] seg = File.ReadAllBytes(Path.Combine(output, "stream0", s.Uri));
                    Assert.Equal(0, seg.Length % 188);
                    Assert.Equal(0x47, seg[0]);
                    f.Write(seg);
                }
            }
        }

        foreach (TrackKind kind in new[] { TrackKind.Video, TrackKind.Audio })
        {
            Assert.Equal(Packets(Fixture("gop1s-bframes-aac.mp4"), kind).Count, Packets(joined, kind).Count);
        }

        // every segment opens on its own and starts with a keyframe
        if (!singleFile)
        {
            foreach (HlsSegment s in list.Segments)
            {
                Assert.True(Packets(Path.Combine(output, "stream0", s.Uri), TrackKind.Video)[0].Key);
            }
        }
    }

    [Fact]
    public void EventsBecomeAnEventStreamAndDateRanges()
    {
        string output = Out("events");
        PackagingEvent cue = new(new MediaTime(2500, 1000), new MediaTime(1, 1), "urn:scte:scte35:2013:bin", string.Empty, 7, new byte[] { 0xFC, 0x30, 0x11 });
        Packager.Package(Fixture("gop1s-bframes-aac.mp4"), output, new PackagingOptions { SegmentDuration = new MediaTime(1, 1), Events = [cue] });
        DashMpd mpd = DashMpd.Parse(File.ReadAllText(Path.Combine(output, "manifest.mpd")));
        string stream = Assert.Single(mpd.Periods[0].OtherElements);
        Assert.Contains("schemeIdUri=\"urn:scte:scte35:2013:bin\"", stream);
        Assert.Contains("presentationTime=\"2500\"", stream);
        Assert.Contains("messageData=\"/DAR\"", stream);
        foreach (string track in new[] { "video0", "audio0" })
        {
            string text = File.ReadAllText(Path.Combine(output, track, "index.m3u8"));
            Assert.Contains("#EXT-X-DATERANGE:ID=\"urn:scte:scte35:2013:bin-7\",CLASS=\"urn:scte:scte35:2013:bin\",START-DATE=\"1970-01-01T00:00:02.500Z\",DURATION=1.000,SCTE35-CMD=0xFC3011", text);
            Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:1970-01-01T00:00:0", text);
            Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(text));
        }
    }

    [Fact]
    public void SegmentsHoldTheSourcePacketsUnchanged()
    {
        string output = Out("vod");
        PackagingReport report = Packager.Package(Fixture("gop1s-bframes-aac.mp4"), output, new PackagingOptions { SegmentDuration = new MediaTime(1, 1) });
        Assert.Equal(2, report.Tracks.Count);
        Assert.Contains("master.m3u8", report.Manifests);
        Assert.Contains("manifest.mpd", report.Manifests);
        foreach ((string track, TrackKind kind) in new[] { ("video0", TrackKind.Video), ("audio0", TrackKind.Audio) })
        {
            Assert.Equal(Packets(Fixture("gop1s-bframes-aac.mp4"), kind), Packets(Concatenate(output, track), kind));
        }

        HlsMediaPlaylist video = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(File.ReadAllText(Path.Combine(output, "video0", "index.m3u8"))));
        Assert.True(video.EndList);
        Assert.Equal(HlsPlaylistType.Vod, video.PlaylistType);
        Assert.True(video.Segments.Count >= 2);
        // Every segment starts at a keyframe.
        foreach (HlsSegment s in video.Segments)
        {
            string path = Path.Combine(_dir, "one.mp4");
            File.WriteAllBytes(path, [.. File.ReadAllBytes(Path.Combine(output, "video0", "init.mp4")), .. File.ReadAllBytes(Path.Combine(output, "video0", s.Uri))]);
            Assert.True(Packets(path, TrackKind.Video)[0].Key);
        }

        HlsMultivariantPlaylist master = Assert.IsType<HlsMultivariantPlaylist>(HlsPlaylist.Parse(File.ReadAllText(Path.Combine(output, "master.m3u8"))));
        Assert.Single(master.Variants);
        Assert.Contains("avc1.", master.Variants[0].Codecs, StringComparison.Ordinal);
        Assert.Contains("mp4a.40.2", master.Variants[0].Codecs, StringComparison.Ordinal);
        Assert.Equal("audio", master.Variants[0].AudioGroup);

        DashMpd mpd = DashMpd.Parse(File.ReadAllText(Path.Combine(output, "manifest.mpd")));
        Assert.Equal(DashPresentationType.Static, mpd.Type);
        DashRepresentation rep = mpd.Periods[0].AdaptationSets[0].Representations[0];
        IReadOnlyList<DashSegmentRef> refs = rep.SegmentTemplate!.Segments(rep.Id, rep.Bandwidth, mpd.MediaPresentationDuration ?? MediaTime.Unknown);
        Assert.Equal(video.Segments.Count, refs.Count);
        Assert.All(refs, r => Assert.True(File.Exists(Path.Combine(output, r.Uri))));
        Assert.True(File.Exists(Path.Combine(output, rep.SegmentTemplate.Initialization!)));
    }

    [Fact]
    public void SingleFileModeIndexesTheFragments()
    {
        string output = Out("single");
        Packager.Package(Fixture("gop1s-bframes-aac.mp4"), output, new PackagingOptions { SegmentDuration = new MediaTime(1, 1), SingleFile = true });
        Assert.Equal(Packets(Fixture("gop1s-bframes-aac.mp4"), TrackKind.Video), Packets(Path.Combine(output, "video0", "media.mp4"), TrackKind.Video));
        HlsMediaPlaylist video = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(File.ReadAllText(Path.Combine(output, "video0", "index.m3u8"))));
        Assert.True(video.Segments.Count >= 5);
        Assert.All(video.Segments, s => Assert.NotNull(s.ByteRange));
        DashMpd mpd = DashMpd.Parse(File.ReadAllText(Path.Combine(output, "manifest.mpd")));
        DashSegmentBase sb = mpd.Periods[0].AdaptationSets[0].Representations[0].SegmentBase!;
        string[] range = sb.IndexRange!.Split('-');
        byte[] file = File.ReadAllBytes(Path.Combine(output, "video0", "media.mp4"));
        int at = int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("sidx", System.Text.Encoding.ASCII.GetString(file, at + 4, 4));
        Assert.Equal(int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture) + 1, at + ((file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3]));
    }

    [Fact]
    public void LiveKeepsASlidingWindowAndEveryPlaylistIsValid()
    {
        string output = Out("live");
        int updates = 0;
        long lastSequence = -1;
        PackagingOptions options = new()
        {
            SegmentDuration = new MediaTime(1, 1),
            Live = new LiveOptions { StartTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), WindowSegments = 2, ExpiredSegmentsKept = 1 },
            ManifestWritten = path =>
            {
                updates++;
                string text = File.ReadAllText(Path.Combine(output, path));
                if (path == "video0/index.m3u8")
                {
                    HlsMediaPlaylist p = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(text));
                    Assert.True(p.Segments.Count <= 2);
                    Assert.True(p.MediaSequence >= lastSequence);
                    lastSequence = p.MediaSequence;
                    foreach (HlsSegment s in p.Segments)
                    {
                        Assert.True(File.Exists(Path.Combine(output, "video0", s.Uri)), s.Uri);
                        Assert.NotNull(s.ProgramDateTime);
                    }
                }
                else if (path == "manifest.mpd")
                {
                    Assert.True(DashMpd.TryParse(text, out DashMpd? m));
                    // The last write, when the input has ended, is the static MPD.
                    Assert.True(m!.Type == DashPresentationType.Static || m.TimeShiftBufferDepth is not null);
                }
            },
        };
        using MediaFile input = Media.Open(Fixture("gop1s-bframes-aac.mp4"));
        PackagingReport report = Packager.Package(input, output, options);
        Assert.True(updates > 4);
        HlsMediaPlaylist last = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(File.ReadAllText(Path.Combine(output, "video0", "index.m3u8"))));
        Assert.True(last.EndList);
        Assert.True(last.MediaSequence > 0);
        // Segments that left the window and its grace were deleted.
        Assert.True(Directory.GetFiles(Path.Combine(output, "video0"), "seg-*.m4s").Length <= 4);
        Assert.True(report.Tracks[0].Segments > 3);
    }

    [Fact]
    public void WebVttSegmentsCarryTheTimestampMapAndOverlappingCues()
    {
        SubtitleDocument doc = SubtitleFormats.WebVtt.ReadText("WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nfirst\n\n00:00:07.000 --> 00:00:08.000\nsecond\n");
        string a = Packager.WebVttSegment(doc, MediaTime.Zero, new MediaTime(2, 1));
        Assert.StartsWith("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000\n", a, StringComparison.Ordinal);
        Assert.Contains("first", a, StringComparison.Ordinal);
        Assert.DoesNotContain("second", a, StringComparison.Ordinal);
        string c = Packager.WebVttSegment(doc, new MediaTime(2, 1), new MediaTime(4, 1));
        Assert.Contains("first", c, StringComparison.Ordinal); // it still shows at 2 s to 3 s
        string d = Packager.WebVttSegment(doc, new MediaTime(6, 1), MediaTime.Unknown);
        Assert.Contains("second", d, StringComparison.Ordinal);
    }

    [Fact]
    public void ALadderOfInputsSharesOneAdaptationSet()
    {
        string output = Out("ladder");
        PackagingReport report = Packager.Package([Fixture("gop1s-bframes-aac.mp4"), Fixture("gop1s-bframes-aac.mp4")], output, new PackagingOptions { SegmentDuration = new MediaTime(1, 1) });
        Assert.Equal(["video0", "audio0", "video1"], report.Tracks.Select(t => t.Name).ToArray());
        DashMpd mpd = DashMpd.Parse(File.ReadAllText(Path.Combine(output, "manifest.mpd")));
        Assert.Equal(2, mpd.Periods[0].AdaptationSets.Count);
        Assert.Equal(2, mpd.Periods[0].AdaptationSets[0].Representations.Count);
        HlsMultivariantPlaylist master = Assert.IsType<HlsMultivariantPlaylist>(HlsPlaylist.Parse(File.ReadAllText(Path.Combine(output, "master.m3u8"))));
        Assert.Equal(2, master.Variants.Count);
    }

    [Fact]
    public void AnExistingManifestIsNotReplacedUnlessAsked()
    {
        string output = Out("again");
        Packager.Package(Fixture("gop1s-bframes-aac.mp4"), output);
        Assert.Throws<IOException>(() => Packager.Package(Fixture("gop1s-bframes-aac.mp4"), output));
        Packager.Package(Fixture("gop1s-bframes-aac.mp4"), output, new PackagingOptions { Overwrite = true });
    }
}
