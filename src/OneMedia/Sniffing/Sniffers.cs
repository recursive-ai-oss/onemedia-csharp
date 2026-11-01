using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Sniffing;

/// <summary>Shared byte-matching helpers for sniffers.</summary>
internal static class SniffHelpers
{
    public static bool StartsWith(ReadOnlySpan<byte> head, ReadOnlySpan<byte> signature) => head.StartsWith(signature);

    public static bool Is(ReadOnlySpan<byte> head, int offset, ReadOnlySpan<byte> signature) =>
        head.Length >= offset + signature.Length && head.Slice(offset, signature.Length).SequenceEqual(signature);
}

/// <summary>Recognises RIFF WAVE files, including Broadcast Wave, RF64/BW64 and Sony Wave64.</summary>
public sealed class WavSniffer : IMediaFormatSniffer
{
    private static ReadOnlySpan<byte> Riff => "RIFF"u8;
    private static ReadOnlySpan<byte> Rf64 => "RF64"u8;
    private static ReadOnlySpan<byte> Bw64 => "BW64"u8;
    private static ReadOnlySpan<byte> Wave => "WAVE"u8;

    // Wave64 starts with the GUID of its 'riff' chunk, then a 'wave' GUID at offset 24.
    private static ReadOnlySpan<byte> W64Riff => [0x72, 0x69, 0x66, 0x66, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];
    private static ReadOnlySpan<byte> W64Wave => [0x77, 0x61, 0x76, 0x65, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A];

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Wav;

    /// <inheritdoc />
    public string DisplayName => "WAV (RIFF, RF64, Wave64)";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        if (SniffHelpers.Is(head, 8, Wave))
        {
            if (SniffHelpers.StartsWith(head, Riff))
            {
                return new SniffResult(SniffConfidence.Certain, "riff");
            }

            if (SniffHelpers.StartsWith(head, Rf64))
            {
                return new SniffResult(SniffConfidence.Certain, "rf64");
            }

            if (SniffHelpers.StartsWith(head, Bw64))
            {
                return new SniffResult(SniffConfidence.Certain, "bw64");
            }
        }

        if (SniffHelpers.StartsWith(head, W64Riff))
        {
            return SniffHelpers.Is(head, 24, W64Wave) ? new SniffResult(SniffConfidence.Certain, "w64") : new SniffResult(SniffConfidence.Possible, "w64");
        }

        return SniffResult.None;
    }
}

/// <summary>Recognises AVI files (RIFF form type <c>AVI </c>, or an OpenDML <c>AVIX</c> extension).</summary>
public sealed class AviSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Avi;

    /// <inheritdoc />
    public string DisplayName => "AVI";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        if (!SniffHelpers.StartsWith(head, "RIFF"u8))
        {
            return SniffResult.None;
        }

        if (SniffHelpers.Is(head, 8, "AVI "u8) || SniffHelpers.Is(head, 8, "AVIX"u8))
        {
            return new SniffResult(SniffConfidence.Certain);
        }

        // Observed in files from some cameras: the form type is "AVI" followed by a stray byte and the header list follows as usual.
        return SniffHelpers.Is(head, 8, "AVI"u8) && SniffHelpers.Is(head, 12, "LIST"u8) ? new SniffResult(SniffConfidence.Possible) : SniffResult.None;
    }
}

/// <summary>Recognises ASF (WMV, WMA) by the GUID of its header object, which every ASF file begins with.</summary>
public sealed class AsfSniffer : IMediaFormatSniffer
{
    // The Header Object GUID, as it appears in files: 75B22630-668E-11CF-A6D9-00AA0062CE6C with the first three fields little-endian.
    private static ReadOnlySpan<byte> HeaderGuid => [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Asf;

    /// <inheritdoc />
    public string DisplayName => "ASF (WMV, WMA)";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) =>
        SniffHelpers.StartsWith(head, HeaderGuid) ? new SniffResult(SniffConfidence.Certain) : SniffResult.None;
}

/// <summary>
/// Recognises MPEG transport streams by their sync byte repeating at the packet spacing: 188 bytes (plain), 204 (with
/// Reed-Solomon parity) or 192 (M2TS/AVCHD, a 4-byte timestamp before each packet).
/// </summary>
public sealed class MpegTsSniffer : IMediaFormatSniffer
{
    // ISO/IEC 13818-1 §2.4.3.2: every transport packet starts with the sync byte 0x47.
    private const byte Sync = 0x47;

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.MpegTs;

