using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Big-endian reads that return 0 past the end instead of throwing; callers check lengths first and report a defect.</summary>
internal static class Be
{
    public static uint U32(ReadOnlySpan<byte> s, int o) => o >= 0 && o <= s.Length - 4 ? BinaryPrimitives.ReadUInt32BigEndian(s[o..]) : 0;

    public static int I32(ReadOnlySpan<byte> s, int o) => unchecked((int)U32(s, o));

    public static ulong U64(ReadOnlySpan<byte> s, int o) => o >= 0 && o <= s.Length - 8 ? BinaryPrimitives.ReadUInt64BigEndian(s[o..]) : 0;

    public static ushort U16(ReadOnlySpan<byte> s, int o) => o >= 0 && o <= s.Length - 2 ? BinaryPrimitives.ReadUInt16BigEndian(s[o..]) : (ushort)0;
}

/// <summary>The file type box (§4.3): the brand that names the file's specification and the brands it is compatible with.</summary>
/// <param name="MajorBrand">The major brand, e.g. <c>isom</c>, <c>qt  </c>, <c>3gp4</c>, <c>M4A </c>.</param>
/// <param name="MinorVersion">The minor version.</param>
/// <param name="CompatibleBrands">Compatible brands in file order.</param>
public sealed record FileTypeBox(string MajorBrand, uint MinorVersion, IReadOnlyList<string> CompatibleBrands)
{
    /// <summary>True when the major brand is <c>qt  </c>: QuickTime sample-entry and atom layouts apply.</summary>
    public bool IsQuickTime => MajorBrand == "qt  ";

    /// <summary>Parses a <c>ftyp</c> or <c>styp</c> payload.</summary>
    /// <param name="payload">The box payload.</param>
    /// <returns>The brands; a payload shorter than 8 bytes yields an empty brand list and a blank major brand.</returns>
    public static FileTypeBox Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return new FileTypeBox(string.Empty, 0, []);
        }

        List<string> brands = [];
        for (int o = 8; o + 4 <= payload.Length; o += 4)
        {
            brands.Add(BoxReader.Latin1(payload.Slice(o, 4)));
        }

        return new FileTypeBox(BoxReader.Latin1(payload[..4]), Be.U32(payload, 4), brands);
    }
}

/// <summary>The movie header box (§8.2.2).</summary>
/// <param name="Timescale">Time units per second for the movie's durations and edit lists.</param>
/// <param name="Duration">Movie duration in <paramref name="Timescale"/> units; all ones means unknown.</param>
/// <param name="CreationSeconds">Creation time, seconds since 1904-01-01 UTC; 0 when unset.</param>
/// <param name="ModificationSeconds">Modification time, same epoch.</param>
/// <param name="NextTrackId">The next free track id.</param>
public sealed record MovieHeader(uint Timescale, ulong Duration, ulong CreationSeconds, ulong ModificationSeconds, uint NextTrackId)
{
    /// <summary>Creation time as a UTC instant, or <c>null</c> when unset or out of range.</summary>
    public DateTimeOffset? CreationTime => FromMacSeconds(CreationSeconds);

    /// <summary>Modification time as a UTC instant, or <c>null</c> when unset or out of range.</summary>
    public DateTimeOffset? ModificationTime => FromMacSeconds(ModificationSeconds);

    internal static DateTimeOffset? FromMacSeconds(ulong seconds)
    {
        // DateTimeOffset ends in the year 9999, 255 billion seconds after 1904; larger values come from damaged headers.
        if (seconds == 0 || seconds > 250_000_000_000UL)
        {
            return null;
        }

        return new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
    }
}

/// <summary>The track header box (§8.3.2).</summary>
/// <param name="TrackId">The track id.</param>
/// <param name="Flags">Bit 0 enabled, bit 1 in movie, bit 2 in preview.</param>
/// <param name="Duration">Track duration in movie timescale units.</param>
/// <param name="Width">Presentation width, 16.16 fixed point.</param>
/// <param name="Height">Presentation height, 16.16 fixed point.</param>
/// <param name="Matrix">The nine matrix values (a b u c d v x y w), fixed point as stored.</param>
/// <param name="AlternateGroup">Alternate group; 0 means none.</param>
/// <param name="Layer">Front-to-back layer.</param>
public sealed record TrackHeader(uint TrackId, uint Flags, ulong Duration, uint Width, uint Height, IReadOnlyList<int> Matrix, int AlternateGroup, int Layer)
{
    /// <summary>True when the enabled flag is set.</summary>
    public bool IsEnabled => (Flags & 1) != 0;

