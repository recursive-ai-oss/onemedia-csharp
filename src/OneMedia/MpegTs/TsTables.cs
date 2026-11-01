using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.MpegTs;

/// <summary>One elementary stream announced by a program map table (ISO/IEC 13818-1 §2.4.4.8).</summary>
/// <param name="StreamType">The stream_type byte.</param>
/// <param name="Pid">The 13-bit packet identifier.</param>
/// <param name="Descriptors">The ES_info descriptor bytes, as stored.</param>
public sealed record TransportStreamEntry(int StreamType, int Pid, ReadOnlyMemory<byte> Descriptors);

/// <summary>One program of a transport stream: its PAT entry, its PMT and, when the SDT was seen, its service names.</summary>
/// <param name="ProgramNumber">The program_number from the PAT.</param>
/// <param name="PmtPid">The PID of the program map table.</param>
/// <param name="PcrPid">The PID carrying the program clock reference, or -1 before the PMT is read.</param>
/// <param name="Streams">The elementary streams in PMT order.</param>
/// <param name="TrackIndexes">The index in <see cref="IMediaContainer.Tracks"/> of each stream, in the same order.</param>
/// <param name="ServiceName">The DVB service name from the SDT, or null.</param>
/// <param name="ServiceProvider">The DVB service provider name from the SDT, or null.</param>
public sealed record TransportStreamProgram(
    int ProgramNumber,
    int PmtPid,
    int PcrPid,
    IReadOnlyList<TransportStreamEntry> Streams,
    IReadOnlyList<int> TrackIndexes,
    string? ServiceName,
    string? ServiceProvider);

/// <summary>What a stream_type plus its descriptors says a stream is.</summary>
internal readonly record struct TsStreamKind(TrackKind Kind, CodecId Codec, string? Tag, TrackDisposition Disposition = TrackDisposition.None, string? Language = null);

