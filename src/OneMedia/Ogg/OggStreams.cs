using System.Buffers;
using System.Text;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Codecs.Opus;
using RecursiveAI.OneMedia.Codecs.Vorbis;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.VorbisComment;

namespace RecursiveAI.OneMedia.Ogg;

/// <summary>What an Ogg logical stream carries, recognised from the first packet on its first page.</summary>
public enum OggStreamKind
{
    /// <summary>A stream whose first packet is not recognised; its packets are kept as a data track.</summary>
    Unknown = 0,

    /// <summary>Vorbis audio (Vorbis I §4.2): packet <c>\x01vorbis</c>.</summary>
    Vorbis,

    /// <summary>Opus audio (RFC 7845 §5.1): packet <c>OpusHead</c>.</summary>
    Opus,

    /// <summary>FLAC audio in Ogg (Xiph's FLAC-in-Ogg mapping): packet <c>\x7FFLAC</c>.</summary>
    Flac,

    /// <summary>Theora video (Theora specification §6.2): packet <c>\x80theora</c>.</summary>
    Theora,

    /// <summary>Speex audio: packet <c>Speex   </c>.</summary>
    Speex,

    /// <summary>Ogg Skeleton metadata: packet <c>fishead\0</c>. It is not a track; see <see cref="OggDemuxer.Skeleton"/>.</summary>
    Skeleton,
}

/// <summary>One logical bitstream of an Ogg file (RFC 3533 §2): the pages that share a serial number.</summary>
public sealed class OggLogicalBitstream
{
    internal List<byte[]> Headers { get; } = [];
    internal int HeadersExpected = 1;
    internal bool HeadersDone;
    internal bool Ended;
    internal bool Seen;
    internal uint LastSequence;
    internal byte[] Assembly = [];
    internal int AssemblyLength;
    internal bool Pending;
    internal byte[] Carry = [];
    internal long PacketStartPage = -1;
    internal long Running = -1;
    internal bool Enabled = true;
    internal bool WaitForKeyframe;
    internal MediaTime LinkBase;
    internal long LastGranule = -1;
    internal VorbisIdentificationHeader Vorbis;
    internal VorbisModes? Modes;
    internal bool ModesTried;
    internal int PrevBlock;
    internal int PreSkip;
    internal int TheoraShift;
    internal bool TheoraNewGranule;
    internal int SpeexFrameSamples;
    internal int FlacMinBlock;
    internal long Timescale = 1;
    internal long UnitTicks = 1;     // ticks of Timescale per codec position unit (Theora: the frame duration)
    internal long PtsOrigin;
    internal bool Reported;
    internal List<(long Start, long End)> HeaderPageRanges { get; } = [];

    internal OggLogicalBitstream(uint serial, OggStreamKind kind, long firstPageOffset)
    {
        SerialNumber = serial;
        Kind = kind;
        FirstPageOffset = firstPageOffset;
    }

    /// <summary>The serial number shared by the stream's pages.</summary>
    public uint SerialNumber { get; }

    /// <summary>What the stream carries.</summary>
    public OggStreamKind Kind { get; }

    /// <summary>The index of the stream's track, or -1 for Skeleton.</summary>
    public int TrackIndex { get; internal set; } = -1;

    /// <summary>Offset of the stream's first page.</summary>
    public long FirstPageOffset { get; }

    /// <summary>The header packets read for the stream, in order, as they appear in the file.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> HeaderPackets
    {
        get
        {
            List<ReadOnlyMemory<byte>> list = new(Headers.Count);
            foreach (byte[] h in Headers)
            {
                list.Add(h);
            }

            return list;
        }
    }
}

/// <summary>One <c>fisbone</c> packet of an Ogg Skeleton stream: what the skeleton says about another logical stream.</summary>
/// <param name="SerialNumber">The serial number of the stream described.</param>
/// <param name="MessageHeaders">The header fields as name and value pairs, for example <c>Content-Type</c>, <c>Role</c>, <c>Name</c>, <c>Language</c>.</param>
/// <param name="GranuleRateNumerator">Granule position units per second, numerator.</param>
/// <param name="GranuleRateDenominator">Granule position units per second, denominator.</param>
/// <param name="StartGranule">The granule position at which the stream starts.</param>
/// <param name="PreRoll">The number of packets to decode before the first presented one.</param>
/// <param name="GranuleShift">The number of low bits of the granule position used for the offset from the last keyframe.</param>
public sealed record OggSkeletonBone(uint SerialNumber, IReadOnlyList<KeyValuePair<string, string>> MessageHeaders, long GranuleRateNumerator, long GranuleRateDenominator, long StartGranule, uint PreRoll, int GranuleShift)
{
    /// <summary>The value of the first message header called <paramref name="name"/> (compared ignoring case), or null.</summary>
    /// <param name="name">The field name.</param>
    public string? Header(string name)
    {
        foreach (KeyValuePair<string, string> h in MessageHeaders)
        {
            if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return h.Value;
            }
        }

