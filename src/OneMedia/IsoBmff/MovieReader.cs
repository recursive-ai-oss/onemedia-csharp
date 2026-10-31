using System.Text;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Turns a <c>moov</c> box into an <see cref="IsoMovie"/>. Sample tables are located, not read.</summary>
public static class MovieReader
{
    /// <summary>
    /// Reads the movie box. Missing or short boxes are defects and the movie still comes back with what could be read; a track without a
    /// media header, handler or sample description is skipped (with a defect), so the result never contains a half-built track.
    /// </summary>
    /// <param name="moov">The <c>moov</c> node.</param>
    /// <param name="source">The source the node was read from (the sample-table ranges are validated against it).</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="isQuickTime">True when the file's major brand is <c>qt  </c>, which changes the audio sample-entry layout.</param>
    /// <returns>The movie.</returns>
    /// <exception cref="MediaLimitExceededException">More tracks than <see cref="MediaLimits.MaxTracks"/>, or a box larger than the allocation limit.</exception>
    public static IsoMovie Read(BoxNode moov, IMediaSource source, MediaLimits limits, DefectLog defects, bool isQuickTime)
    {
        ArgumentNullException.ThrowIfNull(moov);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);

        MovieHeader? header = null;
        List<IsoTrack> tracks = [];
        List<TrackExtends> extends = [];
        bool fragmented = false;
        long fragmentDuration = -1;
        foreach (BoxNode child in moov.EnumerateChildren())
        {
            switch (child.Type)
            {
                case BoxTypes.Mvhd:
                    header ??= ReadMovieHeader(child, defects);
                    break;
                case BoxTypes.Trak:
                    limits.CheckTracks(tracks.Count + 1, child.Offset);
                    IsoTrack? track = ReadTrack(child, tracks.Count, source, limits, defects, isQuickTime);
                    if (track is not null)
                    {
                        tracks.Add(track);
                    }

                    break;
                case BoxTypes.Mvex:
                    fragmented = true;
                    foreach (BoxNode ex in child.EnumerateChildren())
                    {
                        if (ex.Type == BoxTypes.Trex)
                        {
                            byte[] p = ex.ReadPayload(64);
                            if (p.Length >= 24)
                            {
                                extends.Add(new TrackExtends(Be.U32(p, 4), Be.U32(p, 8), Be.U32(p, 12), Be.U32(p, 16), Be.U32(p, 20)));
                            }
                            else
                            {
                                defects.Add(DefectKind.Truncated, DefectSeverity.Warning, ex.Offset, "The track extends box is shorter than 24 bytes.");
                            }
                        }
                        else if (ex.Type == BoxTypes.Mehd)
                        {
                            byte[] p = ex.ReadPayload(32);
                            if (p.Length >= 8)
                            {
                                ulong d = p[0] == 1 ? Be.U64(p, 4) : Be.U32(p, 4);
                                fragmentDuration = d > long.MaxValue ? -1 : (long)d;
                            }
                        }
                    }

                    break;
            }
        }

