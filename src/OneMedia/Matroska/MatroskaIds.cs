using System.Collections.Frozen;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// The element IDs this reader knows, with their names, nesting level and whether they are master elements (RFC 8794 §11.2 and RFC 9559 §5).
/// The table is explicit data: nothing here is discovered by reflection.
/// </summary>
internal static class MatroskaIds
{
    // EBML header (RFC 8794 §11.2).
    public const uint Ebml = 0x1A45DFA3;
    public const uint EbmlVersion = 0x4286;
    public const uint EbmlReadVersion = 0x42F7;
    public const uint EbmlMaxIdLength = 0x42F2;
    public const uint EbmlMaxSizeLength = 0x42F3;
    public const uint DocType = 0x4282;
    public const uint DocTypeVersion = 0x4287;
    public const uint DocTypeReadVersion = 0x4285;
    public const uint DocTypeExtension = 0x4281;
    public const uint Void = 0xEC;
    public const uint Crc32 = 0xBF;

    // Segment and its direct children (RFC 9559 §5.1).
    public const uint Segment = 0x18538067;
    public const uint SeekHead = 0x114D9B74;
    public const uint Seek = 0x4DBB;
    public const uint SeekId = 0x53AB;
    public const uint SeekPosition = 0x53AC;
    public const uint Info = 0x1549A966;
    public const uint SegmentUuid = 0x73A4;
    public const uint TimestampScale = 0x2AD7B1;
    public const uint Duration = 0x4489;
    public const uint DateUtc = 0x4461;
    public const uint Title = 0x7BA9;
    public const uint MuxingApp = 0x4D80;
    public const uint WritingApp = 0x5741;
    public const uint Cluster = 0x1F43B675;
    public const uint Tracks = 0x1654AE6B;
    public const uint Cues = 0x1C53BB6B;
    public const uint Attachments = 0x1941A469;
    public const uint Chapters = 0x1043A770;
    public const uint Tags = 0x1254C367;

    // Cluster children.
    public const uint Timestamp = 0xE7;
    public const uint SilentTracks = 0x5854;
    public const uint Position = 0xA7;
    public const uint PrevSize = 0xAB;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;
    public const uint BlockVirtual = 0xA2;
    public const uint BlockAdditions = 0x75A1;
    public const uint BlockMore = 0xA6;
    public const uint BlockAdditional = 0xA5;
    public const uint BlockAddId = 0xEE;
    public const uint BlockDuration = 0x9B;
    public const uint ReferencePriority = 0xFA;
    public const uint ReferenceBlock = 0xFB;
    public const uint CodecState = 0xA4;
    public const uint DiscardPadding = 0x75A2;
    public const uint EncryptedBlock = 0xAF;

    // Tracks (RFC 9559 §5.1.4).
    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackUid = 0x73C5;
    public const uint TrackType = 0x83;
    public const uint FlagEnabled = 0xB9;
    public const uint FlagDefault = 0x88;
    public const uint FlagForced = 0x55AA;
    public const uint FlagHearingImpaired = 0x55AB;
    public const uint FlagVisualImpaired = 0x55AC;
    public const uint FlagTextDescriptions = 0x55AD;
    public const uint FlagOriginal = 0x55AE;
    public const uint FlagCommentary = 0x55AF;
    public const uint FlagLacing = 0x9C;
    public const uint DefaultDuration = 0x23E383;
    public const uint DefaultDecodedFieldDuration = 0x234E7A;
    public const uint TrackTimestampScale = 0x23314F;
    public const uint MaxBlockAdditionId = 0x55EE;
    public const uint BlockAdditionMapping = 0x41E4;
    public const uint Name = 0x536E;
    public const uint Language = 0x22B59C;
    public const uint LanguageBcp47 = 0x22B59D;
    public const uint CodecIdElement = 0x86;
    public const uint CodecPrivate = 0x63A2;
    public const uint CodecName = 0x258688;
    public const uint CodecDelay = 0x56AA;
    public const uint SeekPreRoll = 0x56BB;
    public const uint TrackTranslate = 0x6624;
    public const uint Video = 0xE0;
    public const uint FlagInterlaced = 0x9A;
    public const uint FieldOrder = 0x9D;
    public const uint StereoMode = 0x53B8;
    public const uint AlphaMode = 0x53C0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;
    public const uint PixelCropBottom = 0x54AA;
    public const uint PixelCropTop = 0x54BB;
    public const uint PixelCropLeft = 0x54CC;
    public const uint PixelCropRight = 0x54DD;
    public const uint DisplayWidth = 0x54B0;
    public const uint DisplayHeight = 0x54BA;
    public const uint DisplayUnit = 0x54B2;
    public const uint UncompressedFourCc = 0x2EB524;
    public const uint Colour = 0x55B0;
    public const uint Projection = 0x7670;
    public const uint Audio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint OutputSamplingFrequency = 0x78B5;
    public const uint Channels = 0x9F;
    public const uint BitDepth = 0x6264;
    public const uint TrackOperation = 0xE2;
    public const uint ContentEncodings = 0x6D80;
    public const uint ContentEncoding = 0x6240;
    public const uint ContentEncodingOrder = 0x5031;
    public const uint ContentEncodingScope = 0x5032;
    public const uint ContentEncodingType = 0x5033;
    public const uint ContentCompression = 0x5034;
    public const uint ContentCompAlgo = 0x4254;
    public const uint ContentCompSettings = 0x4255;
    public const uint ContentEncryption = 0x5035;
    public const uint ContentEncAlgo = 0x47E1;
    public const uint ContentEncKeyId = 0x47E2;

