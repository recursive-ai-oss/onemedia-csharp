using System.Buffers.Binary;
using System.Collections.Frozen;

namespace RecursiveAI.OneMedia.Asf;

/// <summary>What an ASF object is, by its GUID. Objects this library does not interpret are <see cref="Unknown"/> and stay in the tree under their GUID.</summary>
public enum AsfObjectKind
{
    /// <summary>An object whose GUID is not in the table; its bytes are preserved as a range.</summary>
    Unknown = 0,
    /// <summary>The Header Object that every ASF file begins with; its children describe the file.</summary>
    Header,
    /// <summary>The Data Object holding the data packets.</summary>
    Data,
    /// <summary>File Properties: duration, preroll, packet size and flags.</summary>
    FileProperties,
    /// <summary>Stream Properties: one stream's type, number and type-specific format data.</summary>
    StreamProperties,
    /// <summary>Header Extension: a container for further header objects.</summary>
    HeaderExtension,
    /// <summary>Codec List: human-readable names of the codecs used.</summary>
    CodecList,
    /// <summary>Content Description: title, author, copyright, description and rating.</summary>
    ContentDescription,
    /// <summary>Extended Content Description: named, typed attributes.</summary>
    ExtendedContentDescription,
    /// <summary>Stream Bitrate Properties: the average bit rate of each stream.</summary>
    StreamBitrateProperties,
    /// <summary>Content Encryption: the first-generation rights management header. Its presence makes the file protected.</summary>
    ContentEncryption,
    /// <summary>Extended Content Encryption: the second-generation rights management header. Its presence makes the file protected.</summary>
    ExtendedContentEncryption,
    /// <summary>Digital Signature.</summary>
    DigitalSignature,
    /// <summary>Padding: unused space that a tag editor can grow into.</summary>
    Padding,
    /// <summary>Script Command: timed script commands (URLs, captions).</summary>
    ScriptCommand,
    /// <summary>Simple Index: for each time interval, the data packet holding the nearest keyframe of the primary video stream.</summary>
    SimpleIndex,
    /// <summary>Index: per-stream index blocks of packet offsets.</summary>
    Index,
    /// <summary>Index Parameters: which streams the Index object covers and at what interval.</summary>
    IndexParameters,
    /// <summary>Extended Stream Properties: per-stream timing, bit rate, language and names.</summary>
    ExtendedStreamProperties,
    /// <summary>Metadata: attributes tied to a stream.</summary>
    Metadata,
    /// <summary>Metadata Library: attributes tied to a stream, with values too large for the Metadata object (pictures).</summary>
    MetadataLibrary,
    /// <summary>Language List: the language tags that streams and attributes refer to by index.</summary>
    LanguageList,
    /// <summary>Compatibility.</summary>
    Compatibility,
    /// <summary>Stream Prioritization.</summary>
    StreamPrioritization,
}

/// <summary>What kind of data a stream carries, from the GUID in its Stream Properties.</summary>
public enum AsfStreamKind
{
    /// <summary>A stream type this library does not know.</summary>
    Unknown = 0,
    /// <summary>Audio; the type-specific data is a WAVEFORMATEX structure.</summary>
    Audio,
    /// <summary>Video; the type-specific data holds the dimensions and a BITMAPINFOHEADER structure.</summary>
    Video,
    /// <summary>Script commands.</summary>
    Command,
    /// <summary>JFIF still images.</summary>
    Jfif,
    /// <summary>Degradable JPEG images.</summary>
    DegradableJpeg,
    /// <summary>Opaque binary data.</summary>
    Binary,
    /// <summary>File transfer.</summary>
    FileTransfer,
}

/// <summary>How the content of an ASF file is protected.</summary>
[Flags]
public enum AsfProtection
{
    /// <summary>Not protected.</summary>
    None = 0,
    /// <summary>The file has a Content Encryption object.</summary>
    ContentEncryption = 1,
    /// <summary>The file has an Extended Content Encryption object.</summary>
    ExtendedContentEncryption = 2,
    /// <summary>A stream's properties say its payloads are encrypted.</summary>
    EncryptedStream = 4,
    /// <summary>An attribute named <c>Is_Protected</c> or a <c>DRM</c> attribute says so.</summary>
    ProtectedAttribute = 8,
}

