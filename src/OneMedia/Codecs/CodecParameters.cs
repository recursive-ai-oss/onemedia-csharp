namespace RecursiveAI.OneMedia.Codecs;

/// <summary>Chroma sampling of a video stream.</summary>
public enum ChromaFormat
{
    /// <summary>Not known.</summary>
    Unknown = 0,

    /// <summary>Luma only (4:0:0).</summary>
    Monochrome,

    /// <summary>4:2:0.</summary>
    Yuv420,

    /// <summary>4:2:2.</summary>
    Yuv422,

    /// <summary>4:4:4.</summary>
    Yuv444,
}

/// <summary>
/// Colour description signalled in a bitstream, with the code points of ITU-T H.273. Unspecified is 2 for each code.
/// </summary>
/// <param name="IsSignalled">True when the stream carries a colour description; otherwise the other fields are the unspecified value.</param>
/// <param name="Primaries">Colour primaries code (H.273 Table 2).</param>
/// <param name="Transfer">Transfer characteristics code (H.273 Table 3).</param>
/// <param name="Matrix">Matrix coefficients code (H.273 Table 4).</param>
/// <param name="FullRange">True for full-range samples, false for limited (studio) range.</param>
public readonly record struct ColorInfo(bool IsSignalled, int Primaries, int Transfer, int Matrix, bool FullRange)
{
    /// <summary>No colour description: all three codes are 2 (unspecified), limited range.</summary>
    public static ColorInfo Unspecified => new(false, 2, 2, 2, false);
}

/// <summary>
/// A codec-neutral summary of what a stream's header or configuration says, in the units players and manifests use. Fields that a
/// codec does not carry are zero, <see cref="ChromaFormat.Unknown"/> or null.
/// </summary>
public sealed record CodecParameters
{
    /// <summary>The codec.</summary>
    public CodecId Codec { get; init; }

    /// <summary>The <c>codecs=</c> string of RFC 6381 (and the codec registrations it points to), for example <c>avc1.64001F</c>.
    /// Null when the codec has no registered form or the data is insufficient.</summary>
    public string? Rfc6381 { get; init; }

    /// <summary>The profile as the codec's own number (for example H.264 profile_idc, AAC audio object type), or -1 when not known.</summary>
    public int Profile { get; init; } = -1;

    /// <summary>The level as the codec's own number (for example H.264 level_idc, AV1 seq_level_idx), or -1 when not known.</summary>
    public int Level { get; init; } = -1;

    /// <summary>The profile in words, for example <c>High</c>; null when not known.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Display width in luma samples after cropping; 0 when not known.</summary>
    public int Width { get; init; }

    /// <summary>Display height in luma samples after cropping; 0 when not known.</summary>
    public int Height { get; init; }

    /// <summary>Bits per luma sample (video) or per sample (audio, when the codec states it); 0 when not known.</summary>
    public int BitDepth { get; init; }

    /// <summary>Chroma sampling of video.</summary>
    public ChromaFormat Chroma { get; init; }

    /// <summary>Colour description of video.</summary>
    public ColorInfo Color { get; init; } = ColorInfo.Unspecified;

    /// <summary>True when the stream is signalled as interlaced or field-coded, false when progressive or not stated.</summary>
    public bool IsInterlaced { get; init; }

    /// <summary>Sample aspect ratio numerator, 0 when not signalled.</summary>
    public int SarWidth { get; init; }

    /// <summary>Sample aspect ratio denominator, 0 when not signalled.</summary>
    public int SarHeight { get; init; }

    /// <summary>Frame rate numerator from timing information in the headers, 0 when absent.</summary>
    public int FrameRateNumerator { get; init; }

    /// <summary>Frame rate denominator, 0 when absent.</summary>
    public int FrameRateDenominator { get; init; }

    /// <summary>Largest number of frames that precede a frame in decode order and follow it in output order, or -1 when not signalled.</summary>
    public int ReorderFrames { get; init; } = -1;

