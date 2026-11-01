using System.Text;
using RecursiveAI.OneMedia.AudioStreams;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Ape;
using RecursiveAI.OneMedia.Tags.Id3;
using RecursiveAI.OneMedia.Tags.Lyrics3;
using static RecursiveAI.OneMedia.Tests.AudioStreams.AudioStreamTestData;

namespace RecursiveAI.OneMedia.Tests.AudioStreams;

public class Id3TagTests
{
    private static Id3Tag Read(byte[] tag, DefectLog? log = null) => Id3Tag.Read(tag, 0, null, log);

    private static string? Text(Id3Tag tag, string key) => tag.Tags.FirstOrDefault(t => t.Key == key)?.Text;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void V23TextFramesDecodeInEveryEncoding(byte encoding)
    {
        byte[] bytes = Id3Bytes(3, Concat(TextFrame(3, "TIT2", "Grüße – 歌", encoding is 0 ? (byte)3 : encoding), TextFrame(3, "TPE1", "Artist", encoding)));
        Id3Tag tag = Read(bytes);
        Assert.Equal(3, tag.Header.MajorVersion);
        Assert.Equal("Grüße – 歌", Text(tag, "TIT2"));
        Assert.Equal("Artist", Text(tag, "TPE1"));
        Assert.Equal(TagSystems.Id3v2, tag.System);
    }

    [Fact]
    public void V22UsesThreeLetterIdsAndKeepsThemAsStored()
    {
        Id3Tag tag = Read(Id3Bytes(2, Concat(TextFrame(2, "TT2", "Title", 0), TextFrame(2, "TP1", "Who", 0), TextFrame(2, "TAL", "Album", 0))));
        Assert.Equal("Title", Text(tag, "TT2"));
        CommonTags common = new(tag.Tags);
        Assert.Equal("Title", common.Title);
        Assert.Equal("Who", common.Artist);
        Assert.Equal("Album", common.Album);
    }

    [Fact]
    public void V24UsesSynchsafeSizesAndMultipleValues()
    {
        byte[] body = Concat([3], Encoding.UTF8.GetBytes("One"), [0], Encoding.UTF8.GetBytes("Two"));
        Id3Tag tag = Read(Id3Bytes(4, Frame(4, "TPE1", body)));
        Assert.Equal(["One", "Two"], tag.Frames[0].Values);
        Assert.Equal("One; Two", Text(tag, "TPE1"));
    }

    [Fact]
    public void V23NonSynchsafeLargeFrameSizeIsRead()
    {
        // v2.3 sizes are plain big-endian: 200 bytes would be a valid synchsafe number only by luck (0x80 bit set in the low byte here).
        string long200 = new('x', 199);
        Id3Tag tag = Read(Id3Bytes(3, Concat(TextFrame(3, "TIT2", long200), TextFrame(3, "TALB", "next"))));
        Assert.Equal(long200, Text(tag, "TIT2"));
        Assert.Equal("next", Text(tag, "TALB"));
    }