/// <summary>Maps stream types and descriptors to the neutral track kind and codec (ISO/IEC 13818-1 Table 2-34, ETSI EN 300 468 §6, ATSC A/53 and A/52, registration descriptors as observed in files).</summary>
internal static class TsStreamTypes
{
    public static TsStreamKind Resolve(int streamType, ReadOnlySpan<byte> descriptors, bool hdmv = false)
    {
        string? registration = null;
        string? language = null;
        TrackDisposition disposition = TrackDisposition.None;
        bool ac3 = false, eac3 = false, dts = false, aac = false, teletext = false, dvbSubtitle = false, opus = false;
        DescriptorReader reader = new(descriptors);
        while (reader.TryRead(out int tag, out ReadOnlySpan<byte> body))
        {
            switch (tag)
            {
                case 0x05 when body.Length >= 4:
                    registration = Encoding.ASCII.GetString(body[..4]);
                    break;
                case 0x0A when body.Length >= 4:
                    // §2.6.18: ISO_639_language_code (3 bytes) and audio_type.
                    language ??= Encoding.ASCII.GetString(body[..3]);
                    disposition |= body[3] switch { 2 => TrackDisposition.HearingImpaired, 3 => TrackDisposition.VisualImpaired, _ => TrackDisposition.None };
                    break;
                case 0x6A:
                    ac3 = true;
                    break;
                case 0x7A:
                    eac3 = true;
                    break;
                case 0x7B:
                    dts = true;
                    break;
                case 0x7C:
                    aac = true;
                    break;
                case 0x56:
                    teletext = true;
                    break;
                case 0x59 when body.Length >= 8:
                    // EN 300 468 §6.2.41: language (3), subtitling_type, composition_page_id, ancillary_page_id.
                    dvbSubtitle = true;
                    language ??= Encoding.ASCII.GetString(body[..3]);
                    disposition |= body[3] is 0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 ? TrackDisposition.HearingImpaired : TrackDisposition.None;
                    break;
                case 0x7F when body.Length >= 1 && body[0] == 0x80:
                    opus = true; // extension descriptor tag 0x80 carries the Opus channel configuration
                    break;
            }
        }

        language = language is { Length: 3 } l && l != "\0\0\0" ? l.ToLowerInvariant() : null;
        TsStreamKind Make(TrackKind kind, CodecId codec, string? tag) => new(kind, codec, tag, disposition, language);

        switch (streamType)
        {
            case 0x01:
                return Make(TrackKind.Video, new CodecId("mpeg1video"), "mpeg1video");
            case 0x02:
                return Make(TrackKind.Video, CodecId.Mpeg2Video, "mpeg2video");
            case 0x03:
            case 0x04:
                return Make(TrackKind.Audio, CodecId.Mp3, "mpegaudio");
            case 0x0F:
                return Make(TrackKind.Audio, CodecId.Aac, "aac-adts");
            case 0x11:
                return Make(TrackKind.Audio, new CodecId("aac-latm"), "aac-latm");
            case 0x10:
                return Make(TrackKind.Video, new CodecId("mpeg4"), "mpeg4-visual");
            case 0x1B:
                return Make(TrackKind.Video, CodecId.H264, "h264");
            case 0x24:
                return Make(TrackKind.Video, CodecId.Hevc, "hevc");
            case 0x33:
                return Make(TrackKind.Video, CodecId.Vvc, "vvc");
            case 0xEA:
                return Make(TrackKind.Video, new CodecId("vc1"), "vc1");
            case 0x81:
                return Make(TrackKind.Audio, CodecId.Ac3, "ac3");
            case 0x87:
            case 0x84:
                return Make(TrackKind.Audio, CodecId.Eac3, "eac3");
            case 0x82:
            case 0x85:
            case 0x8A:
                return Make(TrackKind.Audio, new CodecId("dts"), "dts");
            case 0x86 when registration == "CUEI" || descriptors.IsEmpty:
                return Make(TrackKind.Data, new CodecId("scte-35"), "scte-35");
            case 0x86:
                return Make(TrackKind.Audio, new CodecId("dts"), "dts-hd-ma");
            case 0x83:
                return Make(TrackKind.Audio, new CodecId("truehd"), "truehd");
            case 0x80 when hdmv:
                return Make(TrackKind.Audio, new CodecId("hdmv-lpcm"), "hdmv-lpcm");
            case 0x80:
                // ATSC / DigiCipher II broadcasts use 0x80 for MPEG-2 video; only Blu-ray and AVCHD layouts (M2TS packets, or a program-level 'HDMV' registration) use it for LPCM.
                return Make(TrackKind.Video, CodecId.Mpeg2Video, "mpeg2video");
            case 0x90:
                return Make(TrackKind.Subtitle, new CodecId("hdmv-pgs"), "hdmv-pgs");
            case 0x92:
                return Make(TrackKind.Subtitle, new CodecId("hdmv-text"), "hdmv-text");
            case 0x15:
                return Make(TrackKind.Data, new CodecId("timed-id3"), "metadata-pes");
            case 0x06:
                if (ac3 || registration == "AC-3")
                {
                    return Make(TrackKind.Audio, CodecId.Ac3, "ac3");
                }

                if (eac3 || registration == "EAC3")
                {
                    return Make(TrackKind.Audio, CodecId.Eac3, "eac3");
                }

                if (dts || registration is "DTS1" or "DTS2" or "DTS3")
                {
                    return Make(TrackKind.Audio, new CodecId("dts"), "dts");
                }

                if (aac)
                {
                    return Make(TrackKind.Audio, CodecId.Aac, "aac-adts");
                }

                if (opus || registration == "Opus")
                {
                    return Make(TrackKind.Audio, CodecId.Opus, "opus");
                }

                if (teletext)
                {
                    return Make(TrackKind.Subtitle, new CodecId("dvb-teletext"), "dvb-teletext");
                }

                if (dvbSubtitle)
                {
                    return Make(TrackKind.Subtitle, new CodecId("dvb-subtitle"), "dvb-subtitle");
                }

                if (registration == "KLVA")
                {
                    return Make(TrackKind.Data, new CodecId("klv"), "klv");
                }

                return Make(TrackKind.Data, CodecId.Unknown, "private-pes");
            default:
                return Make(TrackKind.Data, CodecId.Unknown, "0x" + streamType.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>Walks a descriptor loop (ISO/IEC 13818-1 §2.6): tag, length, body.</summary>
internal ref struct DescriptorReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public bool TryRead(out int tag, out ReadOnlySpan<byte> body)
    {
        if (_pos + 2 > _data.Length)
        {
            tag = -1;
            body = default;
            return false;
        }

        tag = _data[_pos];
        int length = _data[_pos + 1];
        if (_pos + 2 + length > _data.Length)
        {
            // A descriptor that runs past its loop is cut short rather than dropped.
            length = _data.Length - _pos - 2;
        }

        body = _data.Slice(_pos + 2, length);
        _pos += 2 + length;
        return true;
    }
}

/// <summary>The CRC-32 of program-specific information sections (ISO/IEC 13818-1 Annex A): polynomial 0x04C11DB7, initial value all ones, no reflection, no final inversion.</summary>
internal static class Mpeg2Crc
{
    private static readonly uint[] s_table = Build();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ s_table[(crc >> 24) ^ b];
        }

        return crc;
    }

    private static uint[] Build()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            }