    /// <inheritdoc />
    public string DisplayName => "MPEG transport stream";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        SniffResult best = SniffResult.None;
        best = Better(best, Check(head, 0, 188));
        best = Better(best, Check(head, 0, 204));
        best = Better(best, Check(head, 4, 192));
        return best;
    }

    private static SniffResult Better(SniffResult a, SniffResult b) => b.Confidence > a.Confidence ? b : a;

    private static SniffResult Check(ReadOnlySpan<byte> head, int start, int period)
    {
        if (head.Length <= start)
        {
            return SniffResult.None;
        }

        // Every packet start inside the head must carry the sync byte.
        int positions = ((head.Length - 1 - start) / period) + 1;
        if (positions < 2)
        {
            return SniffResult.None;
        }

        for (int i = 0; i < positions; i++)
        {
            if (head[start + (i * period)] != Sync)
            {
                return SniffResult.None;
            }
        }

        string detail = period.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new SniffResult(positions >= 5 ? SniffConfidence.Certain : SniffConfidence.Likely, detail);
    }
}

/// <summary>Recognises MPEG program streams (and VOB) by the pack start code.</summary>
public sealed class MpegPsSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.MpegPs;

    /// <inheritdoc />
    public string DisplayName => "MPEG program stream";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        // ISO/IEC 13818-1 §2.5.3.3: pack_start_code 0x000001BA, then '01' (MPEG-2) or '0010' (MPEG-1) marker bits.
        if (head.Length < 4 || head[0] != 0 || head[1] != 0 || head[2] != 1 || head[3] != 0xBA)
        {
            return SniffResult.None;
        }

        if (head.Length < 5)
        {
            return new SniffResult(SniffConfidence.Possible);
        }

        if ((head[4] & 0xC0) == 0x40)
        {
            return new SniffResult(SniffConfidence.Certain, "mpeg2");
        }

        return (head[4] & 0xF0) == 0x20 ? new SniffResult(SniffConfidence.Certain, "mpeg1") : SniffResult.None;
    }
}

/// <summary>Recognises Flash Video files.</summary>
public sealed class FlvSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Flv;

    /// <inheritdoc />
    public string DisplayName => "FLV";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == (byte)'F' && head[1] == (byte)'L' && head[2] == (byte)'V' && head[3] == 1
            ? new SniffResult(SniffConfidence.Certain)
            : SniffResult.None;
}

/// <summary>Recognises Ogg by the capture pattern of its first page.</summary>
public sealed class OggSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Ogg;

    /// <inheritdoc />
    public string DisplayName => "Ogg";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) =>
        // RFC 3533 §6: capture_pattern 'OggS', then stream_structure_version, which is 0.
        head.Length >= 5 && SniffHelpers.StartsWith(head, "OggS"u8) && head[4] == 0
            ? new SniffResult(SniffConfidence.Certain)
            : SniffResult.None;
}

/// <summary>Recognises native FLAC files.</summary>
public sealed class FlacSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Flac;

    /// <inheritdoc />
    public string DisplayName => "FLAC";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) =>
        SniffHelpers.StartsWith(head, "fLaC"u8) ? new SniffResult(SniffConfidence.Certain) : SniffResult.None;
}

/// <summary>Recognises AIFF and AIFF-C files.</summary>
public sealed class AiffSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Aiff;

    /// <inheritdoc />
    public string DisplayName => "AIFF";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        if (!SniffHelpers.StartsWith(head, "FORM"u8))
        {
            return SniffResult.None;
        }

        if (SniffHelpers.Is(head, 8, "AIFF"u8))
        {
            return new SniffResult(SniffConfidence.Certain, "aiff");
        }

        return SniffHelpers.Is(head, 8, "AIFC"u8) ? new SniffResult(SniffConfidence.Certain, "aifc") : SniffResult.None;
    }
}