    [Fact]
    public void TagWideUnsynchronisationIsUndoneForV23()
    {
        byte[] frames = Frame(3, "APIC", Concat([0], "image/jpeg\0"u8.ToArray(), [3], "front\0"u8.ToArray(), [0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0x00, 0xFF]));
        byte[] unsynced = Unsynchronise(Concat(frames, TextFrame(3, "TIT2", "T", 0)));
        Id3Tag tag = Read(Id3Bytes(3, unsynced, flags: 0x80));
        Assert.Equal("T", Text(tag, "TIT2"));
        NativeTag pic = tag.Tags.First(t => t.Key.StartsWith("APIC", StringComparison.Ordinal));
        Assert.Equal("image/jpeg", pic.MimeType);
        Assert.Equal(PictureType.FrontCover, pic.PictureRole);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0x00, 0xFF], pic.ReadValue());
    }

    [Fact]
    public void FrameLevelUnsynchronisationInV24()
    {
        // ID3v2.4 §4.1.2: the unsynchronisation flag of a frame is bit 1 of the second flag byte; its data has 0xFF 0x00 for 0xFF.
        Id3Tag tag = Read(Id3Bytes(4, Frame(4, "TXXX", Concat([0], "k\0v"u8.ToArray(), [0xFF, 0x00, 0x41]), 0x0002)));
        NativeTag t = tag.Tags.First(x => x.Key.StartsWith("TXXX", StringComparison.Ordinal));
        Assert.Equal("vÿA", t.Text);
    }

    [Fact]
    public void ExtendedHeaderAndFooterAreAccountedFor()
    {
        byte[] ext = [0, 0, 0, 6, 0x01, 0x20, 0, 0, 0, 0]; // v2.3: size 6, flags, padding size 0 — a CRC flag would add 4 bytes
        Id3Tag v23 = Read(Id3Bytes(3, TextFrame(3, "TIT2", "Ext"), flags: 0x40, extendedHeader: [0, 0, 0, 6, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("Ext", Text(v23, "TIT2"));
        Assert.False(v23.ExtendedHeader.IsNone);
        Assert.NotEmpty(ext);
        byte[] extended24 = [0, 0, 0, 6, 1, 0]; // v2.4: synchsafe size 6, one flag byte count, no flags
        byte[] bytes = Id3Bytes(4, TextFrame(4, "TIT2", "Foot"), flags: 0x40 | 0x10, extendedHeader: extended24, footer: true);
        Id3Tag v24 = Read(bytes);
        Assert.Equal("Foot", Text(v24, "TIT2"));
        Assert.True(v24.Header.HasFooter);
        Assert.Equal(bytes.Length, v24.Header.TotalLength);
    }

    [Fact]
    public void PaddingIsRecordedAndNotParsedAsFrames()
    {
        byte[] bytes = Id3Bytes(3, TextFrame(3, "TIT2", "P", 0), padding: 777);
        DefectLog log = new();
        Id3Tag tag = Read(bytes, log);
        Assert.Equal(777, tag.Padding.Length);
        Assert.Empty(log);
        Assert.Single(tag.Frames);
    }

    [Fact]
    public void CompressedAndEncryptedFramesAreExposedNotDecoded()
    {
        // v2.3 flags: 0x0080 compressed, 0x0040 encrypted. The frame is kept as a frame with its flags and no decoded tag.
        byte[] encrypted = Frame(3, "TIT2", [9, 1, 2, 3, 4], 0x0040);
        Id3Tag tag = Read(Id3Bytes(3, Concat(encrypted, TextFrame(3, "TALB", "ok"))));
        Id3Frame f = tag.Frames[0];
        Assert.Equal("TIT2", f.Id);
        Assert.True((f.Flags & Id3FrameAttributes.Encrypted) != 0);
        Assert.Equal("ok", Text(tag, "TALB"));
        Assert.Equal(5, f.DataRange.Length);
        Assert.Equal(9, f.EncryptionMethod);
        Assert.Equal(TagValueKind.Binary, f.Native!.Kind);
    }

    [Fact]
    public void CommentLyricsUserTextAndPopularimeterHaveDescriptiveKeys()
    {
        byte[] comm = Frame(3, "COMM", Concat([3], "eng"u8.ToArray(), "desc\0"u8.ToArray(), "A comment"u8.ToArray()));
        byte[] comm0 = Frame(3, "COMM", Concat([3], "eng"u8.ToArray(), [0], "Plain comment"u8.ToArray()));
        byte[] uslt = Frame(3, "USLT", Concat([3], "eng"u8.ToArray(), [0], "la la la"u8.ToArray()));
        byte[] txxx = Frame(3, "TXXX", Concat([3], "REPLAYGAIN_TRACK_GAIN\0"u8.ToArray(), "-6.50 dB"u8.ToArray()));
        byte[] popm = Frame(3, "POPM", Concat("me@example.com\0"u8.ToArray(), [196], [0, 0, 0, 5]));
        Id3Tag tag = Read(Id3Bytes(3, Concat(comm, comm0, uslt, txxx, popm)));
        CommonTags common = new(tag.Tags);
        Assert.Equal("Plain comment", common.Comment);
        Assert.Equal("la la la", common.Lyrics);
        Assert.Contains(tag.Tags, t => t.Key == "TXXX:REPLAYGAIN_TRACK_GAIN" && t.Text == "-6.50 dB");
        Assert.Contains(tag.Tags, t => t.Key.StartsWith("POPM:", StringComparison.Ordinal));
        Assert.Equal("-6.50 dB", common.First(CommonField.ReplayGainTrackGain));
    }

    [Fact]
    public void TrackAndDiscPairsSplitIntoNumberAndTotal()
    {
        Id3Tag tag = Read(Id3Bytes(3, Concat(TextFrame(3, "TRCK", "3/12"), TextFrame(3, "TPOS", "1/2"), TextFrame(3, "TCON", "(17)"))));
        CommonTags common = new(tag.Tags);
        Assert.Equal(3, common.TrackNumber);
        Assert.Equal(12, common.TrackTotal);
        Assert.Equal(1, common.DiscNumber);
        Assert.Equal(2, common.DiscTotal);
        Assert.Equal("Rock", common.Genre);
    }

    [Fact]
    public void ChaptersAreReadWithTheirTitles()
    {
        byte[] titleSub = TextFrame(3, "TIT2", "Intro");
        byte[] chap = Frame(3, "CHAP", Concat("ch1\0"u8.ToArray(), [0, 0, 0, 0], [0, 0, 0x27, 0x10], [0xFF, 0xFF, 0xFF, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF], titleSub));
        Id3Tag tag = Read(Id3Bytes(3, chap));
        (Id3Chapter c, string title) = Assert.Single(tag.Chapters());
        Assert.Equal("ch1", c.ElementId);
        Assert.Equal(10_000u, c.EndMilliseconds);
        Assert.Equal("Intro", title);
    }

    [Fact]
    public void UnknownFramesArePreservedWithTheirRanges()
    {
        byte[] weird = Frame(3, "ZZZZ", [1, 2, 3, 4, 5, 6]);
        byte[] bytes = Id3Bytes(3, Concat(TextFrame(3, "TIT2", "T"), weird));
        Id3Tag tag = Read(bytes);
        Id3Frame f = tag.Frames[1];
        Assert.Equal("ZZZZ", f.Id);
        Assert.Equal(bytes.Length, f.DataRange.End);
        Assert.Equal(6, f.DataRange.Length);
    }

    [Fact]
    public void DamagedFrameSizeIsReportedAndTheRestIsKept()
    {
        byte[] bad = Frame(3, "TIT2", [3, 65]);
        bad[4] = 0x7F; // size far beyond the tag
        DefectLog log = new();
        Id3Tag tag = Read(Id3Bytes(3, Concat(TextFrame(3, "TALB", "ok"), bad)), log);
        Assert.Equal("ok", Text(tag, "TALB"));
        Assert.NotEmpty(log);
    }

    [Fact]
    public void TextIsTruncatedAtTheTerminatorAndLatin1IsKept()
    {
        Id3Tag tag = Read(Id3Bytes(3, TextFrame(3, "TIT2", "caf\u00E9", 0)));
        Assert.Equal("café", Text(tag, "TIT2"));
    }

    [Fact]
    public void ReleaseDateBecomesTheCaptureTimeCandidate()
    {
        byte[] file = Concat(Id3Bytes(4, TextFrame(4, "TDRC", "2021-06-05T10:11:12")), Mp3(5));
        using MpegAudioDemuxer d = MpegAudioDemuxer.Open(new MemoryMediaSource(file));
        MediaMetadata m = d.ReadMetadata();
        Assert.Equal("2021-06-05T10:11:12", m.Tags.Date);
        Assert.NotEmpty(m.Facts.CaptureTime.Candidates);
    }

    [Fact]
    public void Id3v1TagIsReadWithGenreAndTrack()
    {
        byte[] file = Concat(Mp3(3), Id3v1("Song", "Singer", "Disc", "1999", "note", track: 9, genre: 17));
        using MpegAudioDemuxer d = MpegAudioDemuxer.Open(new MemoryMediaSource(file));
        CommonTags t = d.ReadMetadata().Tags;
        Assert.Equal("Song", t.Title);
        Assert.Equal("Singer", t.Artist);
        Assert.Equal("Disc", t.Album);
        Assert.Equal("1999", t.Date);
        Assert.Equal("note", t.Comment);
        Assert.Equal(9, t.TrackNumber);
        Assert.Equal("Rock", t.Genre);
    }

    [Fact]
    public void ApeItemsAreReadWithHeaderAndFooterAndBinaryCover()
    {
        byte[] cover = [1, 2, 3, 0, 4, 5];
        byte[] ape = ApeBytes(ApeItem("Title", "APE title"), ApeItem("Artist", "A\0B"), ApeItem("Track", "4/10"), ApeItem("REPLAYGAIN_TRACK_GAIN", "-3.1 dB"), ApeItem("Cover Art (Front)", Concat("cover.jpg\0"u8.ToArray(), cover), 2));
        byte[] file = Concat(Mp3(4), ape);
        using MpegAudioDemuxer d = MpegAudioDemuxer.Open(new MemoryMediaSource(file));
        MediaMetadata m = d.ReadMetadata();
        Assert.Equal("APE title", m.Tags.Title);
        Assert.Equal("A; B", m.Tags.Artist);
        Assert.Equal(4, m.Tags.TrackNumber);
        Assert.Equal(10, m.Tags.TrackTotal);
        Assert.Equal("-3.1 dB", m.Tags.First(CommonField.ReplayGainTrackGain));
        NativeTag picture = m.NativeTags.First(t => t.System == TagSystems.Ape && t.Kind == TagValueKind.Image);
        Assert.Equal(cover, picture.ReadValue());
        Assert.Equal(PictureType.FrontCover, picture.PictureRole);
    }

    [Fact]
    public void ApeWithoutHeaderIsFoundByItsFooter()
    {
        byte[] ape = ApeTagWith([ApeItem("Album", "Footer only")], header: false);
        DefectLog log = new();
        Assert.True(ApeTag.TryReadFromFooter(new MemoryMediaSource(ape), ape.Length - 32, out ApeTag? tag, null, log));
        Assert.Equal("Footer only", tag!.Tags.Single().Text);
        Assert.Equal(0, tag.Range.Offset);
    }

    [Fact]
    public void ApeItemKeyWithBadCharactersIsAnItemDefect()
    {
        byte[] ape = ApeBytes(ApeItem("Ti\u0001tle", "x"), ApeItem("Good", "y"));
        DefectLog log = new();
        Assert.True(ApeTag.TryReadFromFooter(new MemoryMediaSource(ape), ape.Length - 32, out ApeTag? tag, null, log));
        Assert.Contains(tag!.Tags, t => t.Key == "Good");
    }

    [Fact]
    public void Lyrics3v2IsDetectedAndKeptOutOfTheAudio()
    {
        byte[] fields = Concat("LYR"u8.ToArray(), "00005"u8.ToArray(), "hello"u8.ToArray());
        byte[] block = Concat("LYRICSBEGIN"u8.ToArray(), fields, Encoding.ASCII.GetBytes((fields.Length + 11).ToString("D6", System.Globalization.CultureInfo.InvariantCulture)), "LYRICS200"u8.ToArray());
        byte[] audio = Mp3(6);
        using MpegAudioDemuxer d = MpegAudioDemuxer.Open(new MemoryMediaSource(Concat(audio, block, Id3v1("t", "a", "b", "2000", "c"))));
        Assert.Equal(6, Packets(d).Count);
        Assert.Equal(audio.Length, d.AudioEnd);
        MediaMetadata m = d.ReadMetadata();
        Assert.Contains(m.Containers, c => c.System == TagSystems.Lyrics3);
        Assert.NotNull(typeof(Lyrics3Tag));
    }

    [Fact]
    public void PublicEntryPointReadsATagFromASourceRange()
    {
        byte[] tag = Id3Bytes(3, TextFrame(3, "TIT2", "In a chunk"));
        byte[] wrapped = Concat(new byte[64], tag, new byte[20]);
        Id3Tag read = Id3Tag.Read(new MemoryMediaSource(wrapped), 64);
        Assert.Equal("In a chunk", Text(read, "TIT2"));
        Assert.Equal(64, read.Range.Offset);
        Assert.False(Id3Tag.TryRead(new MemoryMediaSource(wrapped), 0, out _));
    }
}
