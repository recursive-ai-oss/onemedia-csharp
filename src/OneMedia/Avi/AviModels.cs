using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>The <c>avih</c> chunk: the AVI main header (Microsoft AVI RIFF file reference, <c>AVIMAINHEADER</c>).</summary>
/// <param name="MicroSecondsPerFrame">Frame period in microseconds; zero when unstated.</param>
/// <param name="MaxBytesPerSecond">Approximate data rate.</param>
/// <param name="PaddingGranularity">Alignment of data in the file.</param>
/// <param name="Flags">The <c>AVIF_*</c> flags.</param>
/// <param name="TotalFrames">Frames in the first RIFF (not the whole file for OpenDML; see <see cref="OpenDmlHeader"/>).</param>
/// <param name="InitialFrames">Frames of interleave skew.</param>
/// <param name="Streams">Number of streams the writer claims.</param>
/// <param name="SuggestedBufferSize">Largest chunk the writer expected.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
public readonly record struct AviMainHeader(
    uint MicroSecondsPerFrame,
    uint MaxBytesPerSecond,
    uint PaddingGranularity,
    uint Flags,
    uint TotalFrames,
    uint InitialFrames,
    uint Streams,
    uint SuggestedBufferSize,
    uint Width,
    uint Height)
{
    /// <summary>Size of the structure in the file.</summary>
    public const int Size = 56;

    /// <summary><c>AVIF_HASINDEX</c>: the file has an <c>idx1</c> chunk.</summary>
    public const uint HasIndex = 0x10;

    /// <summary><c>AVIF_MUSTUSEINDEX</c>: the index, not the chunk order, gives the presentation order.</summary>
    public const uint MustUseIndex = 0x20;

    /// <summary><c>AVIF_ISINTERLEAVED</c>.</summary>
    public const uint IsInterleaved = 0x100;

    /// <summary>Parses the 56-byte structure; shorter data leaves the missing fields zero.</summary>
    /// <param name="data">The chunk data.</param>
    public static AviMainHeader Parse(ReadOnlySpan<byte> data)
    {
        Span<uint> v = stackalloc uint[14]; // 14 DWORDs of AVIMAINHEADER, fixed
        for (int i = 0; i < 14 && (i + 1) * 4 <= data.Length; i++)
        {
            v[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        }

        return new AviMainHeader(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]);
    }
}

