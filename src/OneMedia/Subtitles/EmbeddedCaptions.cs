using RecursiveAI.OneMedia.Codecs.Captions;
using RecursiveAI.OneMedia.Subtitles.Cea608;
using RecursiveAI.OneMedia.Subtitles.Cea708;

namespace RecursiveAI.OneMedia.Subtitles;

/// <summary>Everything the embedded caption data of one video track decodes to.</summary>
/// <param name="Cea608">Caption and text screens of CC1–CC4 and Text1–Text4.</param>
/// <param name="Xds">Extended data services packets.</param>
/// <param name="Cea708">Caption screens of the DTVCC services.</param>
public sealed record EmbeddedCaptions(IReadOnlyList<Cea608Caption> Cea608, IReadOnlyList<XdsPacket> Xds, IReadOnlyList<Cea708Caption> Cea708)
{
    /// <summary>True when no caption screen of either standard was found.</summary>
    public bool IsEmpty => Cea608.Count == 0 && Cea708.Count == 0;

    /// <summary>Reads the caption data of a video track and decodes it.</summary>
    /// <param name="file">An open file with a synchronous source.</param>
    /// <param name="track">A video track (H.264, HEVC or MPEG-2).</param>
    /// <param name="cancellationToken">Checked per packet.</param>
    /// <returns>The decoded captions; empty for codecs with no supported carriage.</returns>
    public static EmbeddedCaptions Read(MediaFile file, MediaTrack track, CancellationToken cancellationToken = default) =>
        Decode(CaptionExtractor.ReadFrames(file, track, cancellationToken));

    /// <summary>Decodes caption frames in presentation order.</summary>
    /// <param name="frames">Frames from <see cref="CaptionExtractor.ReadFrames"/>.</param>
    /// <param name="end">The time at which screens still open at the end are closed; null uses the last frame time plus its duration.</param>
    /// <returns>The decoded captions.</returns>
    public static EmbeddedCaptions Decode(IReadOnlyList<CaptionFrame> frames, MediaTime? end = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var d608 = new Cea608Decoder();
        var d708 = new Cea708Decoder();
        MediaTime last = MediaTime.Zero;
        for (int i = 0; i < frames.Count; i++)
        {
            CaptionFrame f = frames[i];
            if (!f.Pts.IsKnown)
            {
                continue;
            }

            MediaTime dur = f.Duration.IsKnown && f.Duration.Ticks > 0 ? f.Duration : NextDelta(frames, i);
            int n0 = 0, n1 = 0, n3 = 0;
            foreach (CcTriplet t in f.Triplets)
            {
                if (t.Valid)
                {
                    if (t.Type == 0)
                    {
                        n0++;
                    }
                    else if (t.Type == 1)
                    {
                        n1++;
                    }
                    else
                    {
                        n3++;
                    }
                }
            }

            int j0 = 0, j1 = 0, j3 = 0;
            foreach (CcTriplet t in f.Triplets)
            {
                if (!t.Valid)
                {
                    continue;
                }

                // A frame's pairs are spread evenly over the frame's duration.
                int j = t.Type == 0 ? j0++ : t.Type == 1 ? j1++ : j3++;
                int n = t.Type == 0 ? n0 : t.Type == 1 ? n1 : n3;
                MediaTime time = At(f.Pts, dur, j, n);
                if (t.Type < 2)
                {
                    d608.Push(time, t.Type, t.Data1, t.Data2);
                }
                else
                {
                    d708.Push(time, t.Type, t.Data1, t.Data2);
                }
            }

            last = f.Pts + dur;
        }

        MediaTime close = end ?? last;
        d608.Finish(close);
        d708.Finish(close);
        return new EmbeddedCaptions(d608.Captions, d608.Xds, d708.Captions);
    }

    private static MediaTime At(MediaTime pts, MediaTime dur, int j, int n)
    {
        if (n <= 1 || !dur.IsKnown)
        {
            return pts;
        }

        // Exact integer arithmetic on the picture's own ticks; no floating point.
        return new MediaTime(pts.Ticks + (dur.Rescale(pts.Timescale, MediaRounding.Floor).Ticks * j / n), pts.Timescale);
    }

    private static MediaTime NextDelta(IReadOnlyList<CaptionFrame> frames, int i)
    {
        for (int k = i + 1; k < frames.Count; k++)
        {
            if (frames[k].Pts.IsKnown && frames[k].Pts > frames[i].Pts)
            {
                return frames[k].Pts - frames[i].Pts;
            }
        }

        return new MediaTime(1001, 30000); // one NTSC frame when nothing else is known
    }
}
