using System.Buffers;

namespace RecursiveAI.OneMedia.Codecs;

/// <summary>The NAL-unit based video codecs, which share Annex B and length-prefixed framing.</summary>
public enum NalCodec
{
    /// <summary>ITU-T H.264 (AVC): one-byte NAL header.</summary>
    H264,

    /// <summary>ITU-T H.265 (HEVC): two-byte NAL header.</summary>
    Hevc,

    /// <summary>ITU-T H.266 (VVC): two-byte NAL header.</summary>
    Vvc,
}

/// <summary>Helpers for the NAL unit header of the three NAL-based codecs.</summary>
public static class NalUnit
{
    /// <summary>The size of the NAL unit header in bytes: 1 for H.264, 2 for H.265 and H.266.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>The header size.</returns>
    public static int HeaderSize(NalCodec codec) => codec == NalCodec.H264 ? 1 : 2;

    /// <summary>Reads nal_unit_type from a NAL unit (ITU-T H.264 §7.3.1, H.265 §7.3.1.2, H.266 §7.3.1.2).</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="nal">The NAL unit including its header.</param>
    /// <returns>The type, or -1 when <paramref name="nal"/> is too short.</returns>
    public static int GetType(NalCodec codec, ReadOnlySpan<byte> nal)
    {
        if (nal.Length < HeaderSize(codec))
        {
            return -1;
        }

        return codec switch
        {
            NalCodec.H264 => nal[0] & 0x1f,
            NalCodec.Hevc => (nal[0] >> 1) & 0x3f,
            _ => nal[1] >> 3, // H.266 §7.3.1.2: the type is the top five bits of the second header byte
        };
    }

    /// <summary>True when the NAL unit is a parameter set (SPS or PPS for H.264; VPS, SPS or PPS for H.265; DCI, OPI, VPS, SPS, PPS or APS for H.266).</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="type">A value from <see cref="GetType"/>.</param>
    /// <returns>True for a parameter set.</returns>
    public static bool IsParameterSet(NalCodec codec, int type) => codec switch
    {
        NalCodec.H264 => type is 7 or 8,
        NalCodec.Hevc => type is >= 32 and <= 34,
        _ => type is >= 12 and <= 17,
    };

    /// <summary>True when the NAL unit starts a random access point picture that a decoder can begin at (IDR for H.264; IRAP for H.265 and H.266).</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="type">A value from <see cref="GetType"/>.</param>
    /// <returns>True for a random access picture's slice.</returns>
    public static bool IsRandomAccess(NalCodec codec, int type) => codec switch
    {
        NalCodec.H264 => type == 5,
        NalCodec.Hevc => type is >= 16 and <= 23,
        _ => type is >= 7 and <= 9, // H.266 Table 5: IDR_W_RADL, IDR_N_LP, CRA_NUT
    };

    /// <summary>True for a video coding layer (slice) NAL unit.</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="type">A value from <see cref="GetType"/>.</param>
    /// <returns>True for a slice.</returns>
    public static bool IsSlice(NalCodec codec, int type) => codec switch
    {
        NalCodec.H264 => type is >= 1 and <= 5,
        NalCodec.Hevc => type <= 31,
        _ => type <= 11,
    };
}

/// <summary>
/// Walks the NAL units of an Annex B byte stream (ITU-T H.264 Annex B): start codes 00 00 01 or 00 00 00 01 between units.
/// Trailing zero bytes of a unit (trailing_zero_8bits and the leading zero of a four-byte start code) are not part of the unit.
/// </summary>
public ref struct AnnexBReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    /// <summary>Creates a reader over a stream or a sample.</summary>
    /// <param name="data">Annex B bytes. Bytes before the first start code are ignored.</param>
    public AnnexBReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    /// <summary>Finds the next NAL unit.</summary>
    /// <param name="nal">The unit's bytes, header included, start code excluded.</param>
    /// <param name="offset">The offset of the unit's first byte within the data.</param>
    /// <param name="startCodeLength">3 or 4: the length of the start code that preceded the unit.</param>
    /// <returns>False when no further unit exists.</returns>
    public bool TryRead(out ReadOnlySpan<byte> nal, out int offset, out int startCodeLength)
    {
        ReadOnlySpan<byte> pattern = [0, 0, 1];
        ReadOnlySpan<byte> rest = _data[_pos..];
        int at = rest.IndexOf(pattern);
        if (at < 0)
        {
            nal = default;
            offset = 0;
            startCodeLength = 0;
            return false;
        }

        int start = _pos + at;
        // A zero byte directly before 00 00 01 makes it a four-byte start code; a NAL unit never ends in a zero byte (H.264 §7.4.1.2.3).
        startCodeLength = start > 0 && _data[start - 1] == 0 ? 4 : 3;

        int begin = start + 3;
        ReadOnlySpan<byte> after = _data[begin..];
        int next = after.IndexOf(pattern);
        int end = next < 0 ? _data.Length : begin + next;
        _pos = end;
        // Strip trailing zero bytes: they belong to the next start code or are padding.
        int trimmed = end;
        while (trimmed > begin && _data[trimmed - 1] == 0)
        {
            trimmed--;
        }

        nal = _data[begin..trimmed];
        offset = begin;
        return true;
    }
}

