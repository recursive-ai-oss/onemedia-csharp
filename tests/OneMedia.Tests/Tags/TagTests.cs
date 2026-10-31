using System.Text;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tests.IsoBmffSupport;
using static RecursiveAI.OneMedia.Tests.IsoBmffSupport.Mp4Builder;

namespace RecursiveAI.OneMedia.Tests;

internal static class TagTestHelpers
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "metadata", name);

    public static byte[] Fixture(string name) => File.ReadAllBytes(Path(name));

    // Runs a check over the metadata of an ISO file while its demuxer is open (the metadata reads from it lazily).
    public static void WithIso(byte[] file, Action<MediaMetadata> check, MediaLimits? limits = null)
    {
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file), new MediaOpenOptions { Limits = limits ?? MediaLimits.Default });
        check(d.ReadMetadata());
    }

    public static void WithMkv(byte[] file, Action<MediaMetadata> check, MediaLimits? limits = null)
    {
        using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(file), new MediaOpenOptions { Limits = limits ?? MediaLimits.Default });
        check(d.ReadMetadata());
    }

    // A file with one video track and the given extra movie-level boxes.
    public static byte[] IsoWith(params byte[][] moovBoxes)
    {
        Mp4Builder.TrackSpec spec = new() { Handler = "vide", SampleEntries = DefaultVideoEntry() };
        return Build([spec], true, extraMoovBoxes: Cat(moovBoxes)).ToArray([spec]);
    }

    // data atom of an ilst item: type indicator, locale, payload.
    public static byte[] Data(uint type, byte[] payload) => Box("data", U32(type, 0), payload);

    public static byte[] Item(string key, params byte[][] children) => Box(key, children);

    public static byte[] Ilst(params byte[][] items) => Box("meta", Full("hdlr", 0, 0, U32(0), Encoding.Latin1.GetBytes("mdir"), new byte[12]), Box("ilst", items));

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static bool Has(MediaMetadata m, string code) => m.Defects.Any(d => d.Code == code);
}