        return null;
    }
}

/// <summary>The Ogg Skeleton stream of a file, read as a description of the other streams. It is not exposed as a track.</summary>
/// <param name="VersionMajor">Skeleton major version.</param>
/// <param name="VersionMinor">Skeleton minor version.</param>
/// <param name="Bones">The <c>fisbone</c> packets.</param>
/// <param name="Packets">Every skeleton packet in order (fishead, fisbones, index packets), as stored.</param>
public sealed record OggSkeleton(int VersionMajor, int VersionMinor, IReadOnlyList<OggSkeletonBone> Bones, IReadOnlyList<ReadOnlyMemory<byte>> Packets);

internal static class OggHeaders
{
    // Skeleton fisbone (Ogg Skeleton 3/4): the fixed fields end at byte 52; the message headers are CRLF-separated "Name: value" lines.
    public static OggSkeletonBone? ParseFisbone(ReadOnlySpan<byte> p)
    {
        if (p.Length < 52 || !p[..8].SequenceEqual("fisbone\0"u8))
        {
            return null;
        }

        uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
        uint serial = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);
        long num = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(p[20..]);
        long den = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(p[28..]);
        long start = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(p[36..]);
        uint preroll = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[44..]);
        int shift = p[48];
        // The offset field counts from its own position (byte 8); 44 is the usual value. A value that does not fit falls back to the fixed size.
        int at = offset >= 44 && 8 + (long)offset <= p.Length ? 8 + (int)offset : 52;
        List<KeyValuePair<string, string>> headers = [];
        foreach (string line in Encoding.UTF8.GetString(p[at..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers.Add(new KeyValuePair<string, string>(line[..colon].Trim(), line[(colon + 1)..].Trim().TrimEnd('\0')));
            }
        }

        return new OggSkeletonBone(serial, headers, num, den, start, preroll, shift);
    }

    // Theora specification §6.2, the identification header; fields are big-endian and bit-packed.
    public static bool TryParseTheora(ReadOnlySpan<byte> p, out int major, out int minor, out int revision, out int width, out int height, out int frn, out int frd, out int parn, out int pard, out int shift)
    {
        major = minor = revision = width = height = frn = frd = parn = pard = shift = 0;
        if (p.Length < 42 || p[0] != 0x80 || !p[1..7].SequenceEqual("theora"u8))
        {
            return false;
        }

        major = p[7];
        minor = p[8];
        revision = p[9];
        width = (p[14] << 16) | (p[15] << 8) | p[16];
        height = (p[17] << 16) | (p[18] << 8) | p[19];
        frn = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p[22..]);
        frd = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p[26..]);
        parn = (p[30] << 16) | (p[31] << 8) | p[32];
        pard = (p[33] << 16) | (p[34] << 8) | p[35];
        int packed = (p[40] << 8) | p[41];
        shift = (packed >> 5) & 0x1F;
        return frn > 0 && frd > 0 && width > 0 && height > 0;
    }

    public static OggStreamKind Classify(ReadOnlySpan<byte> first, out int headersExpected)
    {
        headersExpected = 1;
        if (first.Length >= 7 && first[0] == 1 && first.Slice(1, 6).SequenceEqual("vorbis"u8))
        {
            headersExpected = 3;
            return OggStreamKind.Vorbis;
        }

        if (first.Length >= 8 && first[..8].SequenceEqual("OpusHead"u8))
        {
            headersExpected = 2;
            return OggStreamKind.Opus;
        }

        if (first.Length >= 9 && first[0] == 0x7F && first.Slice(1, 4).SequenceEqual("FLAC"u8))
        {
            // The mapping states the number of header packets after the first; 0 means unknown, which the reader resolves by looking at each packet.
            int more = (first[7] << 8) | first[8];
            headersExpected = more == 0 ? int.MaxValue : 1 + more;
            return OggStreamKind.Flac;
        }

        if (first.Length >= 7 && first[0] == 0x80 && first.Slice(1, 6).SequenceEqual("theora"u8))
        {
            headersExpected = 3;
            return OggStreamKind.Theora;
        }

        if (first.Length >= 8 && first[..8].SequenceEqual("Speex   "u8))
        {
            // Speex header: extra_headers at offset 68.
            int extra = first.Length >= 72 ? (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(first[68..]), 16) : 0;
            headersExpected = 2 + extra;
            return OggStreamKind.Speex;
        }

        if (first.Length >= 8 && first[..8].SequenceEqual("fishead\0"u8))
        {
            return OggStreamKind.Skeleton;
        }

        return OggStreamKind.Unknown;
    }

    public static string? PrintableTag(ReadOnlySpan<byte> first)
    {
        int n = Math.Min(first.Length, 8);
        StringBuilder sb = new();
        for (int i = 0; i < n; i++)
        {
            byte b = first[i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return n == 0 ? null : sb.ToString();
    }
}
