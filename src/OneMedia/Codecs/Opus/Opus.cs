using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Codecs.Opus;

/// <summary>The Opus identification header (RFC 7845 §5.1), also the content of the <c>dOps</c> box in a different byte order.</summary>
public sealed class OpusHead
{
    /// <summary>The Version field (1 in OpusHead, 0 in dOps).</summary>
    public int Version { get; init; } = 1;

    /// <summary>Output channel count.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Samples at 48 kHz to discard from the decoder output at the start.</summary>
    public int PreSkip { get; init; }

    /// <summary>The sampling rate of the original input in Hz (informational).</summary>
    public int InputSampleRate { get; init; }

    /// <summary>Output gain in dB as a signed 16-bit fixed-point value with 8 fractional bits.</summary>
    public int OutputGain { get; init; }

    /// <summary>Channel mapping family: 0 mono/stereo, 1 Vorbis order up to 8 channels, 255 undefined.</summary>
    public int ChannelMappingFamily { get; init; }

    /// <summary>Stream count (family other than 0).</summary>
    public int StreamCount { get; init; }

    /// <summary>Coupled stream count (family other than 0).</summary>
    public int CoupledCount { get; init; }

    /// <summary>The channel mapping table, one byte per output channel (family other than 0).</summary>
    public ReadOnlyMemory<byte> ChannelMapping { get; init; }

    /// <summary>Parses an OpusHead packet.</summary>
    /// <param name="data">Bytes starting with <c>OpusHead</c>.</param>
    /// <param name="head">The result when true.</param>
    /// <returns>False when the magic is wrong, the version's major part is not 0, or data is truncated.</returns>
    public static bool TryParseOpusHead(ReadOnlyMemory<byte> data, out OpusHead? head)
    {
        head = null;
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < 19 || !s[..8].SequenceEqual("OpusHead"u8) || (s[8] >> 4) != 0)
        {
            return false;
        }

        int channels = s[9];
        int family = s[18];
        int stream = 0, coupled = 0;
        ReadOnlyMemory<byte> map = default;
        if (family != 0)
        {
            if (s.Length < 21 + channels)
            {
                return false;
            }

            stream = s[19];
            coupled = s[20];
            map = data.Slice(21, channels);
        }
        else if (channels is < 1 or > 2)
        {
            return false;
        }