public class IsoTagTests
{
    [Fact]
    public void IlstItemsFeedTheCommonView()
    {
        byte[] trkn = [0, 0, 0, 3, 0, 12, 0, 0];
        byte[] file = TagTestHelpers.IsoWith(Box("udta", TagTestHelpers.Ilst(
            TagTestHelpers.Item("©nam", TagTestHelpers.Data(1, TagTestHelpers.Utf8("Harbour"))),
            TagTestHelpers.Item("©ART", TagTestHelpers.Data(1, TagTestHelpers.Utf8("Ada"))),
            TagTestHelpers.Item("trkn", TagTestHelpers.Data(0, trkn)),
            TagTestHelpers.Item("covr", TagTestHelpers.Data(13, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3])))));
        TagTestHelpers.WithIso(file, m =>
        {
            Assert.Equal("Harbour", m.Tags.Title);
            Assert.Equal("Ada", m.Tags.Artist);
            Assert.Equal(3, m.Tags.TrackNumber);
            Assert.Equal(12, m.Tags.TrackTotal);
            TagPicture pic = Assert.Single(m.Tags.Pictures);
            Assert.Equal("image/jpeg", pic.MimeType);
            Assert.Equal(7, pic.Length);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }, pic.ReadData());
            Assert.Equal(TagSystems.ITunes, m.Tags.Values(CommonField.Title)[0].Source.System);
        });
    }

    [Fact]
    public void ItemWithoutDataIsReported()
    {
        byte[] file = TagTestHelpers.IsoWith(Box("udta", TagTestHelpers.Ilst(TagTestHelpers.Item("©nam"))));
        TagTestHelpers.WithIso(file, m =>
        {
            Assert.True(TagTestHelpers.Has(m, "tags.item-no-data"));
            Assert.All(m.Defects.Where(d => d.Code == "tags.item-no-data"), d => Assert.True(d.Offset > 0));
            Assert.Null(m.Tags.Title);
        });
    }

    [Fact]
    public void InvalidUtf8AndUtf16AreReportedAndReplaced()
    {
        byte[] file = TagTestHelpers.IsoWith(Box("udta", TagTestHelpers.Ilst(
            TagTestHelpers.Item("©nam", TagTestHelpers.Data(1, [0x41, 0xC3, 0x28, 0x42])),
            TagTestHelpers.Item("©alb", TagTestHelpers.Data(2, [0xD8, 0x00, 0x00, 0x41])))));
        TagTestHelpers.WithIso(file, m =>
        {
            Assert.True(TagTestHelpers.Has(m, "tags.invalid-utf8"));
            Assert.True(TagTestHelpers.Has(m, "tags.invalid-utf16"));
            Assert.Contains('\uFFFD', m.Tags.Title!);
            Assert.Contains('\uFFFD', m.Tags.Album!);
        });
    }

    [Fact]
    public void OddDataTypesAndIntegerLengthsDoNotThrow()
    {
        byte[] file = TagTestHelpers.IsoWith(Box("udta", TagTestHelpers.Ilst(
            TagTestHelpers.Item("©nam", TagTestHelpers.Data(77, [1, 2, 3])),
            TagTestHelpers.Item("tmpo", TagTestHelpers.Data(21, [1, 2, 3])),
            TagTestHelpers.Item("cpil", TagTestHelpers.Data(21, [])),
            TagTestHelpers.Item("©day", Box("data", [0, 0])))));
        TagTestHelpers.WithIso(file, m =>
        {
            Assert.NotEmpty(m.Defects);
            Assert.All(m.Defects, d => Assert.True(d.Code is null || d.Code.StartsWith("tags.", StringComparison.Ordinal) || d.Code.StartsWith("isobmff.", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void FreeformItemKeepsMeanAndName()
    {
        byte[] file = TagTestHelpers.IsoWith(Box("udta", TagTestHelpers.Ilst(
            TagTestHelpers.Item("----", Full("mean", 0, 0, TagTestHelpers.Utf8("com.apple.iTunes")), Full("name", 0, 0, TagTestHelpers.Utf8("iTunSMPB")), TagTestHelpers.Data(1, TagTestHelpers.Utf8(" 00000000 00000840"))))));
        TagTestHelpers.WithIso(file, m =>
        {
            NativeTag t = Assert.Single(m.NativeTags);
            Assert.Equal("----:com.apple.iTunes:iTunSMPB", t.Key);
            Assert.Equal(" 00000000 00000840", t.Text);
            Assert.Same(t, m[TagSystems.ITunes, "----:com.apple.iTunes:iTunSMPB"]);
        });
    }

    [Fact]
    public void KeyTableValuesAreNamedByTheirKeys()
    {
        byte[] keys = Full("keys", 0, 0, U32(2), Box("mdta", TagTestHelpers.Utf8("com.apple.quicktime.make")), Box("mdta", TagTestHelpers.Utf8("com.apple.quicktime.model")));
        byte[] items = Box("ilst", Box("\0\0\0\u0001", TagTestHelpers.Data(1, TagTestHelpers.Utf8("Contoso"))), Box("\0\0\0\u0002", TagTestHelpers.Data(1, TagTestHelpers.Utf8("X1"))));
        byte[] meta = Box("meta", Full("hdlr", 0, 0, U32(0), Encoding.Latin1.GetBytes("mdta"), new byte[12]), keys, items);
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(meta), m =>
        {
            Assert.Equal("Contoso", m.Facts.Make.Value);
            Assert.Equal("X1", m.Facts.Model.Value);
            Assert.Equal("mdta", m.NativeTags[0].System);
        });
    }

    [Fact]
    public void TruncatedKeyTableAndBadKeyIndexAreReported()
    {
        // The table claims 5 keys and holds one; one item points at key 9.
        byte[] keys = Full("keys", 0, 0, U32(5), Box("mdta", TagTestHelpers.Utf8("com.apple.quicktime.make")));
        byte[] items = Box("ilst", Box("\0\0\0\u0001", TagTestHelpers.Data(1, TagTestHelpers.Utf8("Contoso"))), Box("\0\0\0\u0009", TagTestHelpers.Data(1, TagTestHelpers.Utf8("Lost"))));
        byte[] meta = Box("meta", Full("hdlr", 0, 0, U32(0), Encoding.Latin1.GetBytes("mdta"), new byte[12]), keys, items);
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(meta), m =>
        {
            Assert.True(TagTestHelpers.Has(m, "tags.keys-count") || TagTestHelpers.Has(m, "tags.keys-short"));
            Assert.True(TagTestHelpers.Has(m, "tags.keys-index"));
            Assert.Equal("Contoso", m.Facts.Make.Value);
            Assert.All(m.Defects.Where(d => d.Code?.StartsWith("tags.keys", StringComparison.Ordinal) == true), d => Assert.True(d.Offset > 0));
        });
    }

    [Theory]
    [InlineData("north_of_here")]
    [InlineData("+95.0000+000.0000/")]
    [InlineData("+37.7749-222.4194/")]
    [InlineData("")]
    [InlineData("+37.7749")]
    public void BadIso6709IsReportedAndGivesNoLocation(string text)
    {
        byte[] atom = Box("©xyz", U16((ushort)text.Length), U16(0x15C7), TagTestHelpers.Utf8(text));
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", atom)), m =>
        {
            Assert.False(m.Facts.Location.HasValue);
        });
    }

    [Fact]
    public void QuickTimeLocationBecomesAFactWithItsCandidate()
    {
        const string text = "+37.7749-122.4194+012.500/";
        byte[] atom = Box("©xyz", U16((ushort)text.Length), U16(0x15C7), TagTestHelpers.Utf8(text));
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", atom)), m =>
        {
            GeoLocation g = m.Facts.Location.Value;
            Assert.Equal(37.7749, g.Latitude, 6);
            Assert.Equal(-122.4194, g.Longitude, 6);
            Assert.Equal(12.5, g.Altitude!.Value, 3);
            FactCandidate<GeoLocation> c = m.Facts.Location.Best!;
            Assert.Equal(text, c.RawValue);
            Assert.Contains("©xyz", c.Source, StringComparison.Ordinal);
            Assert.True(c.Range.Length > 0);
        });
    }

    [Fact]
    public void AnOversizedPictureIsNotReadPastTheLimit()
    {
        byte[] big = new byte[5000];
        byte[] file = TagTestHelpers.IsoWith(Box("udta", TagTestHelpers.Ilst(TagTestHelpers.Item("covr", TagTestHelpers.Data(14, big)))));
        MediaLimits limits = new() { MaxAttachmentBytes = 1000 };
        TagTestHelpers.WithIso(file, m =>
        {
            TagPicture p = Assert.Single(m.Tags.Pictures);
            Assert.Equal(5000, p.Length);
            Assert.Equal("image/png", p.MimeType);
            Assert.Throws<MediaLimitExceededException>(() => p.ReadData());
        }, limits);
    }

    [Fact]
    public void XmpEntityBombIsRefusedWithoutExpansion()
    {
        string xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">&c;</x:xmpmeta>";
        byte[] uuid = Box("uuid", Mp4BuilderGuid(), TagTestHelpers.Utf8(xml));
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(uuid), m =>
        {
            Assert.True(TagTestHelpers.Has(m, "tags.xmp-malformed"));
            Assert.Empty(m.XmpPackets.SelectMany(p => p.Properties));
        });
    }

    [Fact]
    public void XmpPropertiesAndDatesBecomeFacts()
    {
        string xml = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmp:CreateDate=\"2022-07-08T09:10:11+02:00\" exif:GPSLatitude=\"37,46.494N\" exif:GPSLongitude=\"122,25.164W\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">XMP title</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] uuid = Box("uuid", Mp4BuilderGuid(), TagTestHelpers.Utf8(xml));
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(uuid), m =>
        {
            Assert.Equal("XMP title", m.Tags.Title);
            MediaDateTime t = m.Facts.CaptureTime.Value;
            Assert.Equal(TimeZoneCertainty.Offset, t.Certainty);
            Assert.Equal(120, t.OffsetMinutes);
            Assert.Equal(37.7749, m.Facts.Location.Value.Latitude, 3);
            Assert.Equal(-122.4194, m.Facts.Location.Value.Longitude, 3);
        });
    }

    [Fact]
    public void ThreeGppBoxesAreReadAndShortOnesReported()
    {
        // titl: version/flags, language (packed 'eng'), zero-terminated UTF-8.
        byte[] titl = Full("titl", 0, 0, U16(0x15C7), TagTestHelpers.Utf8("Pocket\0"));
        byte[] unterminated = Full("auth", 0, 0, U16(0x15C7), TagTestHelpers.Utf8("NoTerminator"));
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", titl, unterminated)), m =>
        {
            Assert.Equal("Pocket", m.Tags.Title);
            Assert.Equal("NoTerminator", m.Tags.Author);
            Assert.True(TagTestHelpers.Has(m, "tags.3gpp-unterminated"));
        });
    }

    [Fact]
    public void ReadingTagsNeverTouchesMediaData()
    {
        Mp4Builder.TrackSpec spec = new() { Handler = "vide", SampleEntries = DefaultVideoEntry(), SampleCount = 50, SizeOf = _ => 2000 };
        string text = "Title";
        Mp4Builder.Layout layout = Build([spec], moovFirst: false, extraMoovBoxes: Box("udta", Box("©nam", U16((ushort)text.Length), U16(0x15C7), TagTestHelpers.Utf8(text))));
        byte[] file = layout.ToArray([spec]);
        RecordingObserver rec = new();
        using ObservedMediaSource source = new(new MemoryMediaSource(file), null, rec);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(source);
        rec.Reads.Clear(); // opening sniffs the first block of the file; the question is what the tag read adds
        MediaMetadata m = d.ReadMetadata();
        Assert.Equal("Title", m.Tags.Title);
        long dataStart = layout.MdatPayloadOffset, dataEnd = dataStart + layout.MdatPayloadSize;
        // The top-level scan buffers one block from the start of the file; nothing else may reach into the media data.
        Assert.True(rec.Reads.Where(r => r.Offset < dataEnd && r.Offset + r.Length > dataStart).Sum(r => (long)r.Length) <= 32 * 1024);
        Assert.DoesNotContain(rec.Reads, r => r.Offset > dataStart && r.Offset < dataEnd);
    }

    private static byte[] Mp4BuilderGuid() => Cat(new Guid("BE7ACFCB-97A9-42E8-9C71-999491E3AFAC").ToByteArray(bigEndian: true));

    internal sealed class RecordingObserver : IMediaReadObserver
    {
        public List<(long Offset, int Length)> Reads { get; } = [];

        public void OnRead(long offset, int requested, int returned) => Reads.Add((offset, returned));
    }
}

