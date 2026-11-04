using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Finds what a file holds that the strip has no rule for, so that the report names it instead of staying silent: opaque or image tag values, data tracks of
/// unknown layout, boxes, chunks, elements and objects outside the structure the format defines, and bytes after the last structure. Each finding is a
/// <see cref="StripLeftover"/> (it is in <see cref="StripReport.NotRemoved"/>), driven by what is present in this file. Free text in known fields, make and
/// model, and container dates are out of scope by design and are not listed here.
/// </summary>
internal static class StripInventory
{
    private const int MaxZeroScan = 16 * 1024 * 1024;
    private const int ImageHead = 64 * 1024;
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    // Binary tag values that name no position and no device: the FLAC seek table; the QuickTime movie-window atoms (Apple's QuickTime File Format, user data:
    // WLOC is where the movie window sat on the screen, SelO / AllF / LOOP / ptv  are play flags); QuickTime Player's own mute flag; the firmware string some
    // cameras keep (make, model and firmware stay by design).
    private static readonly HashSet<(string System, string Key)> HarmlessBinary =
    [
        (TagSystems.Flac, "SEEKTABLE"), (TagSystems.Flac, "seektable"), (TagSystems.QuickTimeUserData, "FIRM"),
        (TagSystems.QuickTimeUserData, "WLOC"), (TagSystems.QuickTimeUserData, "SelO"), (TagSystems.QuickTimeUserData, "AllF"), (TagSystems.QuickTimeUserData, "LOOP"),
        (TagSystems.QuickTimeUserData, "ptv "), (TagSystems.QuickTimeKeys, "com.apple.quicktime.player.movie.audio.mute"),
        (TagSystems.Asf, "ASFLeakyBucketPairs"), (TagSystems.Asf, "WM/MCDI"), (TagSystems.Id3v2, "MCDI"),
    ];