/// <summary>The <c>strh</c> chunk: the header of one stream (<c>AVISTREAMHEADER</c>).</summary>
/// <param name="Type">The stream type: <c>vids</c>, <c>auds</c>, <c>txts</c>, <c>mids</c> or <c>iavs</c> (DV type 1).</param>
/// <param name="Handler">The preferred codec handler; for video usually the codec's FourCC.</param>
/// <param name="Flags">The <c>AVISF_*</c> flags.</param>
/// <param name="Priority">Stream priority.</param>
/// <param name="Language">Language code (unused by most writers).</param>
/// <param name="InitialFrames">Interleave skew.</param>
/// <param name="Scale">Time scale: one tick lasts <c>Scale / Rate</c> seconds.</param>
/// <param name="Rate">Ticks per <c>Scale</c> seconds.</param>
/// <param name="Start">Start time in ticks.</param>
/// <param name="Length">Stream length in ticks.</param>
/// <param name="SuggestedBufferSize">Largest chunk expected.</param>
/// <param name="Quality">Quality, 0–10000, or 0xFFFFFFFF for default.</param>
/// <param name="SampleSize">Size of one sample in bytes; zero when every chunk is one sample (usual for video and VBR audio).</param>
/// <param name="Left">Destination rectangle left.</param>
/// <param name="Top">Destination rectangle top.</param>
/// <param name="Right">Destination rectangle right.</param>
/// <param name="Bottom">Destination rectangle bottom.</param>
public readonly record struct AviStreamHeader(
    uint Type,
    uint Handler,
    uint Flags,
    ushort Priority,
    ushort Language,
    uint InitialFrames,
    uint Scale,
    uint Rate,
    uint Start,
    uint Length,
    uint SuggestedBufferSize,
    uint Quality,
    uint SampleSize,
    short Left,
    short Top,
    short Right,
    short Bottom)
{
    /// <summary>Size of the structure in the file.</summary>
    public const int Size = 56;

    /// <summary><c>AVISF_DISABLED</c>: the stream is off by default.</summary>
    public const uint Disabled = 0x1;

    /// <summary>Video stream.</summary>
    public static readonly uint Vids = RiffMake("vids");

    /// <summary>Audio stream.</summary>
    public static readonly uint Auds = RiffMake("auds");

    /// <summary>Text stream.</summary>
    public static readonly uint Txts = RiffMake("txts");

    /// <summary>MIDI stream.</summary>
    public static readonly uint Mids = RiffMake("mids");

    /// <summary>Interleaved audio and video (DV type 1).</summary>
    public static readonly uint Iavs = RiffMake("iavs");

    /// <summary>The stream type as text.</summary>
    public string TypeText => Riff.RiffChunk.FourCcToString(Type);

    /// <summary>The handler as text.</summary>
    public string HandlerText => Riff.RiffChunk.FourCcToString(Handler);

    /// <summary>Parses the 56-byte structure; shorter data leaves the missing fields zero.</summary>
    /// <param name="data">The chunk data.</param>
    public static AviStreamHeader Parse(ReadOnlySpan<byte> data)
    {
        Span<byte> b = stackalloc byte[Size]; // AVISTREAMHEADER is 56 bytes, fixed
        data[..Math.Min(data.Length, Size)].CopyTo(b);
        return new AviStreamHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(b),
            BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(b[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(b[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[36..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[44..]),
            BinaryPrimitives.ReadInt16LittleEndian(b[48..]),
            BinaryPrimitives.ReadInt16LittleEndian(b[50..]),
            BinaryPrimitives.ReadInt16LittleEndian(b[52..]),
            BinaryPrimitives.ReadInt16LittleEndian(b[54..]));
    }

    private static uint RiffMake(string code) => Riff.RiffChunk.Make(code);
}

/// <summary>The fixed part of a video <c>strf</c> chunk: <c>BITMAPINFOHEADER</c>.</summary>
/// <param name="HeaderSize">The <c>biSize</c> field.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels; negative for a top-down bitmap.</param>
/// <param name="Planes">Always 1.</param>
/// <param name="BitCount">Bits per pixel.</param>
/// <param name="Compression">The FourCC of the codec, or 0 (<c>BI_RGB</c>) / 3 (<c>BI_BITFIELDS</c>) for uncompressed.</param>
/// <param name="SizeImage">Size of an image in bytes.</param>
/// <param name="ColorsUsed">Palette entries used.</param>
public readonly record struct BitmapInfoHeader(uint HeaderSize, int Width, int Height, ushort Planes, ushort BitCount, uint Compression, uint SizeImage, uint ColorsUsed)
{
    /// <summary>Size of the fixed structure.</summary>
    public const int Size = 40;

    /// <summary>The compression FourCC as text, or empty for the numeric values.</summary>
    public string CompressionText => Compression >= 0x20202020 ? Riff.RiffChunk.FourCcToString(Compression) : string.Empty;

    /// <summary>Parses the structure; <c>false</c> when the chunk is shorter than 40 bytes.</summary>
    /// <param name="data">The <c>strf</c> data.</param>
    /// <param name="header">The parsed header.</param>
    public static bool TryParse(ReadOnlySpan<byte> data, out BitmapInfoHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        header = new BitmapInfoHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[32..]));
        return true;
    }
}

/// <summary>The fixed part of an audio <c>strf</c> chunk: <c>WAVEFORMATEX</c>, with <c>WAVEFORMATEXTENSIBLE</c> resolved to its sub-format.</summary>
/// <param name="FormatTag">The <c>wFormatTag</c>; for the extensible tag (0xFFFE) the format code in the sub-format GUID.</param>
/// <param name="Channels">Number of channels.</param>
/// <param name="SamplesPerSecond">Sample rate.</param>
/// <param name="AverageBytesPerSecond">Average data rate.</param>
/// <param name="BlockAlign">Size of an indivisible block of audio data.</param>
/// <param name="BitsPerSample">Bits per sample, 0 for compressed formats that do not state it.</param>
/// <param name="ExtraSize">The <c>cbSize</c> field.</param>
/// <param name="ChannelMask">The speaker mask of the extensible form; 0 otherwise.</param>
public readonly record struct WaveFormat(ushort FormatTag, ushort Channels, uint SamplesPerSecond, uint AverageBytesPerSecond, ushort BlockAlign, ushort BitsPerSample, ushort ExtraSize, uint ChannelMask)
{
    /// <summary>Size of the 18-byte <c>WAVEFORMATEX</c>; a 16-byte <c>PCMWAVEFORMAT</c> without <c>cbSize</c> is also accepted.</summary>
    public const int Size = 18;

    /// <summary>The <c>WAVE_FORMAT_EXTENSIBLE</c> tag.</summary>
    public const ushort Extensible = 0xFFFE;

    /// <summary>Parses the structure; <c>false</c> when the chunk is shorter than 16 bytes.</summary>
    /// <param name="data">The <c>strf</c> data.</param>
    /// <param name="format">The parsed format.</param>
    public static bool TryParse(ReadOnlySpan<byte> data, out WaveFormat format)
    {
        if (data.Length < 16)
        {
            format = default;
            return false;
        }

        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(data);
        ushort cb = data.Length >= Size ? BinaryPrimitives.ReadUInt16LittleEndian(data[16..]) : (ushort)0;
        uint mask = 0;
        if (tag == Extensible && cb >= 22 && data.Length >= Size + 22)
        {
            // WAVEFORMATEXTENSIBLE: valid bits (2), channel mask (4), sub-format GUID (16) whose first two bytes are the format code.
            mask = BinaryPrimitives.ReadUInt32LittleEndian(data[(Size + 2)..]);
            tag = BinaryPrimitives.ReadUInt16LittleEndian(data[(Size + 6)..]);
        }

        format = new WaveFormat(
            tag,
            BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            cb,
            mask);
        return true;
    }
}

/// <summary>The <c>vprp</c> chunk (OpenDML AVI File Format Extensions §"Video Properties Header"): how the video is meant to be displayed.</summary>
/// <param name="VideoFormatToken">Colour/format token.</param>
/// <param name="VideoStandard">Broadcast standard (1 = NTSC, 2 = PAL, 3 = SECAM…).</param>
/// <param name="VerticalRefreshRate">Fields or frames per second.</param>
/// <param name="HorizontalTotal">Total samples per line.</param>
/// <param name="VerticalTotal">Total lines per frame.</param>
/// <param name="AspectX">Display aspect ratio, numerator (high word of <c>dwFrameAspectRatio</c>).</param>
/// <param name="AspectY">Display aspect ratio, denominator (low word).</param>
/// <param name="FrameWidth">Active width in pixels.</param>
/// <param name="FrameHeight">Active height in lines.</param>
/// <param name="FieldsPerFrame">1 for progressive, 2 for interlaced.</param>
public readonly record struct VideoPropertiesHeader(uint VideoFormatToken, uint VideoStandard, uint VerticalRefreshRate, uint HorizontalTotal, uint VerticalTotal, ushort AspectX, ushort AspectY, uint FrameWidth, uint FrameHeight, uint FieldsPerFrame)
{
    /// <summary>Parses the fixed 36 bytes; <c>false</c> when shorter.</summary>
    /// <param name="data">The chunk data.</param>
    /// <param name="header">The parsed header.</param>
    public static bool TryParse(ReadOnlySpan<byte> data, out VideoPropertiesHeader header)
    {
        if (data.Length < 36)
        {
            header = default;
            return false;
        }

        header = new VideoPropertiesHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[22..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[32..]));
        return true;
    }
}