public class FixtureTagTests
{
    [Fact]
    public void ItunesFixture()
    {
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("tags_ilst.mp4"), m =>
        {
            Assert.Equal("Harbour_Lights", m.Tags.Title);
            Assert.Equal("Ada_Quill", m.Tags.Artist);
            Assert.Equal("Ada_Quill", m.Tags.AlbumArtist);
            Assert.Equal("Night_Ferries", m.Tags.Album);
            Assert.Equal("Basil_Ferne", m.Tags.Composer);
            Assert.Equal("Ambient", m.Tags.Genre);
            Assert.Equal(3, m.Tags.TrackNumber);
            Assert.Equal(12, m.Tags.TrackTotal);
            Assert.Equal(1, m.Tags.DiscNumber);
            Assert.Equal(2, m.Tags.DiscTotal);
            Assert.Equal("2023-04-05", m.Tags.Date);
            Assert.StartsWith("Lavf", m.Tags.Encoder, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CoverArtIsAPictureWithItsBytes()
    {
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("tags_cover.mp4"), m =>
        {
            Assert.Equal("With_Cover", m.Tags.Title);
        });
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("mp4box_itags.mp4"), m =>
        {
            TagPicture p = Assert.Single(m.Tags.Pictures);
            Assert.Equal("image/png", p.MimeType);
            byte[] data = p.ReadData();
            Assert.Equal(p.Length, data.Length);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, data[..4]);
            Assert.Equal("Boxed_Title", m.Tags.Title);
            // MP4Box adds its track as a text item (©trk) beside the numeric one; the numeric pair is the track number.
            Assert.Equal(3, m.Tags.TrackNumber);
            Assert.Equal("2/9", m[TagSystems.ITunes, "©trk"]!.Text);
            Assert.DoesNotContain(m.Defects, d => d.Code == "tags.int-length");
        });
    }

