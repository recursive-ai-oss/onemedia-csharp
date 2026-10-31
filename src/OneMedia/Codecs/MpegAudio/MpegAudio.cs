using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Codecs.MpegAudio;

/// <summary>An MPEG audio frame header (ISO/IEC 11172-3 §2.4.1.3 and ISO/IEC 13818-3): Layer I, II or III of MPEG-1, MPEG-2 and MPEG-2.5.</summary>
public readonly record struct MpegAudioFrameHeader
{
    private static int BitRateKbps(int version, int layer, int index)
    {
        if (index is < 1 or > 14)
        {
            return 0;
        }

        ReadOnlySpan<ushort> table = (version == 3, layer) switch
        {
            (true, 1) => [32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
            (true, 2) => [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
            (true, _) => [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
            (false, 1) => [32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
            (false, _) => [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
        };
        return table[index - 1];
    }

    /// <summary>MPEG version: 3 for MPEG-1, 2 for MPEG-2, 0 for MPEG-2.5 (the two-bit field value).</summary>
    public int Version { get; init; }

    /// <summary>Layer: 1, 2 or 3.</summary>
    public int Layer { get; init; }

    /// <summary>True when a 16-bit CRC follows the header (protection_bit is 0).</summary>
    public bool HasCrc { get; init; }

    /// <summary>Bit rate in bits per second; 0 for the free-format index.</summary>
    public int BitRate { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>The padding bit.</summary>
    public bool Padding { get; init; }

    /// <summary>Channel mode: 0 stereo, 1 joint stereo, 2 dual channel, 3 single channel.</summary>
    public int ChannelMode { get; init; }

    /// <summary>Samples per channel in a frame: 384 for Layer I, 1152 for Layer II, 1152 or 576 for Layer III.</summary>
    public int SamplesPerFrame => Layer == 1 ? 384 : Layer == 2 || Version == 3 ? 1152 : 576;

    /// <summary>Channel count.</summary>
    public int Channels => ChannelMode == 3 ? 1 : 2;

    /// <summary>The frame length in bytes including the header; 0 for free format.</summary>
    public int FrameLength => BitRate == 0 ? 0 : Layer == 1 ? ((12 * BitRate / SampleRate) + (Padding ? 1 : 0)) * 4 : (SamplesPerFrame / 8 * BitRate / SampleRate) + (Padding ? 1 : 0);

    /// <summary>Parses a frame header.</summary>
    /// <param name="data">Four or more bytes starting at the sync word.</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when the sync word is absent or a field uses a reserved value (version 1, layer 0, bit rate index 15, sample rate index 3).</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out MpegAudioFrameHeader header)
    {
        header = default;
        if (data.Length < 4 || data[0] != 0xff || (data[1] & 0xe0) != 0xe0)
        {
            return false;
        }

        int version = (data[1] >> 3) & 3;
        int layerBits = (data[1] >> 1) & 3;
        int brIndex = data[2] >> 4;
        int srIndex = (data[2] >> 2) & 3;
        if (version == 1 || layerBits == 0 || brIndex == 15 || srIndex == 3)
        {
            return false;
        }

        int layer = 4 - layerBits;
        int baseRate = srIndex switch { 0 => 44100, 1 => 48000, _ => 32000 };
        int rate = version switch { 3 => baseRate, 2 => baseRate / 2, _ => baseRate / 4 };
        header = new MpegAudioFrameHeader
        {
            Version = version,
            Layer = layer,
            HasCrc = (data[1] & 1) == 0,
            BitRate = BitRateKbps(version, layer, brIndex) * 1000,
            SampleRate = rate,
            Padding = (data[2] & 2) != 0,
            ChannelMode = data[3] >> 6,
        };
        return true;
    }

    /// <summary>The codec parameters of a stream of such frames; Layer III is <c>mp3</c>, Layers II and I are <c>mp2</c> and <c>mp1</c>.</summary>
    /// <returns>The summary.</returns>
    public CodecParameters Describe() => new()
    {
        Codec = Layer == 3 ? CodecId.Mp3 : new CodecId(Layer == 2 ? "mp2" : "mp1"),
        Rfc6381 = Version == 3 ? "mp4a.6B" : "mp4a.69", // object type indications 0x6B (MPEG-1 audio) and 0x69 (MPEG-2 audio)
        SampleRate = SampleRate,
        Channels = Channels,
        SamplesPerFrame = SamplesPerFrame,
        BitRate = BitRate,
    };
}

/// <summary>The Xing, Info or VBRI header found in the first frame of a Layer III stream, with the LAME extension fields.</summary>
/// <param name="IsVbri">True for a Fraunhofer VBRI header; false for Xing/Info.</param>
/// <param name="IsCbr">True when the tag is spelled <c>Info</c> (constant bit rate); false for <c>Xing</c>.</param>
/// <param name="FrameCount">Number of frames, or -1 when absent.</param>
/// <param name="ByteCount">Number of bytes of audio, or -1 when absent.</param>
/// <param name="EncoderDelay">LAME encoder delay in samples, or -1 when no LAME tag.</param>
/// <param name="EncoderPadding">LAME end padding in samples, or -1 when no LAME tag.</param>
/// <param name="EncoderVersion">The nine-character encoder version string, empty when absent.</param>
public readonly record struct MpegAudioVbrHeader(bool IsVbri, bool IsCbr, long FrameCount, long ByteCount, int EncoderDelay, int EncoderPadding, string EncoderVersion)
{
    /// <summary>Looks for the tag in a first frame.</summary>
    /// <param name="frame">The first frame of the stream, starting at its header.</param>
    /// <param name="vbr">The result when true.</param>
    /// <returns>False when no Xing, Info or VBRI tag is found where it belongs.</returns>
    public static bool TryParse(ReadOnlySpan<byte> frame, out MpegAudioVbrHeader vbr)
    {
        vbr = default;
        if (!MpegAudioFrameHeader.TryParse(frame, out MpegAudioFrameHeader h) || h.Layer != 3)
        {
            return false;
        }

        // Side information precedes the tag: 32 bytes (MPEG-1 stereo), 17 (MPEG-1 mono or MPEG-2 stereo), 9 (MPEG-2 mono).
        bool mono = h.ChannelMode == 3;
        int side = h.Version == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        int at = 4 + (h.HasCrc ? 2 : 0) + side;
        if (frame.Length >= at + 4 && (frame.Slice(at, 4).SequenceEqual("Xing"u8) || frame.Slice(at, 4).SequenceEqual("Info"u8)))
        {
            bool cbr = frame[at] == (byte)'I';
            if (frame.Length < at + 8)
            {
                return false;
            }

            uint flags = BinaryPrimitives.ReadUInt32BigEndian(frame[(at + 4)..]);
            int p = at + 8;
            long frames = -1, bytes = -1;
            if ((flags & 1) != 0 && frame.Length >= p + 4)
            {
                frames = BinaryPrimitives.ReadUInt32BigEndian(frame[p..]);
                p += 4;
            }

            if ((flags & 2) != 0 && frame.Length >= p + 4)
            {
                bytes = BinaryPrimitives.ReadUInt32BigEndian(frame[p..]);
                p += 4;
            }

            if ((flags & 4) != 0)
            {
                p += 100;
            }

            if ((flags & 8) != 0)
            {
                p += 4;
            }

            int delay = -1, padding = -1;
            string version = string.Empty;
            // The LAME extension follows: 9 bytes of encoder version, then fields; delay and padding are 12 bits each at offset 21.
            if (frame.Length >= p + 24 && IsPrintable(frame.Slice(p, 4)))
            {
                version = System.Text.Encoding.ASCII.GetString(frame.Slice(p, 9)).TrimEnd('\0', ' ');
                int packed = (frame[p + 21] << 16) | (frame[p + 22] << 8) | frame[p + 23];
                delay = packed >> 12;
                padding = packed & 0xfff;
            }

            vbr = new MpegAudioVbrHeader(false, cbr, frames, bytes, delay, padding, version);
            return true;
        }

        // VBRI is always 32 bytes after the header (§ Fraunhofer; observed in files).
        int vbriAt = 4 + 32;
        if (frame.Length >= vbriAt + 18 && frame.Slice(vbriAt, 4).SequenceEqual("VBRI"u8))
        {
            long bytesV = BinaryPrimitives.ReadUInt32BigEndian(frame[(vbriAt + 10)..]);
            long framesV = BinaryPrimitives.ReadUInt32BigEndian(frame[(vbriAt + 14)..]);
            vbr = new MpegAudioVbrHeader(true, false, framesV, bytesV, -1, -1, string.Empty);
            return true;
        }

        return false;
    }

    private static bool IsPrintable(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s)
        {
            if (b is < 0x20 or > 0x7e)
            {
                return false;
            }
        }

        return true;
    }
}