internal static class AsfGuids
{
    // GUIDs as they are written in files (first three fields little-endian), listed here in their usual text form.
    // Every one of them was read out of sample files with tools/asf/dump-objects.py; the role of each follows from where it sits in the
    // file, the size of the object, and what ffprobe, MediaInfo and ExifTool report for the same file.
    public static readonly Guid Header = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid Data = new("75B22636-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid FileProperties = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    public static readonly Guid StreamProperties = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");

    /// <summary>The error-correction type of audio streams whose objects are scrambled across several virtual packets ("audio spread"); observed in a file that ffprobe de-scrambles.</summary>
    public static readonly Guid AudioSpread = new("BFC3CD50-618F-11CF-8BB2-00AA00B4E220");
    public static readonly Guid HeaderExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
    public static readonly Guid CodecList = new("86D15240-311D-11D0-A3A4-00A0C90348F6");
    public static readonly Guid ContentDescription = new("75B22633-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid ExtendedContentDescription = new("D2D0A440-E307-11D2-97F0-00A0C95EA850");
    public static readonly Guid StreamBitrateProperties = new("7BF875CE-468D-11D1-8D82-006097C9A2B2");
    public static readonly Guid ContentEncryption = new("2211B3FB-BD23-11D2-B4B7-00A0C955FC6E");
    public static readonly Guid ExtendedContentEncryption = new("298AE614-2622-4C17-B935-DAE07EE9289C");
    public static readonly Guid DigitalSignature = new("2211B3FC-BD23-11D2-B4B7-00A0C955FC6E");
    public static readonly Guid Padding = new("1806D474-CADF-4509-A4BA-9AABCB96AAE8");
    public static readonly Guid ScriptCommand = new("1EFB1A30-0B62-11D0-A39B-00A0C90348F6");
    public static readonly Guid SimpleIndex = new("33000890-E5B1-11CF-89F4-00A0C90349CB");
    public static readonly Guid Index = new("D6E229D3-35DA-11D1-9034-00A0C90349BE");
    public static readonly Guid IndexParameters = new("D6E229DF-35DA-11D1-9034-00A0C90349BE");
    public static readonly Guid ExtendedStreamProperties = new("14E6A5CB-C672-4332-8399-A96952065B5A");
    public static readonly Guid Metadata = new("C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA");
    public static readonly Guid MetadataLibrary = new("44231C94-9498-49D1-A141-1D134E457054");
    public static readonly Guid LanguageList = new("7C4346A9-EFE0-4BFC-B229-393EDE415C85");
    public static readonly Guid Compatibility = new("26F18B5D-4584-47EC-9F5F-0E651F0452C9");
    public static readonly Guid StreamPrioritization = new("D4FED15B-88D3-454F-81F0-ED5C45999E24");

    // The first 16 bytes of a Header Extension's body, a fixed marker GUID seen in every file that has one.
    public static readonly Guid HeaderExtensionReserved = new("ABD3D211-A9BA-11CF-8EE6-00C00C205365");

    // Stream types: the values of the stream-type field of Stream Properties (the set ExifTool documents, confirmed on audio and video samples).
    public static readonly Guid AudioStream = new("F8699E40-5B4D-11CF-A8FD-00805F5C442B");
    public static readonly Guid VideoStream = new("BC19EFC0-5B4D-11CF-A8FD-00805F5C442B");
    public static readonly Guid CommandStream = new("59DACFC0-59E6-11D0-A3AC-00A0C90348F6");
    public static readonly Guid JfifStream = new("B61BE100-5B4E-11CF-A8FD-00805F5C442B");
    public static readonly Guid DegradableJpegStream = new("35907DE0-E415-11CF-A917-00805F5C442B");
    public static readonly Guid BinaryStream = new("3AFB65E2-47EF-40F2-AC2C-70A90D71D343");
    public static readonly Guid FileTransferStream = new("91BD222C-F21C-497A-8B6D-5AA86BFC0185");

    private static readonly FrozenDictionary<Guid, AsfObjectKind> Kinds = new Dictionary<Guid, AsfObjectKind>
    {
        [Header] = AsfObjectKind.Header,
        [Data] = AsfObjectKind.Data,
        [FileProperties] = AsfObjectKind.FileProperties,
        [StreamProperties] = AsfObjectKind.StreamProperties,
        [HeaderExtension] = AsfObjectKind.HeaderExtension,
        [CodecList] = AsfObjectKind.CodecList,
        [ContentDescription] = AsfObjectKind.ContentDescription,
        [ExtendedContentDescription] = AsfObjectKind.ExtendedContentDescription,
        [StreamBitrateProperties] = AsfObjectKind.StreamBitrateProperties,
        [ContentEncryption] = AsfObjectKind.ContentEncryption,
        [ExtendedContentEncryption] = AsfObjectKind.ExtendedContentEncryption,
        [DigitalSignature] = AsfObjectKind.DigitalSignature,
        [Padding] = AsfObjectKind.Padding,
        [ScriptCommand] = AsfObjectKind.ScriptCommand,
        [SimpleIndex] = AsfObjectKind.SimpleIndex,
        [Index] = AsfObjectKind.Index,
        [IndexParameters] = AsfObjectKind.IndexParameters,
        [ExtendedStreamProperties] = AsfObjectKind.ExtendedStreamProperties,
        [Metadata] = AsfObjectKind.Metadata,
        [MetadataLibrary] = AsfObjectKind.MetadataLibrary,
        [LanguageList] = AsfObjectKind.LanguageList,
        [Compatibility] = AsfObjectKind.Compatibility,
        [StreamPrioritization] = AsfObjectKind.StreamPrioritization,
    }.ToFrozenDictionary();

    public static AsfObjectKind KindOf(Guid guid) => Kinds.GetValueOrDefault(guid);

    public static AsfStreamKind StreamKindOf(Guid guid) =>
        guid == AudioStream ? AsfStreamKind.Audio
        : guid == VideoStream ? AsfStreamKind.Video
        : guid == CommandStream ? AsfStreamKind.Command
        : guid == JfifStream ? AsfStreamKind.Jfif
        : guid == DegradableJpegStream ? AsfStreamKind.DegradableJpeg
        : guid == BinaryStream ? AsfStreamKind.Binary
        : guid == FileTransferStream ? AsfStreamKind.FileTransfer
        : AsfStreamKind.Unknown;
}

/// <summary>
/// One object of an ASF file: a 16-byte GUID and a 64-bit size that counts the 24-byte object header. The tree holds ranges only. The Header
/// Object and the Header Extension have children; every other object is a leaf whose bytes stay in the file.
/// </summary>
public sealed class AsfObjectNode : IStructureNode
{
    private static readonly IReadOnlyList<IStructureNode> NoChildren = [];

