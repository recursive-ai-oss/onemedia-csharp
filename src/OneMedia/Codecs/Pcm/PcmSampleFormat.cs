namespace RecursiveAI.OneMedia.Codecs.Pcm;

/// <summary>How the numbers in an uncompressed (or companded) audio stream are encoded.</summary>
public enum PcmEncoding
{
    /// <summary>Unsigned integers (8-bit WAVE and AIFF-C <c>raw </c> samples).</summary>
    UnsignedInteger = 0,

    /// <summary>Two's-complement signed integers.</summary>
    SignedInteger,

    /// <summary>IEEE 754 floating point (32 or 64 bits).</summary>
    FloatingPoint,

    /// <summary>ITU-T G.711 A-law, one byte per sample.</summary>
    ALaw,

    /// <summary>ITU-T G.711 µ-law, one byte per sample.</summary>
    MuLaw,
}

/// <summary>
/// The sample layout of a PCM stream as its container declares it. The container does not change the bytes: a reader of the packets
/// uses this to know how to interpret them.
/// </summary>
/// <param name="Encoding">How each sample is encoded.</param>
/// <param name="BitsPerSample">Width of one sample in the file (8, 16, 24, 32 or 64); the container size, not the significant bits.</param>
/// <param name="ValidBits">Significant bits in each sample (the rest is padding); equals <paramref name="BitsPerSample"/> unless the file says less.</param>
/// <param name="BigEndian">True when multi-byte samples are stored most significant byte first.</param>
public readonly record struct PcmSampleFormat(PcmEncoding Encoding, int BitsPerSample, int ValidBits, bool BigEndian)
{
    /// <summary>Bytes in one sample.</summary>
    public int BytesPerSample => (BitsPerSample + 7) / 8;

    /// <summary>A short name in the style audio tools use: <c>s16le</c>, <c>u8</c>, <c>f32be</c>, <c>alaw</c>, <c>mulaw</c>.</summary>
    public string Name => Encoding switch
    {
        PcmEncoding.ALaw => "alaw",
        PcmEncoding.MuLaw => "mulaw",
        PcmEncoding.UnsignedInteger when BitsPerSample <= 8 => "u8",
        _ => (Encoding == PcmEncoding.FloatingPoint ? "f" : Encoding == PcmEncoding.UnsignedInteger ? "u" : "s")
            + BitsPerSample.ToString(System.Globalization.CultureInfo.InvariantCulture) + (BitsPerSample <= 8 ? string.Empty : BigEndian ? "be" : "le"),
    };

    /// <inheritdoc />
    public override string ToString() => Name;
}
