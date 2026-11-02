using RecursiveAI.OneMedia.MpegTs;

namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>The subtitle pages found in one teletext track.</summary>
/// <param name="TrackIndex">The track's index in the demuxer.</param>
/// <param name="Pid">The transport stream PID, or -1 when the container has none.</param>
/// <param name="Services">The pages the PMT teletext descriptor announces; empty outside transport streams.</param>
/// <param name="Subtitles">One entry per subtitle page found, in order of first appearance.</param>
public sealed record TeletextTrackSubtitles(int TrackIndex, int Pid, IReadOnlyList<TeletextService> Services, IReadOnlyList<TeletextSubtitleTrack> Subtitles);

/// <summary>
/// Reads the teletext subtitle pages of the teletext tracks of a demuxer: transport streams (including M2TS) and MPEG program streams
/// (where a recorder put the PES packets of a teletext stream into private stream 1). Packets of other tracks are skipped without
/// reading their payloads. The demuxer is read to its end.
/// </summary>
public static class TeletextExtractor
{
    /// <summary>The codec name the demuxers give teletext tracks.</summary>
    public const string CodecName = "dvb-teletext";

    /// <summary>True when <paramref name="track"/> is a teletext track.</summary>
    /// <param name="track">A track.</param>
    public static bool IsTeletext(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Codec.Name == CodecName;
    }

    /// <summary>
    /// Reads every teletext track of <paramref name="demuxer"/>.
    /// </summary>
    /// <param name="demuxer">A demuxer positioned at the start. It is read to the end.</param>
    /// <param name="options">Page selection; default decodes subtitle pages (header bit C6, and pages the PMT announces as subtitles).</param>
    /// <param name="defects">Where decoding problems are reported, or null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One entry per teletext track that has subtitle pages (tracks without any are omitted).</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static IReadOnlyList<TeletextTrackSubtitles> ReadAll(IDemuxer demuxer, TeletextOptions? options = null, DefectLog? defects = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        Dictionary<int, TeletextSubtitleReader> readers = [];
        Dictionary<int, (int Pid, IReadOnlyList<TeletextService> Services)> info = [];
        if (demuxer is MpegTsDemuxer ts)
        {
            foreach (TransportStreamProgram program in ts.Programs)
            {
                for (int i = 0; i < program.Streams.Count; i++)
                {
                    int track = program.TrackIndexes[i];
                    if (track >= 0 && track < demuxer.Tracks.Count && IsTeletext(demuxer.Tracks[track]))
                    {
                        info[track] = (program.Streams[i].Pid, TeletextService.Parse(program.Streams[i].Descriptors.Span));
                    }
                }
            }
        }

        foreach (MediaTrack track in demuxer.Tracks)
        {
            if (IsTeletext(track))
            {
                (int pid, IReadOnlyList<TeletextService> services) = info.TryGetValue(track.Index, out var found) ? found : (-1, []);
                info[track.Index] = (pid, services);
                readers[track.Index] = new TeletextSubtitleReader(options, services, defects);
            }
            else
            {
                demuxer.SetTrackEnabled(track.Index, false);
            }
        }

        if (readers.Count == 0)
        {
            return [];
        }

        while (demuxer.TryReadPacket(out PacketView packet))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readers.TryGetValue(packet.Info.TrackIndex, out TeletextSubtitleReader? reader))
            {
                MediaTime time = packet.Info.Pts.IsKnown ? packet.Info.Pts : packet.Info.Dts;
                reader.Push(packet.Data, time, packet.Info.SourceRange.Offset);
            }
        }

        MediaTime end = demuxer is MpegTsDemuxer t2 && t2.StartTime.IsKnown && demuxer.Duration.IsKnown ? t2.StartTime + demuxer.Duration : default;
        List<TeletextTrackSubtitles> result = [];
        foreach ((int index, TeletextSubtitleReader reader) in readers)
        {
            IReadOnlyList<TeletextSubtitleTrack> subs = reader.Finish(end);
            if (subs.Count > 0)
            {
                result.Add(new TeletextTrackSubtitles(index, info[index].Pid, info[index].Services, subs));
            }
        }

        result.Sort(static (a, b) => a.TrackIndex.CompareTo(b.TrackIndex));
        return result;
    }
}