/// <summary>The <c>dmlh</c> chunk of the <c>odml</c> list: the real frame count of an OpenDML file.</summary>
/// <param name="TotalFrames">Frames in the whole file, all RIFF chunks together.</param>
public readonly record struct OpenDmlHeader(uint TotalFrames);

/// <summary>One entry of an OpenDML super index (<c>indx</c>): where a standard index chunk lives.</summary>
/// <param name="Offset">Absolute file offset of the <c>ix##</c> chunk header.</param>
/// <param name="Size">Size of the index chunk, header included.</param>
/// <param name="Duration">Stream ticks the entries of that index cover.</param>
public readonly record struct SuperIndexEntry(long Offset, uint Size, uint Duration);

/// <summary>Everything the <c>strl</c> list of one stream states.</summary>
public sealed class AviStreamInfo
{
    internal AviStreamInfo(int index, AviStreamHeader header, ByteRange formatRange, byte[] format, string? name, ByteRange nameRange, VideoPropertiesHeader? properties, IReadOnlyList<SuperIndexEntry>? superIndex, uint superIndexChunkId, ByteRange headerRange)
    {
        Index = index;
        Header = header;
        FormatRange = formatRange;
        Format = format;
        Name = name;
        NameRange = nameRange;
        Properties = properties;
        SuperIndex = superIndex;
        SuperIndexChunkId = superIndexChunkId;
        HeaderRange = headerRange;
    }