    // Cues (RFC 9559 §5.1.5).
    public const uint CuePoint = 0xBB;
    public const uint CueTime = 0xB3;
    public const uint CueTrackPositions = 0xB7;
    public const uint CueTrack = 0xF7;
    public const uint CueClusterPosition = 0xF1;
    public const uint CueRelativePosition = 0xF0;
    public const uint CueDuration = 0xB2;
    public const uint CueBlockNumber = 0x5378;

    private static readonly FrozenDictionary<uint, Entry> Table = BuildTable();

    private readonly record struct Entry(string Name, int Level, bool Master);

    /// <summary>The ID's name from the specification, or null when unknown.</summary>
    public static string? NameOf(uint id) => Table.TryGetValue(id, out Entry e) ? e.Name : null;

    /// <summary>True for IDs of elements that contain other elements.</summary>
    public static bool IsMaster(uint id) => Table.TryGetValue(id, out Entry e) && e.Master;

    /// <summary>
    /// Nesting level of a known element in the specification (0 = EBML header and Segment, 1 = Segment children), or -1 for unknown IDs
    /// and for the global elements <c>Void</c> and <c>CRC-32</c>, which may appear at any level.
    /// </summary>
    public static int LevelOf(uint id) => Table.TryGetValue(id, out Entry e) ? e.Level : -1;

    /// <summary>True for the elements that sit directly under the Segment, or above it.</summary>
    public static bool IsTopLevelOrSegmentChild(uint id)
    {
        int level = LevelOf(id);
        return level is 0 or 1;
    }