/// <summary>Recognises Apple Core Audio Format files.</summary>
public sealed class CafSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Caf;

    /// <inheritdoc />
    public string DisplayName => "CAF";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) =>
        // The file header is 'caff', a 16-bit version (1) and 16-bit flags.
        head.Length >= 6 && SniffHelpers.StartsWith(head, "caff"u8) && BinaryPrimitives.ReadUInt16BigEndian(head[4..]) == 1
            ? new SniffResult(SniffConfidence.Certain)
            : SniffResult.None;
}

/// <summary>Recognises an ID3v2 tag at the start of a file (the audio behind it is for the format reader to find).</summary>
public sealed class Id3Sniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Id3;

    /// <inheritdoc />
    public string DisplayName => "ID3v2-tagged audio";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        // ID3v2 §3.1: 'ID3', version and revision bytes (never 0xFF), a flags byte and four size bytes of 7 bits each.
        if (head.Length < 10 || !SniffHelpers.StartsWith(head, "ID3"u8) || head[3] == 0xFF || head[4] == 0xFF
            || ((head[6] | head[7] | head[8] | head[9]) & 0x80) != 0)
        {
            return SniffResult.None;
        }

        return new SniffResult(SniffConfidence.Likely, "2." + head[3].ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Recognises raw MPEG audio (MP3, MP2) by a frame header at the start.</summary>
public sealed class MpegAudioSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.MpegAudio;

    /// <inheritdoc />
    public string DisplayName => "MPEG audio";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        // ISO/IEC 11172-3 §2.4.1.3: 11 sync bits, 2 version bits (01 reserved), 2 layer bits (00 reserved),
        // a bit-rate index (1111 invalid) and a sampling-frequency index (11 reserved).
        if (head.Length < 3 || head[0] != 0xFF || (head[1] & 0xE0) != 0xE0)
        {
            return SniffResult.None;
        }

        int version = (head[1] >> 3) & 3;
        int layer = (head[1] >> 1) & 3;
        int bitrate = head[2] >> 4;
        int rate = (head[2] >> 2) & 3;
        if (version == 1 || layer == 0 || bitrate == 15 || rate == 3)
        {
            return SniffResult.None;
        }

        return new SniffResult(SniffConfidence.Possible, layer switch { 3 => "layer1", 2 => "layer2", _ => "layer3" });
    }
}

/// <summary>Recognises AAC in ADTS framing by its sync word and a second header at the frame length.</summary>
public sealed class AdtsSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Adts;

    /// <inheritdoc />
    public string DisplayName => "AAC (ADTS)";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        // ISO/IEC 13818-7 §6.2: 12 sync bits, then ID, a layer field that is always 00, protection_absent;
        // sampling_frequency_index (13 and up are reserved); aac_frame_length is 13 bits spread over bytes 3 to 5.
        if (head.Length < 7 || head[0] != 0xFF || (head[1] & 0xF6) != 0xF0 || ((head[2] >> 2) & 0xF) >= 13)
        {
            return SniffResult.None;
        }

        int length = ((head[3] & 3) << 11) | (head[4] << 3) | (head[5] >> 5);
        if (length < 7)
        {
            return SniffResult.None;
        }

        if (head.Length >= length + 2)
        {
            return head[length] == 0xFF && (head[length + 1] & 0xF6) == 0xF0
                ? new SniffResult(SniffConfidence.Likely)
                : SniffResult.None;
        }

        return new SniffResult(SniffConfidence.Possible);
    }
}

/// <summary>Recognises AMR and AMR-WB storage files by their magic line.</summary>
public sealed class AmrSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Amr;

    /// <inheritdoc />
    public string DisplayName => "AMR";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        // RFC 4867 §5: "#!AMR\n" for narrowband, "#!AMR-WB\n" for wideband.
        if (SniffHelpers.StartsWith(head, "#!AMR-WB\n"u8))
        {
            return new SniffResult(SniffConfidence.Certain, "wb");
        }

        return SniffHelpers.StartsWith(head, "#!AMR\n"u8) ? new SniffResult(SniffConfidence.Certain, "nb") : SniffResult.None;
    }
}

/// <summary>Recognises IVF files (VP8, VP9 and AV1 frames).</summary>
public sealed class IvfSniffer : IMediaFormatSniffer
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Ivf;

    /// <inheritdoc />
    public string DisplayName => "IVF";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) =>
        SniffHelpers.StartsWith(head, "DKIF"u8) ? new SniffResult(SniffConfidence.Certain) : SniffResult.None;
}