        head = new OpusHead
        {
            Version = s[8],
            ChannelCount = channels,
            PreSkip = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
            InputSampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
            OutputGain = BinaryPrimitives.ReadInt16LittleEndian(s[16..]),
            ChannelMappingFamily = family,
            StreamCount = stream,
            CoupledCount = coupled,
            ChannelMapping = map,
        };
        return true;
    }

    /// <summary>Parses the content of a <c>dOps</c> box (Encapsulation of Opus in ISO Base Media File Format §4.3.2).</summary>
    /// <param name="data">The box content after its header; all fields are big-endian.</param>
    /// <param name="head">The result when true.</param>
    /// <returns>False when truncated or the version is not 0.</returns>
    public static bool TryParseDOps(ReadOnlyMemory<byte> data, out OpusHead? head)
    {
        head = null;
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < 11 || s[0] != 0)
        {
            return false;
        }

        int channels = s[1];
        int family = s[10];
        int stream = 0, coupled = 0;
        ReadOnlyMemory<byte> map = default;
        if (family != 0)
        {
            if (s.Length < 13 + channels)
            {
                return false;
            }

            stream = s[11];
            coupled = s[12];
            map = data.Slice(13, channels);
        }

        head = new OpusHead
        {
            Version = 1,
            ChannelCount = channels,
            PreSkip = BinaryPrimitives.ReadUInt16BigEndian(s[2..]),
            InputSampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
            OutputGain = BinaryPrimitives.ReadInt16BigEndian(s[8..]),
            ChannelMappingFamily = family,
            StreamCount = stream,
            CoupledCount = coupled,
            ChannelMapping = map,
        };
        return true;
    }

    /// <summary>The size of the OpusHead packet.</summary>
    public int OpusHeadSize => 19 + (ChannelMappingFamily != 0 ? 2 + ChannelCount : 0);

    /// <summary>The size of the <c>dOps</c> content.</summary>
    public int DOpsSize => 11 + (ChannelMappingFamily != 0 ? 2 + ChannelCount : 0);

    /// <summary>Writes the OpusHead packet (little-endian, version 1).</summary>
    /// <param name="writer">Receives <see cref="OpusHeadSize"/> bytes.</param>
    public void WriteOpusHead(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> w = writer.GetSpan(OpusHeadSize);
        "OpusHead"u8.CopyTo(w);
        w[8] = 1;
        w[9] = (byte)ChannelCount;
        BinaryPrimitives.WriteUInt16LittleEndian(w[10..], (ushort)PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(w[12..], (uint)InputSampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(w[16..], (short)OutputGain);
        w[18] = (byte)ChannelMappingFamily;
        if (ChannelMappingFamily != 0)
        {
            w[19] = (byte)StreamCount;
            w[20] = (byte)CoupledCount;
            ChannelMapping.Span[..ChannelCount].CopyTo(w[21..]);
        }

        writer.Advance(OpusHeadSize);
    }

    /// <summary>Writes the <c>dOps</c> content (big-endian, version 0).</summary>
    /// <param name="writer">Receives <see cref="DOpsSize"/> bytes.</param>
    public void WriteDOps(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> w = writer.GetSpan(DOpsSize);
        w[0] = 0;
        w[1] = (byte)ChannelCount;
        BinaryPrimitives.WriteUInt16BigEndian(w[2..], (ushort)PreSkip);
        BinaryPrimitives.WriteUInt32BigEndian(w[4..], (uint)InputSampleRate);
        BinaryPrimitives.WriteInt16BigEndian(w[8..], (short)OutputGain);
        w[10] = (byte)ChannelMappingFamily;
        if (ChannelMappingFamily != 0)
        {
            w[11] = (byte)StreamCount;
            w[12] = (byte)CoupledCount;
            ChannelMapping.Span[..ChannelCount].CopyTo(w[13..]);
        }

        writer.Advance(DOpsSize);
    }

    /// <summary>Describes the stream. Opus always decodes at 48 kHz.</summary>
    /// <returns>The summary.</returns>
    public CodecParameters Describe() => new()
    {
        Codec = CodecId.Opus,
        Rfc6381 = "opus",
        SampleRate = 48000,
        Channels = ChannelCount,
        Priming = PreSkip,
    };
}

/// <summary>The TOC byte of an Opus packet (RFC 6716 §3.1).</summary>
public static class OpusToc
{
    /// <summary>Gets the number of samples at 48 kHz a packet decodes to.</summary>
    /// <param name="packet">One Opus packet.</param>
    /// <param name="samples">Total samples per channel at 48 kHz: frame duration × frame count.</param>
    /// <returns>False for an empty packet, a code 3 packet without its frame count byte, or a frame count of 0 or above 48 (RFC 6716 §3.2.5: the 120 ms limit).</returns>
    public static bool TryGetSamples(ReadOnlySpan<byte> packet, out int samples)
    {
        samples = 0;
        if (packet.Length < 1)
        {
            return false;
        }

        int toc = packet[0];
        int config = toc >> 3;
        int frameSamples = config switch
        {
            < 12 => (config & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 },        // SILK 10, 20, 40, 60 ms
            < 16 => (config & 1) == 0 ? 480 : 960,                                          // hybrid 10, 20 ms
            _ => (config & 3) switch { 0 => 120, 1 => 240, 2 => 480, _ => 960 },            // CELT 2.5, 5, 10, 20 ms
        };
        int frames;
        switch (toc & 3)
        {
            case 0: frames = 1; break;
            case 1:
            case 2: frames = 2; break;
            default:
                if (packet.Length < 2)
                {
                    return false;
                }

                frames = packet[1] & 0x3f;
                break;
        }

        if (frames == 0 || frames * frameSamples > 5760)
        {
            return false;
        }

        samples = frames * frameSamples;
        return true;
    }
}
