using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.ITunes;
using RecursiveAI.OneMedia.Tags.QuickTime;
using RecursiveAI.OneMedia.Tags.ThreeGpp;
using RecursiveAI.OneMedia.Tags.Id3;
using RecursiveAI.OneMedia.Tags.Xmp;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Gathers the tag containers of an ISO BMFF / QuickTime file: <c>udta</c> and <c>meta</c> at movie and track level, file-level <c>meta</c>, and XMP
/// packets in <c>uuid</c> and <c>XMP_</c> boxes. Only box headers and the tag boxes themselves are read; sample tables and media data are
/// never touched.
/// </summary>
internal sealed class IsoMetadataReader
{
    private const int MaxLeadingBoxes = 64;

    private readonly IMediaSource _source;
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly List<ITagContainer> _containers = [];
    private readonly List<Chapter> _chapters = [];
    private readonly FactsBuilder _facts;
    private bool _movieDone;

    private IsoMetadataReader(IMediaSource source, MediaLimits limits, DefectLog defects)
    {
        _source = source;
        _limits = limits;
        _defects = defects;
        _facts = new FactsBuilder(defects);
    }

    /// <param name="source">The file (or, for ranged and forward-only sources, the movie box held in memory).</param>
    /// <param name="scanFile">True to also look at the top-level boxes before and after the movie box (file-level <c>meta</c>, XMP <c>uuid</c>); false when the source holds the movie box only.</param>
    /// <param name="movie">The parsed movie.</param>
    /// <param name="tracks">The neutral tracks (frame sizes).</param>
    /// <param name="duration">The container duration.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">The container's defect log; tag defects are added to it.</param>
    /// <param name="cancellationToken">Observed between top-level boxes.</param>
    public static MediaMetadata Read(IMediaSource source, bool scanFile, IsoMovie movie, IReadOnlyList<MediaTrack> tracks, MediaTime duration, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        IsoMetadataReader r = new(source, limits, defects);
        DefectLog structural = new(limits);
        ReadAheadSource window = new(source);
        BoxTree tree = new(window, limits, structural);
        try
        {
            if (scanFile)
            {
                r.ScanTopLevel(window, tree, structural, cancellationToken);
            }

            if (!r._movieDone && BoxReader.TryRead(window, movie.Range.Offset, window.Length, true, structural, out BoxHeader h, cancellationToken) && h.Type == BoxTypes.Moov)
            {
                r.ReadMovieBox(tree.CreateNode(h));
            }
        }
        catch (MediaLimitExceededException e)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
        }

        // Box-structure problems were reported when the movie opened; add only those found now in boxes the open did not walk.
        foreach (MediaDefect d in structural)
        {
            bool known = false;
            foreach (MediaDefect existing in defects)
            {
                if (existing == d)
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                defects.Add(d);
            }
        }

