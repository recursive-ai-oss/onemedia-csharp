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

    public static StripPlan Plan(IMediaSource source, MediaFile file, StripOptions options, CancellationToken ct)
    {
        StripCategories cats = options.Categories;
        PatchSet patches = new();
        StripSink sink = new(patches);
        List<Secret> secrets = [];
        MediaMetadata meta = file.Metadata;
        HashSet<long> freed = [];
        byte[] firmware = [];

        foreach (NativeTag tag in meta.NativeTags)
        {
            ct.ThrowIfCancellationRequested();
            if (tag.System == TagSystems.QuickTimeUserData && tag.Key == "FIRM" && !tag.ValueRange.IsNone && tag.ValueRange.Length is >= 6 and <= 64)
            {
                firmware = Trimmed(tag.ReadValue());
            }

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
        }

        foreach (ITagContainer c in meta.Containers)
        {
            if (c.System == TagSystems.QuickTimeUserData)
            {
                ScrubExifAtoms(source, c, options, sink, ct);
            }
        }

        foreach (XmpPacket x in meta.XmpPackets)
        {
            ct.ThrowIfCancellationRequested();
            ScrubXmp(source, x, cats, sink);
        }

        // What the facts layer found as serial numbers and positions is searched for by value in the data tracks too.
        if ((cats & StripCategories.DeviceIds) != 0)
        {
            foreach (Metadata.FactCandidate<string> c in meta.Facts.Serial.Candidates)
            {
                AddSecret(secrets, StripCategories.DeviceIds, "facts:" + c.Source, c.Value);
            }
        }

        if ((cats & StripCategories.Location) != 0)
        {
            foreach (Metadata.FactCandidate<Metadata.GeoLocation> c in meta.Facts.Location.Candidates)
            {
                AddSecret(secrets, StripCategories.Location, "facts:" + c.Source, c.RawValue);
            }
        }

        ScrubTracks(file, options, secrets, firmware, sink, ct);
        ScrubLeadingGap(source, file, options, secrets, firmware, sink, ct);
        // Vendor atoms and boxes often wrap an Exif block (a thumbnail with its own Exif, a maker's record): the blocks found inside values that no rule handles are
        // scrubbed like any other Exif block. What the strip still has no rule for is named in the report, from what this file holds.
        HashSet<(string, string, long)> scrubbedInside = [];
        foreach (NativeTag tag in meta.NativeTags)
        {
            ct.ThrowIfCancellationRequested();
            if (tag.Kind != TagValueKind.Binary || tag.ValueRange.IsNone || IsHandled(tag))
            {
                continue;
            }

            if (tag.Key is "free" or "skip" or "wide")
            {
                // Padding that holds bytes kept something; padding has no meaning, so it is zeroed.
                if (!StripInventory.IsAllZero(source, tag.ValueRange.Offset, tag.ValueRange.Length, ct))
                {
                    sink.Zero(cats, tag.System, tag.Key, tag.TrackId, tag.ValueRange.Offset, tag.ValueRange.Length);
                    scrubbedInside.Add((tag.System, tag.Key, tag.TrackId));
                }
            }
            else if (ScrubEmbeddedExif(source, tag.ValueRange, tag.TrackId, options, sink, ct) > 0)
            {
                scrubbedInside.Add((tag.System, tag.Key, tag.TrackId));
                sink.AddNote("The '" + tag.Key + "' atom holds Exif blocks, which were scrubbed; its other content is not inspected.");
            }
        }

        List<StripLeftover> found = [];
        StripInventory.OpaqueTags(source, meta, cats, t => IsHandled(t) || scrubbedInside.Contains((t.System, t.Key, t.TrackId)), found, ct);
        StripInventory.IsoBoxes(source, cats, found, (box, type) =>
        {
            if (type is "free" or "skip" or "wide")
            {
                sink.Zero(cats, "iso", type, 0, box.PayloadOffset, box.PayloadSize);
                return true;
            }

            if (ScrubEmbeddedExif(source, new ByteRange(box.PayloadOffset, box.PayloadSize), 0, options, sink, ct) == 0)
            {
                return false;
            }

            sink.AddNote("The 'uuid' box at offset " + box.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + " holds Exif blocks, which were scrubbed; its other content is not inspected.");
            return true;
        }, ct);
        foreach (StripLeftover l in found)
        {
            sink.Leftover(l.Category, l.System, l.Name, l.TrackId, l.Range, l.Reason);
        }

        sink.AddNote("Subtitle and text tracks are scanned for coordinates and serial numbers written as text (docs/strip.md lists the patterns); other text in them stays.");
        sink.AddNote("Key names in a QuickTime metadata key table (keys box) and in mebx sample entries stay; their values are gone.");
        sink.AddNote("Layout is unchanged: every changed range keeps its length and no other byte moved.");
        return new StripPlan(patches.ToPlan(source.Length), new StripReport(cats, sink.Entries(), sink.Leftovers, sink.Notes));
    }

    // A value found in another place is usually stored without the NUL padding the labelled field had.
    private static byte[] Trimmed(byte[] v)
    {
        int n = v.Length;
        while (n > 0 && v[n - 1] == 0)
        {
            n--;
        }

        return n == v.Length ? v : v[..n];
    }

    private static void AddSecret(List<Secret> secrets, StripCategories c, string name, string value)
    {
        if (value.Length >= 6 && !secrets.Any(x => x.Name == name && x.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(value))))
        {
            secrets.Add(new Secret(c, name, Encoding.UTF8.GetBytes(value)));
        }
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

            value = Trimmed(value);
            if (tag.System == TagSystems.QuickTimeUserData && tag.Key == "MUID" && value.Length > 16)
            {
                // GoPro's MUID holds the 16-byte media id followed by a date; the settings block repeats the id alone.
                secrets.Add(new Secret(c, "udta:MUID (id)", value[..16]));
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

    // A tag is removed by turning its atom (in an item list: its item) into a free box of the same size. Readers skip free boxes in an item list;
    // leaving the item with only its value atoms turned to padding made ExifTool report the key with the text "#free".
    private static void FreeTag(IMediaSource source, NativeTag tag, StripCategories c, StripSink sink, HashSet<long> freed)
    {
        if (!freed.Add(tag.Range.Offset))
        {
            sink.Note(c, tag.System, tag.Key, tag.TrackId, tag.Range.Offset, 0, StripAction.ConvertedToPadding, 0);
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

    private static void ScrubValue(IMediaSource source, NativeTag tag, StripOptions options, StripSink sink, List<Secret> secrets, ScrubFn scrub)
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
                secrets.Add(new Secret(c, "GPMF:" + key, Trimmed(data.Slice(body, avail).ToArray())));
            }

            long step = 8 + ((length + 3) & ~3L);
            if (step > data.Length - pos)
            {
                break;
            }

            pos += (int)step;
        }
    }

    // Camera makers keep an Exif block in these user-data atoms (the same list the reader uses for Exif evidence); a few wrapper bytes may precede the TIFF header.
    private static readonly string[] ExifAtoms = ["QVMI", "MVTG", "RMKN", "SIGM", "Exif"];

    private static void ScrubExifAtoms(IMediaSource source, ITagContainer udta, StripOptions options, StripSink sink, CancellationToken ct)
    {
        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        ByteRange r = udta.Range;
        if (r.IsNone || r.Length < 8 || !BoxReader.TryRead(source, r.Offset, r.End, false, null, out BoxHeader box, ct))
        {
            return;
        }

        long at = box.PayloadOffset;
        while (at < r.End && BoxReader.TryRead(source, at, r.End, false, null, out BoxHeader child, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (Array.IndexOf(ExifAtoms, BoxTypes.ToText(child.Type)) >= 0)
            {
                if (child.PayloadSize > limits.MaxTagBytes)
                {
                    sink.Leftover(options.Categories, "exif", BoxTypes.ToText(child.Type), udta.TrackId, new ByteRange(child.PayloadOffset, child.PayloadSize), "The atom is larger than the tag limit and was not read.");
                }
                else
                {
                    byte[] bytes = new byte[child.PayloadSize];
                    source.ReadExactly(child.PayloadOffset, bytes, ct);
                    TiffScrub.Scrub(bytes, child.PayloadOffset, "exif", udta.TrackId, options.Categories, sink, limits);
                }
            }

            at = child.End;
        }
    }

    // A key some rule handles, in the sense of the rules (removed, or scrubbed as GPMF or as an Exif atom).
    private static bool IsHandled(NativeTag t) =>
        StripRules.Classify(t.System, t.Key) != StripCategories.None
        || (t.System == TagSystems.QuickTimeUserData && (t.Key == "GPMF" || Array.IndexOf(ExifAtoms, t.Key) >= 0));

    // Exif blocks inside a vendor value, found by their APP1 marker text (Exif, two zero bytes, then a TIFF header; Exif 2.32 §4.7.2). Returns how many were scrubbed.
    private static int ScrubEmbeddedExif(IMediaSource source, ByteRange range, long trackId, StripOptions options, StripSink sink, CancellationToken ct)
    {
        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        if (range.IsNone || range.Length < 16 || range.Length > limits.MaxTagBytes)
        {
            return 0;
        }

        byte[] bytes = new byte[range.Length];
        source.ReadExactly(range.Offset, bytes, ct);
        int count = 0, pos = 0;
        while (pos < bytes.Length - 16)
        {
            ct.ThrowIfCancellationRequested();
            int i = bytes.AsSpan(pos).IndexOf("Exif\0\0"u8);
            if (i < 0)
            {
                break;
            }

            i += pos;
            int length = (int)Math.Min(bytes.Length - i, 1 << 20);
            if (TiffScrub.Scrub(bytes.AsMemory(i, length), range.Offset + i, "exif", trackId, options.Categories, sink, limits))
            {
                count++;
            }

            pos = i + 6;
        }

        return count;
    }

    private static void ScrubXmp(IMediaSource source, XmpPacket x, StripCategories cats, StripSink sink)
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

        // XMP allows UTF-16 and UTF-32 packets (a byte order mark, or NUL bytes between the ASCII characters of the wrapper); the property scan reads UTF-8 only.
        if (packet.Length >= 4 && (packet[1] == 0 || packet[3] == 0 || (packet[0] is 0xFE or 0xFF && packet[1] is 0xFE or 0xFF) || (packet[0] == 0 && packet[1] == 0)))
        {
            sink.Leftover(cats, TagSystems.Xmp, "packet", x.TrackId, x.PacketRange, "The XMP packet is not UTF-8; its properties were not inspected.");
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

    // GoPro cameras write a settings block (firmware, lens and camera serial numbers, the position) at the start of the media data, before the first
    // sample of any track. No track refers to it, so it is searched for the values the labelled fields held.
    private const long MaxGapBytes = 1 << 20;

    private static void ScrubLeadingGap(IMediaSource source, MediaFile file, StripOptions options, List<Secret> secrets, byte[] firmware, StripSink sink, CancellationToken ct)
    {
        if ((secrets.Count == 0 && firmware.Length == 0) || file.Demuxer is not { } demuxer)
        {
            return;
        }

        long start = -1, pos = 0;
        for (int i = 0; i < 64 && pos < source.Length && BoxReader.TryRead(source, pos, source.Length, true, null, out BoxHeader h, ct); i++)
        {
            if (h.Type == BoxTypes.Mdat)
            {
                start = h.PayloadOffset;
                break;
            }

            pos = h.End;
        }

        if (start < 0)
        {
            return;
        }

        long first = long.MaxValue;
        foreach (MediaTrack track in demuxer.Tracks)
        {
            foreach (MediaTrack t in demuxer.Tracks)
            {
                demuxer.SetTrackEnabled(t.Index, t.Index == track.Index);
            }

            int generation = file.BeginRead();
            if (demuxer.TryReadPacket(out PacketView view) && !view.Info.SourceRange.IsNone)
            {
                first = Math.Min(first, view.Info.SourceRange.Offset);
            }

            file.EnsureCurrentRead(generation);
        }

        foreach (MediaTrack t in demuxer.Tracks)
        {
            demuxer.SetTrackEnabled(t.Index, true);
        }

        if (first <= start || first - start > MaxGapBytes)
        {
            return;
        }

        byte[] gap = new byte[first - start];
        source.ReadExactly(start, gap, ct);
        TelemetryScrub.GoProSettings(gap, start, firmware, "mdat-gap", 0, options.Categories, sink);
        TelemetryScrub.BySecrets(gap, start, "mdat-gap", 0, options.Categories, secrets, sink);
    }

    private static void ScrubTracks(MediaFile file, StripOptions options, List<Secret> secrets, byte[] firmware, StripSink sink, CancellationToken ct)
    {
        if (file.Demuxer is not { } demuxer)
        {
            return;
        }

        foreach (MediaTrack track in demuxer.Tracks)
        {
            TelemetryFormat format = TelemetryTrackReader.FormatOf(track);
            bool otherData = track.Kind == TrackKind.Data && format == TelemetryFormat.None && (secrets.Count > 0 || firmware.Length > 0);
            bool text = track.Kind == TrackKind.Subtitle;
            if (track.Kind == TrackKind.Data && track.CodecTag == "dbgi")
            {
                // DJI's second protobuf track holds debug values in an undocumented layout and is not decoded, so it is not edited.
                sink.Leftover(options.Categories, "dbgi", "debug track", track.Id, ByteRange.None, "The track's layout is not known; any position or serial number in it stays.");
            }

            if (format == TelemetryFormat.None && track.Kind == TrackKind.Data && track.CodecTag is not ("dbgi" or "tmcd" or "fdsc"))
            {
                sink.Leftover(options.Categories, "track", (track.CodecTag ?? "unknown") + " data", track.Id, ByteRange.None,
                    "The track's layout is not known: only the values of fields removed elsewhere were searched for in it; a position or serial number stored in another form stays.");
            }

            if (format == TelemetryFormat.None && !otherData && !text)
            {
                continue;
            }

            MebxKeyTable[] tables = format == TelemetryFormat.Mebx ? TelemetryTrackReader.KeyTables(demuxer, track) : [];
            foreach (MediaTrack t in demuxer.Tracks)
            {
                demuxer.SetTrackEnabled(t.Index, t.Index == track.Index);
            }

            int generation = file.BeginRead();
            int conservativeSamples = 0, samplesRead = 0;
            sink.BeginSamples(track.Id);
            DjiScrub.State djmdState = new();
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

                    samplesRead++;
                    long offset = view.Info.SourceRange.IsNone ? -1 : view.Info.SourceRange.Offset;
                    if (offset < 0)
                    {
                        sink.Leftover(options.Categories, track.CodecTag ?? "data", "sample", track.Id, ByteRange.None, "A sample is not stored as one unmodified range, so it cannot be overwritten in place.");
                        continue;
                    }

                    ReadOnlySpan<byte> bytes = view.Data;
                    if (text)
                    {
                        TextScrub.Sample(bytes, offset, "text", track.Id, options.Categories, sink);
                        continue;
                    }

                    switch (format)
                    {
                        case TelemetryFormat.Djmd:
                            if (DjiScrub.Djmd(bytes, offset, track.Id, options.Categories, sink, djmdState))
                            {
                                conservativeSamples++;
                            }

                            break;
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
                            TelemetryScrub.GoProSettings(bytes, offset, firmware, track.CodecTag ?? "data", track.Id, options.Categories, sink);
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

                sink.EndSamples(track.Id, samplesRead);
            }

            if (conservativeSamples > 0)
            {
                sink.AddNote("Track " + track.Id + ": " + conservativeSamples + " djmd sample(s) belong to a protocol that is not in the table of known DJI layouts; the serial number was removed by its path and everything outside the device description, the stream description, the frame counter and the camera settings was removed, as was every double (docs/strip.md).");
            }
        }
    }
}
