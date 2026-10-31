using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Codecs.Flac;

/// <summary>The STREAMINFO metadata block (RFC 9639 §8.2).</summary>
/// <param name="MinBlockSize">Minimum block size in samples.</param>
/// <param name="MaxBlockSize">Maximum block size in samples.</param>
/// <param name="MinFrameSize">Minimum frame size in bytes, 0 when unknown.</param>
/// <param name="MaxFrameSize">Maximum frame size in bytes, 0 when unknown.</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Channel count, 1 to 8.</param>
/// <param name="BitsPerSample">Bits per sample, 4 to 32.</param>
/// <param name="TotalSamples">Total samples per channel, 0 when unknown.</param>
public readonly record struct FlacStreamInfo(int MinBlockSize, int MaxBlockSize, int MinFrameSize, int MaxFrameSize, int SampleRate, int Channels, int BitsPerSample, long TotalSamples)
{
    /// <summary>Length of the STREAMINFO block body.</summary>
    public const int BodyLength = 34;

    /// <summary>Parses a STREAMINFO block body.</summary>
    /// <param name="body">The 34-byte block body.</param>
    /// <param name="info">The result when true.</param>
    /// <returns>False when shorter than 34 bytes, or the sample rate is 0 or block sizes are below 16.</returns>
    public static bool TryParse(ReadOnlySpan<byte> body, out FlacStreamInfo info)
    {
        info = default;
        if (body.Length < BodyLength)
        {
            return false;
        }

        int minBlock = BinaryPrimitives.ReadUInt16BigEndian(body);
        int maxBlock = BinaryPrimitives.ReadUInt16BigEndian(body[2..]);
        int minFrame = (body[4] << 16) | (body[5] << 8) | body[6];
        int maxFrame = (body[7] << 16) | (body[8] << 8) | body[9];
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(body[10..]);
        int rate = (int)(packed >> 44);
        int channels = (int)((packed >> 41) & 7) + 1;
        int bits = (int)((packed >> 36) & 0x1f) + 1;
        long total = (long)(packed & 0xf_ffff_ffffUL);
        if (rate == 0 || minBlock < 16 || maxBlock < 16)
        {
            return false;
        }

        info = new FlacStreamInfo(minBlock, maxBlock, minFrame, maxFrame, rate, channels, bits, total);
        return true;
    }

    /// <summary>Describes the stream.</summary>
    /// <returns>The summary.</returns>
    public CodecParameters Describe() => new()
    {
        Codec = CodecId.Flac,
        Rfc6381 = "flac",
        SampleRate = SampleRate,
        Channels = Channels,
        BitDepth = BitsPerSample,
        SamplesPerFrame = MinBlockSize == MaxBlockSize ? MinBlockSize : 0,
    };
}

/// <summary>A FLAC metadata block header (RFC 9639 §8.1).</summary>
/// <param name="IsLast">The last-metadata-block flag.</param>
/// <param name="Type">The block type: 0 STREAMINFO, 1 PADDING, 3 SEEKTABLE, 4 VORBIS_COMMENT, 6 PICTURE.</param>
/// <param name="Length">The body length in bytes.</param>
public readonly record struct FlacBlockHeader(bool IsLast, int Type, int Length)
{
    /// <summary>The size of the header.</summary>
    public const int Size = 4;

    /// <summary>Parses a header.</summary>
    /// <param name="data">At least four bytes.</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when too short or the type is 127 (invalid).</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out FlacBlockHeader header)
    {
        header = default;
        if (data.Length < Size || (data[0] & 0x7f) == 127)
        {
            return false;
        }

        header = new FlacBlockHeader((data[0] & 0x80) != 0, data[0] & 0x7f, (data[1] << 16) | (data[2] << 8) | data[3]);
        return true;
    }
}