/// <summary>Options for turning length-prefixed samples into Annex B.</summary>
/// <param name="ParameterSets">Parameter-set NAL units to put before the first random access picture of a sample that carries none,
/// already in Annex B form (start codes included), for example from <see cref="NalRepackager.ParameterSetsToAnnexB"/>. Empty to insert nothing.</param>
public readonly record struct AnnexBOptions(ReadOnlyMemory<byte> ParameterSets = default);

/// <summary>
/// Converts between Annex B and length-prefixed NAL unit framing, without copying the NAL units' contents except into the output
/// buffer the caller supplies.
/// </summary>
public static class NalRepackager
{
    private static ReadOnlySpan<byte> StartCode4 => [0, 0, 0, 1];

    /// <summary>Converts a length-prefixed sample (as stored in MP4, with 1, 2 or 4 byte lengths) to Annex B.</summary>
    /// <param name="codec">The codec, which decides start-code length and where parameter sets go.</param>
    /// <param name="sample">The sample.</param>
    /// <param name="lengthSize">The size of the length fields: 1, 2, 3 or 4.</param>
    /// <param name="destination">Receives the Annex B bytes.</param>
    /// <param name="written">The bytes written when <see cref="OperationStatus.Done"/>; otherwise the size the output needs when that is known, else 0.</param>
    /// <param name="options">Parameter sets to insert.</param>
    /// <returns><see cref="OperationStatus.Done"/>, <see cref="OperationStatus.DestinationTooSmall"/> (with <paramref name="written"/> giving the required size), or
    /// <see cref="OperationStatus.InvalidData"/> when a length runs past the sample or <paramref name="lengthSize"/> is not 1 to 4.</returns>
    /// <remarks>H.264 units use a four-byte start code for the first unit of the sample and for parameter sets and a three-byte one
    /// elsewhere; H.265 and H.266 units always use four bytes. This is the layout the common tools produce, which keeps outputs comparable.</remarks>
    public static OperationStatus LengthPrefixedToAnnexB(NalCodec codec, ReadOnlySpan<byte> sample, int lengthSize, Span<byte> destination, out int written, AnnexBOptions options = default)
    {
        written = 0;
        if (lengthSize is < 1 or > 4)
        {
            return OperationStatus.InvalidData;
        }

        long needed = 0;
        int pos = 0;
        bool first = true;
        bool insertPending = !options.ParameterSets.IsEmpty;
        bool sawParameterSet = false;
        int hdr = NalUnit.HeaderSize(codec);
        while (pos < sample.Length)
        {
            if (sample.Length - pos < lengthSize)
            {
                return OperationStatus.InvalidData;
            }

            uint len = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                len = (len << 8) | sample[pos + i];
            }

            pos += lengthSize;
            if (len > (uint)(sample.Length - pos))
            {
                return OperationStatus.InvalidData;
            }

            ReadOnlySpan<byte> nal = sample.Slice(pos, (int)len);
            pos += (int)len;
            if (nal.Length < hdr)
            {
                // A unit without a header carries nothing; keep it as an empty-bodied unit rather than guessing.
                continue;
            }

            int type = NalUnit.GetType(codec, nal);
            bool isParameterSet = NalUnit.IsParameterSet(codec, type);
            sawParameterSet |= isParameterSet;
            if (insertPending && !sawParameterSet && NalUnit.IsRandomAccess(codec, type))
            {
                ReadOnlySpan<byte> sets = options.ParameterSets.Span;
                needed += sets.Length;
                if (needed <= destination.Length)
                {
                    sets.CopyTo(destination[(int)(needed - sets.Length)..]);
                }

                insertPending = false;
                first = false;
            }

            int startCode = codec != NalCodec.H264 || first || isParameterSet ? 4 : 3;
            first = false;
            long at = needed;
            needed += startCode + nal.Length;
            if (needed <= destination.Length)
            {
                if (startCode == 4)
                {
                    StartCode4.CopyTo(destination[(int)at..]);
                }
                else
                {
                    destination[(int)at] = 0;
                    destination[(int)at + 1] = 0;
                    destination[(int)at + 2] = 1;
                }

                nal.CopyTo(destination[(int)(at + startCode)..]);
            }
        }