    private static FrozenDictionary<uint, Entry> BuildTable()
    {
        Dictionary<uint, Entry> t = new(256);
        void Add(uint id, string name, int level, bool master = false) => t[id] = new Entry(name, level, master);

        Add(Ebml, "EBML", 0, true);
        Add(EbmlVersion, "EBMLVersion", 2);
        Add(EbmlReadVersion, "EBMLReadVersion", 2);
        Add(EbmlMaxIdLength, "EBMLMaxIDLength", 2);
        Add(EbmlMaxSizeLength, "EBMLMaxSizeLength", 2);
        Add(DocType, "DocType", 2);
        Add(DocTypeVersion, "DocTypeVersion", 2);
        Add(DocTypeReadVersion, "DocTypeReadVersion", 2);
        Add(DocTypeExtension, "DocTypeExtension", 2, true);
        Add(Void, "Void", -1);
        Add(Crc32, "CRC-32", -1);

        Add(Segment, "Segment", 0, true);
        Add(SeekHead, "SeekHead", 1, true);
        Add(Seek, "Seek", 2, true);
        Add(SeekId, "SeekID", 3);
        Add(SeekPosition, "SeekPosition", 3);
        Add(Info, "Info", 1, true);
        Add(SegmentUuid, "SegmentUUID", 2);
        Add(TimestampScale, "TimestampScale", 2);
        Add(Duration, "Duration", 2);
        Add(DateUtc, "DateUTC", 2);
        Add(Title, "Title", 2);
        Add(MuxingApp, "MuxingApp", 2);
        Add(WritingApp, "WritingApp", 2);
        Add(Cluster, "Cluster", 1, true);
        Add(Tracks, "Tracks", 1, true);
        Add(Cues, "Cues", 1, true);
        Add(Attachments, "Attachments", 1, true);
        Add(Chapters, "Chapters", 1, true);
        Add(Tags, "Tags", 1, true);

        Add(Timestamp, "Timestamp", 2);
        Add(SilentTracks, "SilentTracks", 2, true);
        Add(Position, "Position", 2);
        Add(PrevSize, "PrevSize", 2);
        Add(SimpleBlock, "SimpleBlock", 2);
        Add(BlockGroup, "BlockGroup", 2, true);
        Add(Block, "Block", 3);
        Add(BlockVirtual, "BlockVirtual", 3);
        Add(BlockAdditions, "BlockAdditions", 3, true);
        Add(BlockMore, "BlockMore", 4, true);
        Add(BlockAdditional, "BlockAdditional", 5);
        Add(BlockAddId, "BlockAddID", 5);
        Add(BlockDuration, "BlockDuration", 3);
        Add(ReferencePriority, "ReferencePriority", 3);
        Add(ReferenceBlock, "ReferenceBlock", 3);
        Add(CodecState, "CodecState", 3);
        Add(DiscardPadding, "DiscardPadding", 3);
        Add(EncryptedBlock, "EncryptedBlock", 2);

        Add(TrackEntry, "TrackEntry", 2, true);
        Add(TrackNumber, "TrackNumber", 3);
        Add(TrackUid, "TrackUID", 3);
        Add(TrackType, "TrackType", 3);
        Add(FlagEnabled, "FlagEnabled", 3);
        Add(FlagDefault, "FlagDefault", 3);
        Add(FlagForced, "FlagForced", 3);
        Add(FlagHearingImpaired, "FlagHearingImpaired", 3);
        Add(FlagVisualImpaired, "FlagVisualImpaired", 3);
        Add(FlagTextDescriptions, "FlagTextDescriptions", 3);
        Add(FlagOriginal, "FlagOriginal", 3);
        Add(FlagCommentary, "FlagCommentary", 3);
        Add(FlagLacing, "FlagLacing", 3);
        Add(DefaultDuration, "DefaultDuration", 3);
        Add(DefaultDecodedFieldDuration, "DefaultDecodedFieldDuration", 3);
        Add(TrackTimestampScale, "TrackTimestampScale", 3);
        Add(MaxBlockAdditionId, "MaxBlockAdditionID", 3);
        Add(BlockAdditionMapping, "BlockAdditionMapping", 3, true);
        Add(Name, "Name", 3);
        Add(Language, "Language", 3);
        Add(LanguageBcp47, "LanguageBCP47", 3);
        Add(CodecIdElement, "CodecID", 3);
        Add(CodecPrivate, "CodecPrivate", 3);
        Add(CodecName, "CodecName", 3);
        Add(CodecDelay, "CodecDelay", 3);
        Add(SeekPreRoll, "SeekPreRoll", 3);
        Add(TrackTranslate, "TrackTranslate", 3, true);
        Add(Video, "Video", 3, true);
        Add(FlagInterlaced, "FlagInterlaced", 4);
        Add(FieldOrder, "FieldOrder", 4);
        Add(StereoMode, "StereoMode", 4);
        Add(AlphaMode, "AlphaMode", 4);
        Add(PixelWidth, "PixelWidth", 4);
        Add(PixelHeight, "PixelHeight", 4);
        Add(PixelCropBottom, "PixelCropBottom", 4);
        Add(PixelCropTop, "PixelCropTop", 4);
        Add(PixelCropLeft, "PixelCropLeft", 4);
        Add(PixelCropRight, "PixelCropRight", 4);
        Add(DisplayWidth, "DisplayWidth", 4);
        Add(DisplayHeight, "DisplayHeight", 4);
        Add(DisplayUnit, "DisplayUnit", 4);
        Add(UncompressedFourCc, "UncompressedFourCC", 4);
        Add(Colour, "Colour", 4, true);
        Add(Projection, "Projection", 4, true);
        Add(Audio, "Audio", 3, true);
        Add(SamplingFrequency, "SamplingFrequency", 4);
        Add(OutputSamplingFrequency, "OutputSamplingFrequency", 4);
        Add(Channels, "Channels", 4);
        Add(BitDepth, "BitDepth", 4);
        Add(TrackOperation, "TrackOperation", 3, true);
        Add(ContentEncodings, "ContentEncodings", 3, true);
        Add(ContentEncoding, "ContentEncoding", 4, true);
        Add(ContentEncodingOrder, "ContentEncodingOrder", 5);
        Add(ContentEncodingScope, "ContentEncodingScope", 5);
        Add(ContentEncodingType, "ContentEncodingType", 5);
        Add(ContentCompression, "ContentCompression", 5, true);
        Add(ContentCompAlgo, "ContentCompAlgo", 6);
        Add(ContentCompSettings, "ContentCompSettings", 6);
        Add(ContentEncryption, "ContentEncryption", 5, true);
        Add(ContentEncAlgo, "ContentEncAlgo", 6);
        Add(ContentEncKeyId, "ContentEncKeyID", 6);

        Add(CuePoint, "CuePoint", 2, true);
        Add(CueTime, "CueTime", 3);
        Add(CueTrackPositions, "CueTrackPositions", 3, true);
        Add(CueTrack, "CueTrack", 4);
        Add(CueClusterPosition, "CueClusterPosition", 4);
        Add(CueRelativePosition, "CueRelativePosition", 4);
        Add(CueDuration, "CueDuration", 4);
        Add(CueBlockNumber, "CueBlockNumber", 4);

        // Chapters, Tags and Attachments are kept as ranges, but their structure is walkable.
        Add(0x45B9, "EditionEntry", 2, true);
        Add(0xB6, "ChapterAtom", 3, true);
        Add(0x80, "ChapterDisplay", 4, true);
        Add(0x6944, "ChapProcess", 4, true);
        Add(0x6911, "ChapProcessCommand", 5, true);
        Add(0x6924, "ChapterTranslate", 2, true);
        Add(0x7373, "Tag", 2, true);
        Add(0x63C0, "Targets", 3, true);
        Add(0x67C8, "SimpleTag", 3, true);
        Add(0x61A7, "AttachedFile", 2, true);
        return t.ToFrozenDictionary();
    }
}