            t[i] = c;
        }

        return t;
    }
}

/// <summary>Reassembles private or PSI sections from transport packet payloads (ISO/IEC 13818-1 §2.4.4.1: a pointer_field on the packet that starts a section).</summary>
internal sealed class SectionAssembler
{
    private const int MaxSection = 4096;
    private readonly byte[] _buf = new byte[MaxSection + 3];
    private int _length;
    private long _startOffset = -1;

    /// <summary>File offset of the transport packet that began the section last returned.</summary>
    public long SectionOffset { get; private set; }

    public void Reset()
    {
        _length = 0;
        _startOffset = -1;
    }

    /// <summary>Feeds one payload. Calls <paramref name="onSection"/> for each section completed.</summary>
    public void Push(ReadOnlySpan<byte> payload, bool payloadUnitStart, long packetOffset, DefectLog defects, Action<ReadOnlySpan<byte>, long> onSection)
    {
        if (payloadUnitStart)
        {
            if (payload.IsEmpty)
            {
                return;
            }

            int pointer = payload[0];
            if (pointer + 1 > payload.Length)
            {
                Reset();
                return;
            }

            // The bytes before the pointer finish the section in progress.
            if (_length > 0)
            {
                Append(payload.Slice(1, pointer));
                Drain(defects, onSection);
            }

            Reset();
            payload = payload[(1 + pointer)..];
            _startOffset = packetOffset;
        }
        else if (_length == 0 && _startOffset < 0)
        {
            return;
        }

        Append(payload);
        Drain(defects, onSection);
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        int n = Math.Min(data.Length, _buf.Length - _length);
        data[..n].CopyTo(_buf.AsSpan(_length));
        _length += n;
    }

    private void Drain(DefectLog defects, Action<ReadOnlySpan<byte>, long> onSection)
    {
        while (_length >= 3)
        {
            if (_buf[0] == 0xFF)
            {
                // Stuffing: the rest of the payload is 0xFF.
                Reset();
                return;
            }

            int total = 3 + (BinaryPrimitives.ReadUInt16BigEndian(_buf.AsSpan(1)) & 0x0FFF);
            if (total > MaxSection)
            {
                defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, _startOffset, "A section is longer than the 4096 bytes the format allows; it is skipped.", "ts.section-too-long");
                Reset();
                return;
            }

            if (_length < total)
            {
                return;
            }

            SectionOffset = _startOffset;
            onSection(_buf.AsSpan(0, total), _startOffset);
            _length -= total;
            Buffer.BlockCopy(_buf, total, _buf, 0, _length);
        }
    }
}

/// <summary>Reads DVB text (ETSI EN 300 468 Annex A): an optional leading character-table byte, then the characters.</summary>
internal static class DvbText
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        int first = bytes[0];
        // Annex A Table A.3: 0x15 selects UTF-8; 0x10 selects ISO/IEC 8859 by a following two-byte part number. Other tables fall back to Latin-1.
        if (first == 0x15)
        {
            return Clean(Encoding.UTF8.GetString(bytes[1..]));
        }

        if (first == 0x10 && bytes.Length >= 3)
        {
            return Clean(Encoding.Latin1.GetString(bytes[3..]));
        }

        if (first < 0x20)
        {
            bytes = bytes[1..];
        }

        return Clean(Encoding.Latin1.GetString(bytes));
    }

    private static string Clean(string s)
    {
        // 0x86/0x87 are emphasis on/off and 0x8A is a line break (Annex A.2).
        StringBuilder sb = new(s.Length);
        foreach (char c in s)
        {
            if (c == '\u008A')
            {
                sb.Append('\n');
            }
            else if (c is not ('\u0086' or '\u0087'))
            {
                sb.Append(c);
            }
        }

        return sb.ToString().TrimEnd('\0');
    }
}