        if (header is null)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, moov.Offset, "The movie has no movie header box.", "isobmff.no-mvhd");
            header = new MovieHeader(0, 0, 0, 0, 0);
        }

        foreach (IsoTrack t in tracks)
        {
            foreach (TrackExtends e in extends)
            {
                if (e.TrackId == t.Header.TrackId)
                {
                    t.Extends = e;
                    break;
                }
            }
        }

        return new IsoMovie
        {
            Header = header,
            Tracks = tracks,
            IsFragmented = fragmented,
            FragmentDuration = fragmentDuration,
            GaplessInfo = FindGaplessInfo(moov, limits),
            Range = new ByteRange(moov.Offset, moov.Header.HeaderSize + moov.PayloadSize),
        };
    }

    private static MovieHeader ReadMovieHeader(BoxNode node, DefectLog defects)
    {
        byte[] p = node.ReadPayload(128);
        bool v1 = p.Length > 0 && p[0] == 1;
        int need = v1 ? 32 : 20;
        if (p.Length < need)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, node.Offset, "The movie header box is too short.");
            return new MovieHeader(0, 0, 0, 0, 0);
        }

        int next = v1 ? 108 : 96;
        return v1
            ? new MovieHeader(Be.U32(p, 20), Be.U64(p, 24), Be.U64(p, 4), Be.U64(p, 12), Be.U32(p, next))
            : new MovieHeader(Be.U32(p, 12), Be.U32(p, 16) == uint.MaxValue ? ulong.MaxValue : Be.U32(p, 16), Be.U32(p, 4), Be.U32(p, 8), Be.U32(p, next));
    }

    private static IsoTrack? ReadTrack(BoxNode trak, int index, IMediaSource source, MediaLimits limits, DefectLog defects, bool isQuickTime)
    {
        TrackHeader? tkhd = null;
        MediaHeader? mdhd = null;
        string? handler = null;
        string handlerName = string.Empty;
        List<EditListEntry> edits = [];
        BoxNode? stbl = null;
        foreach (BoxNode child in trak.EnumerateChildren())
        {
            if (child.Type == BoxTypes.Tkhd)
            {
                tkhd ??= ReadTrackHeader(child, defects);
            }
            else if (child.Type == BoxTypes.Edts)
            {
                BoxNode? elst = child.Find(BoxTypes.Elst);
                if (elst is not null && edits.Count == 0)
                {
                    ReadEditList(elst, edits, limits, defects);
                }
            }
            else if (child.Type == BoxTypes.Mdia)
            {
                foreach (BoxNode m in child.EnumerateChildren())
                {
                    if (m.Type == BoxTypes.Mdhd)
                    {
                        mdhd ??= ReadMediaHeader(m, defects);
                    }
                    else if (m.Type == BoxTypes.Hdlr && handler is null)
                    {
                        byte[] p = m.ReadPayload(4096);
                        if (p.Length >= 12)
                        {
                            handler = BoxReader.Latin1(p.AsSpan(8, 4));
                            handlerName = ReadHandlerName(p);
                        }
                    }
                    else if (m.Type == BoxTypes.Minf)
                    {
                        stbl ??= m.Find(BoxTypes.Stbl);
                    }
                }
            }
        }

        if (tkhd is null || mdhd is null || handler is null)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, trak.Offset,
                $"Track {index} lacks its {(tkhd is null ? "track header" : mdhd is null ? "media header" : "handler")} box and is skipped.", "isobmff.track-incomplete");
            return null;
        }

        SampleTableLayout layout = new();
        List<SampleEntry> entries = [];
        List<ByteRange> groups = [];
        if (stbl is not null)
        {
            ReadSampleTable(stbl, layout, entries, groups, handler, isQuickTime, source, limits, defects);
        }
        else
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, trak.Offset, $"Track {index} has no sample table box.", "isobmff.no-stbl");
        }

        return new IsoTrack(index)
        {
            Header = tkhd,
            Media = mdhd,
            HandlerType = handler,
            HandlerName = handlerName,
            Edits = edits,
            SampleEntries = entries,
            Layout = layout,
            SampleGroupBoxes = groups,
        };
    }

    private static string ReadHandlerName(byte[] p)
    {
        if (p.Length <= 24)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> name = p.AsSpan(24);
        // QuickTime stores a length-prefixed string, ISO a null-terminated UTF-8 one. Telling them apart: a Pascal length covers the rest.
        if (name[0] == name.Length - 1 || name[0] == name.Length - 1 - (name.Length > 1 && name[^1] == 0 ? 1 : 0))
        {
            name = name[1..];
        }

        int nul = name.IndexOf((byte)0);
        if (nul >= 0)
        {
            name = name[..nul];
        }

        return Encoding.UTF8.GetString(name);
    }

    private static TrackHeader? ReadTrackHeader(BoxNode node, DefectLog defects)
    {
        byte[] p = node.ReadPayload(256);
        bool v1 = p.Length > 0 && p[0] == 1;
        int need = v1 ? 96 : 84;
        if (p.Length < need)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, node.Offset, "The track header box is too short.");
            return null;
        }

        int baseOffset = v1 ? 52 : 40;
        int[] matrix = new int[9];
        for (int i = 0; i < 9; i++)
        {
            matrix[i] = Be.I32(p, baseOffset + (i * 4));
        }

        return new TrackHeader(
            Be.U32(p, v1 ? 20 : 12),
            (uint)(Be.U32(p, 0) & 0xFFFFFF),
            v1 ? Be.U64(p, 28) : Be.U32(p, 20),
            Be.U32(p, v1 ? 88 : 76),
            Be.U32(p, v1 ? 92 : 80),
            matrix,
            (short)Be.U16(p, v1 ? 46 : 34),
            (short)Be.U16(p, v1 ? 44 : 32));
    }

    private static MediaHeader? ReadMediaHeader(BoxNode node, DefectLog defects)
    {
        byte[] p = node.ReadPayload(64);
        bool v1 = p.Length > 0 && p[0] == 1;
        if (p.Length < (v1 ? 34 : 22))
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, node.Offset, "The media header box is too short.");
            return null;
        }

        ulong duration = v1 ? Be.U64(p, 24) : Be.U32(p, 16) == uint.MaxValue ? ulong.MaxValue : Be.U32(p, 16);
        return new MediaHeader(v1 ? Be.U32(p, 20) : Be.U32(p, 12), duration, LanguageCodes.Decode(Be.U16(p, v1 ? 32 : 20)), v1 ? Be.U64(p, 4) : Be.U32(p, 4));
    }

    private static void ReadEditList(BoxNode node, List<EditListEntry> edits, MediaLimits limits, DefectLog defects)
    {
        byte[] head = node.ReadPayload(8);
        if (head.Length < 8)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Warning, node.Offset, "The edit list box is too short.");
            return;
        }

        bool v1 = head[0] == 1;
        int entrySize = v1 ? 20 : 12;
        long declared = Be.U32(head, 4);
        long fit = (node.PayloadSize - 8) / entrySize;
        if (declared > fit)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Warning, node.Offset, $"The edit list declares {declared} entries but holds {fit}.", "isobmff.elst-count");
            declared = fit;
        }

        limits.CheckSampleTable(declared, node.Offset);
        byte[] p = node.ReadPayload(8 + (declared * entrySize));
        for (int i = 0; i < declared && 8 + ((i + 1) * entrySize) <= p.Length; i++)
        {
            int o = 8 + (i * entrySize);
            edits.Add(v1
                ? new EditListEntry(Be.U64(p, o), unchecked((long)Be.U64(p, o + 8)), (short)Be.U16(p, o + 16), (short)Be.U16(p, o + 18))
                : new EditListEntry(Be.U32(p, o), Be.I32(p, o + 4), (short)Be.U16(p, o + 8), (short)Be.U16(p, o + 10)));
        }
    }

    private static void ReadSampleTable(BoxNode stbl, SampleTableLayout t, List<SampleEntry> entries, List<ByteRange> groups, string handler, bool isQuickTime,
        IMediaSource source, MediaLimits limits, DefectLog defects)
    {
        Span<byte> head = stackalloc byte[16]; // the count fields at the start of each table box
        foreach (BoxNode box in stbl.EnumerateChildren())
        {
            switch (box.Type)
            {
                case BoxTypes.Stsd:
                    ReadSampleDescriptions(box, entries, handler, isQuickTime, limits, defects);
                    break;
                case BoxTypes.Stts:
                    t.Stts = LocateTable(box, source, head, 8, 8, limits, defects);
                    break;
                case BoxTypes.Ctts:
                    t.Ctts = LocateTable(box, source, head, 8, 8, limits, defects);
                    break;
                case BoxTypes.Stss:
                    t.Stss = LocateTable(box, source, head, 8, 4, limits, defects);
                    break;
                case BoxTypes.Stsc:
                    t.Stsc = LocateTable(box, source, head, 8, 12, limits, defects);
                    break;
                case BoxTypes.Stco:
                    t.ChunkOffsets = LocateTable(box, source, head, 8, 4, limits, defects);
                    break;
                case BoxTypes.Co64:
                    t.ChunkOffsets = LocateTable(box, source, head, 8, 8, limits, defects);
                    t.ChunkOffsets64 = true;
                    break;
                case BoxTypes.Sdtp:
                    t.Sdtp = new TableRef(box.Header.PayloadOffset + 4, Math.Max(0, box.PayloadSize - 4));
                    break;
                case BoxTypes.Stsz:
                    LocateSizes(box, t, source, head, 32, limits, defects);
                    break;
                case BoxTypes.Stz2:
                    LocateSizes(box, t, source, head, -1, limits, defects);
                    break;
                case BoxTypes.Sgpd or BoxTypes.Sbgp:
                    groups.Add(new ByteRange(box.Offset, box.Header.HeaderSize + box.PayloadSize));
                    break;
            }
        }
    }

    private static TableRef LocateTable(BoxNode box, IMediaSource source, Span<byte> head, int entriesAt, int entrySize, MediaLimits limits, DefectLog defects)
    {
        if (box.PayloadSize < entriesAt || source.Read(box.Header.PayloadOffset, head[..8]) < 8)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, box.Offset, $"The '{box.TypeName}' box is shorter than its count field.");
            return new TableRef(box.Header.PayloadOffset + entriesAt, 0);
        }

        long declared = Be.U32(head, 4);
        long fit = (box.PayloadSize - entriesAt) / entrySize;
        if (declared > fit)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, box.Offset,
                $"The '{box.TypeName}' box declares {declared} entries but holds {fit}.", "isobmff.table-count");
            declared = fit;
        }

        limits.CheckSampleTable(declared, box.Offset);
        return new TableRef(box.Header.PayloadOffset + entriesAt, declared);
    }

    private static void LocateSizes(BoxNode box, SampleTableLayout t, IMediaSource source, Span<byte> head, int fieldBits, MediaLimits limits, DefectLog defects)
    {
        if (box.PayloadSize < 12 || source.Read(box.Header.PayloadOffset, head[..12]) < 12)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, box.Offset, $"The '{box.TypeName}' box is shorter than its header.");
            return;
        }

        long count = Be.U32(head, 8);
        long entriesAt = box.Header.PayloadOffset + 12;
        t.HasSizes = true;
        if (fieldBits == 32)
        {
            t.ConstantSize = Be.U32(head, 4);
            t.SizeFieldBits = 32;
        }
        else
        {
            // §8.7.3.3: the compact size box stores 4-, 8- or 16-bit sizes; the field size is the last byte of the 4-byte reserved/size word.
            int bits = head[7];
            if (bits is not (4 or 8 or 16))
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, box.Offset, $"The compact sample size box has field size {bits}.");
                t.HasSizes = false;
                return;
            }

            t.SizeFieldBits = bits;
            t.ConstantSize = 0;
        }

        t.SampleCount = count;
        limits.CheckSampleTable(count, box.Offset);
        if (t.ConstantSize == 0)
        {
            long perEntryBits = t.SizeFieldBits;
            long fit = (box.PayloadSize - 12) * 8 / perEntryBits;
            if (count > fit)
            {
                defects.Add(DefectKind.Truncated, DefectSeverity.Error, box.Offset, $"The sample size box declares {count} samples but holds {fit}.", "isobmff.table-count");
                t.SampleCount = fit;
                count = fit;
            }

            t.SizeTable = new TableRef(entriesAt, count);
        }
    }

    private static void ReadSampleDescriptions(BoxNode stsd, List<SampleEntry> entries, string handler, bool isQuickTime, MediaLimits limits, DefectLog defects)
    {
        foreach (BoxNode entry in stsd.EnumerateChildren())
        {
            // An entry's fixed fields and configuration boxes are small. Reading at most 1 MiB bounds what a hostile file can make us keep per entry.
            const int MaxEntryBytes = 1 << 20;
            if (entry.PayloadSize > MaxEntryBytes)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, entry.Offset, "A sample entry is larger than 1 MiB; only its first mebibyte is read.", "isobmff.entry-large");
            }

            byte[] p = entry.ReadPayload(MaxEntryBytes);
            entries.Add(ParseSampleEntry(entry, p, handler, isQuickTime, limits, defects));
        }
    }

    private static SampleEntry ParseSampleEntry(BoxNode entry, byte[] p, string handler, bool isQuickTime, MediaLimits limits, DefectLog defects)
    {
        string format = entry.TypeName;
        int dri = Be.U16(p, 6);
        int prefix = 8;
        int width = 0, height = 0, channels = 0, bits = 0, rate = 0, version = 0, perPacket = 0, bytesPerPacket = 0, bytesPerFrame = 0;
        if (handler == "vide")
        {
            prefix = 78;
            width = Be.U16(p, 24);
            height = Be.U16(p, 26);
        }
        else if (handler == "soun")
        {
            version = Be.U16(p, 8);
            channels = Be.U16(p, 16);
            bits = Be.U16(p, 18);
            rate = (int)(Be.U32(p, 24) >> 16);
            prefix = 28;
            if (isQuickTime && version == 1)
            {
                prefix = 44;
                // QuickTime File Format, sound sample description version 1: samples per packet, bytes per packet, bytes per frame, bytes per sample.
                perPacket = (int)Math.Min(Be.U32(p, 28), int.MaxValue);
                bytesPerPacket = (int)Math.Min(Be.U32(p, 32), int.MaxValue);
                bytesPerFrame = (int)Math.Min(Be.U32(p, 36), int.MaxValue);
            }
            else if (isQuickTime && version == 2)
            {
                // QuickTime sound description version 2: a float64 rate and a 32-bit channel count follow the common fields.
                prefix = 64;
                channels = (int)Math.Min(Be.U32(p, 40), 4096);
                double hz = BitConverter.Int64BitsToDouble(unchecked((long)Be.U64(p, 32)));
                rate = hz is > 0 and < 4e9 ? (int)Math.Min(hz, int.MaxValue) : 0;
                bits = (int)Math.Min(Be.U32(p, 48), 256);
            }
            else if (!isQuickTime)
            {
                version = 0; // ISO/IEC 14496-12 audio entry version 1 keeps the 28-byte layout; the extra boxes follow as children.
            }
        }

        bool isProtected = format is "encv" or "enca" or "enct" or "encs" or "encm";
        List<ExtensionBox> extensions = [];
        string original = format;
        if (p.Length > prefix && (handler is "vide" or "soun"))
        {
            ReadExtensions(p, prefix, entry.Header.PayloadOffset, extensions, 0, limits, defects);
        }

        if (isProtected)
        {
            foreach (ExtensionBox ext in extensions)
            {
                if (ext.Type == BoxTypes.Sinf)
                {
                    List<ExtensionBox> inner = [];
                    ReadExtensions(ext.Payload.Span.ToArray(), 0, ext.Offset + 8, inner, 1, limits, defects);
                    foreach (ExtensionBox i in inner)
                    {
                        if (i.TypeName == "frma" && i.Payload.Length >= 4)
                        {
                            original = BoxReader.Latin1(i.Payload.Span[..4]);
                        }
                    }
                }
            }
        }

        CodecConfiguration config = default;
        if (handler is "vide" or "soun")
        {
            uint want = ConfigBoxType(original);
            if (want != 0)
            {
                foreach (ExtensionBox ext in extensions)
                {
                    if (ext.Type == want)
                    {
                        config = new CodecConfiguration(ext.Payload, ext.TypeName);
                        break;
                    }
                }
            }
        }
        else if (p.Length > 8)
        {
            config = new CodecConfiguration(p.AsMemory(8), format);
        }

        return new SampleEntry
        {
            Format = format,
            OriginalFormat = original,
            IsProtected = isProtected,
            Offset = entry.Offset,
            DataReferenceIndex = dri,
            Width = width,
            Height = height,
            ChannelCount = channels,
            SampleSizeBits = bits,
            SampleRate = rate,
            SoundVersion = version,
            SamplesPerPacket = perPacket,
            BytesPerPacket = bytesPerPacket,
            BytesPerFrame = bytesPerFrame,
            Extensions = extensions,
            Configuration = config,
        };
    }

    private static uint ConfigBoxType(string format) => format switch
    {
        "avc1" or "avc2" or "avc3" or "avc4" => BoxTypes.Make("avcC"),
        "hvc1" or "hev1" or "dvh1" or "dvhe" => BoxTypes.Make("hvcC"),
        "vvc1" or "vvi1" => BoxTypes.Make("vvcC"),
        "av01" => BoxTypes.Make("av1C"),
        "vp08" or "vp09" => BoxTypes.Make("vpcC"),
        "mp4a" or "mp4v" or "mp4s" => BoxTypes.Esds,
        "Opus" => BoxTypes.Make("dOps"),
        "fLaC" => BoxTypes.Make("dfLa"),
        "ac-3" => BoxTypes.Make("dac3"),
        "ec-3" => BoxTypes.Make("dec3"),
        "samr" or "sawb" or "sawp" => BoxTypes.Make("damr"),
        "alac" => BoxTypes.Make("alac"),
        _ => 0,
    };

    // Child boxes of a sample entry. QuickTime puts the codec boxes inside a 'wave' atom; they are surfaced too.
    private static void ReadExtensions(byte[] p, int start, long payloadFileOffset, List<ExtensionBox> into, int depth, MediaLimits limits, DefectLog defects)
    {
        int pos = start;
        while (pos < p.Length)
        {
            if (!BoxReader.TryParse(p.AsSpan(pos, Math.Min(BoxReader.MaxHeaderBytes, p.Length - pos)), pos, p.Length, false, null, out BoxHeader h))
            {
                break;
            }

            long fileOffset = payloadFileOffset + h.Offset;
            ReadOnlyMemory<byte> payload = p.AsMemory((int)h.PayloadOffset, (int)h.PayloadSize);
            into.Add(new ExtensionBox(h.Type, fileOffset, payload));
            if (h.Type == BoxTypes.Wave && depth < 4)
            {
                ReadExtensions(p, (int)h.PayloadOffset, payloadFileOffset, into, depth + 1, limits, defects);
            }

            pos = (int)h.End;
        }
    }

    // The iTunes gapless tag lives in moov/udta/meta/ilst/'----' or moov/meta/ilst/'----', as the three boxes mean (namespace),
    // name and data.
    private static string? FindGaplessInfo(BoxNode moov, MediaLimits limits)
    {
        foreach (BoxNode top in moov.EnumerateChildren())
        {
            if (top.Type == BoxTypes.Meta)
            {
                string? found = SearchIlst(top, limits);
                if (found is not null)
                {
                    return found;
                }
            }
            else if (top.Type == BoxTypes.Udta)
            {
                foreach (BoxNode m in top.EnumerateChildren())
                {
                    if (m.Type == BoxTypes.Meta && SearchIlst(m, limits) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    private static string? SearchIlst(BoxNode meta, MediaLimits limits)
    {
        BoxNode? ilst = meta.Find(BoxTypes.Ilst);
        if (ilst is null)
        {
            return null;
        }

        uint dashes = BoxTypes.Make("----");
        foreach (BoxNode item in ilst.EnumerateChildren())
        {
            if (item.Type != dashes)
            {
                continue;
            }

            BoxNode? name = item.Find(BoxTypes.Make("name"));
            BoxNode? data = item.Find(BoxTypes.Make("data"));
            if (name is null || data is null || name.PayloadSize < 4 || data.PayloadSize < 8 || name.PayloadSize > 256)
            {
                continue;
            }

            byte[] n = name.ReadPayload(256);
            if (Encoding.UTF8.GetString(n.AsSpan(4)) != "iTunSMPB")
            {
                continue;
            }

            limits.CheckTag(data.PayloadSize, data.Offset);
            byte[] d = data.ReadPayload(limits.MaxTagBytes);
            return d.Length > 8 ? Encoding.UTF8.GetString(d.AsSpan(8)).TrimEnd('\0') : null;
        }

        return null;
    }
}

/// <summary>Decoding of the 16-bit language field of the media header (ISO/IEC 14496-12 §8.4.2.3; Apple QuickTime File Format, language codes).</summary>
internal static class LanguageCodes
{
    // QuickTime Macintosh language codes 0..94 mapped to ISO 639-2/T.
    private static readonly string[] MacToIso =
    [
        "eng", "fra", "deu", "ita", "nld", "swe", "spa", "dan", "por", "nor", "heb", "jpn", "ara", "fin", "ell", "isl",
        "mlt", "tur", "hrv", "zho", "urd", "hin", "tha", "kor", "lit", "pol", "hun", "est", "lav", "sme", "fao", "fas",
        "rus", "zho", "nld", "gle", "sqi", "ron", "ces", "slk", "slv", "yid", "srp", "mkd", "bul", "ukr", "bel", "uzb",
        "kaz", "aze", "aze", "hye", "kat", "ron", "kir", "tgk", "tuk", "mon", "mon", "pus", "kur", "kas", "snd", "bod",
        "nep", "san", "mar", "ben", "asm", "guj", "pan", "ori", "mal", "kan", "tam", "tel", "sin", "mya", "khm", "lao",
        "vie", "ind", "tgl", "msa", "msa", "amh", "tir", "orm", "som", "swa", "kin", "run", "nya", "mlg", "epo",
    ];

    // Codes 128..138 (Table 4-1 of the QuickTime File Format Specification): Welsh to Javanese (Roman).
    private static readonly string[] MacHighToIso = ["cym", "eus", "cat", "lat", "que", "grn", "aym", "tat", "uig", "dzo", "jav"];

    public static string Decode(ushort code)
    {
        if (code is 0x7FFF)
        {
            return "und";
        }

        if (code < 0x400)
        {
            return code < MacToIso.Length ? MacToIso[code] : code is >= 128 && code - 128 < MacHighToIso.Length ? MacHighToIso[code - 128] : "und";
        }

        // Three characters of 5 bits, each the letter minus 0x60.
        Span<char> chars = stackalloc char[3]; // fixed three-character buffer
        for (int i = 0; i < 3; i++)
        {
            int v = (code >> (10 - (5 * i))) & 0x1F;
            chars[i] = (char)(v + 0x60);
            if (v is 0 or > 26)
            {
                return "und";
            }
        }

        return new string(chars);
    }
}
