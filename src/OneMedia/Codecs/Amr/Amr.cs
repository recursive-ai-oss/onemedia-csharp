using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Codecs.Amr;

/// <summary>AMR frame framing (RFC 4867 §5.3 for the storage format, 3GPP TS 26.101 for narrowband and TS 26.201 for wideband).</summary>
public static class AmrFrame
{
    private static ReadOnlySpan<byte> NarrowbandSizes => [13, 14, 16, 18, 20, 21, 27, 32, 6, 0, 0, 0, 0, 0, 0, 1];
    private static ReadOnlySpan<byte> WidebandSizes => [18, 24, 33, 37, 41, 47, 51, 59, 61, 6, 0, 0, 0, 0, 1, 1];

    /// <summary>Samples per channel in a 20 ms frame: 160 at 8 kHz, 320 at 16 kHz.</summary>
    /// <param name="wideband">True for AMR-WB.</param>
    /// <returns>The sample count.</returns>
    public static int SamplesPerFrame(bool wideband) => wideband ? 320 : 160;

    /// <summary>Gets the size of a frame including its table-of-contents byte, from that byte.</summary>
    /// <param name="toc">The TOC byte: P(1) FT(4) Q(1) padding(2).</param>
    /// <param name="wideband">True for AMR-WB.</param>
    /// <param name="size">The frame size in bytes (TOC included).</param>
    /// <returns>False for the reserved frame types (10 to 14 for narrowband, 10 to 13 for wideband) and for a set padding bit pattern is not checked.</returns>
    public static bool TryGetFrameSize(byte toc, bool wideband, out int size)
    {
        int ft = (toc >> 3) & 0xf;
        size = wideband ? WidebandSizes[ft] : NarrowbandSizes[ft];
        return size != 0;
    }

    /// <summary>Recognises the magic number that starts an AMR storage file.</summary>
    /// <param name="data">The start of a file.</param>
    /// <param name="wideband">True for <c>#!AMR-WB\n</c>.</param>
    /// <param name="headerLength">The magic's length: 6 for narrowband, 9 for wideband.</param>
    /// <returns>True when the magic is present. Multi-channel files (<c>#!AMR_MC1.0</c>) are not recognised.</returns>
    public static bool TryReadMagic(ReadOnlySpan<byte> data, out bool wideband, out int headerLength)
    {
        wideband = false;
        headerLength = 0;
        if (data.StartsWith("#!AMR-WB\n"u8))
        {
            wideband = true;
            headerLength = 9;
            return true;
        }

        if (data.StartsWith("#!AMR\n"u8))
        {
            headerLength = 6;
            return true;
        }

        return false;
    }
}

/// <summary>The content of the <c>damr</c> box (3GPP TS 26.244 §8.4.2).</summary>
/// <param name="VendorCode">Four-character vendor code of the encoder.</param>
/// <param name="DecoderVersion">Decoder version, normally 0.</param>
/// <param name="ModeSet">Bit mask of the frame types used (bit n for frame type n).</param>
/// <param name="ModeChangePeriod">Mode change period in frames, 0 when unrestricted.</param>
/// <param name="FramesPerSample">Number of frames stored in each sample.</param>
public readonly record struct AmrSpecificBox(uint VendorCode, int DecoderVersion, int ModeSet, int ModeChangePeriod, int FramesPerSample)
{
    /// <summary>The box content length.</summary>
    public const int Size = 9;

    /// <summary>Parses the box content.</summary>
    /// <param name="data">The content after the box header.</param>
    /// <param name="box">The result when true.</param>
    /// <returns>False when shorter than nine bytes.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out AmrSpecificBox box)
    {
        box = default;
        if (data.Length < Size)
        {
            return false;
        }

        box = new AmrSpecificBox(BinaryPrimitives.ReadUInt32BigEndian(data), data[4], BinaryPrimitives.ReadUInt16BigEndian(data[5..]), data[7], data[8]);
        return true;
    }

    /// <summary>Writes the box content.</summary>
    /// <param name="destination">Receives nine bytes.</param>
    /// <returns>False when the destination is too short.</returns>
    public bool TryWrite(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination, VendorCode);
        destination[4] = (byte)DecoderVersion;
        BinaryPrimitives.WriteUInt16BigEndian(destination[5..], (ushort)ModeSet);
        destination[7] = (byte)ModeChangePeriod;
        destination[8] = (byte)FramesPerSample;
        return true;
    }
}
