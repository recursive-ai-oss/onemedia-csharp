using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs.Pcm;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// The <c>fmt </c> chunk of a WAVE file: WAVEFORMAT, PCMWAVEFORMAT, WAVEFORMATEX or WAVEFORMATEXTENSIBLE, read from however many
/// bytes the chunk has.
/// </summary>
public sealed record WaveFormat
{
    private static readonly byte[] SubFormatTail = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    /// <summary>WAVE_FORMAT_EXTENSIBLE.</summary>
    public const ushort Extensible = 0xFFFE;

    /// <summary>The format tag as stored: 1 PCM, 3 IEEE float, 6 A-law, 7 µ-law, 0xFFFE extensible…</summary>
    public ushort FormatTag { get; init; }

    /// <summary>Number of channels.</summary>
    public int Channels { get; init; }

    /// <summary>Frames per second.</summary>
    public int SampleRate { get; init; }

    /// <summary>Average data rate in bytes per second as declared.</summary>
    public long AverageBytesPerSecond { get; init; }

    /// <summary>Size in bytes of the smallest unit of data: one frame for PCM, one block for block-based codecs.</summary>
    public int BlockAlign { get; init; }

    /// <summary>Bits per sample as declared (0 when the chunk is too short to say).</summary>
    public int BitsPerSample { get; init; }

    /// <summary>For WAVE_FORMAT_EXTENSIBLE, the number of significant bits per sample; otherwise equal to <see cref="BitsPerSample"/>.</summary>
    public int ValidBitsPerSample { get; init; }

    /// <summary>For WAVE_FORMAT_EXTENSIBLE, the speaker position mask (bit 0 front left, bit 1 front right, …); 0 when the file gives none.</summary>
    public uint ChannelMask { get; init; }

    /// <summary>For WAVE_FORMAT_EXTENSIBLE, the sub-format GUID as stored; <see cref="Guid.Empty"/> otherwise.</summary>
    public Guid SubFormat { get; init; }

    /// <summary>The format-specific bytes after the 16 fixed ones (the cbSize field excluded), as stored: ADPCM coefficients, the extensible tail, an AudioSpecificConfig.</summary>
    public ReadOnlyMemory<byte> Extra { get; init; }

    /// <summary>The tag that identifies the codec: <see cref="FormatTag"/>, or for WAVE_FORMAT_EXTENSIBLE the first four bytes of the sub-format when it uses the standard base GUID (else <see cref="Extensible"/>).</summary>
    public ushort EffectiveTag { get; init; }

    /// <summary>True for WAVE_FORMAT_EXTENSIBLE.</summary>
    public bool IsExtensible => FormatTag == Extensible;

    /// <summary>
    /// Reads the chunk payload.
    /// </summary>
    /// <param name="data">The payload of the <c>fmt </c> chunk.</param>
    /// <param name="format">The format.</param>
    /// <returns>False when fewer than 14 bytes are present.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out WaveFormat? format)
    {
        format = null;
        if (data.Length < 14)
        {
            return false;
        }

        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int bits = data.Length >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) : 0;
        ReadOnlySpan<byte> extra = default;
        if (data.Length >= 18)
        {
            int cb = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
            extra = data.Slice(18, Math.Min(cb, data.Length - 18));
        }

        int valid = bits;
        uint mask = 0;
        Guid sub = Guid.Empty;
        ushort effective = tag;
        if (tag == Extensible && extra.Length >= 22)
        {
            valid = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            mask = BinaryPrimitives.ReadUInt32LittleEndian(extra[2..]);
            sub = new Guid(extra.Slice(6, 16));
            ReadOnlySpan<byte> raw = extra.Slice(6, 16);
            // Microsoft's sub-format GUIDs are {tag-0000-0010-8000-00AA00389B71}; any other base is a vendor's own.
            effective = raw[4..].SequenceEqual(SubFormatTail) && raw[2] == 0 && raw[3] == 0 ? BinaryPrimitives.ReadUInt16LittleEndian(raw) : Extensible;
        }

        format = new WaveFormat
        {
            FormatTag = tag,
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            SampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), int.MaxValue),
            AverageBytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            BitsPerSample = bits,
            ValidBitsPerSample = valid,
            ChannelMask = mask,
            SubFormat = sub,
            Extra = extra.ToArray(),
            EffectiveTag = effective,
        };
        return true;
    }
}

/// <summary>Names the codec a WAVE format tag stands for, and the PCM layout where the codec is uncompressed.</summary>
public static class WaveCodecs
{
    /// <summary>Maps the format to a codec.</summary>
    /// <param name="format">The parsed <c>fmt </c> chunk.</param>
    /// <param name="pcm">For PCM, float and G.711 formats, the sample layout; otherwise <c>null</c>.</param>
    /// <returns>The codec, or <see cref="CodecId.Unknown"/> for a tag this layer does not know (the tag stays available as the codec tag).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is null.</exception>
    public static CodecId Describe(WaveFormat format, out PcmSampleFormat? pcm)
    {
        ArgumentNullException.ThrowIfNull(format);
        pcm = null;
        // Container width: blockAlign / channels is what the data really uses when the declared width is odd (20 bits in 3 bytes).
        int container = format.BlockAlign > 0 && format.Channels > 0 && format.BlockAlign % format.Channels == 0 ? format.BlockAlign / format.Channels * 8 : (format.BitsPerSample + 7) / 8 * 8;
        int valid = format.ValidBitsPerSample > 0 && format.ValidBitsPerSample <= container ? format.ValidBitsPerSample : container;
        switch (format.EffectiveTag)
        {
            case 0x0001:
                if (container == 0)
                {
                    return CodecId.Pcm;
                }

                pcm = new PcmSampleFormat(container <= 8 ? PcmEncoding.UnsignedInteger : PcmEncoding.SignedInteger, container, valid, false);
                return CodecId.Pcm;
            case 0x0003:
                pcm = new PcmSampleFormat(PcmEncoding.FloatingPoint, container, valid, false);
                return CodecId.Pcm;
            case 0x0006:
                pcm = new PcmSampleFormat(PcmEncoding.ALaw, 8, 8, false);
                return CodecId.Pcm;
            case 0x0007:
                pcm = new PcmSampleFormat(PcmEncoding.MuLaw, 8, 8, false);
                return CodecId.Pcm;
            case 0x0002:
                return CodecId.AdpcmMs;
            case 0x0011:
                return CodecId.AdpcmImaWav;
            case 0x0050:
                return CodecId.Mp2;
            case 0x0055:
                return CodecId.Mp3;
            case 0x00FF or 0x1600 or 0x1601 or 0x1602 or 0xA106:
                return CodecId.Aac;
            case 0x2000:
                return CodecId.Ac3;
            case 0xF1AC:
                return CodecId.Flac;
            default:
                return CodecId.Unknown;
        }
    }

    /// <summary>The codec tag as <see cref="MediaTrack.CodecTag"/> spells it for WAVE: the format tag in hexadecimal, such as <c>0x0001</c>, with the sub-format's tag for WAVE_FORMAT_EXTENSIBLE.</summary>
    /// <param name="format">The parsed <c>fmt </c> chunk.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is null.</exception>
    public static string Tag(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return "0x" + format.EffectiveTag.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
    }
}
