using System.Linq;
using System.Text;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Metadata.Telemetry;
using RecursiveAI.OneMedia.Metadata.Telemetry.Mebx;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Xmp;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// The strip for ISO base media files (MP4, MOV, 3GP, fragmented MP4). Every change keeps the file's length and the position of every byte: a field
/// goes by being overwritten with zeros, a whole atom by becoming a <c>free</c> box of the same size, so no chunk offset, <c>moof</c> offset or <c>sidx</c>
/// entry needs to change and the media payload is copied as it was.
/// </summary>
internal static class IsoStrip
{
    private static readonly byte[] FreeType = "free"u8.ToArray();

    public static StripPlan Plan(string path, MediaFile file, StripOptions options, CancellationToken ct)
    {
        StripCategories cats = options.Categories;
        using FileMediaSource source = FileMediaSource.Open(path);
        PatchSet patches = new();
        StripSink sink = new(patches);
        List<Secret> secrets = [];
        MediaMetadata meta = file.Metadata;
        HashSet<long> freed = [];

        foreach (NativeTag tag in meta.NativeTags)
        {
            ct.ThrowIfCancellationRequested();
            StripCategories c = StripRules.Classify(tag.System, tag.Key) & cats;
            if (c != 0 && !tag.Range.IsNone)
            {
                Remember(secrets, tag, c);
                FreeTag(source, tag, c, sink, freed);
            }
            else if (tag.System == TagSystems.QuickTimeUserData && tag.Key == "GPMF" && !tag.ValueRange.IsNone)
            {
                ScrubValue(source, tag, options, sink, secrets, static (b, off, tr, cs, sk) => TelemetryScrub.Gpmf(b, off, "udta", tr, cs, sk));
            }
            else if (tag.System == TagSystems.QuickTimeUserData && tag.Key.Equals("Exif", StringComparison.OrdinalIgnoreCase) && !tag.ValueRange.IsNone)
            {
                ScrubValue(source, tag, options, sink, secrets, static (b, off, tr, cs, sk) => TiffScrub.Scrub(b, off, "exif", tr, cs, sk));
            }
        }

        foreach (XmpPacket x in meta.XmpPackets)
        {
            ct.ThrowIfCancellationRequested();
            ScrubXmp(source, x, cats, sink);
        }

        ScrubTracks(file, options, secrets, sink, ct);
        sink.AddNote("Subtitle and text tracks are not inspected: position text that a recorder stores in them stays.");
        sink.AddNote("Key names in a QuickTime metadata key table (keys box) and in mebx sample entries stay; their values are gone.");
        sink.AddNote("Layout is unchanged: every changed range keeps its length and no other byte moved.");
        return new StripPlan(patches.ToPlan(source.Length), new StripReport(cats, sink.Entries(), sink.Leftovers, sink.Notes));
    }

    private static void Remember(List<Secret> secrets, NativeTag tag, StripCategories c)
    {
        // The value as stored, so the same bytes can be found in tracks that repeat it without a label.
        try
        {
            byte[] value = tag.ValueRange.IsNone || tag.ValueRange.Length > 4096 ? [] : tag.ReadValue();
            if (tag.Kind == TagValueKind.Text && tag.Text is { Length: >= 6 } text)
            {
                value = Encoding.UTF8.GetBytes(text.TrimEnd('\0'));
            }

            if (value.Length >= 6)
            {
                secrets.Add(new Secret(c, tag.System + ":" + tag.Key, value));
            }
        }
        catch (IOException)
        {
            // A value that cannot be read cannot be searched for; the tag itself is still removed.
        }
    }

