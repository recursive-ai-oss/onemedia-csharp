using System.Linq;
using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Tags.Id3;

namespace RecursiveAI.OneMedia.TimedEvents;

/// <summary>
/// Finds and reads the timed event streams of a file: QuickTime timecode tracks, SCTE 35 and ID3 timed metadata in transport streams, and
/// <c>emsg</c> boxes in fragmented MP4.
/// </summary>
/// <remarks>
/// Listing the streams is cheap: it uses the tracks the file already described, plus one walk over the top-level box headers of an ISO file
/// (a few reads, whatever the size; <c>emsg</c> boxes sit between segments). Reading a stream's events reads its packets: one pass over a
/// transport stream, since its messages are spread through the file, or the few samples of a timecode track.
/// </remarks>
public static class MediaEvents
{
    private const string TimecodeTag = "tmcd";

    /// <summary>Lists the event streams of an open file.</summary>
    /// <param name="file">The file.</param>
    /// <param name="limits">Limits for the number of events and the size of an <c>emsg</c> box; <see cref="MediaLimits.Default"/> when null.</param>
    /// <param name="includeMessages">False to skip the walk over an ISO file's top-level boxes that finds <c>emsg</c> boxes (a cheap listing of tracks only).</param>
    /// <param name="cancellationToken">Observed while walking an ISO file's top-level boxes.</param>
    /// <returns>The streams, track-based ones in track order then <c>emsg</c> streams in order of first appearance. Empty when there are none.</returns>
    public static IReadOnlyList<EventChannel> List(MediaFile file, MediaLimits? limits = null, bool includeMessages = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        limits ??= MediaLimits.Default;
        MediaLimits lim = limits;
        List<EventChannel> streams = [];
        foreach (MediaTrack track in file.Tracks)
        {
            if (track.Kind != TrackKind.Data)
            {
                continue;
            }

            if (track.CodecTag == TimecodeTag && TimecodeSampleEntry.TryParse(track.Configuration.Data.Span, out TimecodeSampleEntry? entry) && entry is not null)
            {
                streams.Add(new EventChannel(MediaEventKind.Timecode, TimecodeTag, null, track, entry, () => ReadTimecode(file, track, entry, lim)));
            }
            else if (track.Codec.Name == "scte-35")
            {
                streams.Add(new EventChannel(MediaEventKind.Scte35, "scte-35", null, track, null, () => ReadScte35(file, track, lim)));
            }
            else if (track.Codec.Name == "timed-id3")
            {
                streams.Add(new EventChannel(MediaEventKind.TimedId3, "timed-id3", null, track, null, () => ReadId3(file, track, lim)));
            }
        }

        if (includeMessages && file.Demuxer is IsoBmffDemuxer { Source: { } source } iso)
        {
            AddEmsgStreams(streams, source, iso, file.Defects, lim, cancellationToken);
        }

        return streams;
    }

    // ---- timecode ----