    internal AsfObjectNode(Guid guid, long offset, int headerSize, long payloadSize, IReadOnlyList<IStructureNode>? children = null, bool sizeInferred = false)
    {
        ObjectId = guid;
        Kind = AsfGuids.KindOf(guid);
        Offset = offset;
        HeaderSize = headerSize;
        PayloadSize = payloadSize;
        Children = children ?? NoChildren;
        IsSizeInferred = sizeInferred;
    }

    /// <summary>The object's GUID.</summary>
    public Guid ObjectId { get; }

    /// <summary>What the GUID identifies, or <see cref="AsfObjectKind.Unknown"/>.</summary>
    public AsfObjectKind Kind { get; }

    /// <summary>The first eight bytes of the GUID as stored, read as a little-endian integer; enough to tell objects apart in a tree.</summary>
    public ulong TypeKey
    {
        get
        {
            Span<byte> bytes = stackalloc byte[16]; // a GUID is 16 bytes
            ObjectId.TryWriteBytes(bytes);
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
    }

    /// <inheritdoc />
    public long Offset { get; }

    /// <summary>
    /// Bytes before the first child or the body: 24 for most objects, 30 for the Header Object (which adds a child count and two reserved
    /// bytes), 46 for the Header Extension and 50 for the Data Object.
    /// </summary>
    public int HeaderSize { get; }

    /// <inheritdoc />
    public long PayloadSize { get; }

    /// <summary>True when the stated size was wrong and the range was cut to what the file or the parent holds.</summary>
    public bool IsSizeInferred { get; }

    /// <inheritdoc />
    public IReadOnlyList<IStructureNode> Children { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} {ObjectId} at {Offset}, {HeaderSize + PayloadSize} bytes";
}

/// <summary>The File Properties object: global facts about the file.</summary>
/// <param name="FileId">The GUID that ties the Data and Index objects to this header.</param>
/// <param name="FileSize">The size of the whole file as the writer recorded it; 0 or stale in broadcast and live captures.</param>
/// <param name="CreationFileTime">The creation date as a Windows FILETIME: 100 ns ticks since 1601-01-01 UTC.</param>
/// <param name="DataPacketCount">The number of data packets; 0 when unknown (broadcast).</param>
/// <param name="PlayDuration100ns">The playing time in 100 ns units, including the preroll.</param>
/// <param name="SendDuration100ns">The time to send the file in 100 ns units.</param>
/// <param name="PrerollMs">The time in milliseconds a player buffers before the first presentation time.</param>
/// <param name="Flags">Bit 0: broadcast (live); bit 1: seekable.</param>
/// <param name="MinPacketSize">The smallest data packet size in bytes; equal to the maximum when packets are fixed-size.</param>
/// <param name="MaxPacketSize">The largest data packet size in bytes.</param>
/// <param name="MaxBitrate">The highest bit rate of the file in bits per second.</param>
/// <param name="Range">Where the object sits in the file.</param>
public sealed record AsfFileProperties(Guid FileId, long FileSize, long CreationFileTime, long DataPacketCount, long PlayDuration100ns, long SendDuration100ns,
    long PrerollMs, uint Flags, uint MinPacketSize, uint MaxPacketSize, uint MaxBitrate, ByteRange Range)
{
    /// <summary>True when the file is a live capture of a broadcast: its packet count and duration are not final.</summary>
    public bool IsBroadcast => (Flags & 1) != 0;

    /// <summary>True when the file may be seeked.</summary>
    public bool IsSeekable => (Flags & 2) != 0;

    /// <summary>The creation date in UTC, or null when the field is zero or out of range.</summary>
    public DateTime? CreationUtc =>
        CreationFileTime > 0 && CreationFileTime < DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks ? DateTime.FromFileTimeUtc(CreationFileTime) : null;
}

/// <summary>A stream: its Stream Properties object, joined with the Extended Stream Properties object that has the same stream number, if any.</summary>
public sealed class AsfStreamEntry
{
    internal AsfStreamEntry(int streamNumber, Guid streamType, Guid errorCorrectionType, long timeOffset100ns, bool encrypted, byte[] typeSpecificData, int errorCorrectionDataLength, byte[] errorCorrectionData, ByteRange range)
    {
        StreamNumber = streamNumber;
        StreamType = streamType;
        Kind = AsfGuids.StreamKindOf(streamType);
        ErrorCorrectionType = errorCorrectionType;
        TimeOffset100ns = timeOffset100ns;
        IsEncrypted = encrypted;
        TypeSpecificData = typeSpecificData;
        ErrorCorrectionDataLength = errorCorrectionDataLength;
        ErrorCorrectionData = errorCorrectionData;
        Range = range;
        // Audio spread data, as observed: span (1 byte), virtual packet length (2), virtual chunk length (2), silence data length (2), silence data.
        if (errorCorrectionType == AsfGuids.AudioSpread && errorCorrectionData.Length >= 7)
        {
            int span = errorCorrectionData[0];
            int packet = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(errorCorrectionData.AsSpan(1));
            int chunk = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(errorCorrectionData.AsSpan(3));
            if (span > 1 && packet > 0 && chunk > 0 && packet % chunk == 0)
            {
                Spread = new AsfAudioSpread(span, packet, chunk);
            }
        }
    }