    /// <summary>
    /// Clockwise display rotation in degrees (0, 90, 180 or 270) when the matrix is a pure rotation by a multiple of 90 degrees, else 0.
    /// </summary>
    public int RotationDegrees
    {
        get
        {
            if (Matrix.Count < 9)
            {
                return 0;
            }

            int a = Matrix[0], b = Matrix[1], c = Matrix[3], d = Matrix[4];
            const int one = 0x10000;
            return (a, b, c, d) switch
            {
                (one, 0, 0, one) => 0,
                (0, one, -one, 0) => 90,
                (-one, 0, 0, -one) => 180,
                (0, -one, one, 0) => 270,
                _ => 0,
            };
        }
    }
}

/// <summary>The media header box (§8.4.2).</summary>
/// <param name="Timescale">Time units per second for this track's samples.</param>
/// <param name="Duration">Media duration in <paramref name="Timescale"/> units; all ones means unknown.</param>
/// <param name="Language">ISO-639-2/T code, or the packed 15-bit code's text; <c>und</c> when unset. QuickTime Macintosh language codes are mapped.</param>
/// <param name="CreationSeconds">Creation time, seconds since 1904-01-01 UTC.</param>
public sealed record MediaHeader(uint Timescale, ulong Duration, string Language, ulong CreationSeconds);

/// <summary>One entry of an edit list (§8.6.6).</summary>
/// <param name="SegmentDuration">Length of the segment in movie timescale units.</param>
/// <param name="MediaTime">Start in media timescale units, or -1 for an empty edit (a gap in presentation).</param>
/// <param name="RateInteger">Media rate, integer part (1 normally; 0 is a dwell).</param>
/// <param name="RateFraction">Media rate, fraction part.</param>
public readonly record struct EditListEntry(ulong SegmentDuration, long MediaTime, short RateInteger, short RateFraction)
{
    /// <summary>True for an empty edit (media time -1).</summary>
    public bool IsEmpty => MediaTime == -1;
}

/// <summary>Sample-to-codec defaults declared by a track extends box (§8.8.3).</summary>
/// <param name="TrackId">The track the defaults apply to.</param>
/// <param name="DefaultSampleDescriptionIndex">Default sample description index.</param>
/// <param name="DefaultSampleDuration">Default sample duration.</param>
/// <param name="DefaultSampleSize">Default sample size.</param>
/// <param name="DefaultSampleFlags">Default sample flags.</param>
public readonly record struct TrackExtends(uint TrackId, uint DefaultSampleDescriptionIndex, uint DefaultSampleDuration, uint DefaultSampleSize, uint DefaultSampleFlags);

/// <summary>A box inside a sample entry, kept as the bytes after its header.</summary>
/// <param name="Type">The box type.</param>
/// <param name="Offset">Absolute file offset of the box header.</param>
/// <param name="Payload">The bytes after the header (for full boxes this starts with version and flags).</param>
public sealed record ExtensionBox(uint Type, long Offset, ReadOnlyMemory<byte> Payload)
{
    /// <summary>The type as text.</summary>
    public string TypeName => BoxTypes.ToText(Type);
}

/// <summary>
/// One entry of a sample description box (§8.5.2): the format code and the codec configuration boxes that follow the entry's fixed fields.
/// The configuration is kept as bytes; codec-specific parsing belongs to the codec layer.
/// </summary>
public sealed class SampleEntry
{
    /// <summary>The sample entry type, e.g. <c>avc1</c>, <c>mp4a</c>.</summary>
    public required string Format { get; init; }

    /// <summary>For a protected entry (<c>encv</c>, <c>enca</c>), the clear format named by the <c>frma</c> box; else the same as <see cref="Format"/>.</summary>
    public required string OriginalFormat { get; init; }

    /// <summary>True for <c>encv</c>/<c>enca</c>/<c>enct</c>/<c>encs</c>/<c>encm</c> entries.</summary>
    public bool IsProtected { get; init; }

    /// <summary>Absolute file offset of the entry's box header.</summary>
    public long Offset { get; init; }

    /// <summary>Index of the data reference this entry uses (1-based).</summary>
    public int DataReferenceIndex { get; init; }

    /// <summary>Visual entries: width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Visual entries: height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Audio entries: channel count as declared.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Audio entries: bits per sample as declared (0 when unset).</summary>
    public int SampleSizeBits { get; init; }

    /// <summary>Audio entries: sample rate in Hz as declared (the integer part of the 16.16 field, or the QuickTime version 2 double rounded).</summary>
    public int SampleRate { get; init; }

    /// <summary>Audio entries: QuickTime sound version (0, 1 or 2).</summary>
    public int SoundVersion { get; init; }

    /// <summary>The boxes after the entry's fixed fields, in file order.</summary>
    public IReadOnlyList<ExtensionBox> Extensions { get; init; } = [];

    /// <summary>
    /// The codec configuration for this entry: the payload of its configuration box (<c>avcC</c>, <c>hvcC</c>, <c>vvcC</c>, <c>av1C</c>,
    /// <c>vpcC</c>, <c>esds</c>, <c>dOps</c>, <c>dfLa</c>, <c>dac3</c>, <c>dec3</c>, <c>damr</c>, <c>alac</c>), with the box type as
    /// <see cref="CodecConfiguration.Format"/>. Entries that carry their setup in the fixed fields (text, metadata) return the bytes after the
    /// eight common bytes, tagged with the entry type.
    /// </summary>
    public CodecConfiguration Configuration { get; init; }

