using System.Buffers;

namespace RecursiveAI.OneMedia.Codecs.Ac3;

/// <summary>A Dolby Digital (AC-3) or Dolby Digital Plus (E-AC-3) syncframe header (ETSI TS 102 366 §4.3 and Annex E).</summary>
public readonly record struct Ac3SyncFrame
{
    private static ReadOnlySpan<int> Rates => [48000, 44100, 32000];
    private static ReadOnlySpan<byte> AcmodChannels => [2, 1, 2, 3, 3, 4, 4, 5];
    private static ReadOnlySpan<ushort> BitRates => [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    /// <summary>True for E-AC-3 (bsid 11 to 16), false for AC-3 (bsid 8 or less).</summary>
    public bool IsEac3 { get; init; }

    /// <summary>fscod: 0 48 kHz, 1 44.1 kHz, 2 32 kHz (3 selects the half rates of E-AC-3).</summary>
    public int Fscod { get; init; }

    /// <summary>bsid.</summary>
    public int Bsid { get; init; }

    /// <summary>bsmod (AC-3 only).</summary>
    public int Bsmod { get; init; }

    /// <summary>acmod: the audio coding mode (Table 4.3).</summary>
    public int Acmod { get; init; }

    /// <summary>lfeon.</summary>
    public bool LfeOn { get; init; }

    /// <summary>frmsizecod (AC-3 only).</summary>
    public int FrmSizeCod { get; init; }

    /// <summary>strmtyp (E-AC-3 only): 0 independent, 1 dependent, 2 AC-3 converted.</summary>
    public int StreamType { get; init; }

    /// <summary>substreamid (E-AC-3 only).</summary>
    public int SubstreamId { get; init; }

    /// <summary>Number of audio blocks of 256 samples in the frame: 6 for AC-3, 1, 2, 3 or 6 for E-AC-3.</summary>
    public int Blocks { get; init; }

    /// <summary>The frame size in bytes.</summary>
    public int FrameSize { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Channel count including the LFE channel.</summary>
    public int Channels => AcmodChannels[Acmod] + (LfeOn ? 1 : 0);

    /// <summary>Samples per channel in the frame.</summary>
    public int SamplesPerFrame => Blocks * 256;

    /// <summary>The bit rate in bits per second: from frmsizecod for AC-3, from the frame size for E-AC-3.</summary>
    public int BitRate => IsEac3 ? (int)((long)FrameSize * 8 * SampleRate / SamplesPerFrame) : BitRates[FrmSizeCod >> 1] * 1000;

    /// <summary>Parses a syncframe header.</summary>
    /// <param name="data">Bytes starting at the 0x0B77 syncword.</param>
    /// <param name="frame">The result when true.</param>
    /// <returns>False when the syncword is missing, a reserved code is used or the data is short. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out Ac3SyncFrame frame)
    {
        frame = default;
        if (data.Length < 8 || data[0] != 0x0b || data[1] != 0x77)
        {
            return false;
        }

        int bsid = data[5] >> 3;
        if (bsid <= 10)
        {
            // ETSI TS 102 366 §4.3.1: syncword, crc1, fscod(2) frmsizecod(6), bsid(5) bsmod(3), acmod(3) then mixing level fields.
            int fscod = data[4] >> 6;
            int frmsizecod = data[4] & 0x3f;
            if (fscod == 3 || frmsizecod > 37 || bsid > 8)
            {
                return false;
            }

            int bsmod = data[5] & 7;
            BitReader r = new(data[6..]);
            int acmod = (int)r.ReadBits(3);
            if ((acmod & 1) != 0 && acmod != 1)
            {
                r.SkipBits(2); // cmixlev
            }

            if ((acmod & 4) != 0)
            {
                r.SkipBits(2); // surmixlev
            }

            if (acmod == 2)
            {
                r.SkipBits(2); // dsurmod
            }

            bool lfe = r.ReadFlag();
            if (r.Overrun)
            {
                return false;
            }

            int words = BitRates[frmsizecod >> 1] * fscod switch { 0 => 2, 2 => 3, _ => 0 };
            if (fscod == 1)
            {
                // 44.1 kHz frames are 1536 samples at a non-integer word count, so odd codes carry one padding word (Table 4.13).
                words = (int)((long)BitRates[frmsizecod >> 1] * 1000 * 1536 / (44100 * 16)) + (frmsizecod & 1);
            }

            frame = new Ac3SyncFrame
            {
                IsEac3 = false,
                Fscod = fscod,
                Bsid = bsid,
                Bsmod = bsmod,
                Acmod = acmod,
                LfeOn = lfe,
                FrmSizeCod = frmsizecod,
                Blocks = 6,
                FrameSize = words * 2,
                SampleRate = Rates[fscod],
            };
            return true;
        }

        if (bsid is < 11 or > 16)
        {
            return false;
        }

        // Annex E.1.2: strmtyp(2) substreamid(3) frmsiz(11) fscod(2) numblkscod(2)|fscod2(2) acmod(3) lfeon(1) bsid(5)
        BitReader e = new(data[2..]);
        int strmtyp = (int)e.ReadBits(2);
        int substream = (int)e.ReadBits(3);
        int frmsiz = (int)e.ReadBits(11);
        int fs = (int)e.ReadBits(2);
        int numblk;
        int rate;
        if (fs == 3)
        {
            int fs2 = (int)e.ReadBits(2);
            if (fs2 == 3)
            {
                return false;
            }

            numblk = 3;
            rate = Rates[fs2] / 2;
        }
        else
        {
            numblk = (int)e.ReadBits(2);
            rate = Rates[fs];
        }

        int am = (int)e.ReadBits(3);
        bool lfeon = e.ReadFlag();
        e.ReadBits(5);
        if (e.Overrun || strmtyp == 3)
        {
            return false;
        }

        frame = new Ac3SyncFrame
        {
            IsEac3 = true,
            Fscod = fs,
            Bsid = bsid,
            Acmod = am,
            LfeOn = lfeon,
            StreamType = strmtyp,
            SubstreamId = substream,
            Blocks = numblk switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 },
            FrameSize = (frmsiz + 1) * 2,
            SampleRate = rate,
        };
        return true;
    }

    /// <summary>Describes the stream.</summary>
    /// <returns>The summary.</returns>
    public CodecParameters Describe() => new()
    {
        Codec = IsEac3 ? CodecId.Eac3 : CodecId.Ac3,
        Rfc6381 = IsEac3 ? "ec-3" : "ac-3",
        SampleRate = SampleRate,
        Channels = Channels,
        SamplesPerFrame = SamplesPerFrame,
        BitRate = BitRate,
    };

    /// <summary>Gets the <c>dac3</c> box content for an AC-3 frame (ETSI TS 102 366 §F.4).</summary>
    /// <param name="writer">Receives three bytes.</param>
    /// <returns>False for an E-AC-3 frame.</returns>
    public bool TryWriteDac3(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (IsEac3)
        {
            return false;
        }

        // fscod(2) bsid(5) bsmod(3) acmod(3) lfeon(1) bit_rate_code(5) reserved(5)
        int bits = (Fscod << 22) | (Bsid << 17) | (Bsmod << 14) | (Acmod << 11) | ((LfeOn ? 1 : 0) << 10) | ((FrmSizeCod >> 1) << 5);
        Span<byte> w = writer.GetSpan(3);
        w[0] = (byte)(bits >> 16);
        w[1] = (byte)(bits >> 8);
        w[2] = (byte)bits;
        writer.Advance(3);
        return true;
    }

    /// <summary>Gets the <c>dec3</c> box content for an E-AC-3 stream whose first access unit has a single independent substream.</summary>
    /// <param name="dataRate">data_rate: the peak bit rate in kbit/s (13 bits).</param>
    /// <param name="writer">Receives five bytes.</param>
    /// <returns>False for an AC-3 frame.</returns>
    public bool TryWriteDec3(int dataRate, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!IsEac3)
        {
            return false;
        }

        // data_rate(13) num_ind_sub(3) | fscod(2) bsid(5) reserved(1) asvc(1) bsmod(3) acmod(3) lfeon(1) reserved(3) num_dep_sub(4) reserved(1)
        Span<byte> w = writer.GetSpan(5);
        int first = ((dataRate & 0x1fff) << 3) | 0; // num_ind_sub_minus1 = 0
        w[0] = (byte)(first >> 8);
        w[1] = (byte)first;
        int second = (Fscod << 22) | (Bsid << 17) | (0 << 16) | (0 << 15) | (0 << 12) | (Acmod << 9) | ((LfeOn ? 1 : 0) << 8);
        w[2] = (byte)(second >> 16);
        w[3] = (byte)(second >> 8);
        w[4] = (byte)second;
        writer.Advance(5);
        return true;
    }
}