    /// <summary>Size in bytes of the length field of length-prefixed NAL units (1, 2 or 4), or 0 when not applicable.</summary>
    public int NalLengthSize { get; init; }

    /// <summary>Audio sample rate in Hz; 0 when not known. For AAC with explicit SBR this is the output (extension) rate.</summary>
    public int SampleRate { get; init; }

    /// <summary>The core (pre-SBR) sample rate of AAC when SBR is signalled, otherwise equal to <see cref="SampleRate"/>.</summary>
    public int CoreSampleRate { get; init; }

    /// <summary>True when the AAC configuration signals spectral band replication (HE-AAC), explicitly or by the backward-compatible extension. A decoder that only handles the core coder outputs <see cref="CoreSampleRate"/> instead of <see cref="SampleRate"/>.</summary>
    public bool SbrPresent { get; init; }

    /// <summary>How <see cref="SbrPresent"/> is signalled; <see cref="SbrSignalling.None"/> when it is not.</summary>
    public SbrSignalling SbrSignalling { get; init; }

    /// <summary>
    /// True when the AAC configuration itself shows no SBR, but the container declares an output rate of exactly twice the configuration's.
    /// Only <see cref="MediaTrack.DescribeCodec"/> sets it, since it needs the container's rate. <see cref="SbrPresent"/> stays false and
    /// <see cref="SbrSignalling"/> is <see cref="SbrSignalling.ContainerRate"/>.
    /// </summary>
    /// <remarks>
    /// A hint, not evidence: some muxers write the doubled output rate (Matroska OutputSamplingFrequency) for streams that carry no SBR at all, and
    /// the rate alone cannot tell the two apart. Only SBR data found in the stream proves SBR, and that is known after reading.
    /// </remarks>
    public bool ContainerDeclaresDoubleRate { get; init; }

    /// <summary>The same as <see cref="ContainerDeclaresDoubleRate"/>, under its first name; it only ever was a hint.</summary>
    [Obsolete("Renamed ContainerDeclaresDoubleRate: the container's doubled rate is a hint, not evidence of SBR.")]
    public bool SbrImplied { get => ContainerDeclaresDoubleRate; init => ContainerDeclaresDoubleRate = value; }

    /// <summary>True when the AAC configuration signals parametric stereo (HE-AACv2): the coded channel count is one less than the output's.</summary>
    public bool PsPresent { get; init; }

    /// <summary>Audio channel count including any low-frequency channel; 0 when not known.</summary>
    public int Channels { get; init; }

    /// <summary>Samples per channel in one coded frame, when fixed by the codec header (for example 1152 for MPEG-1 Layer II); 0 otherwise.</summary>
    public int SamplesPerFrame { get; init; }

    /// <summary>Samples a decoder discards at the start (Opus pre-skip, MP3 encoder delay), in samples at <see cref="SampleRate"/>; 0 when none.</summary>
    public int Priming { get; init; }

    /// <summary>Average or nominal bit rate in bits per second when the header carries one; 0 otherwise.</summary>
    public int BitRate { get; init; }
}

/// <summary>How an AAC stream's use of spectral band replication (HE-AAC) is signalled.</summary>
public enum SbrSignalling
{
    /// <summary>Nothing signals SBR.</summary>
    None = 0,

    /// <summary>Explicit hierarchical signalling: the AudioSpecificConfig starts with audio object type 5 (SBR) or 29 (PS), ISO/IEC 14496-3 §1.6.2.1.</summary>
    Explicit,

    /// <summary>Backward-compatible signalling: a sync extension (0x2b7) after the core configuration, which a core-only decoder ignores.</summary>
    BackwardCompatible,

    /// <summary>Only the container's declared rate, twice the configuration's, suggests SBR (a hint, see <see cref="CodecParameters.ContainerDeclaresDoubleRate"/>).</summary>
    ContainerRate,
}