    [Fact]
    public void KeysFixtureGivesFactsWithCandidates()
    {
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("tags_keys.mov"), m =>
        {
            Assert.Equal("Contoso", m.Facts.Make.Value);
            Assert.Equal("Camera_X1", m.Facts.Model.Value);
            Assert.Equal("17.1", m.Facts.Software.Value);
            MediaDateTime t = m.Facts.CaptureTime.Value;
            Assert.Equal(new DateTime(2023, 4, 5, 10, 11, 12), t.Value);
            Assert.Equal(TimeZoneCertainty.Offset, t.Certainty);
            Assert.Equal(120, t.OffsetMinutes);
            Assert.Equal(37.7749, m.Facts.Location.Value.Latitude, 4);
            // The movie header's time is also a candidate, ranked below the vendor time.
            Assert.True(m.Facts.CaptureTime.Candidates.Count >= 1);
            Assert.Contains("creationdate", m.Facts.CaptureTime.Best!.Source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AndroidKeysGiveDeviceFacts()
    {
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("tags_keys_android.mp4"), m =>
        {
            Assert.Equal("Northwind", m.Facts.Make.Value);
            Assert.Equal("Droid_7", m.Facts.Model.Value);
            Assert.Equal("14", m.Facts.Software.Value);
        });
    }

    [Fact]
    public void ThreeGppFixture()
    {
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("tags_3gp.3gp"), m =>
        {
            Assert.Equal("Pocket_Clip", m.Tags.Title);
            Assert.Equal("Handset_Reel", m.Tags.Album);
            Assert.Equal(51.5007, m.Facts.Location.Value.Latitude, 3);
            Assert.Equal(-0.1246, m.Facts.Location.Value.Longitude, 3);
        });
    }

    [Fact]
    public void ChaptersAndMalformedLocationFixtures()
    {
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("tags_chapters.mp4"), m =>
        {
            Assert.Collection(m.Chapters,
                c => { Assert.Equal("Intro", c.Title); Assert.Equal(0, c.Start.Ticks); },
                c => { Assert.Equal("Main", c.Title); Assert.Equal(0.4, (double)c.Start.Ticks / c.Start.Timescale, 3); });
        });
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("tags_bad_gps.mov"), m =>
        {
            Assert.False(m.Facts.Location.HasValue);
            Assert.True(TagTestHelpers.Has(m, "tags.iso6709"));
        });
    }

    [Fact]
    public void XmpFixtureGivesLocation()
    {
        TagTestHelpers.WithIso(TagTestHelpers.Fixture("et_xmp_gps.mp4"), m =>
        {
            Assert.NotEmpty(m.XmpPackets);
            Assert.True(m.Facts.Location.HasValue);
        });
    }

    [Fact]
    public void MatroskaGlobalTagsTargetsAndLanguages()
    {
        TagTestHelpers.WithMkv(TagTestHelpers.Fixture("mkvmerge_global.mkv"), m =>
        {
            Assert.Equal("Ida_Rowe", m.Tags.Director);
            Assert.Equal("2015", m.Tags.Date);
            Assert.Equal(4, m.Tags.TrackNumber);
            Assert.Equal(11, m.Tags.TrackTotal);
            Assert.Equal("Track_Level_Title", m.Tags.Title); // the track-level (30) title comes before the movie-level (50) one
            NativeTag nested = Assert.Single(m.NativeTags, t => t.Key == "TITLE/SORT_WITH");
            Assert.Equal("Movie_Boxed", nested.Text);
            Assert.Equal(50, nested.TargetTypeValue);
            Assert.Contains(m.NativeTags, t => t.System == TagSystems.Matroska && t.Language == "pt-BR");
            Assert.Single(m.Facts.FrameSize.Candidates);
        });
    }

    [Fact]
    public void TagsOfOneTrackStayNativeWhenThereAreSeveral()
    {
        TagTestHelpers.WithMkv(TagTestHelpers.Fixture("mkvmerge_track_tags.mkv"), m =>
        {
            Assert.Null(m.Tags.Title);
            Assert.Contains(m.NativeTags, t => t.System == TagSystems.Matroska && t.TrackId > 0 && t.Key == "TITLE");
        });
    }

    [Fact]
    public void TagsOfTheOnlyTrackFeedTheCommonView()
    {
        TagTestHelpers.WithMkv(TagTestHelpers.Fixture("mka_tags.mka"), m =>
        {
            Assert.Single(m.Facts.Duration.Candidates);
            Assert.Contains(m.NativeTags, t => t.TrackId > 0 && t.System == TagSystems.Matroska);
            Assert.NotNull(m.Tags.TrackNumber);
        });
    }

    [Fact]
    public void MatroskaAttachmentsAreReadLazily()
    {
        TagTestHelpers.WithMkv(TagTestHelpers.Fixture("mkvmerge_attach.mkv"), m =>
        {
            // The second attachment is declared as a font, so it is not a picture; both stay on the native list.
            Assert.Equal(2, m.NativeTags.Count(t => t.System == TagSystems.Matroska && t.Key.StartsWith("cover", StringComparison.Ordinal)));
            Assert.Single(m.Tags.Pictures);
            TagPicture jpeg = m.Tags.Pictures[0];
            Assert.Equal(PictureType.FrontCover, jpeg.Type);
            Assert.Equal("image/jpeg", jpeg.MimeType);
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg.ReadData()[..2]);
        });
    }

    [Fact]
    public async Task AsyncMatroskaMetadataEqualsTheSyncOne()
    {
        foreach (string name in new[] { "mkvmerge_global.mkv", "mkvmerge_attach.mkv", "mka_tags.mka", "mkvmerge_webm.webm" })
        {
            byte[] file = TagTestHelpers.Fixture(name);
            string[] sync = [];
            TagTestHelpers.WithMkv(file, m => sync = [.. m.NativeTags.Select(t => $"{t.System}:{t.Key}:{t.TrackId}:{t.Text}:{t.ValueRange.Length}")]);
            await using MatroskaAsyncDemuxer d = await MatroskaAsyncDemuxer.OpenAsync(new MemoryMediaSource(file));
            MediaMetadata am = await d.ReadMetadataAsync();
            Assert.Equal(sync, am.NativeTags.Select(t => $"{t.System}:{t.Key}:{t.TrackId}:{t.Text}:{t.ValueRange.Length}"));
            foreach (TagPicture p in am.Tags.Pictures)
            {
                Assert.Equal(p.Length, p.ReadData().Length);
            }
        }
    }

    [Fact]
    public void MatroskaReadsNoClusterBytesAndFetchesAttachmentsOnDemand()
    {
        byte[] cover = new byte[200_000];
        Array.Fill(cover, (byte)0xAB);
        byte[] attachment = Ebl.El(0x1941A469, Ebl.El(0x61A7, Encoding.UTF8.GetBytes("cover.jpg")), Ebl.S(0x4660, "image/jpeg"), Ebl.Bin(0x465C, cover), Ebl.U(0x46AE, 77));
        byte[] cluster = Ebl.ClusterElement(0, Ebl.Simple(1, 0, 0x80, Ebl.Fill(300_000, 1)));
        byte[] file = Ebl.Cat(Ebl.Header(), Ebl.SegmentElement(Ebl.InfoElement(), Ebl.TracksElement(Ebl.Track(1, 1, "V_MPEG4/ISO/AVC")), attachment, cluster));
        long clusterStart = file.Length - cluster.Length;
        IsoTagTests.RecordingObserver rec = new();
        using ObservedMediaSource source = new(new MemoryMediaSource(file), null, rec);
        using MatroskaDemuxer d = MatroskaDemuxer.Open(source);
        rec.Reads.Clear(); // opening reads the first block of the file and the Segment's leading elements
        MediaMetadata m = d.ReadMetadata();
        long before = rec.Reads.Sum(r => (long)r.Length);
        // Reading the last element before the Cluster may buffer one 16 KiB block into it, and peeking at the Cluster header costs 12 bytes; nothing more.
        long overlap = rec.Reads.Where(r => r.Offset + r.Length > clusterStart).Sum(r => r.Offset + r.Length - Math.Max(r.Offset, clusterStart));
        Assert.True(overlap <= 16 * 1024 + 64, "bytes read inside the Cluster: " + overlap + " in " + string.Join(" ", rec.Reads.Select(r => r.Offset + "+" + r.Length)));
        TagPicture p = Assert.Single(m.Tags.Pictures);
        Assert.Equal(200_000, p.Length);
        Assert.True(before < 100_000, "the attachment was read before it was asked for: " + before);
        Assert.Equal(200_000, p.ReadData().Length);
    }

}