/// <summary>Parsers for the <c>dac3</c> and <c>dec3</c> boxes (ETSI TS 102 366 Annex F).</summary>
public static class Ac3Config
{
    private static ReadOnlySpan<int> Rates => [48000, 44100, 32000];
    private static ReadOnlySpan<byte> AcmodChannels => [2, 1, 2, 3, 3, 4, 4, 5];
    private static ReadOnlySpan<ushort> BitRates => [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    /// <summary>Describes an AC-3 stream from a <c>dac3</c> box content.</summary>
    /// <param name="dac3">The three-byte content.</param>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>False when shorter than three bytes or the sample rate code is reserved.</returns>
    public static bool TryDescribeDac3(ReadOnlySpan<byte> dac3, out CodecParameters? parameters)
    {
        parameters = null;
        if (dac3.Length < 3)
        {
            return false;
        }

        int bits = (dac3[0] << 16) | (dac3[1] << 8) | dac3[2];
        int fscod = (bits >> 22) & 3;
        int acmod = (bits >> 11) & 7;
        bool lfe = ((bits >> 10) & 1) != 0;
        int rateCode = (bits >> 5) & 0x1f;
        if (fscod == 3)
        {
            return false;
        }

        parameters = new CodecParameters
        {
            Codec = CodecId.Ac3,
            Rfc6381 = "ac-3",
            SampleRate = Rates[fscod],
            Channels = AcmodChannels[acmod] + (lfe ? 1 : 0),
            SamplesPerFrame = 1536,
            BitRate = rateCode < BitRates.Length ? BitRates[rateCode] * 1000 : 0,
        };
        return true;
    }

    /// <summary>Describes an E-AC-3 stream from a <c>dec3</c> box content, summing the channels of the first independent substream.</summary>
    /// <param name="dec3">The box content.</param>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>False when truncated or the sample rate code is reserved.</returns>
    public static bool TryDescribeDec3(ReadOnlySpan<byte> dec3, out CodecParameters? parameters)
    {
        parameters = null;
        if (dec3.Length < 5)
        {
            return false;
        }

        int dataRate = (dec3[0] << 5) | (dec3[1] >> 3);
        int fscod = dec3[2] >> 6;
        int acmod = (dec3[3] >> 1) & 7;
        bool lfe = (dec3[3] & 1) != 0;
        if (fscod == 3)
        {
            return false;
        }

        parameters = new CodecParameters
        {
            Codec = CodecId.Eac3,
            Rfc6381 = "ec-3",
            SampleRate = Rates[fscod],
            Channels = AcmodChannels[acmod] + (lfe ? 1 : 0),
            SamplesPerFrame = 1536,
            BitRate = dataRate * 1000,
        };
        return true;
    }
}