    // DV packs (IEC 61834-2): recording date and time, camera settings and tape/audio source data. The standard has no position or serial-number pack; the dates
    // are out of scope by design (docs/strip.md).
    private static bool IsHarmless(NativeTag tag) =>
        HarmlessBinary.Contains((tag.System, tag.Key)) || tag.System == TagSystems.Dv
        || (tag.MimeType is { } mime && (mime.StartsWith("font/", StringComparison.OrdinalIgnoreCase) || mime.Contains("truetype", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("opentype", StringComparison.OrdinalIgnoreCase) || mime.Contains("x-font", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Reports tag values that are opaque bytes or pictures and that no rule handled. A picture is looked into for Exif, XMP and IPTC (JPEG, PNG); other
    /// formats of picture are reported as they are. One finding per system, key and track, with the number of occurrences.
    /// </summary>
    public static void OpaqueTags(IMediaSource source, MediaMetadata meta, StripCategories cats, Func<NativeTag, bool> handled, List<StripLeftover> left, CancellationToken ct)
    {
        Dictionary<(string, string, long), (StripLeftover First, int Count)> found = [];
        List<(string, string, long)> order = [];
        foreach (NativeTag tag in meta.NativeTags)
        {
            ct.ThrowIfCancellationRequested();
            if (tag.Kind is not (TagValueKind.Binary or TagValueKind.Image) || handled(tag) || IsHarmless(tag))
            {
                continue;
            }

            // Padding atoms are not a finding unless they hold bytes: a free box that is not all zero kept something.
            if (tag.Key is "free" or "skip" or "wide" && IsAllZero(source, tag.ValueRange.Offset, tag.ValueRange.Length, ct))
            {
                continue;
            }

            string what;
            if (tag.Kind == TagValueKind.Image)
            {
                string? metadataIn = ImageMetadata(source, tag.ValueRange, ct);
                if (metadataIn is null)
                {
                    continue;
                }

                what = "A picture holding " + metadataIn + " (not opened by the strip)";
            }
            else
            {
                what = "An opaque value of unknown layout (not inspected by the strip)";
            }

            (string, string, long) key = (tag.System, tag.Key, tag.TrackId);
            if (found.TryGetValue(key, out var existing))
            {
                found[key] = (existing.First, existing.Count + 1);
            }
            else
            {
                found[key] = (new StripLeftover(cats, tag.System, tag.Key, tag.TrackId, tag.Range, what), 1);
                order.Add(key);
            }
        }

        foreach ((string, string, long) key in order)
        {
            (StripLeftover first, int count) = found[key];
            left.Add(count == 1 ? first : first with { Reason = first.Reason + ", " + count.ToString(CultureInfo.InvariantCulture) + " occurrences" });
        }
    }

    // JPEG (ITU-T T.81 §B.1.1.2, JFIF/Exif APPn segments) and PNG (ISO/IEC 15948 chunks): returns a description of the metadata the picture holds, null for
    // none, and a "may hold" description when the first 64 KiB end before the picture data starts. GIF and BMP carry no such metadata and report nothing.
    private static string? ImageMetadata(IMediaSource source, ByteRange range, CancellationToken ct)
    {
        if (range.IsNone || range.Length < 8)
        {
            return null;
        }

        byte[] head = new byte[(int)Math.Min(range.Length, ImageHead)];
        int n = source.Read(range.Offset, head, ct);
        ReadOnlySpan<byte> h = head.AsSpan(0, n);
        if (h.Length >= 4 && h[0] == 0xFF && h[1] == 0xD8)
        {
            int pos = 2;
            while (pos + 4 <= h.Length)
            {
                if (h[pos] != 0xFF)
                {
                    return null;
                }

                byte marker = h[pos + 1];
                if (marker == 0xFF)
                {
                    pos++;
                    continue;
                }

                if (marker == 0xDA || marker == 0xD9)
                {
                    return null;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(h[(pos + 2)..]);
                ReadOnlySpan<byte> body = h[(pos + 4)..Math.Min(h.Length, pos + 2 + length)];
                if (marker == 0xE1 && body.StartsWith("Exif\0"u8))
                {
                    return "Exif";
                }

                if (marker == 0xE1 && body.StartsWith("http://ns.adobe.com/xap/1.0/"u8))
                {
                    return "XMP";
                }

                if (marker == 0xED && body.StartsWith("Photoshop 3.0"u8))
                {
                    return "Photoshop or IPTC data";
                }

                pos += 2 + length;
            }

            return range.Length > ImageHead ? "metadata that could not be ruled out" : null;
        }

        if (h.Length >= 8 && h.StartsWith(PngSignature.AsSpan()))
        {
            int pos = 8;
            while (pos + 12 <= h.Length)
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(h[pos..]);
                ReadOnlySpan<byte> type = h.Slice(pos + 4, 4);
                if (type.SequenceEqual("eXIf"u8))
                {
                    return "Exif";
                }

                if (type.SequenceEqual("iTXt"u8) && h[(pos + 8)..].StartsWith("XML:com.adobe.xmp"u8))
                {
                    return "XMP";
                }

                if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
                {
                    return null;
                }

                long next = pos + 12L + size;
                if (next > h.Length)
                {
                    break;
                }

                pos = (int)next;
            }

            return range.Length > ImageHead ? "metadata that could not be ruled out" : null;
        }

        if (h.StartsWith("GIF8"u8) || h.StartsWith("BM"u8))
        {
            return null;
        }

        return "an image of a kind the strip cannot look into";
    }

    /// <summary>Reports every track of unknown kind or of data kind (telemetry, timed metadata, private streams) in a format whose strip has no rule for tracks.</summary>
    public static void DataTracks(MediaFile file, StripCategories cats, List<StripLeftover> left)
    {
        foreach (MediaTrack t in file.Tracks)
        {
            if (t.Kind is TrackKind.Data or TrackKind.Unknown)
            {
                left.Add(new StripLeftover(cats, "track", (t.CodecTag ?? t.Codec.Name ?? "data") + " stream", t.Id, ByteRange.None,
                    $"Track {t.Id} holds {(t.Kind == TrackKind.Data ? "timed data" : "a stream of unknown type")} ({t.CodecTag ?? t.Codec.Name ?? "no codec name"}); this format's strip does not look into it."));
            }
        }
    }

    // ISO/IEC 14496-12 §6.2 and the QuickTime File Format: the boxes a movie file is made of. Anything else sitting at the top level, in the movie box or in a
    // track box is a vendor box (a camera's own record, a compressed movie box, a picture) the strip has no rule for.
    private static readonly HashSet<string> KnownTop = ["ftyp", "styp", "moov", "mdat", "free", "skip", "wide", "moof", "mfra", "sidx", "ssix", "prft", "pnot", "meta", "pdin", "emsg", "mdat"];
    private static readonly HashSet<string> KnownMoov = ["mvhd", "trak", "mvex", "udta", "meta", "iods", "free", "skip", "wide", "pssh"];
    private static readonly HashSet<string> KnownTrak = ["tkhd", "mdia", "edts", "tref", "udta", "meta", "free", "skip", "wide", "tapt", "load", "trgr", "senc"];
    private static readonly Guid XmpUuid = new("BE7ACFCB-97A9-42E8-9C71-999491E3AFAC");

    /// <summary>Reports boxes outside the structure of ISO base media (top level, movie box, track boxes) and bytes after the last box.</summary>
    public static void IsoBoxes(IMediaSource source, StripCategories cats, List<StripLeftover> left, Func<BoxHeader, string, bool> handle, CancellationToken ct)
    {
        long end = source.Length, at = 0;
        for (int hops = 0; at < end && hops < 1_000_000; hops++)
        {
            ct.ThrowIfCancellationRequested();
            if (!BoxReader.TryRead(source, at, end, true, null, out BoxHeader box, ct))
            {
                break;
            }

            string type = BoxTypes.ToText(box.Type);
            if (type == "moov")
            {
                WalkIso(source, box, KnownMoov, "moov", cats, left, handle, nested: true, ct);
            }
            else
            {
                CheckIsoBox(source, box, type, KnownTop, "the file", cats, left, handle, ct);
            }

            if (box.End <= at)
            {
                break;
            }

            at = box.End;
        }

        if (at < end && !IsAllZero(source, at, end - at, ct))
        {
            left.Add(new StripLeftover(cats, "iso", "trailer", 0, new ByteRange(at, end - at), $"{end - at} bytes follow the last box and are not inspected."));
        }
    }

    private static void WalkIso(IMediaSource source, BoxHeader parent, HashSet<string> known, string where, StripCategories cats, List<StripLeftover> left, Func<BoxHeader, string, bool> handle, bool nested, CancellationToken ct)
    {
        long at = parent.PayloadOffset;
        for (int hops = 0; at < parent.End && hops < 1_000_000; hops++)
        {
            ct.ThrowIfCancellationRequested();
            if (!BoxReader.TryRead(source, at, parent.End, false, null, out BoxHeader box, ct))
            {
                break;
            }

            string type = BoxTypes.ToText(box.Type);
            if (nested && type == "trak")
            {
                WalkIso(source, box, KnownTrak, "trak", cats, left, handle, nested: false, ct);
            }
            else
            {
                CheckIsoBox(source, box, type, known, "the '" + where + "' box", cats, left, handle, ct);
            }

            if (box.End <= at)
            {
                break;
            }

            at = box.End;
        }
    }

    private static void CheckIsoBox(IMediaSource source, BoxHeader box, string type, HashSet<string> known, string where, StripCategories cats, List<StripLeftover> left, Func<BoxHeader, string, bool> handle, CancellationToken ct)
    {
        string name = type;
        if (type == "uuid" && box.UserType is { } id)
        {
            if (id == XmpUuid)
            {
                return; // the XMP packet is read and edited by its own rule
            }

            if (handle(box, "uuid"))
            {
                return;
            }

            name = "uuid " + id.ToString("D", CultureInfo.InvariantCulture);
        }
        else if (type is "free" or "skip" or "wide")
        {
            // Padding is fine empty; with bytes in it the handler zeroes it (it has no meaning), and without a handler it is reported.
            if (IsAllZero(source, box.PayloadOffset, box.PayloadSize, ct) || handle(box, type))
            {
                return;
            }
        }
        else if (known.Contains(type))
        {
            return;
        }

        left.Add(new StripLeftover(cats, "iso", name, 0, new ByteRange(box.Offset, box.HeaderSize + box.PayloadSize),
            $"The '{name}' box ({box.PayloadSize} bytes) in {where} is a box the strip has no rule for, so it was not inspected or edited."));
    }

    /// <summary>Which flat chunk layout a file uses.</summary>
    public enum ChunkFlavour
    {
        /// <summary>RIFF, RF64 or BW64: four-byte id, four-byte little-endian size, padded to even.</summary>
        Riff,

        /// <summary>AIFF and AIFC: four-byte id, four-byte big-endian size, padded to even.</summary>
        Aiff,

        /// <summary>Core Audio Format: four-byte type, eight-byte big-endian size, no padding.</summary>
        Caf,

        /// <summary>Sony Wave64: 16-byte GUID whose first four bytes are the RIFF-style id, eight-byte little-endian size that includes the 24-byte header, padded to 8.</summary>
        Wave64,
    }

    private static readonly HashSet<string> KnownWave =
        ["fmt ", "data", "fact", "ds64", "bext", "iXML", "axml", "cart", "id3 ", "ID3 ", "cue ", "smpl", "inst", "PEAK", "levl", "plst", "slnt", "wavl"];
    private static readonly HashSet<string> KnownAiff = ["COMM", "SSND", "FVER", "MARK", "INST", "COMT", "NAME", "AUTH", "(c) ", "ANNO", "ID3 ", "id3 ", "CHAN", "basc", "AESD", "MIDI", "SAXL"];
    private static readonly HashSet<string> KnownCaf = ["desc", "data", "pakt", "chan", "info", "kuki", "ovvw", "mark", "regn", "peak", "edct", "strg", "inst", "midi"];
    private static readonly HashSet<string> Padding = ["JUNK", "junk", "PAD ", "FLLR", "free"];

    /// <summary>Reports top-level chunks of RIFF, AIFF, CAF or Wave64 files that are not part of the format's defined set, and bytes after the last chunk.</summary>
    public static void FlatChunks(IMediaSource source, ChunkFlavour flavour, StripCategories cats, List<StripLeftover> left, StripSink sink, CancellationToken ct)
    {
        int headerSize = flavour switch { ChunkFlavour.Caf => 12, ChunkFlavour.Wave64 => 24, _ => 8 };
        // RIFF/FORM: 4 id, 4 size, 4 form type; CAF: 4 'caff', 2 version, 2 flags; Wave64: riff GUID, size, wave GUID.
        long at = flavour switch { ChunkFlavour.Caf => 8, ChunkFlavour.Wave64 => 40, _ => 12 };
        long end = source.Length;
        HashSet<string> known = flavour switch { ChunkFlavour.Aiff => KnownAiff, ChunkFlavour.Caf => KnownCaf, _ => KnownWave };
        byte[] head = new byte[24];
        for (int hops = 0; at + headerSize <= end && hops < 1_000_000; hops++)
        {
            ct.ThrowIfCancellationRequested();
            if (source.Read(at, head.AsSpan(0, headerSize), ct) < headerSize)
            {
                break;
            }

            string id = System.Text.Encoding.Latin1.GetString(head.AsSpan(0, 4));
            if (flavour == ChunkFlavour.Wave64)
            {
                id = id.ToLowerInvariant() is "junk" or "list" ? id.ToLowerInvariant() : id;
            }

            ulong size = flavour switch
            {
                ChunkFlavour.Riff => BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)),
                ChunkFlavour.Aiff => BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4)),
                ChunkFlavour.Caf => BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(4)),
                _ => BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16)) is var w && w >= 24 ? w - 24 : ulong.MaxValue,
            };
            long payload = at + headerSize;
            // A size past the end of the file (RF64 states 0xFFFFFFFF for a chunk whose real size is in ds64; a CAF data chunk may state -1) runs to the end.
            long stop = size > (ulong)(end - payload) ? end : payload + (long)size;
            string name = id;
            bool isKnown = known.Contains(id);
            if (id.Equals("LIST", StringComparison.OrdinalIgnoreCase))
            {
                // A LIST holds INFO text (read as tags) or adtl labels; any other list type is a vendor list.
                byte[] type = new byte[4];
                if (source.Read(payload, type, ct) == 4)
                {
                    string listType = System.Text.Encoding.Latin1.GetString(type);
                    name = "LIST " + listType;
                    isKnown = listType is "INFO" or "adtl";
                }
                else
                {
                    isKnown = true;
                }
            }
            else if (flavour == ChunkFlavour.Wave64 && id is "riff" or "wave")
            {
                isKnown = true;
            }

            if (Padding.Contains(id))
            {
                // Padding has no meaning; bytes in it are zeroed, and the entry says so.
                if (stop > payload && !IsAllZero(source, payload, stop - payload, ct))
                {
                    sink.Zero(cats, "chunk", id, 0, payload, stop - payload);
                }
            }
            else if (!isKnown)
            {
                left.Add(new StripLeftover(cats, "chunk", name, 0, new ByteRange(at, stop - at),
                    $"The '{name}' chunk ({stop - payload} bytes) is a chunk the strip has no rule for, so it was not inspected or edited."));
            }

            if (stop >= end)
            {
                return;
            }

            long align = flavour switch { ChunkFlavour.Caf => 1, ChunkFlavour.Wave64 => 8, _ => 2 };
            at = stop + ((align - (stop % align)) % align);
        }

        if (at < end && !IsAllZero(source, at, end - at, ct))
        {
            left.Add(new StripLeftover(cats, "chunk", "trailer", 0, new ByteRange(at, end - at), $"{end - at} bytes follow the last chunk and are not inspected."));
        }
    }

    /// <summary>
    /// Reports objects of the ASF Header Object (and of its Header Extension) whose GUID is not one the reader knows. The tag-carrying objects are read as tags and
    /// are not listed here; the Data Object and the index objects are structure.
    /// </summary>
    public static void AsfObjects(IMediaSource source, HashSet<Guid> known, StripCategories cats, List<StripLeftover> left, CancellationToken ct)
    {
        byte[] head = new byte[46];
        if (source.Read(0, head.AsSpan(0, 30), ct) < 30)
        {
            return;
        }

        long headerEnd = Math.Min(source.Length, (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16)), (ulong)long.MaxValue));
        WalkAsf(source, 30, headerEnd, known, cats, left, depth: 0, ct);
    }

    private static void WalkAsf(IMediaSource source, long at, long end, HashSet<Guid> known, StripCategories cats, List<StripLeftover> left, int depth, CancellationToken ct)
    {
        byte[] head = new byte[46];
        for (int hops = 0; at + 24 <= end && hops < 100_000; hops++)
        {
            ct.ThrowIfCancellationRequested();
            if (source.Read(at, head.AsSpan(0, 24), ct) < 24)
            {
                return;
            }

            Guid id = new(head.AsSpan(0, 16));
            ulong stated = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16));
            if (stated < 24 || stated > (ulong)(end - at))
            {
                return;
            }

            if (!known.Contains(id) && !(id == Guid.Empty && IsAllZero(source, at + 24, (long)stated - 24, ct)))
            {
                left.Add(new StripLeftover(cats, "asf", "object " + id.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant(), 0, new ByteRange(at, (long)stated),
                    $"An ASF object ({stated} bytes, GUID {id.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant()}) that the strip has no rule for was not inspected or edited."));
            }
            else if (depth == 0 && id == new Guid("5FBF03B5-A92E-11CF-8EE3-00C00C205365") && stated >= 46 && source.Read(at + 24, head.AsSpan(24, 22), ct) == 22)
            {
                // Header Extension Object: 22 bytes of reserved fields and a data size, then the extension's own objects.
                uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(42));
                WalkAsf(source, at + 46, Math.Min(at + (long)stated, at + 46 + dataSize), known, cats, left, depth + 1, ct);
            }

            at += (long)stated;
        }
    }

    /// <summary>True when the range holds only zero bytes (padding); ranges longer than 16 MiB count as holding content past the scan limit.</summary>
    public static bool IsAllZero(IMediaSource source, long offset, long length, CancellationToken ct)
    {
        if (length <= 0)
        {
            return true;
        }

        byte[] buffer = new byte[(int)Math.Min(length, 64 * 1024)];
        long done = 0;
        long cap = Math.Min(length, MaxZeroScan);
        while (done < cap)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, cap - done);
            int got = source.Read(offset + done, buffer.AsSpan(0, want), ct);
            if (got <= 0)
            {
                return true;
            }

            if (buffer.AsSpan(0, got).IndexOfAnyExcept((byte)0) >= 0)
            {
                return false;
            }

            done += got;
        }

        return length <= MaxZeroScan;
    }
}
