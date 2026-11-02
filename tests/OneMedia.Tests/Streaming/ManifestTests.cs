using RecursiveAI.OneMedia.Streaming;

namespace RecursiveAI.OneMedia.Tests.Streaming;

public sealed class ManifestTests
{
    private const string Vod = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:0
        #EXT-X-PLAYLIST-TYPE:VOD
        #EXT-X-INDEPENDENT-SEGMENTS
        #EXT-X-MAP:URI="init.mp4"
        #EXTINF:6.006,
        seg0.m4s
        #EXT-X-DISCONTINUITY
        #EXT-X-PROGRAM-DATE-TIME:2026-01-02T03:04:05.000Z
        #EXTINF:5.5,first
        seg1.m4s
        #EXT-X-ENDLIST
        """;

    [Fact]
    public void MediaPlaylistParsesAndRewritesIdentically()
    {
        HlsMediaPlaylist p = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(Vod));
        Assert.Equal(2, p.Segments.Count);
        Assert.Equal(new MediaTime(6006, 1000), p.Segments[0].Duration);
        Assert.Equal("init.mp4", p.Segments[0].Map!.Uri);
        Assert.True(p.Segments[1].Discontinuity);
        Assert.Equal("first", p.Segments[1].Title);
        Assert.True(p.EndList);
        Assert.Equal(HlsPlaylistType.Vod, p.PlaylistType);
        string again = p.ToString();
        HlsMediaPlaylist q = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(again));
        Assert.Equal(again, q.ToString());
        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4\"\n", again, StringComparison.Ordinal);
        Assert.Single(again.Split('\n'), l => l.StartsWith("#EXT-X-MAP", StringComparison.Ordinal));
    }

    [Fact]
    public void ByteRangesResolveAgainstThePreviousRange()
    {
        HlsMediaPlaylist p = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse("#EXTM3U\n#EXT-X-VERSION:4\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\n#EXT-X-BYTERANGE:1000@0\nf.ts\n#EXTINF:4,\n#EXT-X-BYTERANGE:500\nf.ts\n#EXT-X-ENDLIST\n"));
        Assert.Equal(new HlsByteRange(1000, 0), p.Segments[0].ByteRange);
        Assert.Equal(new HlsByteRange(500, 1000), p.Segments[1].ByteRange);
        string text = p.ToString();
        Assert.Contains("#EXT-X-BYTERANGE:500\n", text, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-BYTERANGE:1000@0\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionFollowsTheFeaturesUsed()
    {
        HlsMediaPlaylist p = new() { Segments = [new HlsSegment { Uri = "a.ts", Duration = new MediaTime(4, 1) }] };
        Assert.Equal(1, p.MinimumVersion);
        p = new HlsMediaPlaylist { Segments = [new HlsSegment { Uri = "a.m4s", Duration = new MediaTime(4004, 1000), Map = new HlsMap("i.mp4"), ByteRange = new HlsByteRange(10, 0) }] };
        Assert.Equal(6, p.MinimumVersion);
        Assert.Equal(5, new HlsMediaPlaylist { IFramesOnly = true, Segments = [new HlsSegment { Uri = "a", Duration = new MediaTime(4, 1), Map = new HlsMap("i") }] }.MinimumVersion);
    }

    [Fact]
    public void LivePlaylistKeepsUnknownTagsAndTheOpenEnd()
    {
        string text = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-DISCONTINUITY-SEQUENCE:2\n#EXT-X-VENDOR:1\n#EXT-X-DATERANGE:ID=\"x\",START-DATE=\"2026-01-01T00:00:00Z\"\n#EXTINF:2.000,\na.ts\n#EXT-X-PRELOAD-HINT:TYPE=PART,URI=\"b\"\n";
        HlsMediaPlaylist p = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(text));
        Assert.False(p.EndList);
        Assert.Equal(7, p.MediaSequence);
        Assert.Equal(2, p.DiscontinuitySequence);
        Assert.Equal(2, p.OtherTags.Count);
        Assert.Empty(p.Segments[0].OtherTags);
        Assert.Single(p.TrailingTags);
        Assert.Equal(text, p.ToString());
    }

    [Fact]
    public void MultivariantRoundTrips()
    {
        string text = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n"
            + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,AUTOSELECT=YES,CHANNELS=\"2\",URI=\"a/index.m3u8\"\n"
            + "#EXT-X-MEDIA:TYPE=CLOSED-CAPTIONS,GROUP-ID=\"cc\",NAME=\"CC1\",DEFAULT=NO,AUTOSELECT=NO,INSTREAM-ID=\"SERVICE1\"\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2000000,AVERAGE-BANDWIDTH=1800000,CODECS=\"avc1.64001f,mp4a.40.2\",RESOLUTION=1280x720,FRAME-RATE=29.970,AUDIO=\"aud\",CLOSED-CAPTIONS=\"cc\",HDCP-LEVEL=NONE\n"
            + "v/720.m3u8\n"
            + "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=200000,URI=\"v/iframes.m3u8\"\n";
        HlsMultivariantPlaylist m = Assert.IsType<HlsMultivariantPlaylist>(HlsPlaylist.Parse(text));
        Assert.Equal(2, m.Variants.Count);
        Assert.Equal(1280, m.Variants[0].Width);
        Assert.Equal("avc1.64001f,mp4a.40.2", m.Variants[0].Codecs);
        Assert.Equal(["HDCP-LEVEL=NONE"], m.Variants[0].OtherAttributes);
        Assert.True(m.Variants[1].IFrameOnly);
        Assert.Equal(2, m.Renditions.Count);
        Assert.Equal(7, m.EffectiveVersion);
        Assert.Equal(text, m.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("#EXTINF:4,\na.ts\n")]
    [InlineData("#EXTM3U\n#EXTINF:abc,\na.ts\n")]
    [InlineData("#EXTM3U\na.ts\n")]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,CODECS=\"x\n")]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n")]
    public void MalformedPlaylistsAreRefusedWithATypedException(string text)
    {
        Assert.Throws<ManifestFormatException>(() => HlsPlaylist.Parse(text));
        Assert.False(HlsPlaylist.TryParse(text, out HlsPlaylist? p));
        Assert.Null(p);
    }

    [Fact]
    public void TotalDurationAddsSegmentsWrittenWithDifferentFractions()
    {
        // Found by fuzzing: durations of 2.5 and 1.333 s are in different power-of-ten timescales and cannot be added exactly in either.
        HlsMediaPlaylist p = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse("#EXTM3U\n#EXTINF:2.5,\na\n#EXTINF:1.333,\nb\n"));
        Assert.Equal(3.833, p.TotalDuration.Ticks / (double)p.TotalDuration.Timescale, 6);
    }

    [Fact]
    public void SecondsAreExact()
    {
        Assert.True(ManifestText.TryParseSeconds("0.0416667", out MediaTime t));
        Assert.Equal(new MediaTime(416667, 10_000_000), t);
        Assert.Equal("6.006", ManifestText.Seconds(new MediaTime(6006, 1000)));
        Assert.Equal("2.000", ManifestText.Seconds(new MediaTime(2, 1)));
        Assert.Equal("0.041708", ManifestText.Seconds(new MediaTime(1001, 24000)));
    }

    private const string Mpd = """
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic" profiles="urn:mpeg:dash:profile:isoff-live:2011" minBufferTime="PT2S" availabilityStartTime="2026-01-01T00:00:00Z" minimumUpdatePeriod="PT4S" timeShiftBufferDepth="PT30S">
          <Period id="0" start="PT0S">
            <AdaptationSet contentType="video" mimeType="video/mp4" segmentAlignment="true" startWithSAP="1">
              <SegmentTemplate timescale="90000" media="v_$Number%05d$.m4s" initialization="v_init.mp4" startNumber="5">
                <SegmentTimeline><S t="450000" d="180000" r="2"/><S d="90000"/></SegmentTimeline>
              </SegmentTemplate>
              <Representation id="v1" bandwidth="800000" codecs="avc1.64001f" width="640" height="360" frameRate="25"/>
            </AdaptationSet>
            <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
              <Role schemeIdUri="urn:mpeg:dash:role:2011" value="main"/>
              <ContentProtection schemeIdUri="urn:x"/>
              <Representation id="a1" bandwidth="128000" codecs="mp4a.40.2" audioSamplingRate="48000">
                <AudioChannelConfiguration schemeIdUri="urn:mpeg:dash:23003:3:audio_channel_configuration:2011" value="2"/>
                <BaseURL>a/</BaseURL>
                <SegmentBase indexRange="800-1500"><Initialization range="0-799"/></SegmentBase>
              </Representation>
            </AdaptationSet>
          </Period>
          <UTCTiming schemeIdUri="urn:mpeg:dash:utc:direct:2014" value="2026-01-01T00:00:00Z"/>
        </MPD>
        """;

    [Fact]
    public void MpdParsesAndRewrites()
    {
        DashMpd m = DashMpd.Parse(Mpd);
        Assert.Equal(DashPresentationType.Dynamic, m.Type);
        Assert.Equal(new MediaTime(30, 1), m.TimeShiftBufferDepth!.Value.Rescale(1, MediaRounding.Floor));
        DashAdaptationSet v = m.Periods[0].AdaptationSets[0];
        Assert.True(v.SegmentAlignment);
        Assert.Equal(2, v.SegmentTemplate!.Timeline.Count);
        DashAdaptationSet a = m.Periods[0].AdaptationSets[1];
        Assert.Equal("main", a.Descriptors[0].Value);
        Assert.Single(a.OtherElements);
        Assert.Equal("800-1500", a.Representations[0].SegmentBase!.IndexRange);
        Assert.Single(m.UtcTimings);
        string text = m.ToString();
        DashMpd again = DashMpd.Parse(text);
        Assert.Equal(text, again.ToString());
        // The schema puts ContentProtection before Role's siblings only after the shared properties and before Accessibility/Role.
        Assert.True(text.IndexOf("ContentProtection", StringComparison.Ordinal) < text.IndexOf("<Role", StringComparison.Ordinal));
    }

    [Fact]
    public void TemplateExpansionFollowsTheTimeline()
    {
        DashMpd m = DashMpd.Parse(Mpd);
        DashRepresentation r = m.Periods[0].AdaptationSets[0].Representations[0];
        IReadOnlyList<DashSegmentRef> segs = m.Periods[0].AdaptationSets[0].SegmentTemplate!.Segments(r.Id, r.Bandwidth, MediaTime.Unknown);
        Assert.Equal(4, segs.Count);
        Assert.Equal("v_00005.m4s", segs[0].Uri);
        Assert.Equal(450000 + (2 * 180000), segs[2].Time);
        Assert.Equal(8, segs[3].Number);
        Assert.Equal("a$b-7-x", DashSegmentTemplate.Expand("a$$b-$Number$-$RepresentationID$", "x", 1, 7, 0));
    }

    [Fact]
    public void DurationsRoundTrip()
    {
        Assert.True(DashMpd.TryParseDuration("PT1H2M3.5S", out MediaTime t));
        Assert.Equal(3723.5, t.Ticks / (double)t.Timescale);
        Assert.Equal("PT1H2M3.5S", DashMpd.FormatDuration(t));
        Assert.Equal("PT0S", DashMpd.FormatDuration(MediaTime.Zero));
        Assert.Equal("PT0.041S", DashMpd.FormatDuration(new MediaTime(41, 1000)));
        Assert.False(DashMpd.TryParseDuration("1S", out _));
        Assert.False(DashMpd.TryParseDuration("PT", out _));
        Assert.False(DashMpd.TryParseDuration("PT5", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<MPD")]
    [InlineData("<Other/>")]
    [InlineData("<!DOCTYPE MPD [<!ENTITY a 'b'>]><MPD/>")]
    [InlineData("<MPD minBufferTime='x'/>")]
    [InlineData("<MPD><Period><AdaptationSet><Representation/></AdaptationSet></Period></MPD>")]
    public void MalformedMpdsAreRefusedWithATypedException(string xml)
    {
        Assert.Throws<ManifestFormatException>(() => DashMpd.Parse(xml));
        Assert.False(DashMpd.TryParse(xml, out _));
    }

    private const string Encrypted = """
        #EXTM3U
        #EXT-X-VERSION:5
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:0
        #EXT-X-KEY:METHOD=AES-128,URI="k1.bin",IV=0x0123456789ABCDEF0123456789ABCDEF
        #EXTINF:6.000,
        a.ts
        #EXTINF:6.000,
        b.ts
        #EXT-X-KEY:METHOD=SAMPLE-AES,URI="skd://x",KEYFORMAT="com.apple.streamingkeydelivery",KEYFORMATVERSIONS="1"
        #EXTINF:6.000,
        c.ts
        #EXT-X-KEY:METHOD=NONE
        #EXTINF:6.000,
        d.ts
        #EXT-X-ENDLIST
        """;

    [Fact]
    public void KeysApplyFromWhereTheyAreDeclaredAndRewrite()
    {
        HlsMediaPlaylist p = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(Encrypted));
        Assert.Empty(p.OtherTags);
        Assert.True(p.Segments[0].KeysChanged);
        HlsKey first = Assert.Single(p.Segments[0].Keys);
        Assert.Equal(HlsKeyMethod.Aes128, first.Method);
        Assert.Equal("k1.bin", first.Uri);
        Assert.Equal("0x0123456789ABCDEF0123456789ABCDEF", first.Iv);
        Assert.False(p.Segments[1].KeysChanged);
        Assert.Equal(first, Assert.Single(p.Segments[1].Keys));
        Assert.True(p.Segments[2].KeysChanged);
        // a different KEYFORMAT does not replace the identity key, but SAMPLE-AES with the default format would; here both apply.
        Assert.Equal(2, p.Segments[2].Keys.Count);
        Assert.Equal("com.apple.streamingkeydelivery", p.Segments[2].Keys[1].KeyFormat);
        Assert.True(p.Segments[3].KeysChanged);
        Assert.Empty(p.Segments[3].Keys);
        // A rewrite lists every key in effect where the keys change, so the text may differ; the meaning does not.
        HlsMediaPlaylist again = Assert.IsType<HlsMediaPlaylist>(HlsPlaylist.Parse(p.ToString()));
        for (int i = 0; i < p.Segments.Count; i++)
        {
            Assert.Equal(p.Segments[i].Keys.Select(k => k.ToString()), again.Segments[i].Keys.Select(k => k.ToString()));
        }
    }

    [Fact]
    public void LenientReadReportsDefectsWithLinesAndOffsets()
    {
        const string text = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:oops\nsegment0.ts\n#EXTINF:6,\nsegment1.ts\n";
        Assert.Throws<ManifestFormatException>(() => HlsPlaylist.Parse(text));
        HlsParseResult r = HlsPlaylist.ParseLenient(text);
        HlsMediaPlaylist p = Assert.IsType<HlsMediaPlaylist>(r.Playlist);
        Assert.Equal(2, p.Segments.Count);
        Assert.Equal(2, r.Defects.Count);
        Assert.Equal(ManifestDefectKind.MalformedTag, r.Defects[0].Kind);
        Assert.Equal(3, r.Defects[0].Line);
        Assert.Equal(text.IndexOf("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal), r.Defects[0].Offset);
        Assert.Equal(ManifestDefectKind.SegmentWithoutDuration, r.Defects[1].Kind);
        Assert.Equal(4, r.Defects[1].Line);
        Assert.Equal(text.IndexOf("segment0.ts", StringComparison.Ordinal), r.Defects[1].Offset);
        Assert.Empty(HlsPlaylist.ParseLenient(Vod).Defects);
        Assert.Contains(HlsPlaylist.ParseLenient("segment0.ts\n").Defects, d => d.Kind == ManifestDefectKind.MissingPlaylistHeader);
    }

    [Fact]
    public void PresentationTimeOffsetIsAppliedToSegmentTimes()
    {
        DashSegmentTemplate t = new() { Timescale = 1000, Duration = 2000, StartNumber = 1, PresentationTimeOffset = 90_000, Media = "s-$Time$.m4s" };
        IReadOnlyList<DashSegmentRef> list = t.Segments("v", 1, new MediaTime(6, 1));
        Assert.Equal(3, list.Count);
        Assert.Equal(90_000, list[0].Time);
        Assert.Equal(0, list[0].PresentationStart);
        Assert.Equal(92_000, list[1].Time);
        Assert.Equal("s-92000.m4s", list[1].Uri);
        DashSegmentTemplate tl = t with { Duration = 0, Timeline = [new DashTimelineEntry(90_000, 2000, 1)] };
        IReadOnlyList<DashSegmentRef> byTimeline = tl.Segments("v", 1, MediaTime.Unknown);
        Assert.Equal([90_000L, 92_000L], byTimeline.Select(x => x.Time));
        Assert.Equal([0L, 2000L], byTimeline.Select(x => x.PresentationStart));
    }
}