        if (needed > destination.Length)
        {
            written = needed > int.MaxValue ? 0 : (int)needed;
            return OperationStatus.DestinationTooSmall;
        }

        written = (int)needed;
        return OperationStatus.Done;
    }

    /// <summary>Converts an Annex B access unit to length-prefixed form.</summary>
    /// <param name="annexB">Annex B bytes.</param>
    /// <param name="lengthSize">The size of the length fields to write: 1 to 4.</param>
    /// <param name="destination">Receives the length-prefixed bytes.</param>
    /// <param name="written">The bytes written, or the size needed when the destination is too small.</param>
    /// <param name="dropParameterSets">True to leave out parameter sets, as for samples whose parameter sets live in the sample entry.</param>
    /// <param name="codec">Needed only with <paramref name="dropParameterSets"/>.</param>
    /// <returns><see cref="OperationStatus.Done"/>, <see cref="OperationStatus.DestinationTooSmall"/> or <see cref="OperationStatus.InvalidData"/>
    /// when a unit does not fit its length field.</returns>
    public static OperationStatus AnnexBToLengthPrefixed(ReadOnlySpan<byte> annexB, int lengthSize, Span<byte> destination, out int written, bool dropParameterSets = false, NalCodec codec = NalCodec.H264)
    {
        written = 0;
        if (lengthSize is < 1 or > 4)
        {
            return OperationStatus.InvalidData;
        }

        long needed = 0;
        AnnexBReader reader = new(annexB);
        long max = lengthSize == 4 ? uint.MaxValue : (1L << (8 * lengthSize)) - 1;
        while (reader.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
        {
            if (nal.Length == 0)
            {
                continue;
            }

            if (dropParameterSets && NalUnit.IsParameterSet(codec, NalUnit.GetType(codec, nal)))
            {
                continue;
            }

            if (nal.Length > max)
            {
                return OperationStatus.InvalidData;
            }

            long at = needed;
            needed += lengthSize + nal.Length;
            if (needed <= destination.Length)
            {
                for (int i = 0; i < lengthSize; i++)
                {
                    destination[(int)at + i] = (byte)(nal.Length >> (8 * (lengthSize - 1 - i)));
                }

                nal.CopyTo(destination[(int)(at + lengthSize)..]);
            }
        }

        if (needed > destination.Length)
        {
            written = needed > int.MaxValue ? 0 : (int)needed;
            return OperationStatus.DestinationTooSmall;
        }

        written = (int)needed;
        return OperationStatus.Done;
    }

    /// <summary>Writes parameter-set NAL units (without start codes) as Annex B, each preceded by a four-byte start code.</summary>
    /// <param name="units">The NAL units.</param>
    /// <returns>The Annex B bytes, ready for <see cref="AnnexBOptions"/>.</returns>
    public static byte[] ParameterSetsToAnnexB(IReadOnlyList<ReadOnlyMemory<byte>> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        int total = 0;
        foreach (ReadOnlyMemory<byte> u in units)
        {
            total += 4 + u.Length;
        }

        byte[] result = new byte[total];
        int at = 0;
        foreach (ReadOnlyMemory<byte> u in units)
        {
            StartCode4.CopyTo(result.AsSpan(at));
            u.Span.CopyTo(result.AsSpan(at + 4));
            at += 4 + u.Length;
        }

        return result;
    }
}