    /// <summary>The stream number, 1 to 127, that data packet payloads carry.</summary>
    public int StreamNumber { get; }

    /// <summary>The stream-type GUID.</summary>
    public Guid StreamType { get; }

    /// <summary>The stream type decoded from <see cref="StreamType"/>.</summary>
    public AsfStreamKind Kind { get; }

    /// <summary>The error-correction type GUID.</summary>
    public Guid ErrorCorrectionType { get; }

    /// <summary>The time offset of the stream in 100 ns units.</summary>
    public long TimeOffset100ns { get; }

    /// <summary>True when the stream's flags say its payloads are encrypted.</summary>
    public bool IsEncrypted { get; }

    /// <summary>The type-specific data: a WAVEFORMATEX for audio, dimensions plus a BITMAPINFOHEADER for video.</summary>
    public ReadOnlyMemory<byte> TypeSpecificData { get; }

    /// <summary>The length of the error-correction data that follows the type-specific data (the audio spread parameters).</summary>
    public int ErrorCorrectionDataLength { get; }

    /// <summary>The error-correction data itself.</summary>
    internal byte[] ErrorCorrectionData { get; }

    /// <summary>The audio spread parameters when the stream is scrambled (span above 1 and a chunk size that divides the packet size); otherwise null.</summary>
    public AsfAudioSpread? Spread { get; }