    // A tag is removed by turning its atom into padding. In an item list of QuickTime keys the item's type is the key's number, so the item stays and
    // its value atoms (data, mean, name) become padding instead: a number whose type is "free" would read as a key that does not exist.
    private static void FreeTag(FileMediaSource source, NativeTag tag, StripCategories c, StripSink sink, HashSet<long> freed)
    {
        if (!freed.Add(tag.Range.Offset))
        {
            sink.Note(c, tag.System, tag.Key, tag.TrackId, tag.Range.Offset, 0, StripAction.ConvertedToPadding, 0);
            return;
        }

        if (tag.System == TagSystems.QuickTimeKeys)
        {
            long pos = tag.Range.Offset;
            if (!BoxReader.TryRead(source, pos, tag.Range.End, false, null, out BoxHeader item))
            {
                sink.Leftover(c, tag.System, tag.Key, tag.TrackId, tag.Range, "The item's box header could not be read.");
                return;
            }

            long at = item.PayloadOffset;
            while (at < tag.Range.End && BoxReader.TryRead(source, at, tag.Range.End, false, null, out BoxHeader child))
            {
                MakeFree(sink, child, c, tag, count: false);
                at = child.End;
            }

            sink.Note(c, tag.System, tag.Key, tag.TrackId, tag.Range.Offset, tag.Range.Length, StripAction.ConvertedToPadding, tag.Range.Length - item.HeaderSize);
            return;
        }

        if (!BoxReader.TryRead(source, tag.Range.Offset, tag.Range.End, false, null, out BoxHeader box))
        {
            sink.Leftover(c, tag.System, tag.Key, tag.TrackId, tag.Range, "The atom's box header could not be read.");
            return;
        }

        MakeFree(sink, box, c, tag, count: true);
    }

    private static void MakeFree(StripSink sink, BoxHeader box, StripCategories c, NativeTag tag, bool count)
    {
        sink.Patches.Write(box.Offset + 4, FreeType);
        sink.Patches.Zero(box.PayloadOffset, box.PayloadSize);
        if (count)
        {
            sink.Note(c, tag.System, tag.Key, tag.TrackId, box.Offset, box.HeaderSize + box.PayloadSize, StripAction.ConvertedToPadding);
        }
    }

    private delegate void ScrubFn(ReadOnlySpan<byte> bytes, long fileOffset, long trackId, StripCategories cats, StripSink sink);

    private static void ScrubValue(FileMediaSource source, NativeTag tag, StripOptions options, StripSink sink, List<Secret> secrets, ScrubFn scrub)
    {
        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        if (tag.ValueRange.Length > limits.MaxTagBytes)
        {
            sink.Leftover(options.Categories, tag.System, tag.Key, tag.TrackId, tag.ValueRange, "The atom is larger than the tag limit and was not read.");
            return;
        }

        byte[] bytes = tag.ReadValue();
        scrub(bytes, tag.ValueRange.Offset, tag.TrackId, options.Categories, sink);
        // Identifiers inside the atom are searched for in the tracks too.
        GpmfSecrets(bytes, secrets);
    }

    private static void GpmfSecrets(ReadOnlySpan<byte> data, List<Secret> secrets, int depth = 0)
    {
        int pos = 0;
        while (data.Length - pos >= 8)
        {
            string key = Encoding.Latin1.GetString(data.Slice(pos, 4));
            long length = (long)data[pos + 5] * ((data[pos + 6] << 8) | data[pos + 7]);
            int body = pos + 8;
            int avail = (int)Math.Min(length, data.Length - body);
            if (data[pos + 4] == 0)
            {
                if (depth < 8)
                {
                    GpmfSecrets(data.Slice(body, avail), secrets, depth + 1);
                }
            }
            else if (StripRules.ClassifyGpmf(key) is { } c and not StripCategories.None && c != StripCategories.Location && avail >= 6)
            {
                secrets.Add(new Secret(c, "GPMF:" + key, data.Slice(body, avail).ToArray()));
            }

            long step = 8 + ((length + 3) & ~3L);
            if (step > data.Length - pos)
            {
                break;
            }

            pos += (int)step;
        }
    }

