using System.Buffers;
using RecursiveAI.OneMedia.Codecs.Mpeg2Video;

namespace RecursiveAI.OneMedia.Codecs.Captions;

/// <summary>The caption triplets of one video access unit, with its presentation time.</summary>
/// <param name="Pts">Presentation time of the picture the data rides on; <see cref="MediaTime.Unknown"/> when the container gave none.</param>
/// <param name="Duration">Picture duration if known.</param>
/// <param name="Triplets">The valid and invalid triplets in transmission order.</param>
public sealed record CaptionFrame(MediaTime Pts, MediaTime Duration, IReadOnlyList<CcTriplet> Triplets);

/// <summary>
/// Finds ATSC caption data in video samples without decoding any picture: SEI NAL units of H.264 and HEVC, user data sections of MPEG-2 video.
/// </summary>
public sealed class CaptionExtractor
{
    private readonly CaptionCarriage _carriage;
    private readonly int _lengthSize;

    /// <summary>Creates an extractor for one video track.</summary>
    /// <param name="carriage">The video codec's carriage.</param>
    /// <param name="nalLengthSize">0 when samples are Annex B byte streams, otherwise the NAL length-prefix size (1–4) from <c>avcC</c>/<c>hvcC</c>. Ignored for MPEG-2.</param>
    /// <exception cref="ArgumentOutOfRangeException">The length size is outside 0–4.</exception>
    public CaptionExtractor(CaptionCarriage carriage, int nalLengthSize = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nalLengthSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nalLengthSize, 4);
        _carriage = carriage;
        _lengthSize = nalLengthSize;
    }

    /// <summary>Chooses the carriage and length size for a video track, or reports that the codec carries no ATSC captions.</summary>
    /// <param name="track">A video track.</param>
    /// <returns>The extractor, or null for codecs without a supported caption carriage.</returns>
    public static CaptionExtractor? ForTrack(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ReadOnlySpan<byte> cfg = track.Configuration.Data.Span;
        if (track.Codec == CodecId.H264)
        {
            // ISO/IEC 14496-15: avcC byte 4 low two bits = lengthSizeMinusOne; no configuration means an Annex B stream.
            return new CaptionExtractor(CaptionCarriage.H264Sei, cfg.Length >= 5 && cfg[0] == 1 ? (cfg[4] & 3) + 1 : 0);
        }

        if (track.Codec == CodecId.Hevc)
        {
            // hvcC byte 21 low two bits = lengthSizeMinusOne.
            return new CaptionExtractor(CaptionCarriage.HevcSei, cfg.Length >= 23 && cfg[0] == 1 ? (cfg[21] & 3) + 1 : 0);
        }

        return track.Codec == CodecId.Mpeg2Video ? new CaptionExtractor(CaptionCarriage.Mpeg2UserData) : null;
    }

    /// <summary>True when the last <see cref="Extract"/> call found DVD GOP user data: its triplets cover a whole group of pictures, two per frame.</summary>
    public bool LastWasGop { get; private set; }

    /// <summary>
    /// True when the last <see cref="Extract"/> call found the display-order layout: the pairs are stored in display order while the pictures
    /// are in decode order, so the n-th pair belongs to the n-th picture shown (see <see cref="ReadFrames"/>).
    /// </summary>
    public bool LastWasDisplayOrder { get; private set; }

    /// <summary>Appends the caption triplets in one sample to <paramref name="into"/>.</summary>
    /// <param name="sample">The sample bytes.</param>
    /// <param name="into">Receives triplets in the order found.</param>
    /// <returns>The number of triplets added. Malformed framing ends the scan of the sample quietly.</returns>
    public int Extract(ReadOnlySpan<byte> sample, List<CcTriplet> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        int before = into.Count;
        if (_carriage == CaptionCarriage.Mpeg2UserData)
        {
            LastWasGop = false;
            LastWasDisplayOrder = false;
            var reader = new Mpeg2StartCodeReader(sample);
            while (reader.TryRead(out int code, out ReadOnlySpan<byte> body, out _))
            {
                if (code != Mpeg2StartCode.UserData)
                {
                    continue;
                }

                if (CaptionData.TryGetFromMpeg2UserData(body, out ReadOnlySpan<byte> cc))
                {
                    AddTriplets(cc, into);
                }
                else
                {
                    switch (CaptionData.TryGetFromLegacyMpeg2UserData(body, into))
                    {
                        case CaptionData.LegacyLayout.DvdGop:
                            LastWasGop = true;
                            break;
                        case CaptionData.LegacyLayout.DisplayOrderPair:
                            LastWasDisplayOrder = true;
                            break;
                    }
                }
            }

            return into.Count - before;
        }

        NalCodec codec = _carriage == CaptionCarriage.H264Sei ? NalCodec.H264 : NalCodec.Hevc;
        if (_lengthSize == 0)
        {
            var reader = new AnnexBReader(sample);
            while (reader.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
            {
                ScanNal(codec, nal, into);
            }
        }
        else
        {
            int pos = 0;
            while (pos + _lengthSize <= sample.Length)
            {
                long len = 0;
                for (int i = 0; i < _lengthSize; i++)
                {
                    len = (len << 8) | sample[pos + i];
                }

                pos += _lengthSize;
                if (len > sample.Length - pos)
                {
                    break;
                }

                ScanNal(codec, sample.Slice(pos, (int)len), into);
                pos += (int)len;
            }
        }

        return into.Count - before;
    }

    private static void AddTriplets(ReadOnlySpan<byte> ccData, List<CcTriplet> into)
    {
        Span<CcTriplet> tmp = stackalloc CcTriplet[31]; // cc_count is 5 bits: at most 31 triplets
        int n = CaptionData.ParseCcData(ccData, tmp);
        for (int i = 0; i < n; i++)
        {
            into.Add(tmp[i]);
        }
    }

    private static void ScanNal(NalCodec codec, ReadOnlySpan<byte> nal, List<CcTriplet> into)
    {
        if (nal.Length < 3)
        {
            return;
        }

        int type = NalUnit.GetType(codec, nal);
        // H.264 §7.4.1: nal_unit_type 6 = SEI. H.265 Table 7-1: 39 = prefix SEI (captions ride there; suffix SEI 40 is accepted too).
        bool sei = codec == NalCodec.H264 ? type == 6 : type is 39 or 40;
        if (!sei)
        {
            return;
        }

        int header = NalUnit.HeaderSize(codec);
        byte[] rented = ArrayPool<byte>.Shared.Rent(nal.Length);
        try
        {
            int n = EmulationPrevention.Remove(nal[header..], rented);
            var sei2 = new SeiMessageReader(rented.AsSpan(0, n));
            while (sei2.TryRead(out int payloadType, out ReadOnlySpan<byte> payload))
            {
                // payloadType 4 = user_data_registered_itu_t_t35.
                if (payloadType == 4 && CaptionData.TryGetFromItuT35(payload, out ReadOnlySpan<byte> cc))
                {
                    AddTriplets(cc, into);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>True for a QuickTime/ISO closed-caption track with CEA-608 samples (sample entry <c>c608</c>).</summary>
    /// <param name="track">Any track.</param>
    /// <returns>True when <see cref="ReadFrames"/> reads the track's own samples rather than video user data or SEI.</returns>
    public static bool IsClosedCaptionTrack(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return string.Equals(track.CodecTag, "c608", StringComparison.Ordinal);
    }

    // Apple QuickTime File Format, closed captioning media: a sample is a sequence of atoms (size, type, payload); 'cdat' holds CEA-608
    // byte pairs of field 1 / data channel 1, one pair per video frame, and the sample may last longer than its pairs (the rest is null
    // pairs). 'cdt2' holds field 2 pairs (seen in files written by other tools; the Apple page names only 'cdat'). Unknown atoms are skipped.
    private static List<CaptionFrame> ReadC608Frames(MediaFile file, MediaTrack track, CancellationToken cancellationToken)
    {
        var frames = new List<CaptionFrame>();
        var one = new MediaTime(1001, 30000); // CEA-608 pairs arrive once per NTSC frame
        foreach (OwnedPacket p in file.Packets(track))
        {
            using (p)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaTime pts = p.Info.Pts.IsKnown ? p.Info.Pts : p.Info.Dts;
                if (!pts.IsKnown)
                {
                    continue;
                }

                ReadOnlySpan<byte> d = p.Data.Span;
                int pos = 0;
                var perFrame = new List<List<CcTriplet>>();
                while (pos + 8 <= d.Length)
                {
                    long size = (uint)((d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3]);
                    if (size < 8 || size > d.Length - pos)
                    {
                        break;
                    }

                    ReadOnlySpan<byte> type = d.Slice(pos + 4, 4);
                    byte cc = type.SequenceEqual("cdat"u8) ? (byte)0 : type.SequenceEqual("cdt2"u8) ? (byte)1 : (byte)255;
                    if (cc != 255)
                    {
                        ReadOnlySpan<byte> pairs = d.Slice(pos + 8, (int)size - 8);
                        for (int i = 0; i + 1 < pairs.Length; i += 2)
                        {
                            int frame = i / 2;
                            while (perFrame.Count <= frame)
                            {
                                perFrame.Add([]);
                            }

                            perFrame[frame].Add(new CcTriplet(true, cc, pairs[i], pairs[i + 1]));
                        }
                    }

                    pos += (int)size;
                }

                for (int f = 0; f < perFrame.Count; f++)
                {
                    MediaTime t = new MediaTime(pts.Ticks, pts.Timescale) + new MediaTime(one.Ticks * f, one.Timescale).Rescale(pts.Timescale, MediaRounding.Floor);
                    frames.Add(new CaptionFrame(t, one.Rescale(pts.Timescale, MediaRounding.Floor), perFrame[f].ToArray()));
                }
            }
        }

        return frames;
    }

    /// <summary>
    /// Reads every caption-bearing sample of a video track and returns them in presentation order (a stable sort by presentation time, so
    /// triplets of one picture keep their order and pictures that share a time keep decode order).
    /// </summary>
    /// <param name="file">An open file with a synchronous source.</param>
    /// <param name="track">The video track.</param>
    /// <param name="cancellationToken">Checked per packet.</param>
    /// <returns>One entry per sample that carried triplets; empty when the codec has no supported carriage or no captions are present.</returns>
    /// <exception cref="MediaReadStateException">As for <see cref="MediaFile.Packets"/>.</exception>
    public static IReadOnlyList<CaptionFrame> ReadFrames(MediaFile file, MediaTrack track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(track);
        CaptionExtractor? ex = ForTrack(track);
        var frames = new List<CaptionFrame>();
        if (ex is null)
        {
            return IsClosedCaptionTrack(track) ? ReadC608Frames(file, track, cancellationToken) : frames;
        }

        var tmp = new List<CcTriplet>();
        bool displayOrder = false;
        foreach (OwnedPacket p in file.Packets(track))
        {
            using (p)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tmp.Clear();
                if (ex.Extract(p.Data.Span, tmp) > 0)
                {
                    MediaTime pts = p.Info.Pts.IsKnown ? p.Info.Pts : p.Info.Dts;
                    displayOrder |= ex.LastWasDisplayOrder;
                    if (ex.LastWasGop && pts.IsKnown && p.Info.Duration.IsKnown && p.Info.Duration.Ticks > 0)
                    {
                        // GOP data: two triplets per frame, the frames following the picture that carries it, one duration apart.
                        for (int k = 0, f = 0; k < tmp.Count; k += 2, f++)
                        {
                            CcTriplet[] pair = tmp.Count - k >= 2 ? [tmp[k], tmp[k + 1]] : [tmp[k]];
                            frames.Add(new CaptionFrame(new MediaTime(pts.Ticks + (p.Info.Duration.Rescale(pts.Timescale, MediaRounding.Floor).Ticks * f), pts.Timescale), p.Info.Duration, pair));
                        }
                    }
                    else
                    {
                        frames.Add(new CaptionFrame(pts, p.Info.Duration, tmp.ToArray()));
                    }
                }
            }
        }

        if (displayOrder)
        {
            // The pairs already run in display order: hand the k-th pair the k-th smallest presentation time and keep the stream order.
            var times = new List<MediaTime>(frames.Count);
            foreach (CaptionFrame f in frames)
            {
                times.Add(f.Pts);
            }

            times.Sort();
            for (int i = 0; i < frames.Count; i++)
            {
                frames[i] = frames[i] with { Pts = times[i] };
            }

            return frames;
        }

        // Presentation order: List.Sort is unstable, so order by (pts, decode index) explicitly.
        var order = new int[frames.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int c = frames[a].Pts.CompareTo(frames[b].Pts);
            return c != 0 ? c : a.CompareTo(b);
        });
        var sorted = new List<CaptionFrame>(frames.Count);
        foreach (int i in order)
        {
            sorted.Add(frames[i]);
        }

        return sorted;
    }
}
