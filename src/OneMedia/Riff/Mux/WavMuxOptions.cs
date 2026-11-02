using RecursiveAI.OneMedia.Codecs.Pcm;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>Which RIFF family layout a <see cref="WavMuxer"/> writes and when it may switch to 64-bit sizes.</summary>
public enum WavLayout
{
    /// <summary>
    /// RIFF WAVE with a 28-byte <c>JUNK</c> chunk in front of <c>fmt </c> that reserves the room of a <c>ds64</c> chunk (EBU Tech 3306 §3). When the
    /// finished file would be larger than a 32-bit RIFF size can state, the header is rewritten as RF64 in place: <c>RIFF</c> becomes <c>RF64</c>,
    /// <c>JUNK</c> becomes <c>ds64</c> and the 32-bit sizes become <c>0xFFFFFFFF</c>. No sample byte moves. The default.
    /// </summary>
    Auto = 0,

    /// <summary>RIFF WAVE without the reservation. A file that would pass 4 GiB − 9 bytes is refused with <see cref="MuxRefusalReason.LimitExceeded"/> before the byte that crosses the limit is written.</summary>
    RiffOnly,

    /// <summary>RF64 from the first byte: <c>RF64</c>, <c>ds64</c>, then the chunks. Chosen when the reader of the file expects RF64 whatever its size.</summary>
    Rf64,

    /// <summary>BW64 (ITU-R BS.2088), the same layout as RF64 under the form id <c>BW64</c>.</summary>
    Bw64,
}

/// <summary>A chunk the muxer writes as given, in front of or behind the sample data.</summary>
/// <param name="Id">The four-character chunk id (printable ASCII). <c>RIFF</c>, <c>RF64</c>, <c>BW64</c>, <c>ds64</c>, <c>fmt </c>, <c>fact</c> and <c>data</c> are written by the muxer itself and are refused here.</param>
/// <param name="Payload">The chunk content, without the header or the pad byte; written unchanged.</param>
/// <param name="AfterData">True to write the chunk behind the sample data (at <see cref="IMuxer.Finish"/>); false (the default) writes it in front, so a reader that stops at <c>data</c> still sees it.</param>
public sealed record WavChunk(string Id, ReadOnlyMemory<byte> Payload, bool AfterData = false);

/// <summary>Settings for a <see cref="WavMuxer"/>. Everything has a default.</summary>
public sealed record WavMuxOptions
{
    /// <summary>The layout and the rule for switching to RF64. Default <see cref="WavLayout.Auto"/>.</summary>
    public WavLayout Layout { get; init; }

    /// <summary>A Broadcast Wave <c>bext</c> chunk to write (EBU Tech 3285). Null writes none.</summary>
    public BextChunk? Bext { get; init; }

    /// <summary>An AES46 <c>cart</c> chunk to write. Null writes none.</summary>
    public CartChunk? Cart { get; init; }

    /// <summary>
    /// An iXML document for the <c>iXML</c> chunk, as text. It is stored as UTF-8 without a byte order mark and must be well-formed XML
    /// (<see cref="WavMuxer"/> refuses it otherwise). Null writes none.
    /// </summary>
    public string? IXml { get; init; }

    /// <summary>
    /// Entries of a <c>LIST</c> <c>INFO</c> chunk: four-character ids (<c>INAM</c>, <c>IART</c>, <c>ICMT</c>…) and their text, stored as UTF-8 with a
    /// terminating zero. Null or empty writes none.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Info { get; init; }

    /// <summary>Further chunks, written as given, in this order. See <see cref="WavChunk"/>.</summary>
    public IReadOnlyList<WavChunk>? Chunks { get; init; }

    /// <summary>
    /// Bytes of <c>JUNK</c> padding written in front of <c>data</c>, so that a tag editor can later grow <c>bext</c>, <c>iXML</c> or <c>INFO</c> in
    /// place without moving the sample data. At least 8 (the chunk header) and even; 0 (the default) writes none.
    /// </summary>
    public int PaddingBytes { get; init; }

    /// <summary>Resource limits: <see cref="MediaLimits.MaxPacketBytes"/>, <see cref="MediaLimits.MaxTagBytes"/> for the chunks written from these options.</summary>
    public MediaLimits Limits { get; init; } = MediaLimits.Default;
}

/// <summary>Per-track settings of a <see cref="WavMuxer"/>.</summary>
public sealed record WavTrackOptions
{
    /// <summary>
    /// The layout of the samples in the packets handed to the muxer, when the track's codec tag and audio parameters do not say it (an AIFF reader
    /// delivers big-endian signed integers). Big-endian samples are byte-swapped and signed 8-bit samples are offset to the unsigned form WAVE uses;
    /// nothing else is changed. Null: taken from the track (see <see cref="WavMuxer.AddTrack(MediaTrack, WavTrackOptions?)"/>).
    /// </summary>
    public PcmSampleFormat? Pcm { get; init; }

    /// <summary>
    /// The speaker position mask for the extensible format (bit 0 front left, bit 1 front right, … as Microsoft's WAVE_FORMAT_EXTENSIBLE defines it).
    /// Null uses the mask in the source's own <c>fmt </c> chunk when the track carries one, else the conventional mask for the channel count
    /// (<see cref="WavMuxer.DefaultChannelMask"/>). 0 writes a mask that assigns no positions.
    /// </summary>
    public uint? ChannelMask { get; init; }
}