/// <summary>Conversion between the <c>dfLa</c> box content, Matroska CodecPrivate and a native FLAC header (RFC 9639 §8; FLAC in ISOBMFF).</summary>
public static class FlacConfig
{
    private static ReadOnlySpan<byte> Marker => "fLaC"u8;

    /// <summary>Finds the STREAMINFO in a run of metadata blocks.</summary>
    /// <param name="blocks">Metadata blocks (headers and bodies), without the <c>fLaC</c> marker.</param>
    /// <param name="info">The STREAMINFO when true.</param>
    /// <returns>False when the first block is not a valid STREAMINFO.</returns>
    public static bool TryGetStreamInfo(ReadOnlySpan<byte> blocks, out FlacStreamInfo info)
    {
        info = default;
        return FlacBlockHeader.TryParse(blocks, out FlacBlockHeader h) && h.Type == 0 && h.Length == FlacStreamInfo.BodyLength
            && blocks.Length >= FlacBlockHeader.Size + h.Length && FlacStreamInfo.TryParse(blocks.Slice(FlacBlockHeader.Size, h.Length), out info);
    }

    /// <summary>Gets the metadata blocks from a Matroska CodecPrivate (<c>fLaC</c> followed by blocks).</summary>
    /// <param name="codecPrivate">The CodecPrivate.</param>
    /// <param name="blocks">The blocks, a slice of the input.</param>
    /// <returns>False when the marker is missing.</returns>
    public static bool TryGetBlocksFromCodecPrivate(ReadOnlyMemory<byte> codecPrivate, out ReadOnlyMemory<byte> blocks)
    {
        blocks = default;
        if (codecPrivate.Length < 4 + FlacBlockHeader.Size || !codecPrivate.Span[..4].SequenceEqual(Marker))
        {
            return false;
        }

        blocks = codecPrivate[4..];
        return true;
    }

    /// <summary>Gets the metadata blocks from a <c>dfLa</c> box content (a full box: four bytes of version and flags, then blocks).</summary>
    /// <param name="dfLa">The box content after the box header.</param>
    /// <param name="blocks">The blocks, a slice of the input.</param>
    /// <returns>False when the version is not 0 or the content is too short.</returns>
    public static bool TryGetBlocksFromDfLa(ReadOnlyMemory<byte> dfLa, out ReadOnlyMemory<byte> blocks)
    {
        blocks = default;
        if (dfLa.Length < 4 + FlacBlockHeader.Size || dfLa.Span[0] != 0)
        {
            return false;
        }

        blocks = dfLa[4..];
        return true;
    }

    /// <summary>Writes a Matroska CodecPrivate: the marker and the blocks.</summary>
    /// <param name="blocks">Metadata blocks.</param>
    /// <param name="writer">Receives <c>fLaC</c> and the blocks.</param>
    public static void WriteCodecPrivate(ReadOnlySpan<byte> blocks, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> w = writer.GetSpan(4 + blocks.Length);
        Marker.CopyTo(w);
        blocks.CopyTo(w[4..]);
        writer.Advance(4 + blocks.Length);
    }

    /// <summary>Writes a <c>dfLa</c> box content: version and flags zero, then the blocks.</summary>
    /// <param name="blocks">Metadata blocks.</param>
    /// <param name="writer">Receives four bytes and the blocks.</param>
    public static void WriteDfLa(ReadOnlySpan<byte> blocks, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> w = writer.GetSpan(4 + blocks.Length);
        w[..4].Clear();
        blocks.CopyTo(w[4..]);
        writer.Advance(4 + blocks.Length);
    }
}

