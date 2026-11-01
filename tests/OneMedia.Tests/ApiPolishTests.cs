using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tool;

namespace RecursiveAI.OneMedia.Tests;

public class ApiPolishTests
{
    private static string Data(string dir, string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", dir, name);

    // A stream that cannot seek and hands out few bytes at a time, like a pipe.
    private sealed class PipeStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 61));
        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, 61)]);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- iTunes (c)cpy ----

    [Fact]
    public void ItunesCopyrightAcceptsTheQuickTimeAtomName()
    {
        TagMappingRow row = TagMappingTable.Rows.Single(r => r.Field == CommonField.Copyright && r.System == TagSystems.ITunes);
        Assert.Contains("cprt", row.Keys);
        Assert.Contains("\u00a9cpy", row.Keys);
        // cprt stays first: a file with both gives the standard atom's value.
        Assert.Equal("cprt", row.Keys[0]);
    }

    // ---- presentation time ----

    private static MediaTrack Track(TrackEditInfo edit) => new() { Index = 0, Kind = TrackKind.Audio, Timescale = 44100, Edit = edit };

    [Fact]
    public void PresentationTimeSubtractsPrimingAndAddsTheEditListsLeadingGap()
    {
        MediaTrack t = Track(new TrackEditInfo(Priming: new MediaTime(2112, 44100), StartTime: new MediaTime(1, 1), Edits: [new EditSegment(new MediaTime(2112, 44100), new MediaTime(10, 1))]));
        MediaTime p = t.PresentationTime(new MediaTime(1024, 44100));
        Assert.Equal(44100 + 1024 - 2112, p.Ticks);
        Assert.Equal(44100, p.Timescale);
    }

    [Fact]
    public void PresentationTimeDoesNotAddTheStartTimeOfStreamsWhosePacketTimesCarryIt()
    {
        // Transport streams report the first time stamp as StartTime and leave packet times unrebased.
        MediaTrack t = Track(new TrackEditInfo(StartTime: new MediaTime(90000, 90000)));
        Assert.Equal(new MediaTime(500, 44100), t.PresentationTime(new MediaTime(500, 44100)));
    }

    [Fact]
    public void PresentationTimeOfMatroskaCodecDelayWorksAcrossTimescales()
    {
        MediaTrack t = new() { Index = 0, Kind = TrackKind.Audio, Timescale = 1_000_000_000, Edit = new TrackEditInfo(Priming: new MediaTime(6_500_000, 1_000_000_000)) };
        PacketInfo packet = new(0, new MediaTime(20_000_000, 1_000_000_000), default, default, PacketAttributes.None, ByteRange.None, 0);
        Assert.Equal(new MediaTime(13_500_000, 1_000_000_000), t.PresentationTime(packet));
        Assert.False(t.PresentationTime(MediaTime.Unknown).IsKnown);
    }

    [Fact]
    public void PresentationTimeRoundsOnlyWhereTheUnitsDoNotDivide()
    {
        MediaTrack t = Track(new TrackEditInfo(Priming: new MediaTime(1, 3)));
        Assert.Equal(44100 - 14700, t.PresentationTime(new MediaTime(44100, 44100)).Ticks);
        MediaTrack u = new() { Index = 0, Kind = TrackKind.Audio, Timescale = 10, Edit = new TrackEditInfo(Priming: new MediaTime(1, 3)) };
        Assert.Equal(7, u.PresentationTime(new MediaTime(10, 10)).Ticks);
        Assert.Equal(6, u.PresentationTime(new MediaTime(10, 10), MediaRounding.Floor).Ticks);
    }

    [Fact]
    public void PresentationTimeOnAnIsoFileMatchesTheDocumentedFormula()
    {
        using MediaFile media = Media.Open(Data("isobmff", "bframes_aac.mp4"));
        MediaTrack audio = media.Audio!;
        using IEnumerator<OwnedPacket> e = media.Packets(audio).GetEnumerator();
        Assert.True(e.MoveNext());
        using OwnedPacket first = e.Current;
        MediaTime expected = first.Info.Pts.Subtract(audio.Edit.Priming, MediaRounding.NearestAwayFromZero).Add(audio.Edit.StartTime, MediaRounding.NearestAwayFromZero);
        Assert.Equal(expected, audio.PresentationTime(first.Info));
    }

    // ---- Media.Open on a stream that cannot seek ----

    [Fact]
    public void OpenTakesANonSeekableMatroskaStreamAndReadsItOnce()
    {
        string path = Data("metadata", "mkvmerge_global.mkv");
        List<(long Ticks, int Size)> expected = [];
        using (MediaFile seekable = Media.Open(path))
        {
            foreach (OwnedPacket p in seekable.Packets(seekable.Tracks[0]))
            {
                using (p)
                {
                    expected.Add((p.Info.Pts.Ticks, p.Info.Size));
                }
            }
        }

        using MediaFile media = Media.Open(new PipeStream(File.ReadAllBytes(path)));
        Assert.Equal(MediaFormatId.Matroska, media.Format);
        Assert.False(media.Demuxer!.CanSeek);
        List<(long Ticks, int Size)> got = [];
        foreach (OwnedPacket p in media.Packets(media.Tracks[0]))
        {
            using (p)
            {
                got.Add((p.Info.Pts.Ticks, p.Info.Size));
            }
        }

        Assert.Equal(expected, got);
        MediaReadStateException ex = Assert.Throws<MediaReadStateException>(() => media.Packets(media.Tracks[0]).First());
        Assert.Equal(MediaReadStateReason.CannotRewind, ex.Reason);
    }

    [Fact]
    public void OpenTakesANonSeekableFragmentedMp4()
    {
        using MediaFile media = Media.Open(new PipeStream(File.ReadAllBytes(Data("isobmff", "frag.mp4"))));
        Assert.Equal(MediaFormatId.IsoBmff, media.Format);
        Assert.NotEmpty(media.Tracks);
        int n = 0;
        foreach (OwnedPacket p in media.Packets(media.Tracks[0]))
        {
            p.Dispose();
            n++;
        }

        Assert.True(n > 0);
    }

    [Fact]
    public void OpenTakesANonSeekableTransportStream()
    {
        using MediaFile media = Media.Open(new PipeStream(File.ReadAllBytes(Data("mpeg", "h264-aac.m2ts"))));
        Assert.Equal(MediaFormatId.MpegTs, media.Format);
        int n = 0;
        foreach (OwnedPacket p in media.Packets(media.Tracks[0]))
        {
            p.Dispose();
            n++;
        }

        Assert.True(n > 0);
    }

    // A format whose reader needs to seek: it has the seekable Open only.
    private sealed class SeekOnlyFormat : IMediaFormat
    {
        public MediaFormatId Id => new("seekonly");
        public string DisplayName => "Seek-only test format";
        public SniffResult Sniff(ReadOnlySpan<byte> head) => head.StartsWith("SEEK"u8) ? new SniffResult(SniffConfidence.Certain) : SniffResult.None;
        public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("not reached");
    }

    [Fact]
    public void OpenNamesTheFormatThatNeedsSeekingWhenTheStreamCannot()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new SeekOnlyFormat());
        UnsupportedMediaException ex = Assert.Throws<UnsupportedMediaException>(() => MediaFile.Open(new StreamForwardSource(new PipeStream("SEEKxxxxxxxx"u8.ToArray())), registry));
        Assert.Contains("Seek-only test format", ex.Message);
        Assert.Contains("cannot seek", ex.Message);
        Assert.Throws<UnsupportedMediaException>(() => Media.Open(new PipeStream("not media at all"u8.ToArray())));
    }

    [Fact]
    public void OpenDisposesTheNonSeekableStreamWhenOpeningFailsUnlessLeftOpen()
    {
        TrackingStream closed = new("not media at all"u8.ToArray());
        Assert.Throws<UnsupportedMediaException>(() => Media.Open(closed));
        Assert.True(closed.Disposed);
        TrackingStream kept = new("not media at all"u8.ToArray());
        Assert.Throws<UnsupportedMediaException>(() => Media.Open(kept, leaveOpen: true));
        Assert.False(kept.Disposed);
    }

    private sealed class TrackingStream(byte[] data) : Stream
    {
        private readonly PipeStream _inner = new(data);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // ---- synchronous read of an asynchronously opened file ----

    [Fact]
    public async Task PacketsOnAnAsyncFileIsATypedStateError()
    {
        await using MediaFile media = await Media.OpenAsync(Data("isobmff", "frag.mp4"));
        MediaReadStateException ex = Assert.Throws<MediaReadStateException>(() => media.Packets(media.Tracks[0]).First());
        Assert.Equal(MediaReadStateReason.AsynchronousSource, ex.Reason);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    // ---- RFC 6381 strings without a configuration ----

    [Theory]
    [InlineData("vp8", null, "vp8")]
    [InlineData("tx3g", "tx3g", "tx3g")]
    [InlineData("webvtt", "wvtt", "wvtt")]
    [InlineData("ttml", "stpp", "stpp")]
    [InlineData("tx3g", "text", null)]
    [InlineData("subrip", "S_TEXT/UTF8", null)]
    [InlineData("webvtt", "S_TEXT/WEBVTT", null)]
    [InlineData("pgs", "S_HDMV/PGS", null)]
    public void CodecStringsExistWhereOneIsRegistered(string codec, string? tag, string? expected)
    {
        MediaTrack t = new() { Index = 0, Kind = TrackKind.Subtitle, Timescale = 1000, Codec = new CodecId(codec), CodecTag = tag };
        Assert.Equal(expected, t.Rfc6381Codec());
    }

    [Fact]
    public void ConfiguredCodecsKeepTheirParsedString()
    {
        using MediaFile media = Media.Open(Data("isobmff", "bframes_aac.mp4"));
        foreach (MediaTrack t in media.Tracks)
        {
            Assert.StartsWith(t.Kind == TrackKind.Video ? "avc1." : "mp4a.40.", t.Rfc6381Codec());
        }
    }

    // ---- command line wording ----

    [Theory]
    [InlineData(200000000, 8333333, "24")]
    [InlineData(1000000000, 41666666, "24")]
    [InlineData(1000000000, 41708333, "24000/1001 (23.976)")]
    [InlineData(30000, 1001, "30000/1001 (29.97)")]
    [InlineData(25, 1, "25")]
    [InlineData(1000000000, 33333333, "30")]
    [InlineData(1000000000, 33366666, "30000/1001 (29.97)")]
    [InlineData(1000000000, 53333333, "18.75")]
    public void FrameRatesAreReducedOrSnappedToTheirBroadcastRate(long n, long d, string expected)
    {
        string shown = CliFormat.FrameRate(n, d);
        if (expected == "18.75")
        {
            Assert.Contains("18.75", shown);
        }
        else
        {
            Assert.Equal(expected, shown);
        }
    }

    [Fact]
    public void HeAacLineNamesTheCoreAndOutputRates()
    {
        using MediaFile media = Media.Open(Data("heaac", "sbr-compatible.m4a"));
        Assert.Equal("AAC-LC core 22,050 Hz; HE-AAC output 44,100 Hz (backward-compatible signalling)", CliFormat.HeAac(media.Audio!));
        using MediaFile implied = Media.Open(Data("heaac", "sbr-implied.m4a"));
        string hint = CliFormat.HeAac(implied.Audio!)!;
        Assert.Contains("twice the AAC core rate of 22,050 Hz", hint);
        Assert.Contains("not proof", hint);
    }
}