        r.AddFacts(movie, tracks, duration);
        return new MediaMetadata(r._containers, r._chapters, r._facts.Build(), defects);
    }

    private void ScanTopLevel(ReadAheadSource window, BoxTree tree, DefectLog structural, CancellationToken ct)
    {
        long length = window.Length;
        long pos = 0;
        for (int i = 0; i < MaxLeadingBoxes && pos < length && BoxReader.TryRead(window, pos, length, true, structural, out BoxHeader h, ct); i++)
        {
            ct.ThrowIfCancellationRequested();
            if (h.Type == BoxTypes.Moof)
            {
                break; // fragments follow; the box list after this is the fragment stream
            }

            if (h.Type == BoxTypes.Moov && !_movieDone)
            {
                ReadMovieBox(tree.CreateNode(h));
            }
            else if (h.Type == BoxTypes.Meta)
            {
                Guard(() => ReadMeta(tree.CreateNode(h), 0));
            }
            else if (h.Type == BoxTypes.Uuid && h.UserType == XmpNamespaces.BoxUserType)
            {
                Guard(() => AddXmp(tree.CreateNode(h), 0));
            }

            pos = h.End;
        }
    }

    private void ReadMovieBox(BoxNode moov)
    {
        _movieDone = true;
        foreach (BoxNode child in moov.EnumerateChildren())
        {
            switch (child.Type)
            {
                case BoxTypes.Udta:
                    Guard(() => ReadUserData(child, 0));
                    break;
                case BoxTypes.Meta:
                    Guard(() => ReadMeta(child, 0));
                    break;
                case BoxTypes.Uuid when child.Header.UserType == XmpNamespaces.BoxUserType:
                    Guard(() => AddXmp(child, 0));
                    break;
                case BoxTypes.Trak:
                    Guard(() => ReadTrack(child));
                    break;
            }
        }
    }

    private void ReadTrack(BoxNode trak)
    {
        long id = 0;
        foreach (BoxNode child in trak.EnumerateChildren())
        {
            if (child.Type == BoxTypes.Tkhd)
            {
                byte[] p = child.ReadPayload(24);
                if (p.Length >= 24)
                {
                    id = p[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20)) : BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12));
                }
            }
            else if (child.Type == BoxTypes.Udta)
            {
                ReadUserData(child, id);
            }
            else if (child.Type == BoxTypes.Meta)
            {
                ReadMeta(child, id);
            }
            else if (child.Type == BoxTypes.Uuid && child.Header.UserType == XmpNamespaces.BoxUserType)
            {
                AddXmp(child, id);
            }
        }
    }

    private void ReadUserData(BoxNode udta, long trackId)
    {
        _containers.Add(QuickTimeUserData.Read(udta, _source, _limits, _defects, trackId));
        ThreeGppAssets assets = ThreeGppAssets.Read(udta, _source, _limits, _defects, trackId);
        if (assets.Tags.Count > 0)
        {
            _containers.Add(assets);
        }

        foreach (BoxNode atom in udta.EnumerateChildren())
        {
            if (atom.TypeName == "XMP_")
            {
                AddXmp(atom, trackId);
            }
            else if (atom.Type == BoxTypes.Uuid && atom.Header.UserType == XmpNamespaces.BoxUserType)
            {
                AddXmp(atom, trackId);
            }
            else if (atom.TypeName is "QVMI" or "MVTG" or "RMKN" or "SIGM")
            {
                ReadExifAtom(atom);
            }
            else if (atom.TypeName == "chpl" && trackId == 0)
            {
                ReadChapterList(atom);
            }
            else if (atom.Type == BoxTypes.Meta)
            {
                ReadMeta(atom, trackId);
            }
        }
    }

    private void ReadMeta(BoxNode meta, long trackId)
    {
        string handler = string.Empty;
        BoxNode? hdlr = meta.Find(BoxTypes.Hdlr);
        if (hdlr is not null)
        {
            byte[] p = hdlr.ReadPayload(12);
            if (p.Length >= 12)
            {
                handler = BoxReader.Latin1(p.AsSpan(8, 4));
            }
        }

        if (handler == "mdta" || meta.Find(BoxTypes.Make("keys")) is not null)
        {
            _containers.Add(QuickTimeKeys.Read(meta, _source, _limits, _defects, trackId));
        }
        else if (meta.Find(BoxTypes.Ilst) is { } ilst)
        {
            _containers.Add(ItunesItemList.Read(ilst, _source, _limits, _defects, trackId));
        }

        // ID3v2 in ISO files (the ID3 registration for MP4): a meta box with handler 'ID32' holds ID32 boxes, each a full box (4 bytes) plus a
        // packed language (2 bytes) before the ID3v2 tag. A box that does not hold a tag is left alone.
        foreach (BoxNode box in meta.Children)
        {
            if (box.Type == Id32Type && box.PayloadSize >= Id32Prefix + Id3Header.HeaderSize)
            {
                if (Id3Tag.TryRead(_source, box.Header.PayloadOffset + Id32Prefix, out Id3Tag? id3, _limits, _defects) && id3 is not null)
                {
                    _containers.Add(id3);
                }
                else
                {
                    _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, box.Header.PayloadOffset + Id32Prefix, "An ID32 box does not hold an ID3v2 tag.", "tags.id32-not-id3");
                }
            }
        }
    }

    private static readonly uint Id32Type = BoxTypes.Make("ID32");
    private const int Id32Prefix = 6;

    // Camera makers store a TIFF/Exif block in some user-data atoms (listed as Exif-structured in ExifTool's QuickTime tag documentation: Casio QVMI,
    // FujiFilm MVTG, Ricoh RMKN, Sigma SIGM). A few bytes of wrapper may precede the TIFF header; the reader looks for it. An atom that holds no TIFF
    // header is left alone.
    private void ReadExifAtom(BoxNode atom)
    {
        if (atom.PayloadSize > _limits.MaxTagBytes)
        {
            _defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, atom.Offset, "A user-data Exif atom is larger than the tag limit and is not read.", "exif.limit");
            return;
        }

        byte[] payload = atom.ReadPayload(_limits.MaxTagBytes);
        if (Exif.ExifBlock.TryParse(payload, atom.Header.PayloadOffset, _limits, _defects, out Exif.ExifBlock block))
        {
            _facts.AddEvidence(block.ToEvidence("exif:" + atom.TypeName));
        }
    }

    private void AddXmp(BoxNode box, long trackId) =>
        _containers.Add(XmpPacket.Read(_source, new ByteRange(box.Offset, box.Header.HeaderSize + box.PayloadSize), new ByteRange(box.Header.PayloadOffset, box.PayloadSize), _limits, _defects, trackId));

    // Nero chapter list: version, flags, (version 1: four reserved bytes,) a one-byte count, then per chapter a 64-bit start in 100 ns units,
    // a one-byte title length and the UTF-8 title. Observed in files written by common muxers; not in a published specification.
    private void ReadChapterList(BoxNode chpl)
    {
        if (chpl.PayloadSize > _limits.MaxTagBytes)
        {
            _defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, chpl.Offset, "The chapter list is larger than the tag limit and is not read.", "tags.value-too-large");
            return;
        }

        byte[] p = chpl.ReadPayload(_limits.MaxTagBytes);
        int pos = 4;
        if (p.Length > 0 && p[0] == 1)
        {
            pos += 4;
        }

        if (pos >= p.Length)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, chpl.Offset, "The chapter list is shorter than its header.", "tags.chpl-short");
            return;
        }

        int count = p[pos++];
        ByteRange range = new(chpl.Offset, chpl.Header.HeaderSize + chpl.PayloadSize);
        for (int i = 0; i < count; i++)
        {
            if (pos + 9 > p.Length || pos + 9 + p[pos + 8] > p.Length)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, chpl.Header.PayloadOffset + pos, $"The chapter list declares {count} chapters but ends after {i}.", "tags.chpl-short");
                return;
            }

            ulong start = BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(pos));
            int len = p[pos + 8];
            string title = TextDecoder.Decode(p.AsSpan(pos + 9, len), TagTextEncoding.Utf8, chpl.Header.PayloadOffset + pos + 9, _defects);
            _chapters.Add(new Chapter(start > long.MaxValue ? MediaTime.Unknown : new MediaTime((long)start, 10_000_000), title, range));
            pos += 9 + len;
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (MediaLimitExceededException e)
        {
            _defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
        }
    }

    private void AddFacts(IsoMovie movie, IReadOnlyList<MediaTrack> tracks, MediaTime duration)
    {
        foreach (ITagContainer c in _containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                _facts.AddNative(t);
            }

            if (c is ThreeGppAssets { Location: { } loc })
            {
                _facts.AddLocationBox(loc);
            }
            else if (c is XmpPacket x)
            {
                _facts.AddXmp(x);
            }
        }

        if (duration.IsKnown)
        {
            _facts.Duration.Add(0, new FactCandidate<MediaTime>(duration, "container", duration.ToString(), "longest track, edit lists applied by the demuxer"));
        }

        if (movie.Header.CreationTime is { } created)
        {
            _facts.CaptureTime.Add(FactsBuilder.RankMovieHeader, new FactCandidate<MediaDateTime>(
                new MediaDateTime(created.UtcDateTime, null, TimeZoneCertainty.Utc), "mvhd", movie.Header.CreationSeconds.ToString(CultureInfo.InvariantCulture),
                "seconds since 1904-01-01 UTC; some devices store local time here", TimeZoneCertainty.Utc, movie.Header.CreationRange));
        }

        IsoTrack? video = null;
        foreach (IsoTrack t in movie.Tracks)
        {
            if (t.HandlerType == "vide" && (video is null || (!video.Header.IsEnabled && t.Header.IsEnabled)))
            {
                video = t;
            }
        }

        if (video is not null)
        {
            AddVideoFacts(video);
        }
    }

    private void AddVideoFacts(IsoTrack video)
    {
        IReadOnlyList<int> m = video.Header.Matrix;
        VideoOrientation? o = m.Count >= 9 ? OrientationFromMatrix(m[0], m[1], m[3], m[4]) : null;
        if (o is { } orientation)
        {
            _facts.Orientation.Add(0, new FactCandidate<VideoOrientation>(orientation, "tkhd.matrix", $"[{m[0]:X8} {m[1]:X8} {m[3]:X8} {m[4]:X8}]",
                "unit matrix entries a, b, c, d of the track header: a rotation by a multiple of 90 degrees, possibly mirrored"));
        }

        if (video.SampleEntries.Count > 0 && video.SampleEntries[0].Width > 0)
        {
            int w = (int)(video.Header.Width >> 16);
            int h = (int)(video.Header.Height >> 16);
            if (o is { RotationDegrees: 90 or 270 })
            {
                (w, h) = (h, w);
            }

            SampleEntry e = video.SampleEntries[0];
            _facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(e.Width, e.Height, w, h), "stsd+tkhd", $"{e.Width}x{e.Height} coded, {w}x{h} display",
                "sample description size; track header size (16.16) swapped for a quarter-turn rotation"));
        }
    }

    /// <summary>
    /// ISO/IEC 14496-12 §8.3.2: a point (x, y) maps to (a·x + c·y, b·x + d·y). A clockwise quarter turn is (a, b, c, d) = (0, 1, −1, 0) and a
    /// mirror in x negates the first column, so the eight orientations are the eight matrices with entries of 0 and ±1.
    /// </summary>
    internal static VideoOrientation? OrientationFromMatrix(int a, int b, int c, int d)
    {
        const int one = 0x10000;
        return (a, b, c, d) switch
        {
            (one, 0, 0, one) => new VideoOrientation(0, false),
            (0, one, -one, 0) => new VideoOrientation(90, false),
            (-one, 0, 0, -one) => new VideoOrientation(180, false),
            (0, -one, one, 0) => new VideoOrientation(270, false),
            (-one, 0, 0, one) => new VideoOrientation(0, true),
            (0, one, one, 0) => new VideoOrientation(90, true),
            (one, 0, 0, -one) => new VideoOrientation(180, true),
            (0, -one, -one, 0) => new VideoOrientation(270, true),
            _ => null,
        };
    }
}