    /// <summary>Finds the first extension box of a type.</summary>
    /// <param name="type">The box type.</param>
    /// <returns>The box, or <c>null</c>.</returns>
    public ExtensionBox? FindExtension(uint type)
    {
        foreach (ExtensionBox box in Extensions)
        {
            if (box.Type == type)
            {
                return box;
            }
        }

        return null;
    }
}

/// <summary>Where a sample table box keeps its entries; the entries themselves stay in the file and are read through a cursor.</summary>
/// <param name="Offset">Absolute offset of the first entry.</param>
/// <param name="Count">Number of entries that fit in the box (never more than the box declares).</param>
internal readonly record struct TableRef(long Offset, long Count)
{
    public bool IsPresent => Offset >= 0;

    public static TableRef None => new(-1, 0);
}

/// <summary>The sample table of one track (§8.5 to §8.7), as ranges into the file.</summary>
internal sealed class SampleTableLayout
{
    public TableRef Stts = TableRef.None;
    public TableRef Ctts = TableRef.None;
    public bool CttsSigned;
    public TableRef Stss = TableRef.None;
    public TableRef Stsc = TableRef.None;
    public TableRef ChunkOffsets = TableRef.None;
    public bool ChunkOffsets64;
    public TableRef Sdtp = TableRef.None;

    /// <summary>The single sample size when the box declares one (non-zero), else 0 and <see cref="SizeTable"/> applies.</summary>
    public uint ConstantSize;
    public TableRef SizeTable = TableRef.None;
    public int SizeFieldBits = 32;
    public long SampleCount;
    public bool HasSizes;
}

/// <summary>One track of the movie: its headers, edit list, sample descriptions and sample table.</summary>
public sealed class IsoTrack
{
    internal IsoTrack(int index) => Index = index;

    /// <summary>Position of the track in the movie box, from 0.</summary>
    public int Index { get; }

    /// <summary>The track header.</summary>
    public required TrackHeader Header { get; init; }

    /// <summary>The media header.</summary>
    public required MediaHeader Media { get; init; }

    /// <summary>The handler type, e.g. <c>vide</c>, <c>soun</c>, <c>text</c>, <c>sbtl</c>, <c>meta</c>, <c>tmcd</c>.</summary>
    public required string HandlerType { get; init; }

    /// <summary>The handler's human-readable name, which may be empty.</summary>
    public string HandlerName { get; init; } = string.Empty;

    /// <summary>The edit list entries (empty when the track has no <c>elst</c>).</summary>
    public IReadOnlyList<EditListEntry> Edits { get; init; } = [];

    /// <summary>The sample descriptions in file order.</summary>
    public IReadOnlyList<SampleEntry> SampleEntries { get; init; } = [];

    /// <summary>Number of samples the track's own sample table declares (0 for a track that lives in fragments).</summary>
    public long SampleCount => Layout.SampleCount;

    /// <summary>Defaults for fragments of this track, when the movie extends box lists it.</summary>
    public TrackExtends? Extends { get; internal set; }

    /// <summary>Whether the track has a usable sample table (sizes, timing and chunk offsets).</summary>
    public bool HasSampleTable => Layout.HasSizes && Layout.Stts.IsPresent && Layout.Stsc.IsPresent && Layout.ChunkOffsets.IsPresent;

    internal SampleTableLayout Layout { get; init; } = new();

    /// <summary>The track's <c>sbgp</c>/<c>sgpd</c> sample group boxes, kept as ranges (not applied to packet flags).</summary>
    public IReadOnlyList<ByteRange> SampleGroupBoxes { get; init; } = [];
}

/// <summary>The parsed movie box: header, tracks and fragment defaults.</summary>
public sealed class IsoMovie
{
    internal IsoMovie() { }

    /// <summary>The movie header (a zero-timescale placeholder when the box is missing, with a defect recorded).</summary>
    public required MovieHeader Header { get; init; }

    /// <summary>The tracks in movie-box order.</summary>
    public required IReadOnlyList<IsoTrack> Tracks { get; init; }

    /// <summary>True when a movie extends box is present: the file may continue in movie fragments.</summary>
    public bool IsFragmented { get; init; }

    /// <summary>The movie extends header's total fragment duration in movie timescale units, or -1 when absent.</summary>
    public long FragmentDuration { get; init; } = -1;

    /// <summary>The iTunes gapless tag (<c>iTunSMPB</c>) text, when the movie metadata carries one.</summary>
    public string? GaplessInfo { get; init; }

    /// <summary>Absolute offset and size of the movie box.</summary>
    public ByteRange Range { get; init; }
}