public class ParserTableTests
{
    [Theory]
    [InlineData("+37.7749-122.4194/", 37.7749, -122.4194, null)]
    [InlineData("+37.7749-122.4194+012.500/", 37.7749, -122.4194, 12.5)]
    [InlineData("-33.8688+151.2093/", -33.8688, 151.2093, null)]
    [InlineData("+374629.4-1221625.2/", 37.774833, -122.273667, null)]
    [InlineData("+3746.49-12225.16+5.0/", 37.774833, -122.419333, 5.0)]
    [InlineData("+37.7749-122.4194+012.500CRSWGS_84/", 37.7749, -122.4194, 12.5)]
    public void Iso6709Forms(string text, double lat, double lon, double? alt)
    {
        Assert.True(GeoLocation.TryParseIso6709(text, out GeoLocation g));
        Assert.Equal(lat, g.Latitude, 4);
        Assert.Equal(lon, g.Longitude, 3);
        Assert.Equal(alt, g.Altitude is { } a ? Math.Round(a, 3) : null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("north")]
    [InlineData("+91.0000+000.0000/")]
    [InlineData("+10.0000+181.0000/")]
    [InlineData("++10.0000/")]
    public void Iso6709Rejects(string text) => Assert.False(GeoLocation.TryParseIso6709(text, out _));

    [Theory]
    [InlineData("2023-04-05T10:11:12+0200", 2023, 4, 5, 10, 11, 12, TimeZoneCertainty.Offset, 120, false)]
    [InlineData("2023-04-05T10:11:12+02:00", 2023, 4, 5, 10, 11, 12, TimeZoneCertainty.Offset, 120, false)]
    [InlineData("2022-12-31T23:59:58-0800", 2022, 12, 31, 23, 59, 58, TimeZoneCertainty.Offset, -480, false)]
    [InlineData("2023-04-05T08:11:12Z", 2023, 4, 5, 8, 11, 12, TimeZoneCertainty.Utc, null, false)]
    [InlineData("2023-04-05T08:11:12", 2023, 4, 5, 8, 11, 12, TimeZoneCertainty.Unknown, null, false)]
    [InlineData("2023-04-05", 2023, 4, 5, 0, 0, 0, TimeZoneCertainty.Unknown, null, true)]
    public void DateForms(string text, int y, int mo, int d, int h, int mi, int s, TimeZoneCertainty certainty, int? offset, bool dateOnly)
    {
        Assert.True(MediaDateTime.TryParse(text, out MediaDateTime t, out bool onlyDate));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), t.Value);
        Assert.Equal(certainty, t.Certainty);
        Assert.Equal(offset, t.OffsetMinutes);
        Assert.Equal(dateOnly, onlyDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2023-13-45T10:00:00")]
    public void DateRejects(string text) => Assert.False(MediaDateTime.TryParse(text, out _, out _));

    [Fact]
    public void YearOnlyDatesAreNotCaptureTimes()
    {
        // A year alone (the usual ©day content) is a date text, not an instant.
        Assert.False(MediaDateTime.TryParse("2021", out _, out _));
    }

    [Fact]
    public void MappingDocumentIsTheRenderedTable()
    {
        string dir = AppContext.BaseDirectory;
        string doc = File.ReadAllText(Path.Combine(dir, "docs", "tag-mapping.md"));
        Assert.Contains(TagMappingTable.RenderMarkdown().TrimEnd(), doc.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMappingRowNamesItsDocument()
    {
        Assert.All(TagMappingTable.Rows, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Document));
            Assert.NotEmpty(r.Keys);
        });
    }
}

