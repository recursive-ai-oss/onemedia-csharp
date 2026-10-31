namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Maps ISO BMFF / QuickTime sample entry codes to the neutral codec ids.</summary>
public static class IsoCodecMap
{
    /// <summary>
    /// Finds the neutral codec for a sample entry.
    /// </summary>
    /// <param name="format">The sample entry's four-character code (the original format for protected entries).</param>
    /// <param name="configuration">The entry's configuration box payload; for <c>esds</c> it names the object type that decides MPEG-4 systems entries.</param>
    /// <returns>The codec id, or <see cref="CodecId.Unknown"/> when the entry is not recognised (the code itself stays in <see cref="MediaTrack.CodecTag"/>).</returns>
    public static CodecId Map(string format, CodecConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(format);
        switch (format)
        {
            case "avc1" or "avc2" or "avc3" or "avc4":
                return CodecId.H264;
            case "hvc1" or "hev1" or "dvh1" or "dvhe":
                return CodecId.Hevc;
            case "vvc1" or "vvi1":
                return CodecId.Vvc;
            case "av01":
                return CodecId.Av1;
            case "vp08":
                return CodecId.Vp8;
            case "vp09":
                return CodecId.Vp9;
            case "jpeg" or "mjpa" or "mjpb" or "mjp2":
                return format == "mjp2" ? new CodecId("jpeg2000") : CodecId.Mjpeg;
            case "s263" or "h263":
                return new CodecId("h263");
            case "mp4v":
                return FromObjectType(configuration, CodecId.Unknown);
            case "mp4a":
                return FromObjectType(configuration, CodecId.Aac);
            case ".mp3" or "ms\0U":
                return CodecId.Mp3;
            case "Opus":
                return CodecId.Opus;
            case "fLaC":
                return CodecId.Flac;
            case "ac-3":
                return CodecId.Ac3;
            case "ec-3":
                return CodecId.Eac3;
            case "samr":
                return CodecId.AmrNb;
            case "sawb":
                return CodecId.AmrWb;
            case "alac":
                return new CodecId("alac");
            case "sowt" or "twos" or "in24" or "in32" or "fl32" or "fl64" or "lpcm" or "raw " or "ipcm" or "fpcm" or "sowt\0":
                return CodecId.Pcm;
            case "tx3g":
                return CodecId.TimedText;
            case "wvtt":
                return CodecId.WebVtt;
            case "stpp":
                return new CodecId("ttml");
            default:
                return CodecId.Unknown;
        }
    }

    // ISO/IEC 14496-1 §7.2.6.6: the DecoderConfigDescriptor's objectTypeIndication names the codec of an MPEG-4 systems sample entry.
    private static CodecId FromObjectType(CodecConfiguration configuration, CodecId fallback)
    {
        if (!TryGetObjectType(configuration, out int oti))
        {
            return fallback;
        }

        return oti switch
        {
            0x20 => new CodecId("mpeg4"),
            0x21 => CodecId.H264,
            0x23 => CodecId.Hevc,
            0x40 or 0x66 or 0x67 or 0x68 => CodecId.Aac,
            0x60 or 0x61 or 0x62 or 0x63 or 0x64 or 0x65 => CodecId.Mpeg2Video,
            0x6A => new CodecId("mpeg1video"),
            0x69 or 0x6B => CodecId.Mp3,
            0x6C => CodecId.Mjpeg,
            0xA5 => CodecId.Ac3,
            0xA6 => CodecId.Eac3,
            0xDD => CodecId.Vorbis,
            _ => fallback,
        };
    }

    /// <summary>Reads the object type indication from an <c>esds</c> payload (ISO/IEC 14496-1 §7.2.6.5 and §7.2.6.6).</summary>
    /// <param name="configuration">The configuration; only <c>esds</c> payloads are understood.</param>
    /// <param name="objectType">The object type indication.</param>
    /// <returns>True when the descriptors were well formed up to the object type.</returns>
    public static bool TryGetObjectType(CodecConfiguration configuration, out int objectType)
    {
        objectType = 0;
        if (configuration.Format != "esds")
        {
            return false;
        }

        ReadOnlySpan<byte> d = configuration.Data.Span;
        int pos = 4; // version and flags
        if (!TryReadDescriptorHeader(d, ref pos, out int tag, out _) || tag != 0x03)
        {
            return false;
        }

        if (pos + 3 > d.Length)
        {
            return false;
        }

        int flags = d[pos + 2];
        pos += 3; // ES_ID and the flag byte
        if ((flags & 0x80) != 0)
        {
            pos += 2;
        }

        if ((flags & 0x40) != 0)
        {
            if (pos >= d.Length)
            {
                return false;
            }

            pos += 1 + d[pos];
        }

        if ((flags & 0x20) != 0)
        {
            pos += 2;
        }

        if (!TryReadDescriptorHeader(d, ref pos, out tag, out _) || tag != 0x04 || pos >= d.Length)
        {
            return false;
        }

        objectType = d[pos];
        return true;
    }

    private static bool TryReadDescriptorHeader(ReadOnlySpan<byte> d, ref int pos, out int tag, out int size)
    {
        tag = 0;
        size = 0;
        if (pos >= d.Length)
        {
            return false;
        }

        tag = d[pos++];
        for (int i = 0; i < 4; i++)
        {
            if (pos >= d.Length)
            {
                return false;
            }

            byte b = d[pos++];
            size = (size << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return true;
    }
}