    /// <summary>Where the Stream Properties object sits in the file.</summary>
    public ByteRange Range { get; }

    /// <summary>True when an Extended Stream Properties object named this stream.</summary>
    public bool HasExtendedProperties { get; internal set; }

    /// <summary>From Extended Stream Properties: the presentation time of the first object, in milliseconds.</summary>
    public long StartTimeMs { get; internal set; }

    /// <summary>From Extended Stream Properties: the presentation time of the end of the last object, in milliseconds; 0 when unknown.</summary>
    public long EndTimeMs { get; internal set; }

    /// <summary>From Extended Stream Properties: the average data rate in bits per second.</summary>
    public uint DataBitrate { get; internal set; }

    /// <summary>From Extended Stream Properties: the index into the Language List, or 0xFFFF for none.</summary>
    public int LanguageIndex { get; internal set; } = 0xFFFF;

    /// <summary>From Extended Stream Properties: the average time per frame in 100 ns units; 0 when unknown.</summary>
    public long AverageTimePerFrame100ns { get; internal set; }

    /// <summary>From Extended Stream Properties: the names of the stream.</summary>
    public IReadOnlyList<string> Names { get; internal set; } = [];

    /// <summary>The average bit rate from Stream Bitrate Properties, or 0.</summary>
    public uint AverageBitrate { get; internal set; }
}

/// <summary>
/// How an audio stream's objects are scrambled: an object holds <see cref="Span"/> virtual packets of <see cref="PacketLength"/> bytes, each cut into chunks of
/// <see cref="ChunkLength"/> bytes. The chunks are stored packet by packet and played column by column.
/// </summary>
/// <param name="Span">The number of virtual packets an object spreads over.</param>
/// <param name="PacketLength">The virtual packet length in bytes.</param>
/// <param name="ChunkLength">The virtual chunk length in bytes.</param>
public sealed record AsfAudioSpread(int Span, int PacketLength, int ChunkLength)
{
    /// <summary>The size of a scrambled object: span times packet length.</summary>
    public int ObjectLength => Span * PacketLength;
}

/// <summary>An entry of the Codec List object.</summary>
/// <param name="IsVideo">True for a video codec, false for audio or another type.</param>
/// <param name="Type">The type number: 1 video, 2 audio, 0xFFFF other.</param>
/// <param name="Name">The codec name.</param>
/// <param name="Description">The codec description.</param>
public sealed record AsfCodecEntry(bool IsVideo, int Type, string Name, string Description);
