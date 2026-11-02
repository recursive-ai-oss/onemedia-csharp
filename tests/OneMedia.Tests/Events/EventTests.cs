using System.Globalization;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.TimedEvents;
using RecursiveAI.OneMedia.Tags.Id3;

namespace RecursiveAI.OneMedia.Tests;

public sealed class EventTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "events", name);

    private static MediaFile Open(string name) => MediaFile.Open(FileMediaSource.Open(Data(name)));

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    // ---- timecode arithmetic ----

    private static TimecodeSampleEntry Entry(int fps, bool drop) => new()
    {
        Flags = drop ? TimecodeAttributes.DropFrame : TimecodeAttributes.None,
        Timescale = (uint)(fps * 1000),
        FrameDuration = 1001,
        FramesPerSecond = fps,
    };

    [Theory]
    [InlineData(0, "00:00:00;00")]
    [InlineData(1799, "00:00:59;29")]
    [InlineData(1800, "00:01:00;02")] // frame numbers 0 and 1 are dropped at the minute
    [InlineData(17981, "00:09:59;29")]
    [InlineData(17982, "00:10:00;00")] // every tenth minute keeps them
    [InlineData(107892, "01:00:00;00")] // an hour is 107892 frames at 29.97
    public void DropFrameCounting(long frame, string expected)
    {
        TimecodeSampleEntry e = Entry(30, true);
        Assert.Equal(expected, e.FromFrameNumber(frame).ToString());
        Assert.Equal(frame, e.ToFrameNumber(e.FromFrameNumber(frame)));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void DropFrameRoundTripsEveryFrameOfTwoHours(int fps)
    {
        TimecodeSampleEntry e = Entry(fps, true);
        Timecode previous = default;
        for (long n = 0; n < fps * 7200L; n++)
        {
            Timecode t = e.FromFrameNumber(n);
            Assert.Equal(n, e.ToFrameNumber(t));
            // A dropped frame number never appears: at the start of a minute that is not a tenth the first address is frame 2 (or 4).
            if (t.Seconds == 0 && t.Minutes % 10 != 0)
            {
                Assert.True(t.Frames >= fps / 15, t.ToString());
            }

            Assert.True(n == 0 || t.ToString() != previous.ToString());
            previous = t;
        }
    }

    [Fact]
    public void NonDropFrameAndNegativeAndWrap()
    {
        TimecodeSampleEntry e = Entry(25, false);
        Assert.Equal("10:00:01:12", e.FromFrameNumber((10 * 3600 * 25) + 25 + 12).ToString());
        Assert.Equal("-00:00:01:00", e.FromFrameNumber(-25).ToString());
        TimecodeSampleEntry wrap = e with { Flags = TimecodeAttributes.Wraps24Hours };
        Assert.Equal("01:00:00:00", wrap.FromFrameNumber((25 * 3600 * 25) + 0).ToString());
    }

    [Fact]
    public void TimecodeParsesBothSeparators()
    {
        Assert.True(Timecode.TryParse("01:02:03;04", out Timecode d));
        Assert.True(d.IsDropFrame);
        Assert.Equal(new Timecode(false, 1, 2, 3, 4, true), d);
        Assert.True(Timecode.TryParse("-10:00:00:12", out Timecode n));
        Assert.False(n.IsDropFrame);
        Assert.True(n.IsNegative);
        Assert.False(Timecode.TryParse("1:2:3", out _));
        Assert.False(Timecode.TryParse("aa:bb:cc:dd", out _));
    }

    [Fact]
    public void SampleEntryParsesNameAndIgnoresTrailingBytes()
    {
        // reserved, flags (drop), timescale 30000, frame duration 1001, 30 frames, reserved, then a name atom and two stray bytes
        byte[] name = [0, 0, 0, 0x10, (byte)'n', (byte)'a', (byte)'m', (byte)'e', 0, 4, 0x15, 0xC7, (byte)'T', (byte)'A', (byte)'P', (byte)'E'];
        byte[] data = [.. Hex("00000000" + "00000001" + "00007530" + "000003E9" + "1E00"), .. name, 0xAA, 0xBB];
        Assert.True(TimecodeSampleEntry.TryParse(data, out TimecodeSampleEntry? e));
        Assert.True(e!.IsDropFrame);
        Assert.Equal(30, e.FramesPerSecond);
        Assert.Equal("TAPE", e.SourceName);
        Assert.Equal(new MediaTime(1001, 30000), e.FrameTime);
        Assert.False(TimecodeSampleEntry.TryParse(data.AsSpan(0, 17), out _));
    }

    [Theory]
    [InlineData("timecode-df.mov", "01:02:03;04", "01:02:04;04", 30, true)]
    [InlineData("timecode-ndf.mp4", "10:00:00:12", "10:00:01:12", 25, false)]
    public void TimecodeTrackInFilesWrittenByFfmpeg(string file, string start, string afterOneSecond, int fps, bool drop)
    {
        using MediaFile f = Open(file);
        EventChannel channel = Assert.Single(f.Events);
        Assert.Equal(MediaEventKind.Timecode, channel.Kind);
        Assert.Equal(fps, channel.Timecode!.FramesPerSecond);
        Assert.Equal(drop, channel.Timecode.IsDropFrame);
        TimecodeEvent first = Assert.IsType<TimecodeEvent>(channel.Events[0]);
        Assert.Equal(start, first.Start.ToString());
        Assert.Equal(MediaTime.Zero, first.Time);
        Assert.True(channel.TryGetTimecodeAt(MediaTime.Zero, out Timecode t0));
        Assert.Equal(start, t0.ToString());
        // One frame duration times the nominal rate is one counted second.
        MediaTime second = new(channel.Timecode.FrameDuration * (long)fps, channel.Timecode.Timescale);
        Assert.True(channel.TryGetTimecodeAt(second, out Timecode t1));
        Assert.Equal(afterOneSecond, t1.ToString());
        // Just before that instant the counter is still one frame short.
        Assert.True(channel.TryGetTimecodeAt(second - new MediaTime(1, channel.Timecode.Timescale), out Timecode before));
        Assert.Equal(fps - 1, FramesBetween(channel.Timecode, t0, before));
    }

    private static long FramesBetween(TimecodeSampleEntry e, Timecode a, Timecode b) => e.ToFrameNumber(b) - e.ToFrameNumber(a);

    [Theory]
    [InlineData("timecode-df.mov", "mov")]
    [InlineData("timecode-df.mov", "mp4")]
    [InlineData("timecode-ndf.mp4", "mov")]
    [InlineData("timecode-ndf.mp4", "mp4")]
    public void RemuxCarriesTheTimecodeTrack(string input, string extension)
    {
        string output = Path.Combine(Path.GetTempPath(), $"onemedia-events-{Guid.NewGuid():N}.{extension}");
        try
        {
            RecursiveAI.OneMedia.Remuxing.RemuxReport report = Media.Remux(Data(input), output);
            Assert.DoesNotContain(report.Losses, l => l.Subject.Contains("tmcd", StringComparison.Ordinal));
            using MediaFile before = Open(input);
            using MediaFile after = MediaFile.Open(FileMediaSource.Open(output));
            EventChannel a = Assert.Single(before.Events);
            EventChannel b = Assert.Single(after.Events);
            Assert.Equal(a.Timecode, b.Timecode);
            TimecodeEvent ea = Assert.IsType<TimecodeEvent>(a.Events[0]);
            TimecodeEvent eb = Assert.IsType<TimecodeEvent>(b.Events[0]);
            Assert.Equal(ea.Start, eb.Start);
            Assert.Equal(ea.FrameNumber, eb.FrameNumber);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void RemuxReportsEmsgAndEventTracksItDoesNotCarry()
    {
        string dir = Path.GetTempPath();
        string mp4 = Path.Combine(dir, $"onemedia-events-{Guid.NewGuid():N}.mp4");
        string fromTs = Path.Combine(dir, $"onemedia-events-{Guid.NewGuid():N}.mp4");
        try
        {
            RecursiveAI.OneMedia.Remuxing.RemuxReport a = Media.Remux(Data("emsg.mp4"), mp4);
            Assert.Equal(3, a.Losses.Count(l => l.Subject == "emsg"));
            Assert.Contains(a.Losses, l => l.Subject == "emsg" && l.Detail.Contains("urn:scte:scte35:2013:bin", StringComparison.Ordinal));

            // the transport stream holds nothing but its two event PIDs, which have no MP4 form: the remux refuses and names both
            MuxRefusedException refused = Assert.Throws<MuxRefusedException>(() => Media.Remux(Data("scte35-id3.ts"), fromTs));
            Assert.Contains("track 0: UnsupportedCodec; track 1: UnsupportedCodec", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(mp4);
            File.Delete(fromTs);
        }
    }

    // ---- SCTE 35 sections ----

    // The 50-byte splice_insert that TSDuck's published listing of a real satellite capture shows (CRC 0xEAA8473F), rebuilt from the listed fields.
    private const string PublishedInsert = "fc302f000000000000fffff01405000000127feffe75c14b4a7e0066ff30000112ff000a00084355454900000012eaa8473f";

    [Fact]
    public void SpliceInsertFromAPublishedCapture()
    {
        DefectLog log = new();
        Assert.True(Scte35Section.TryParse(Hex(PublishedInsert), out Scte35Section? s, log));
        Assert.Empty(log);
        Assert.True(s!.CrcValid);
        Assert.Equal(0xEAA8473Fu, s.Crc32);
        Assert.Equal(0, s.ProtocolVersion);
        Assert.False(s.IsEncrypted);
        Assert.Equal(0, s.PtsAdjustment);
        Assert.Equal(0xFF, s.CwIndex);
        Assert.Equal(0xFFF, s.Tier);
        Assert.Equal(Scte35CommandType.SpliceInsert, s.CommandType);
        Scte35SpliceInsert i = Assert.IsType<Scte35SpliceInsert>(s.Command);
        Assert.Equal(0x12u, i.EventId);
        Assert.False(i.IsCancel);
        Assert.True(i.OutOfNetwork);
        Assert.True(i.ProgramSplice);
        Assert.False(i.SpliceImmediate);
        Assert.Equal(new Scte35SpliceTime(true, 0x075C14B4A), i.SpliceTime);
        Assert.Equal(new Scte35BreakDuration(false, 6750000), i.BreakDuration);
        Assert.Equal(1, i.UniqueProgramId);
        Assert.Equal(0x12, i.AvailNum);
        Assert.Equal(255, i.AvailsExpected);
        Scte35AvailDescriptor d = Assert.IsType<Scte35AvailDescriptor>(Assert.Single(s.Descriptors));
        Assert.Equal(0x43554549u, d.Identifier);
        Assert.Equal(0x12u, d.ProviderAvailId);
        Assert.Equal(0x075C14B4A, s.AdjustedSplicePts);
    }

    [Fact]
    public void CorruptAndShortSectionsAreReportedNotThrown()
    {
        byte[] good = Hex(PublishedInsert);
        byte[] bad = (byte[])good.Clone();
        bad[20] ^= 0xFF;
        DefectLog log = new();
        Assert.True(Scte35Section.TryParse(bad, out Scte35Section? s, log));
        Assert.False(s!.CrcValid);
        Assert.Contains(log, d => d.Code == "scte35.crc");

        log = new DefectLog();
        Assert.True(Scte35Section.TryParse(good.AsSpan(0, 30), out Scte35Section? cut, log));
        Assert.Contains(log, d => d.Kind == DefectKind.Truncated);
        Assert.NotNull(cut);

        Assert.False(Scte35Section.TryParse(good.AsSpan(0, 10), out _));
        Assert.False(Scte35Section.TryParse([0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], out _));
    }

    [Fact]
    public void EncryptedSectionKeepsItsBytesAndDecodesNothing()
    {
        byte[] b = Hex(PublishedInsert);
        b[4] |= 0x80; // encrypted_packet
        Assert.True(Scte35Section.TryParse(b, out Scte35Section? s));
        Assert.True(s!.IsEncrypted);
        Assert.Null(s.Command);
        Assert.Empty(s.Descriptors);
        Assert.Equal(b.Length, s.Data.Length);
    }

    // ---- transport stream with SCTE 35 and ID3 ----

    private const long Clock = 90000;

    private static MediaTime T(long ticks) => new(ticks, Clock);

    [Fact]
    public void TransportStreamListsBothChannels()
    {
        using MediaFile f = Open("scte35-id3.ts");
        Assert.Equal([MediaEventKind.TimedId3, MediaEventKind.Scte35], f.Events.Select(c => c.Kind).ToArray());
        Assert.All(f.Events, c => Assert.NotNull(c.Track));
    }

    [Fact]
    public void ScteMessagesInTransportStream()
    {
        using MediaFile f = Open("scte35-id3.ts");
        // Read the ID3 channel first: the second pass over the file has the ID3 track switched off, and the stream clock must still advance.
        Assert.Equal(6, f.Events.Single(e => e.Kind == MediaEventKind.TimedId3).Events.Count);
        EventChannel c = f.Events.Single(e => e.Kind == MediaEventKind.Scte35);
        IReadOnlyList<Scte35Event> ev = [.. c.Events.Cast<Scte35Event>()];
        Assert.Equal(11, ev.Count);
        Assert.Single(f.Defects, d => d.Code == "scte35.crc"); // exactly one section has a bad CRC (index 10)

        // 0: out-of-network splice_insert with a break duration and an avail descriptor
        Scte35SpliceInsert insert = Assert.IsType<Scte35SpliceInsert>(ev[0].Section.Command);
        Assert.Equal(0x12u, insert.EventId);
        Assert.True(insert.OutOfNetwork && insert.ProgramSplice && !insert.SpliceImmediate);
        Assert.Equal(new Scte35SpliceTime(true, 900000 + (5 * Clock)), insert.SpliceTime);
        Assert.Equal(new Scte35BreakDuration(true, 60 * Clock), insert.BreakDuration);
        Assert.Equal((1, 18, 255), (insert.UniqueProgramId, insert.AvailNum, insert.AvailsExpected));
        Assert.Equal(0x12u, Assert.IsType<Scte35AvailDescriptor>(Assert.Single(ev[0].Section.Descriptors)).ProviderAvailId);
        Assert.True(ev[0].IsSpliceTimeSpecified);
        Assert.Equal(T(900000 + (5 * Clock)), ev[0].Time);
        Assert.Equal(T(60 * Clock), ev[0].Duration);
        Assert.True(ev[0].Section.CrcValid);

        // 1: splice_null has no time of its own, so the event takes the stream clock when it passed (the ID3 PES at 11 s)
        Assert.Equal(Scte35CommandType.SpliceNull, ev[1].Section.CommandType);
        Assert.False(ev[1].IsSpliceTimeSpecified);
        Assert.Equal(T(11 * Clock), ev[1].Time);

        // 2: time_signal with a segmentation descriptor
        Assert.Equal(new Scte35SpliceTime(true, 900000 + (7 * Clock)), Assert.IsType<Scte35TimeSignal>(ev[2].Section.Command).SpliceTime);
        Scte35SegmentationDescriptor seg = Assert.IsType<Scte35SegmentationDescriptor>(Assert.Single(ev[2].Section.Descriptors));
        Assert.Equal(0x1001u, seg.EventId);
        Assert.Equal(0x34, seg.TypeId);
        Assert.Equal(0x0F, seg.UpidType);
        Assert.Equal("urn:example:ad", System.Text.Encoding.ASCII.GetString(seg.Upid.Span));
        Assert.Equal(30 * Clock, seg.Duration);
        Assert.True(seg.ProgramSegmentation && seg.DeliveryNotRestricted);
        Assert.Equal((1, 2, 1, 1), (seg.SegmentNum, seg.SegmentsExpected, seg.SubSegmentNum, seg.SubSegmentsExpected));
        Assert.Equal(T(30 * Clock), ev[2].Duration);

        // 3: immediate
        Scte35SpliceInsert imm = Assert.IsType<Scte35SpliceInsert>(ev[3].Section.Command);
        Assert.True(imm.SpliceImmediate);
        Assert.False(ev[3].IsSpliceTimeSpecified);
        Assert.Equal(T(12 * Clock), ev[3].Time);

        // 4: pts_adjustment wraps modulo 2^33: 8589934000 + 1000000 - 2^33
        Assert.Equal(8589934000, ev[4].Section.SplicePts);
        Assert.Equal(1000000, ev[4].Section.PtsAdjustment);
        Assert.Equal(999408, ev[4].Section.AdjustedSplicePts);
        Assert.Equal(T(999408), ev[4].Time);
        Assert.Equal(0x10, Assert.IsType<Scte35SegmentationDescriptor>(ev[4].Section.Descriptors[0]).TypeId);

        // 5, 6: bandwidth reservation, private command
        Assert.Equal(Scte35CommandType.BandwidthReservation, ev[5].Section.CommandType);
        Scte35PrivateCommand priv = Assert.IsType<Scte35PrivateCommand>(ev[6].Section.Command);
        Assert.Equal(0x4F4E4D44u, priv.Identifier);
        Assert.Equal(new byte[] { 1, 2, 3 }, priv.Data.ToArray());

        // 7: cancel
        Scte35SpliceInsert cancel = Assert.IsType<Scte35SpliceInsert>(ev[7].Section.Command);
        Assert.True(cancel.IsCancel);
        Assert.Equal(0x12u, cancel.EventId);

        // 8: splice_schedule
        Scte35ScheduledSplice sch = Assert.Single(Assert.IsType<Scte35SpliceSchedule>(ev[8].Section.Command).Splices);
        Assert.Equal(0x20u, sch.EventId);
        Assert.True(sch.ProgramSplice);
        Assert.Equal(1700000000u, sch.UtcSpliceTime);
        Assert.Equal(new Scte35BreakDuration(false, 27 * Clock), sch.BreakDuration);
        Assert.Equal((3, 1, 1), (sch.UniqueProgramId, sch.AvailNum, sch.AvailsExpected));

        // 9: return to the network feed
        Scte35SpliceInsert back = Assert.IsType<Scte35SpliceInsert>(ev[9].Section.Command);
        Assert.False(back.OutOfNetwork);
        Assert.Equal(900000 + (65 * Clock), back.SpliceTime.PtsTime);
        Assert.Null(back.BreakDuration);

        // 10: the CRC is wrong; the section is still delivered, flagged
        Assert.False(ev[10].Section.CrcValid);
        Assert.Equal(0x14u, Assert.IsType<Scte35SpliceInsert>(ev[10].Section.Command).EventId);
    }

    [Fact]
    public void ScteTrackPacketsAreTheWholeSections()
    {
        using MediaFile f = Open("scte35-id3.ts");
        MediaTrack t = f.Tracks.Single(x => x.Codec.Name == "scte-35");
        int n = 0;
        foreach (OwnedPacket p in f.Packets(t))
        {
            using (p)
            {
                Assert.Equal(0xFC, p.Data.Span[0]);
                Assert.Equal(3 + (((p.Data.Span[1] & 0xF) << 8) | p.Data.Span[2]), p.Data.Length);
                n++;
            }
        }

        Assert.Equal(11, n);
    }

    [Fact]
    public void TimedId3InTransportStream()
    {
        using MediaFile f = Open("scte35-id3.ts");
        EventChannel c = f.Events.Single(e => e.Kind == MediaEventKind.TimedId3);
        List<TimedId3Event> ev = [.. c.Events.Cast<TimedId3Event>()];
        Assert.Equal([10, 11, 12, 13, 13, 16], ev.Select(e => e.Time.Ticks / Clock));
        Assert.All(ev, e => Assert.Equal(Clock, e.Time.Timescale));

        Id3Frame priv = Assert.Single(ev[0].Tag.Frames);
        Assert.Equal("PRIV", priv.Id);
        Assert.Equal("TXXX", ev[1].Tag.Frames[0].Id);
        Assert.Contains("value1", ev[1].Tag.Frames[0].Values);
        Assert.Equal(["TIT2", "WXXX"], ev[2].Tag.Frames.Select(x => x.Id));
        Assert.Contains("Segment two", ev[2].Tag.Frames[0].Values);
        // two tags in one PES payload become two events at the same time
        Assert.Contains("1", ev[3].Tag.Frames[0].Values);
        Assert.Contains("2", ev[4].Tag.Frames[0].Values);
        Assert.Equal(4, ev[0].Tag.Header.MajorVersion);
    }

    [Fact]
    public void BytesThatAreNotAnId3TagAreReportedAndSkipped()
    {
        // the stream's last ID3 PES (at 17 s) holds four bytes that are not a tag
        using MediaFile f = Open("scte35-id3.ts");
        EventChannel c = f.Events.Single(e => e.Kind == MediaEventKind.TimedId3);
        Assert.Equal(6, c.Events.Count);
        Assert.Contains(f.Defects, d => d.Code == "id3-event.not-a-tag");
    }

    [Fact]
    public void BuilderStreamReadsBackFromMemory()
    {
        byte[] ts = EventBuilder.TransportStream(EventBuilder.SpliceInsert, EventBuilder.Id3Tag("k", "v"), 12 * Clock);
        using MediaFile f = MediaFile.Open(new MemoryMediaSource(ts));
        Scte35Event scte = Assert.IsType<Scte35Event>(Assert.Single(f.Events.Single(c => c.Kind == MediaEventKind.Scte35).Events));
        Assert.Equal(T(0x075C14B4A), scte.Time);
        TimedId3Event id3 = Assert.IsType<TimedId3Event>(Assert.Single(f.Events.Single(c => c.Kind == MediaEventKind.TimedId3).Events));
        Assert.Equal(T(12 * Clock), id3.Time);
        Assert.Contains("v", id3.Tag.Frames[0].Values);
    }

    // ---- emsg in fragmented MP4 ----

    [Fact]
    public void EmsgBoxesInFragmentedMp4()
    {
        string[] expected = File.ReadAllLines(Data("emsg.expected.txt"));
        Assert.Equal(4, expected.Length);
        using MediaFile f = Open("emsg.mp4");
        Assert.Equal([MediaEventKind.Scte35, MediaEventKind.TimedId3, MediaEventKind.Message], f.Events.Select(c => c.Kind).ToArray());
        Assert.All(f.Events, c => Assert.Null(c.Track));

        // the generator lists, per box in file order: scheme, value, id, exact time as a fraction, duration
        MediaTime TimeOf(string line)
        {
            string[] p = line.Split('\t');
            string[] frac = p[3].Split('/');
            return new MediaTime(long.Parse(frac[0], CultureInfo.InvariantCulture), long.Parse(frac[1], CultureInfo.InvariantCulture));
        }

        Scte35Event scte = Assert.IsType<Scte35Event>(Assert.Single(f.Events[0].Events));
        Assert.Equal("urn:scte:scte35:2013:bin", f.Events[0].Scheme);
        Assert.Equal("1001", f.Events[0].Value);
        Assert.Equal(TimeOf(expected[0]), scte.Time);
        Assert.Equal(T(2 * Clock), scte.Time);
        Assert.Equal(7u, scte.Message!.Id);
        Assert.Equal(T(60 * Clock), scte.Duration);
        Assert.Equal(0x12u, Assert.IsType<Scte35SpliceInsert>(scte.Section.Command).EventId);
        Assert.True(scte.Section.CrcValid);

        IReadOnlyList<MediaEvent> id3 = f.Events[1].Events;
        Assert.Equal(2, id3.Count);
        Assert.Equal(TimeOf(expected[1]), id3[0].Time);
        Assert.Equal(new MediaTime(1, 2), id3[0].Time); // delta 500 ms from a segment that starts at 0
        Assert.Equal(TimeOf(expected[3]), id3[1].Time);
        Assert.Contains("one", ((TimedId3Event)id3[0]).Tag.Frames[0].Values);
        Assert.Contains("two", ((TimedId3Event)id3[1]).Tag.Frames[0].Values);
        Assert.True(id3[1].Time > new MediaTime(1, 1)); // the second segment starts after one second

        MessageEvent other = Assert.IsType<MessageEvent>(Assert.Single(f.Events[2].Events));
        Assert.Equal("urn:mpeg:dash:event:2012", other.Message.SchemeIdUri);
        Assert.Equal(TimeOf(expected[2]), other.Time);
        Assert.True(other.TimeIsAbsolute);
        Assert.False(other.Duration.IsKnown);
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(other.Message.MessageData.Span));
    }

    [Fact]
    public void EmsgBoxParsing()
    {
        // version 0: scheme, value, timescale 1000, delta 5, duration 6, id 7, data "xy"
        byte[] v0 = [.. Hex("00000000"), .. "a:b\0v\0"u8.ToArray(), .. Hex("000003E8000000050000000600000007"), (byte)'x', (byte)'y'];
        Assert.True(EventMessageBox.TryParse(v0, 100, out EventMessageBox? b));
        Assert.Equal(("a:b", "v", 1000u, 5u, 6u, 7u), (b!.SchemeIdUri, b.Value, b.Timescale, b.PresentationTimeDelta, b.EventDuration, b.Id));
        Assert.Equal("xy", System.Text.Encoding.ASCII.GetString(b.MessageData.Span));
        Assert.Equal(100, b.Offset);

        DefectLog log = new();
        Assert.False(EventMessageBox.TryParse(v0.AsSpan(0, 10), 0, out _, log));
        Assert.False(EventMessageBox.TryParse([2, 0, 0, 0, 0], 0, out _, log));
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void MatroskaStoresTimecodeOnlyAsATag()
    {
        // ffmpeg -timecode into Matroska writes a TIMECODE tag on the video track and no timecode track: it is a native tag, not an event.
        using MediaFile f = Open("timecode.mkv");
        Assert.Empty(f.Events);
        Assert.Equal("01:02:03;04", f.Native.Find(TagSystems.Matroska, "TIMECODE").Single().Text);
    }

    [Fact]
    public void EmsgDeltaInADifferentTimescaleThanTheSegmentIsRounded()
    {
        // Found by fuzzing: a version 0 message whose timescale does not divide the segment's made the exact sum throw ArithmeticException.
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "events-file", "emsg-delta-timescale-mismatch.bin"));
        MediaOpenOptions open = new() { Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxPacketBytes = 1 << 20, MaxElementCount = 100_000 } };
        using MediaFile f = MediaFile.Open(new MemoryMediaSource(input), null, open);
        foreach (EventChannel c in MediaEvents.List(f, open.Limits))
        {
            _ = c.Events.Count;
        }
    }

    [Fact]
    public void TimecodeLookupAcceptsATimeInAnotherTimescale()
    {
        using MediaFile f = Open("timecode-ndf.mp4");
        EventChannel c = f.Events.Single();
        Assert.True(c.TryGetTimecodeAt(MediaTime.FromTimeSpan(TimeSpan.FromSeconds(0.44)), out Timecode t));
        Assert.Equal("10:00:00:23", t.ToString());
    }
}