    private static void ScrubXmp(FileMediaSource source, XmpPacket x, StripCategories cats, StripSink sink)
    {
        if (x.PacketRange.IsNone)
        {
            return;
        }

        byte[] packet;
        try
        {
            packet = x.ReadPacket(source);
        }
        catch (Exception e) when (e is IOException or MediaException)
        {
            sink.Leftover(cats, TagSystems.Xmp, "packet", x.TrackId, x.PacketRange, "The XMP packet could not be read: " + e.Message);
            return;
        }

        List<XmpScrub.Removed> removed = [];
        byte[]? rewritten = XmpScrub.Scrub(packet, cats, removed, out string? error);
        if (error is not null)
        {
            sink.Leftover(cats, TagSystems.Xmp, "packet", x.TrackId, x.PacketRange, error);
            return;
        }

        if (rewritten is null)
        {
            return;
        }

        sink.Patches.Write(x.PacketRange.Offset, rewritten);
        foreach (XmpScrub.Removed r in removed)
        {
            sink.Note(r.Category, TagSystems.Xmp, r.Key, x.TrackId, x.PacketRange.Offset, x.PacketRange.Length, StripAction.Rewritten, r.Bytes);
        }
    }

    private static void ScrubTracks(MediaFile file, StripOptions options, List<Secret> secrets, StripSink sink, CancellationToken ct)
    {
        if (file.Demuxer is not { } demuxer)
        {
            return;
        }

        foreach (MediaTrack track in demuxer.Tracks)
        {
            TelemetryFormat format = TelemetryTrackReader.FormatOf(track);
            bool otherData = track.Kind == TrackKind.Data && format == TelemetryFormat.None && secrets.Count > 0;
            if (format == TelemetryFormat.None && !otherData)
            {
                continue;
            }

            MebxKeyTable[] tables = format == TelemetryFormat.Mebx ? TelemetryTrackReader.KeyTables(demuxer, track) : [];
            foreach (MediaTrack t in demuxer.Tracks)
            {
                demuxer.SetTrackEnabled(t.Index, t.Index == track.Index);
            }

            int generation = file.BeginRead();
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    file.EnsureCurrentRead(generation);
                    if (!demuxer.TryReadPacket(out PacketView view))
                    {
                        break;
                    }

                    long offset = view.Info.SourceRange.IsNone ? -1 : view.Info.SourceRange.Offset;
                    if (offset < 0)
                    {
                        sink.Leftover(options.Categories, track.CodecTag ?? "data", "sample", track.Id, ByteRange.None, "A sample is not stored as one unmodified range, so it cannot be overwritten in place.");
                        continue;
                    }

                    ReadOnlySpan<byte> bytes = view.Data;
                    switch (format)
                    {
                        case TelemetryFormat.Gpmf:
                            TelemetryScrub.Gpmf(bytes, offset, "gpmd", track.Id, options.Categories, sink);
                            TelemetryScrub.BySecrets(bytes, offset, "gpmd", track.Id, options.Categories, secrets, sink);
                            break;
                        case TelemetryFormat.Camm:
                            TelemetryScrub.Camm(bytes, offset, track.Id, options.Categories, sink);
                            break;
                        case TelemetryFormat.Mebx:
                            int entry = demuxer is IsoBmffDemuxer iso ? iso.SampleDescriptionIndex - 1 : 0;
                            TelemetryScrub.Mebx(bytes, offset, track.Id, entry >= 0 && entry < tables.Length ? tables[entry] : null, options.Categories, sink);
                            break;
                        default:
                            TelemetryScrub.BySecrets(bytes, offset, track.CodecTag ?? "data", track.Id, options.Categories, secrets, sink);
                            break;
                    }
                }
            }
            finally
            {
                foreach (MediaTrack t in demuxer.Tracks)
                {
                    demuxer.SetTrackEnabled(t.Index, true);
                }
            }
        }
    }
}
