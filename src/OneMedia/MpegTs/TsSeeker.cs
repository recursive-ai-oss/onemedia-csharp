namespace RecursiveAI.OneMedia.MpegTs;

/// <summary>
/// Keyframe seeking in a transport stream, which has no index. Bisects the file on the presentation time of one stream until
/// the window is small, then reads forward from the window start (stepping back when it holds no keyframe) and remembers the
/// last keyframe at or before the target (or the first one after it). Reading then continues from that keyframe's first packet.
/// </summary>
internal sealed class TsSeeker(MpegTsDemuxer demuxer)
{
    private const long Window = 2L << 20;
    private const long StepBack = 4L << 20;

    public MediaTime Seek(MediaTime target, SeekMode mode)
    {
        IReadOnlyList<MpegTsDemuxer.TrackState> states = demuxer.TrackStates;
        if (states.Count == 0)
        {
            return MediaTime.Unknown;
        }

        int reference = 0;
        for (int i = 0; i < states.Count; i++)
        {
            if (states[i].Kind.Kind == TrackKind.Video && states[i].FirstPts >= 0)
            {
                reference = i;
                break;
            }
        }

        MpegTsDemuxer.TrackState rs = states[reference];
        if (rs.FirstPts < 0)
        {
            demuxer.ResetForSeek(demuxer.FirstPacketOffset);
            return MediaTime.Unknown;
        }

        long t = target.Rescale(TrackAssembler90k, MediaRounding.Floor).Ticks;
        long step = demuxer.PacketBytes;
        long first = demuxer.FirstPacketOffset;
        long lo = first, hi = demuxer.InputLength;
        demuxer.WatchTrack = reference;
        while (hi - lo > Window)
        {
            long mid = first + ((lo + ((hi - lo) / 2) - first) / step * step);
            long pts = PtsAt(mid);
            if (pts < 0 || pts > t)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        demuxer.WatchTrack = -1;
        long from = lo;
        long keyPos = -1, keyPts = -1;
        while (true)
        {
            demuxer.ResetForSeek(from);
            keyPos = -1;
            keyPts = -1;
            bool done = false;
            while (!done && demuxer.TryReadPacket(out PacketView p))
            {
                if (p.Info.TrackIndex != reference)
                {
                    continue;
                }

                long pts = p.Info.Pts.IsKnown ? p.Info.Pts.Ticks : (p.Info.Dts.IsKnown ? p.Info.Dts.Ticks : -1);
                if (pts < 0)
                {
                    continue;
                }

                if (p.Info.IsKeyframe && p.Info.SourceRange.Offset >= 0)
                {
                    if (mode == SeekMode.PreviousKeyframe)
                    {
                        if (pts <= t)
                        {
                            keyPos = p.Info.SourceRange.Offset;
                            keyPts = pts;
                        }
                        else
                        {
                            done = true;
                        }
                    }
                    else if (pts >= t)
                    {
                        keyPos = p.Info.SourceRange.Offset;
                        keyPts = pts;
                        done = true;
                    }
                }
                else if (mode == SeekMode.PreviousKeyframe && pts > t + (TrackAssembler90k * 2))
                {
                    done = true; // far enough past the target; the last keyframe seen stands
                }
            }

            if (keyPos >= 0 || from <= first)
            {
                break;
            }

            from = Math.Max(first, from - StepBack);
            from = first + ((from - first) / step * step);
        }

        if (keyPos < 0)
        {
            demuxer.ResetForSeek(first);
            return MediaTime.Unknown;
        }

        long aligned = first + ((keyPos - first) / step * step);
        demuxer.ResetForSeek(aligned);
        return new MediaTime(keyPts, TrackAssembler90k);
    }

    private const long TrackAssembler90k = 90000;

    // Reads from a position until the watched stream shows a timestamp; -1 when none shows within a few hundred packets.
    private long PtsAt(long offset)
    {
        demuxer.ResetForSeek(offset);
        demuxer.WatchedPts = -1;
        for (int i = 0; i < 4000 && demuxer.WatchedPts < 0; i++)
        {
            if (!demuxer.Pump())
            {
                break;
            }
        }

        return demuxer.WatchedPts;
    }
}