    private static List<MediaEvent> ReadTimecode(MediaFile file, MediaTrack track, TimecodeSampleEntry entry, MediaLimits limits)
    {
        List<MediaEvent> events = [];
        foreach (OwnedPacket packet in file.Packets(track))
        {
            using (packet)
            {
                ReadOnlySpan<byte> d = packet.Data.Span;
                if (d.Length < 4)
                {
                    file.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, packet.Info.SourceRange.Offset, "A timecode sample is shorter than the 4-byte frame counter.", "tmcd.sample-size");
                    continue;
                }

                // QuickTime File Format, timecode media: a sample is a signed 32-bit frame number (a counter when the counter flag is set).
                long frame = BinaryPrimitives.ReadInt32BigEndian(d);
                if (frame < 0 && (entry.Flags & TimecodeAttributes.NegativeTimesAllowed) == 0)
                {
                    file.Defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, packet.Info.SourceRange.Offset, "A timecode sample is negative but the entry does not allow negative times.", "tmcd.negative");
                }

                events.Add(new TimecodeEvent
                {
                    Time = track.PresentationTime(packet.Info),
                    Duration = packet.Info.Duration,
                    Range = packet.Info.SourceRange,
                    FrameNumber = frame,
                    Start = entry.FromFrameNumber(frame),
                });
                limits.CheckElementCount(events.Count, packet.Info.SourceRange.Offset);
            }
        }

        return events;
    }

    // ---- SCTE 35 in a transport stream ----

    private static List<MediaEvent> ReadScte35(MediaFile file, MediaTrack track, MediaLimits limits)
    {
        List<MediaEvent> events = [];
        foreach (OwnedPacket packet in file.Packets(track))
        {
            using (packet)
            {
                if (Scte35Section.TryParse(packet.Data.Span, out Scte35Section? section, file.Defects, packet.Info.SourceRange.Offset) && section is not null)
                {
                    // The demuxer gives Pts = the adjusted splice time when stated, else the stream clock at arrival (also in Dts).
                    events.Add(new Scte35Event
                    {
                        Section = section,
                        Time = packet.Info.Pts,
                        Duration = SpliceDuration(section),
                        Range = packet.Info.SourceRange,
                        IsSpliceTimeSpecified = section.SplicePts is not null,
                    });
                    limits.CheckElementCount(events.Count, packet.Info.SourceRange.Offset);
                }
            }
        }

        return events;
    }

    private static MediaTime SpliceDuration(Scte35Section section) => section.Command switch
    {
        Scte35SpliceInsert { BreakDuration: { } d } => new MediaTime(d.Duration, 90000),
        _ => section.Descriptors.OfType<Scte35SegmentationDescriptor>().FirstOrDefault(s => s.Duration is not null)?.Duration is long ticks ? new MediaTime(ticks, 90000) : MediaTime.Unknown,
    };

    // ---- ID3 in a transport stream ----

    private static List<MediaEvent> ReadId3(MediaFile file, MediaTrack track, MediaLimits limits)
    {
        List<MediaEvent> events = [];
        foreach (OwnedPacket packet in file.Packets(track))
        {
            using (packet)
            {
                AddId3Tags(events, packet.Data, track.PresentationTime(packet.Info), packet.Info.Duration, packet.Info.SourceRange, null, limits, file.Defects);
            }
        }

        return events;
    }

    // A PES payload of an ID3 stream is one tag (Apple's timed metadata carriage), or several back to back. Bytes that are not a tag are reported, not guessed at.
    private static void AddId3Tags(List<MediaEvent> events, ReadOnlyMemory<byte> data, MediaTime time, MediaTime duration, ByteRange range, EventMessageBox? message, MediaLimits limits, DefectLog defects)
    {
        int position = 0;
        while (position < data.Length)
        {
            ReadOnlyMemory<byte> rest = data[position..];
            if (!Id3Header.TryParse(rest.Span, out Id3Header header) || header.TotalLength > rest.Length)
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, range.IsNone ? -1 : range.Offset, "Timed metadata bytes are not a complete ID3 tag; they are skipped.", "id3-event.not-a-tag");
                return;
            }

            Id3Tag tag = Id3Tag.Read(rest[..(int)header.TotalLength], 0, limits, defects);
            events.Add(new TimedId3Event { Tag = tag, Time = time, Duration = duration, Range = range, Message = message });
            limits.CheckElementCount(events.Count, range.IsNone ? -1 : range.Offset);
            position += (int)header.TotalLength;
        }
    }

    // ---- emsg in fragmented ISO files ----

    private sealed record EmsgFound(EventMessageBox Box, MediaTime Time, bool Absolute);

    private static void AddEmsgStreams(List<EventChannel> streams, IMediaSource source, IsoBmffDemuxer iso, DefectLog defects, MediaLimits limits, CancellationToken cancellationToken)
    {
        List<EmsgFound> found = ScanEmsg(source, iso, defects, limits, cancellationToken);
        if (found.Count == 0)
        {
            return;
        }

        // One stream per scheme and value, in order of first appearance.
        List<((string Scheme, string Value) Key, List<EmsgFound> Items)> groups = [];
        foreach (EmsgFound f in found)
        {
            (string, string) key = (f.Box.SchemeIdUri, f.Box.Value);
            int at = groups.FindIndex(g => g.Key == key);
            if (at < 0)
            {
                groups.Add((key, [f]));
            }
            else
            {
                groups[at].Items.Add(f);
            }
        }

        foreach (((string scheme, string value), List<EmsgFound> items) in groups)
        {
            MediaEventKind kind = scheme == EventMessageBox.Scte35Scheme ? MediaEventKind.Scte35
                : scheme is EventMessageBox.Id3Scheme or EventMessageBox.AppleId3Scheme ? MediaEventKind.TimedId3
                : MediaEventKind.Message;
            List<EmsgFound> captured = items;
            streams.Add(new EventChannel(kind, scheme, value, null, null, () => BuildEmsgEvents(kind, captured, limits, defects)));
        }
    }

    private static List<MediaEvent> BuildEmsgEvents(MediaEventKind kind, List<EmsgFound> items, MediaLimits limits, DefectLog defects)
    {
        List<MediaEvent> events = [];
        foreach (EmsgFound f in items)
        {
            ByteRange range = new(f.Box.Offset, 0);
            switch (kind)
            {
                case MediaEventKind.Scte35:
                    if (Scte35Section.TryParse(f.Box.MessageData.Span, out Scte35Section? section, defects, f.Box.Offset) && section is not null)
                    {
                        // For an emsg the box time is the event time; the splice time inside is only used when the box gives none.
                        MediaTime time = f.Time;
                        events.Add(new Scte35Event
                        {
                            Section = section,
                            Time = time,
                            Duration = f.Box.Duration.IsKnown && f.Box.EventDuration != 0 ? f.Box.Duration : SpliceDuration(section),
                            Range = range,
                            IsSpliceTimeSpecified = section.SplicePts is not null,
                            Message = f.Box,
                        });
                    }
                    else
                    {
                        defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, f.Box.Offset, "An emsg box of the SCTE 35 scheme does not hold a splice information section.", "emsg.scte35");
                    }

                    break;
                case MediaEventKind.TimedId3:
                    AddId3Tags(events, f.Box.MessageData, f.Time, f.Box.Duration, range, f.Box, limits, defects);
                    break;
                default:
                    events.Add(new MessageEvent { Message = f.Box, Time = f.Time, Duration = f.Box.Duration, Range = range, TimeIsAbsolute = f.Absolute });
                    break;
            }
        }

        return events;
    }

    // Walks the top-level boxes of an ISO file. A version 0 emsg is timed from the start of the segment after it, which is the base media
    // decode time of the first track fragment of the next moof (or the sidx earliest presentation time before it).
    private static List<EmsgFound> ScanEmsg(IMediaSource source, IsoBmffDemuxer iso, DefectLog defects, MediaLimits limits, CancellationToken cancellationToken)
    {
        List<EmsgFound> found = [];
        List<EventMessageBox> waiting = [];
        MediaTime sidxStart = MediaTime.Unknown;
        long length = source.Length;
        long offset = 0;
        long boxes = 0;
        byte[] window = new byte[4096];
        while (length < 0 || offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BoxReader.TryRead(source, offset, length < 0 ? long.MaxValue : length, true, null, out BoxHeader header, cancellationToken))
            {
                break;
            }

            limits.CheckElementCount(++boxes, offset);
            if (header.Type == BoxTypes.Emsg)
            {
                long size = Math.Min(header.PayloadSize, limits.MaxTagBytes);
                byte[] payload = size <= window.Length ? window : new byte[size];
                int n = source.Read(header.PayloadOffset, payload.AsSpan(0, (int)size), cancellationToken);
                if (EventMessageBox.TryParse(payload.AsSpan(0, n), header.Offset, out EventMessageBox? box, defects) && box is not null)
                {
                    if (box.Version == 1)
                    {
                        found.Add(new EmsgFound(box, box.Timescale == 0 ? MediaTime.Unknown : new MediaTime(checked((long)Math.Min(box.PresentationTime, long.MaxValue)), box.Timescale), true));
                    }
                    else
                    {
                        waiting.Add(box);
                    }
                }
            }
            else if (header.Type == BoxTypes.Sidx)
            {
                int n = source.Read(header.PayloadOffset, window.AsSpan(0, (int)Math.Min(header.PayloadSize, window.Length)), cancellationToken);
                if (SegmentIndexBox.TryParse(window.AsSpan(0, n), header.Offset, header.End, out SegmentIndexBox? sidx) && sidx is { Timescale: > 0 })
                {
                    sidxStart = new MediaTime((long)Math.Min(sidx.EarliestPresentationTime, long.MaxValue), sidx.Timescale);
                }
            }
            else if (header.Type == BoxTypes.Moof)
            {
                if (waiting.Count > 0)
                {
                    MediaTime start = SegmentStart(source, header, iso, window, cancellationToken);
                    if (!start.IsKnown)
                    {
                        start = sidxStart;
                    }

                    foreach (EventMessageBox box in waiting)
                    {
                        MediaTime delta = box.Timescale == 0 ? MediaTime.Unknown : new MediaTime(box.PresentationTimeDelta, box.Timescale);
                        found.Add(start.IsKnown && delta.IsKnown ? new EmsgFound(box, start + delta, true) : new EmsgFound(box, delta, false));
                    }

                    waiting.Clear();
                }

                sidxStart = MediaTime.Unknown;
            }

            if (header.End <= offset)
            {
                break;
            }

            offset = header.End;
        }

        foreach (EventMessageBox box in waiting)
        {
            // No segment follows: only the delta is known.
            found.Add(new EmsgFound(box, box.Timescale == 0 ? MediaTime.Unknown : new MediaTime(box.PresentationTimeDelta, box.Timescale), false));
        }

        return found;
    }

    // The base media decode time of the first track fragment in a moof, as a time. Only the first 4 KiB of the moof are read: mfhd, traf, tfhd
    // and tfdt sit at its start.
    private static MediaTime SegmentStart(IMediaSource source, BoxHeader moof, IsoBmffDemuxer iso, byte[] window, CancellationToken cancellationToken)
    {
        int n = source.Read(moof.PayloadOffset, window.AsSpan(0, (int)Math.Min(moof.PayloadSize, window.Length)), cancellationToken);
        ReadOnlySpan<byte> m = window.AsSpan(0, n);
        int p = 0;
        while (p + 8 <= m.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(m[p..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(m[(p + 4)..]);
            if (size < 8)
            {
                return MediaTime.Unknown;
            }

            if (type == BoxTypes.Traf)
            {
                return TrafStart(m.Slice(p + 8, (int)Math.Min(size - 8, m.Length - p - 8)), iso);
            }

            p += (int)Math.Min(size, m.Length);
        }

        return MediaTime.Unknown;
    }

    private static MediaTime TrafStart(ReadOnlySpan<byte> traf, IsoBmffDemuxer iso)
    {
        int p = 0;
        uint trackId = 0;
        while (p + 8 <= traf.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(traf[p..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(traf[(p + 4)..]);
            if (size < 8)
            {
                break;
            }

            ReadOnlySpan<byte> body = traf[(p + 8)..(int)Math.Min(traf.Length, p + size)];
            if (type == BoxTypes.Tfhd && body.Length >= 8)
            {
                trackId = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            }
            else if (type == BoxTypes.Tfdt && body.Length >= 8)
            {
                // §8.8.12: version 0 has a 32-bit baseMediaDecodeTime, version 1 a 64-bit one.
                ulong decode = body[0] == 1 && body.Length >= 12 ? BinaryPrimitives.ReadUInt64BigEndian(body[4..]) : BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                foreach (IsoTrack t in iso.Movie.Tracks)
                {
                    if (t.Header.TrackId == trackId && t.Media.Timescale > 0 && decode <= long.MaxValue)
                    {
                        return new MediaTime((long)decode, t.Media.Timescale);
                    }
                }

                return MediaTime.Unknown;
            }

            p += (int)Math.Min(size, traf.Length);
        }

        return MediaTime.Unknown;
    }
}