/// <summary>A FLAC frame header (RFC 9639 §9.1).</summary>
/// <param name="VariableBlockSize">Blocking strategy: true when the frame number field is a sample number.</param>
/// <param name="BlockSize">Block size in samples.</param>
/// <param name="SampleRate">Sample rate in Hz, 0 when the header defers to STREAMINFO.</param>
/// <param name="ChannelAssignment">The 4-bit channel assignment: 0 to 7 is that many channels plus one, 8 to 10 stereo with side channels.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="BitsPerSample">Sample size in bits, 0 when the header defers to STREAMINFO.</param>
/// <param name="Number">The frame number or first sample number.</param>
/// <param name="HeaderSize">Header size in bytes including the CRC-8.</param>
public readonly record struct FlacFrameHeader(bool VariableBlockSize, int BlockSize, int SampleRate, int ChannelAssignment, int Channels, int BitsPerSample, long Number, int HeaderSize)
{
    /// <summary>Parses a frame header and checks its CRC-8.</summary>
    /// <param name="data">Bytes starting at a frame sync code.</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when the sync code, a reserved value or the CRC is wrong, or the data is short. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out FlacFrameHeader header)
    {
        header = default;
        if (data.Length < 6 || data[0] != 0xff || (data[1] & 0xfe) != 0xf8)
        {
            return false;
        }

        bool variable = (data[1] & 1) != 0;
        int bsCode = data[2] >> 4;
        int srCode = data[2] & 0xf;
        int chCode = data[3] >> 4;
        int sizeCode = (data[3] >> 1) & 7;
        if ((data[3] & 1) != 0 || bsCode == 0 || srCode == 15 || chCode > 10 || sizeCode == 3)
        {
            return false;
        }

        int pos = 4;
        // The coded number is UTF-8 style: a leading byte of 0xxxxxxx, 110xxxxx, ... 11111110 and 10xxxxxx continuation bytes (§9.1.5).
        int lead = data[pos];
        int extra = lead < 0x80 ? 0 : lead >= 0xfe ? 6 : lead >= 0xfc ? 5 : lead >= 0xf8 ? 4 : lead >= 0xf0 ? 3 : lead >= 0xe0 ? 2 : lead >= 0xc0 ? 1 : -1;
        if (extra < 0 || lead == 0xff)
        {
            return false;
        }

        long number = extra == 0 ? lead : lead & (0x3f >> extra);
        pos++;
        for (int i = 0; i < extra; i++)
        {
            if (pos >= data.Length || (data[pos] & 0xc0) != 0x80)
            {
                return false;
            }

            number = (number << 6) | (uint)(data[pos++] & 0x3f);
        }

        int blockSize;
        switch (bsCode)
        {
            case 1: blockSize = 192; break;
            case >= 2 and <= 5: blockSize = 576 << (bsCode - 2); break;
            case 6:
                if (pos >= data.Length) { return false; }
                blockSize = data[pos++] + 1;
                break;
            case 7:
                if (pos + 2 > data.Length) { return false; }
                blockSize = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) + 1;
                pos += 2;
                break;
            default: blockSize = 256 << (bsCode - 8); break;
        }

        int rate;
        switch (srCode)
        {
            case 0: rate = 0; break;
            case 12:
                if (pos >= data.Length) { return false; }
                rate = data[pos++] * 1000;
                break;
            case 13:
            case 14:
                if (pos + 2 > data.Length) { return false; }
                rate = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) * (srCode == 14 ? 10 : 1);
                pos += 2;
                break;
            default: rate = FixedRates[srCode - 1]; break;
        }

        if (pos >= data.Length)
        {
            return false;
        }

        // CRC-8 with polynomial x^8 + x^2 + x + 1 over all header bytes before the CRC (§9.1.8).
        int crc = 0;
        for (int i = 0; i < pos; i++)
        {
            crc ^= data[i];
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xff : (crc << 1) & 0xff;
            }
        }

        if (crc != data[pos])
        {
            return false;
        }

        int bits = sizeCode switch { 0 => 0, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, _ => 32 };
        header = new FlacFrameHeader(variable, blockSize, rate, chCode, chCode < 8 ? chCode + 1 : 2, bits, number, pos + 1);
        return true;
    }

    private static readonly int[] s_fixedRates = [88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];

    private static ReadOnlySpan<int> FixedRates => s_fixedRates;
}
