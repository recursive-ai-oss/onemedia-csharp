using System.Buffers;
using System.Globalization;

namespace RecursiveAI.OneMedia.Codecs.Aac;

/// <summary>Audio object types of ISO/IEC 14496-3 Table 1.17 that matter for stream description.</summary>
public static class AacObjectType
{
    /// <summary>AAC Main.</summary>
    public const int AacMain = 1;

    /// <summary>AAC LC.</summary>
    public const int AacLc = 2;

    /// <summary>AAC SSR.</summary>
    public const int AacSsr = 3;

    /// <summary>AAC LTP.</summary>
    public const int AacLtp = 4;

    /// <summary>Spectral band replication (HE-AAC).</summary>
    public const int Sbr = 5;

    /// <summary>AAC Scalable.</summary>
    public const int AacScalable = 6;

    /// <summary>ER AAC LC.</summary>
    public const int ErAacLc = 17;

    /// <summary>ER AAC LD.</summary>
    public const int ErAacLd = 23;

    /// <summary>Parametric stereo (HE-AAC v2).</summary>
    public const int Ps = 29;

    /// <summary>Escape value: the type is 32 + the next six bits.</summary>
    public const int Escape = 31;

    /// <summary>USAC (xHE-AAC).</summary>
    public const int Usac = 42;
}