public class TagRobustnessTests
{
    // Found by the tag fuzz target: a movie header time of about 1.3e12 seconds does not fit a DateTime.
    [Fact]
    public void HugeHeaderTimesGiveNoCreationTime()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "tags", "tags-mvhd-time-overflow.bin"));
        TagTestHelpers.WithIso(input[1..], m => Assert.NotNull(m.Facts));
        Assert.Null(new IsoBmff.MovieHeader(1000, 0, ulong.MaxValue, 300_000_000_000UL, 1).CreationTime);
        Assert.Null(new IsoBmff.MovieHeader(1000, 0, 250_000_000_001UL, 0, 1).CreationTime);
        Assert.NotNull(new IsoBmff.MovieHeader(1000, 0, 249_999_999_999UL, 0, 1).CreationTime);
    }

    private static readonly string[] Names =
    [
        "tags_ilst.mp4", "tags_udta.mov", "tags_keys.mov", "tags_keys_android.mp4", "tags_chapters.mp4", "et_xmp_gps.mp4", "mp4box_itags.mp4", "tags_3gp.3gp",
        "mkv_ffmpeg_tags.mkv", "mkvmerge_global.mkv", "mkvmerge_attach.mkv", "mkvmerge_track_tags.mkv", "mka_tags.mka",
    ];

    [Fact]
    public async Task DamagedFixturesGiveTypedErrorsOnly()
    {
        Random rng = new(12345);
        MediaLimits limits = new() { MaxAllocationBytes = 4 << 20, MaxTagBytes = 1 << 20, MaxAttachmentBytes = 1 << 20, MaxElementCount = 100_000 };
        int opened = 0;
        foreach (string name in Names)
        {
            byte[] original = TagTestHelpers.Fixture(name);
            bool mkv = name.EndsWith(".mkv", StringComparison.Ordinal) || name.EndsWith(".mka", StringComparison.Ordinal);
            for (int i = 0; i < 60; i++)
            {
                byte[] file = (byte[])original.Clone();
                if (i % 3 == 0)
                {
                    file = file[..rng.Next(1, file.Length)]; // cut anywhere
                }
                else
                {
                    for (int k = 0; k < 1 + (i % 7); k++)
                    {
                        file[rng.Next(file.Length)] = (byte)rng.Next(256);
                    }
                }

                try
                {
                    MediaMetadata m;
                    if (mkv)
                    {
                        if (i % 2 == 0)
                        {
                            await using MatroskaAsyncDemuxer d = await MatroskaAsyncDemuxer.OpenAsync(new MemoryMediaSource(file), new MediaOpenOptions { Limits = limits });
                            Touch(await d.ReadMetadataAsync());
                        }
                        else
                        {
                            using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(file), new MediaOpenOptions { Limits = limits });
                            Touch(d.ReadMetadata());
                        }
                    }
                    else
                    {
                        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file), new MediaOpenOptions { Limits = limits });
                        m = d.ReadMetadata();
                        Touch(m);
                    }

                    opened++;
                }
                catch (MediaException)
                {
                    // typed rejection
                }
            }
        }

        Assert.True(opened > 100, "too few damaged files opened to exercise the readers: " + opened);
    }

    private static void Touch(MediaMetadata m)
    {
        foreach (NativeTag t in m.NativeTags)
        {
            _ = t.Text;
            if (t.Kind is TagValueKind.Binary or TagValueKind.Image)
            {
                try
                {
                    _ = t.ReadValue();
                }
                catch (MediaLimitExceededException)
                {
                }
            }
        }

        foreach (CommonField f in Enum.GetValues<CommonField>())
        {
            _ = m.Tags.Values(f);
        }

        _ = m.Facts.CaptureTime.Best;
        _ = m.Facts.Location.Best;
    }
}