    /// <summary>Position of the stream in the header list; chunk identifiers <c>00dc</c>, <c>01wb</c> name it by this number.</summary>
    public int Index { get; }

    /// <summary>The <c>strh</c> structure.</summary>
    public AviStreamHeader Header { get; }

    /// <summary>Where the <c>strf</c> data lies in the file.</summary>
    public ByteRange FormatRange { get; }

    /// <summary>The <c>strf</c> data: <c>BITMAPINFOHEADER</c> plus extra bytes for video, <c>WAVEFORMATEX</c> plus extra bytes for audio, <c>DVINFO</c> for DV type 1.</summary>
    public byte[] Format { get; }

    /// <summary>The <c>strn</c> stream name, or <c>null</c>.</summary>
    public string? Name { get; }

    /// <summary>Where the <c>strn</c> chunk lies, or <see cref="ByteRange.None"/>.</summary>
    public ByteRange NameRange { get; }

    /// <summary>The <c>vprp</c> chunk, when the stream has one.</summary>
    public VideoPropertiesHeader? Properties { get; }

    /// <summary>The OpenDML super index entries, or <c>null</c> when the stream has no <c>indx</c> chunk.</summary>
    public IReadOnlyList<SuperIndexEntry>? SuperIndex { get; }

    /// <summary>The chunk identifier the super index names (for example <c>00dc</c>), 0 without a super index.</summary>
    public uint SuperIndexChunkId { get; }

    /// <summary>Where the <c>strl</c> list lies.</summary>
    public ByteRange HeaderRange { get; }
}

/// <summary>The parsed <c>hdrl</c> list.</summary>
public sealed class AviHeaderList
{
    internal AviHeaderList(AviMainHeader main, IReadOnlyList<AviStreamInfo> streams, OpenDmlHeader? openDml, ByteRange range)
    {
        Main = main;
        Streams = streams;
        OpenDml = openDml;
        Range = range;
    }

    /// <summary>The <c>avih</c> structure.</summary>
    public AviMainHeader Main { get; }

    /// <summary>The streams in order.</summary>
    public IReadOnlyList<AviStreamInfo> Streams { get; }

    /// <summary>The <c>dmlh</c> chunk, when the file has an <c>odml</c> list.</summary>
    public OpenDmlHeader? OpenDml { get; }

    /// <summary>Where the <c>hdrl</c> list lies.</summary>
    public ByteRange Range { get; }
}

internal static class AviText
{
    // Stream names and INFO strings are zero-terminated and, in practice, in the system code page; Latin-1 keeps every byte.
    public static string Latin1(ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end >= 0 ? data[..end] : data);
    }
}