/// <summary>
/// The AudioSpecificConfig of ISO/IEC 14496-3 §1.6.2.1, as far as stream description needs: object type, sampling rate, channel
/// configuration and the SBR and PS signalling in both explicit and backward-compatible forms.
/// </summary>
public readonly record struct AudioSpecificConfig
{
    /// <summary>The AAC sampling frequency table (§1.6.3.4, Table 1.18), indexed by samplingFrequencyIndex.</summary>
    public static ReadOnlySpan<int> SamplingFrequencies => [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    private static ReadOnlySpan<byte> ChannelCounts => [0, 1, 2, 3, 4, 5, 6, 8, 0, 0, 0, 7, 8, 24, 8, 0];

    /// <summary>The audio object type of the core coder (2 for AAC LC), after escape coding.</summary>
    public int AudioObjectType { get; init; }

    /// <summary>samplingFrequencyIndex of the core coder, 15 when the rate is explicit.</summary>
    public int SamplingFrequencyIndex { get; init; }

    /// <summary>The core coder's sampling frequency in Hz.</summary>
    public int SamplingFrequency { get; init; }

    /// <summary>channelConfiguration (0 means the channels come from the program config element).</summary>
    public int ChannelConfiguration { get; init; }

    /// <summary>The channel count, from the channel configuration table or, for configuration 0, from the program config element; 0 if unknown.</summary>
    public int Channels { get; init; }

    /// <summary>True when SBR is signalled, explicitly (object type 5 or 29) or by the backward-compatible extension.</summary>
    public bool SbrPresent { get; init; }

    /// <summary>True when parametric stereo is signalled.</summary>
    public bool PsPresent { get; init; }

    /// <summary>True when the signalling of SBR was explicit (the config starts with object type 5 or 29) rather than backward-compatible.</summary>
    public bool ExplicitSignalling { get; init; }

    /// <summary>The output sampling frequency after SBR (extensionSamplingFrequency), or 0 when not signalled.</summary>
    public int ExtensionSamplingFrequency { get; init; }

    /// <summary>frameLengthFlag of GASpecificConfig: true for 960-sample frames instead of 1024.</summary>
    public bool FrameLength960 { get; init; }

    /// <summary>The number of bits of the config that were parsed, excluding trailing padding.</summary>
    public int BitsParsed { get; init; }

    /// <summary>The sampling frequency a player outputs: the extension frequency when SBR is present, otherwise the core frequency.</summary>
    public int OutputSamplingFrequency => SbrPresent && ExtensionSamplingFrequency != 0 ? ExtensionSamplingFrequency : SbrPresent ? SamplingFrequency * 2 : SamplingFrequency;

    /// <summary>The audio object type string for <c>mp4a.40.N</c> (§RFC 6381): 29 with PS, 5 with SBR, otherwise the core type.</summary>
    public int SignalledObjectType => PsPresent ? AacObjectType.Ps : SbrPresent ? AacObjectType.Sbr : AudioObjectType;

    /// <summary>The RFC 6381 string, for example <c>mp4a.40.2</c>.</summary>
    public string Rfc6381 => string.Create(CultureInfo.InvariantCulture, $"mp4a.40.{SignalledObjectType}");

    /// <summary>The profile name ffprobe prints: <c>LC</c>, <c>HE-AAC</c>, <c>HE-AACv2</c> and so on.</summary>
    public string ProfileName => PsPresent ? "HE-AACv2" : SbrPresent ? "HE-AAC" : AudioObjectType switch
    {
        1 => "Main",
        2 => "LC",
        3 => "SSR",
        4 => "LTP",
        17 => "ER LC",
        23 => "LD",
        39 => "ELD",
        42 => "xHE-AAC",
        _ => "Unknown",
    };

    /// <summary>Parses an AudioSpecificConfig.</summary>
    /// <param name="data">The config bytes; trailing bytes after the config are ignored.</param>
    /// <param name="config">The result when true.</param>
    /// <returns>False when truncated or when a sampling index or channel configuration is reserved. Never throws, never allocates.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out AudioSpecificConfig config)
    {
        config = default;
        BitReader r = new(data);
        int aot = ReadAudioObjectType(ref r);
        int freqIndex = (int)r.ReadBits(4);
        int freq = ReadFrequency(ref r, freqIndex);
        int chan = (int)r.ReadBits(4);
        if (r.Overrun || freq == 0)
        {
            return false;
        }

        bool sbr = false, ps = false, explicitSignal = false;
        int extFreq = 0;
        int coreAot = aot;
        if (aot is 5 or 29)
        {
            // §1.6.2.1: an explicit extension: the first object type is SBR or PS and the core type follows.
            explicitSignal = true;
            sbr = true;
            ps = aot == 29;
            int extIndex = (int)r.ReadBits(4);
            extFreq = ReadFrequency(ref r, extIndex);
            coreAot = ReadAudioObjectType(ref r);
            if (coreAot == 22)
            {
                r.SkipBits(4); // extensionChannelConfiguration
            }
        }

        bool frame960 = false;
        int channels = chan < 16 ? ChannelCounts[chan] : 0;
        if (coreAot is 1 or 2 or 3 or 4 or 6 or 7 or 17 or 19 or 20 or 21 or 22 or 23)
        {
            // GASpecificConfig §4.4.1
            frame960 = r.ReadFlag();
            bool dependsOnCore = r.ReadFlag();
            if (dependsOnCore)
            {
                r.SkipBits(14);
            }

            bool extFlag = r.ReadFlag();
            if (chan == 0)
            {
                int pceChannels = ReadProgramConfigElement(ref r);
                if (pceChannels > 0)
                {
                    channels = pceChannels;
                }
            }

            if (coreAot is 6 or 20)
            {
                r.SkipBits(3);
            }

            if (extFlag)
            {
                if (coreAot == 22)
                {
                    r.SkipBits(16);
                }

                if (coreAot is 17 or 19 or 20 or 23)
                {
                    r.SkipBits(3);
                }

                r.SkipBits(1);
            }

            if (coreAot is 17 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27)
            {
                r.SkipBits(2); // epConfig; values 2 and 3 carry more fields that this layer does not need
            }
        }
        else if (coreAot == 42)
        {
            // USAC carries its own configuration; only the object type and rates are described here.
        }

        // Backward-compatible extension signalling, §1.6.6.1 / §4.4.1: a sync extension after the core config.
        if (!explicitSignal && coreAot != 42 && !r.Overrun && r.BitsRemaining >= 16 && r.BitsConsumed + 16 <= (long)data.Length * 8)
        {
            BitReader peek = r;
            if (peek.ReadBits(11) == 0x2b7)
            {
                int extType = ReadAudioObjectType(ref peek);
                if (extType == 5 && peek.ReadFlag())
                {
                    sbr = true;
                    int extIndex = (int)peek.ReadBits(4);
                    extFreq = ReadFrequency(ref peek, extIndex);
                    if (peek.BitsRemaining >= 12 && peek.ReadBits(11) == 0x548)
                    {
                        ps = peek.ReadFlag();
                    }
                }

                if (!peek.Overrun)
                {
                    r = peek;
                }
            }
        }

        if (r.Overrun)
        {
            return false;
        }

        // For explicit signalling the object type that follows is the core coder's; report that as the core type.
        config = new AudioSpecificConfig
        {
            AudioObjectType = explicitSignal ? coreAot : aot,
            SamplingFrequencyIndex = freqIndex,
            SamplingFrequency = freq,
            ChannelConfiguration = chan,
            Channels = ps && channels == 1 ? 2 : channels,
            SbrPresent = sbr,
            PsPresent = ps,
            ExplicitSignalling = explicitSignal,
            ExtensionSamplingFrequency = extFreq,
            FrameLength960 = frame960,
            BitsParsed = (int)Math.Min(r.BitsConsumed, int.MaxValue),
        };
        return true;
    }

    private static int ReadAudioObjectType(ref BitReader r)
    {
        int t = (int)r.ReadBits(5);
        return t == 31 ? 32 + (int)r.ReadBits(6) : t;
    }

    private static int ReadFrequency(ref BitReader r, int index)
    {
        if (index == 15)
        {
            return (int)r.ReadBits(24);
        }

        return index < SamplingFrequencies.Length ? SamplingFrequencies[index] : 0;
    }

    // ISO/IEC 14496-3 §4.4.1.1 program_config_element(); returns the channel count it describes.
    private static int ReadProgramConfigElement(ref BitReader r)
    {
        r.SkipBits(4 + 2 + 4); // element_instance_tag, object_type, sampling_frequency_index
        int front = (int)r.ReadBits(4);
        int side = (int)r.ReadBits(4);
        int back = (int)r.ReadBits(4);
        int lfe = (int)r.ReadBits(2);
        int assoc = (int)r.ReadBits(3);
        int cc = (int)r.ReadBits(4);
        if (r.ReadFlag())
        {
            r.SkipBits(4);
        }

        if (r.ReadFlag())
        {
            r.SkipBits(4);
        }

        if (r.ReadFlag())
        {
            r.SkipBits(3);
        }

        int channels = 0;
        for (int i = 0; i < front + side + back; i++)
        {
            channels += r.ReadFlag() ? 2 : 1; // is_cpe
            r.SkipBits(4);
        }

        channels += lfe;
        r.SkipBits((4 * lfe) + (4 * assoc) + (5 * cc));
        r.ByteAlign();
        int comment = (int)r.ReadBits(8);
        r.SkipBits(8L * comment);
        return r.Overrun ? 0 : channels;
    }

    /// <summary>Describes the stream.</summary>
    /// <returns>The summary, with the output rate when SBR is signalled.</returns>
    public CodecParameters Describe() => new()
    {
        Codec = CodecId.Aac,
        Rfc6381 = Rfc6381,
        Profile = SignalledObjectType,
        ProfileName = ProfileName,
        SampleRate = OutputSamplingFrequency,
        CoreSampleRate = SamplingFrequency,
        Channels = Channels,
        SamplesPerFrame = (FrameLength960 ? 960 : 1024) * (SbrPresent ? 2 : 1),
    };

    /// <summary>Writes a two-byte AudioSpecificConfig for AAC without SBR and with a channel configuration from the table (not 0), as
    /// derived from an ADTS header.</summary>
    /// <param name="audioObjectType">The object type, 1 to 30.</param>
    /// <param name="samplingFrequencyIndex">Index 0 to 12.</param>
    /// <param name="channelConfiguration">1 to 7.</param>
    /// <param name="destination">Receives two bytes.</param>
    /// <returns>False when an argument is out of range or the destination is shorter than two bytes.</returns>
    public static bool TryWriteSimple(int audioObjectType, int samplingFrequencyIndex, int channelConfiguration, Span<byte> destination)
    {
        if (destination.Length < 2 || audioObjectType is < 1 or > 30 || samplingFrequencyIndex is < 0 or > 12 || channelConfiguration is < 0 or > 15)
        {
            return false;
        }

        // 5 bits object type, 4 bits index, 4 bits channel configuration, then GASpecificConfig (frameLengthFlag, dependsOnCoreCoder, extensionFlag) all zero
        int bits = (audioObjectType << 11) | (samplingFrequencyIndex << 7) | (channelConfiguration << 3);
        destination[0] = (byte)(bits >> 8);
        destination[1] = (byte)bits;
        return true;
    }
}

/// <summary>An ADTS fixed and variable header (ISO/IEC 13818-7 §6.2, ISO/IEC 14496-3 §1.A.2).</summary>
public readonly record struct AdtsHeader
{
    /// <summary>ID: false for MPEG-4, true for MPEG-2.</summary>
    public bool Mpeg2 { get; init; }

    /// <summary>protection_absent: when false a 16-bit CRC follows the header.</summary>
    public bool ProtectionAbsent { get; init; }

    /// <summary>The audio object type: profile + 1.</summary>
    public int AudioObjectType { get; init; }

    /// <summary>sampling_frequency_index.</summary>
    public int SamplingFrequencyIndex { get; init; }

    /// <summary>channel_configuration.</summary>
    public int ChannelConfiguration { get; init; }

    /// <summary>aac_frame_length: header, CRC and payload in bytes.</summary>
    public int FrameLength { get; init; }

    /// <summary>adts_buffer_fullness (0x7FF means variable bit rate).</summary>
    public int BufferFullness { get; init; }

    /// <summary>number_of_raw_data_blocks_in_frame + 1.</summary>
    public int RawDataBlocks { get; init; }

    /// <summary>The header size in bytes: 7, or 9 with a CRC.</summary>
    public int HeaderSize => ProtectionAbsent ? 7 : 9;

    /// <summary>The sampling frequency in Hz, 0 for reserved indexes.</summary>
    public int SamplingFrequency => SamplingFrequencyIndex < 13 ? AudioSpecificConfig.SamplingFrequencies[SamplingFrequencyIndex] : 0;

    /// <summary>The payload size: frame length minus the header.</summary>
    public int PayloadSize => FrameLength - HeaderSize;

    /// <summary>Parses a header.</summary>
    /// <param name="data">Bytes starting at a syncword.</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when the syncword or layer is wrong, the sampling index is reserved, or the frame length is shorter than the header. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out AdtsHeader header)
    {
        header = default;
        if (data.Length < 7 || data[0] != 0xff || (data[1] & 0xf6) != 0xf0)
        {
            return false;
        }

        bool absent = (data[1] & 1) != 0;
        int index = (data[2] >> 2) & 0xf;
        int length = ((data[3] & 3) << 11) | (data[4] << 3) | (data[5] >> 5);
        if (index > 12 || length < (absent ? 7 : 9))
        {
            return false;
        }

        header = new AdtsHeader
        {
            Mpeg2 = (data[1] & 8) != 0,
            ProtectionAbsent = absent,
            AudioObjectType = (data[2] >> 6) + 1,
            SamplingFrequencyIndex = index,
            ChannelConfiguration = ((data[2] & 1) << 2) | (data[3] >> 6),
            FrameLength = length,
            BufferFullness = ((data[5] & 0x1f) << 6) | (data[6] >> 2),
            RawDataBlocks = (data[6] & 3) + 1,
        };
        return true;
    }

    /// <summary>Writes a header without CRC for a frame whose payload is <paramref name="payloadLength"/> bytes.</summary>
    /// <param name="audioObjectType">1 to 4 (profile + 1).</param>
    /// <param name="samplingFrequencyIndex">0 to 12.</param>
    /// <param name="channelConfiguration">0 to 7.</param>
    /// <param name="payloadLength">The raw data block size; the frame length must fit 13 bits.</param>
    /// <param name="destination">Receives seven bytes.</param>
    /// <returns>False when an argument is out of range.</returns>
    public static bool TryWrite(int audioObjectType, int samplingFrequencyIndex, int channelConfiguration, int payloadLength, Span<byte> destination)
    {
        int length = payloadLength + 7;
        if (destination.Length < 7 || audioObjectType is < 1 or > 4 || samplingFrequencyIndex is < 0 or > 12 || channelConfiguration is < 0 or > 7 || payloadLength < 0 || length > 0x1fff)
        {
            return false;
        }

        destination[0] = 0xff;
        destination[1] = 0xf1; // MPEG-4, layer 0, protection absent
        destination[2] = (byte)(((audioObjectType - 1) << 6) | (samplingFrequencyIndex << 2) | (channelConfiguration >> 2));
        destination[3] = (byte)(((channelConfiguration & 3) << 6) | (length >> 11));
        destination[4] = (byte)(length >> 3);
        destination[5] = (byte)(((length & 7) << 5) | 0x1f); // buffer fullness 0x7FF: variable bit rate
        destination[6] = 0xfc;
        return true;
    }
}

/// <summary>Conversion between ADTS frames and raw AAC access units with an AudioSpecificConfig, the form MP4 and Matroska store.</summary>
public static class AdtsRepackager
{
    /// <summary>Derives the AudioSpecificConfig from an ADTS header.</summary>
    /// <param name="frame">The first ADTS frame (the full frame, so a channel configuration of 0 can take its program config element).</param>
    /// <param name="writer">Receives the config bytes.</param>
    /// <returns>False when the header is invalid, or the stream's channel configuration is 0 and the frame does not start with a usable program config element.
    /// Frames holding several raw data blocks are accepted: only the header matters here.</returns>
    public static bool TryCreateConfig(ReadOnlySpan<byte> frame, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!AdtsHeader.TryParse(frame, out AdtsHeader h))
        {
            return false;
        }

        if (h.ChannelConfiguration != 0)
        {
            Span<byte> w = writer.GetSpan(2);
            if (!AudioSpecificConfig.TryWriteSimple(h.AudioObjectType, h.SamplingFrequencyIndex, h.ChannelConfiguration, w))
            {
                return false;
            }

            writer.Advance(2);
            return true;
        }

        // Channel configuration 0: the first raw data block starts with a program_config_element (id_syn_ele = 5, §4.4.1).
        // The ASC is then the usual 13 bits, a zero GASpecificConfig prefix of 3 bits, and the PCE bits copied as they are.
        int start = Math.Min(h.HeaderSize, frame.Length);
        ReadOnlySpan<byte> body = frame[start..];
        BitReader r = new(body);
        if (r.ReadBits(3) != 5)
        {
            return false;
        }

        // Re-read the PCE with the same grammar as the ASC parser to learn its length in bits.
        BitReader probe = new(body);
        probe.SkipBits(3);
        long pceStart = probe.BitsConsumed;
        if (!SkipPce(ref probe))
        {
            return false;
        }

        long pceBits = probe.BitsConsumed - pceStart;
        long totalBits = 5 + 4 + 4 + 3 + pceBits;
        int bytes = (int)((totalBits + 7) / 8);
        Span<byte> o = writer.GetSpan(bytes);
        o[..bytes].Clear();
        BitWriter bw = new(o);
        bw.Write((uint)h.AudioObjectType, 5);
        bw.Write((uint)h.SamplingFrequencyIndex, 4);
        bw.Write(0, 4);
        bw.Write(0, 3);
        BitReader copy = new(body);
        copy.SkipBits(3);
        for (long i = 0; i < pceBits; i++)
        {
            bw.Write((uint)copy.ReadBit(), 1);
        }

        writer.Advance(bytes);
        return true;
    }

    private static bool SkipPce(ref BitReader r)
    {
        r.SkipBits(10);
        int front = (int)r.ReadBits(4);
        int side = (int)r.ReadBits(4);
        int back = (int)r.ReadBits(4);
        int lfe = (int)r.ReadBits(2);
        int assoc = (int)r.ReadBits(3);
        int cc = (int)r.ReadBits(4);
        if (r.ReadFlag())
        {
            r.SkipBits(4);
        }

        if (r.ReadFlag())
        {
            r.SkipBits(4);
        }

        if (r.ReadFlag())
        {
            r.SkipBits(3);
        }

        r.SkipBits(5L * (front + side + back));
        r.SkipBits((4 * lfe) + (4 * assoc) + (5 * cc));
        r.ByteAlign();
        int comment = (int)r.ReadBits(8);
        r.SkipBits(8L * comment);
        return !r.Overrun;
    }

    /// <summary>Removes the ADTS header (and CRC) from one frame.</summary>
    /// <param name="frame">One complete ADTS frame; trailing bytes beyond its length are ignored.</param>
    /// <param name="payload">The raw data block(s), a slice of <paramref name="frame"/>. No copy is made.</param>
    /// <returns>False when the header is invalid or the frame is truncated.</returns>
    public static bool TryGetRawData(ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (!AdtsHeader.TryParse(frame, out AdtsHeader h) || frame.Length < h.FrameLength)
        {
            return false;
        }

        payload = frame.Slice(h.HeaderSize, h.PayloadSize);
        return true;
    }

    /// <summary>Wraps a raw AAC access unit in an ADTS frame, using a parsed config.</summary>
    /// <param name="config">The stream's AudioSpecificConfig; the core object type, rate and channel configuration are used.</param>
    /// <param name="raw">The access unit.</param>
    /// <param name="destination">Receives seven bytes of header plus the payload.</param>
    /// <param name="written">The frame length, or the length required when the destination is too small.</param>
    /// <returns><see cref="OperationStatus.Done"/>, <see cref="OperationStatus.DestinationTooSmall"/>, or <see cref="OperationStatus.InvalidData"/>
    /// when the config cannot be expressed in ADTS (object type above 4, explicit rate, or channel configuration 0 or above 7) or the frame exceeds 8191 bytes.</returns>
    public static OperationStatus WrapRaw(in AudioSpecificConfig config, ReadOnlySpan<byte> raw, Span<byte> destination, out int written)
    {
        written = 0;
        int total = 7 + raw.Length;
        if (config.AudioObjectType is < 1 or > 4 || config.SamplingFrequencyIndex > 12 || config.ChannelConfiguration is < 1 or > 7 || total > 0x1fff)
        {
            return OperationStatus.InvalidData;
        }

        written = total;
        if (destination.Length < total)
        {
            return OperationStatus.DestinationTooSmall;
        }

        AdtsHeader.TryWrite(config.AudioObjectType, config.SamplingFrequencyIndex, config.ChannelConfiguration, raw.Length, destination);
        raw.CopyTo(destination[7..]);
        return OperationStatus.Done;
    }
}

/// <summary>Writes bits, most significant first, into a caller buffer. Writes past the end are dropped.</summary>
internal ref struct BitWriter
{
    private readonly Span<byte> _data;
    private int _bit;

    public BitWriter(Span<byte> data)
    {
        _data = data;
        _bit = 0;
    }

    public void Write(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            int byteIndex = _bit >> 3;
            if (byteIndex < _data.Length && ((value >> i) & 1) != 0)
            {
                _data[byteIndex] |= (byte)(0x80 >> (_bit & 7));
            }

            _bit++;
        }
    }
}
