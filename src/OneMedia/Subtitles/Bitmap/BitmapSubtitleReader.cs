using System.Text;

namespace RecursiveAI.OneMedia.Captions;

/// <summary>
/// Reads bitmap subtitles (Blu-ray PGS, DVD VobSub, DVB subtitles) from sidecar files and container tracks as image cues
/// (<see cref="SubtitleCue.Images"/>: RGBA, position on a stated display size, start and end time).
/// </summary>
/// <remarks>
/// The pictures are decoded exactly as stored: no scaling, no text recognition, no rendering. Palette colours given as YCbCr are
/// converted with ITU-R BT.601 for pictures up to 576 lines tall and BT.709 above, unless <see cref="BitmapSubtitleOptions.Matrix"/> says
/// otherwise. See <c>docs/bitmap-subtitles.md</c>.
/// </remarks>
public static class BitmapSubtitles
{
    /// <summary>The codec names (<see cref="CodecId.Name"/>) of the bitmap subtitle tracks this class decodes.</summary>
    public static IReadOnlyList<string> CodecNames { get; } = ["pgs", "hdmv-pgs", "vobsub", "dvd-subpicture", "dvbsub", "dvb-subtitle"];

    /// <summary>True when <paramref name="track"/> is a bitmap subtitle track this class can decode.</summary>
    /// <param name="track">A track.</param>
    public static bool CanDecode(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Kind == TrackKind.Subtitle && System.Linq.Enumerable.Contains(CodecNames, track.Codec.Name);
    }

    /// <summary>Decodes a PGS <c>.sup</c> file.</summary>
    /// <param name="sup">The file bytes.</param>
    /// <param name="options">Matrix, limits, language; null for the defaults.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <returns>A document of image cues. Its reference size is the video size the first composition states; <see cref="SubtitleDocument.Format"/> is <c>pgs</c>.</returns>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static SubtitleDocument ReadSup(ReadOnlySpan<byte> sup, BitmapSubtitleOptions? options = null, DefectLog? defects = null)
    {
        options ??= new BitmapSubtitleOptions();
        (options.Limits ?? MediaLimits.Default).CheckAllocation(sup.Length, 0);
        IReadOnlyList<SubtitleCue> cues = new PgsDecoder(options, defects).FeedSup(sup);
        return Document("pgs", cues, options.Language);
    }

    /// <summary>Decodes a VobSub pair.</summary>
    /// <param name="idx">The <c>.idx</c> bytes.</param>
    /// <param name="sub">The <c>.sub</c> bytes.</param>
    /// <param name="options">Stream selection, limits, language; null for the defaults.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <returns>A document of image cues; <see cref="SubtitleDocument.Format"/> is <c>vobsub</c>, the reference size is the index's <c>size:</c>.</returns>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static SubtitleDocument ReadVobSub(ReadOnlySpan<byte> idx, ReadOnlySpan<byte> sub, BitmapSubtitleOptions? options = null, DefectLog? defects = null)
    {
        options ??= new BitmapSubtitleOptions();
        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        defects ??= new DefectLog(limits);
        VobSubIndex index = VobSubIndex.Parse(idx, defects, limits);
        IReadOnlyList<SubtitleCue> cues = VobSubReader.Read(idx, sub, options, defects);
        return Document("vobsub", cues, options.Language) with { ReferenceWidth = index.Width, ReferenceHeight = index.Height };
    }

    /// <summary>
    /// Decodes a bitmap subtitle file by its content: a <c>.sup</c> (starts with <c>PG</c>), an <c>.idx</c> (with the <c>.sub</c> beside
    /// it), or a bare <c>.sub</c> program stream (decoded without an index).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <exception cref="UnsupportedMediaException">The file is none of these.</exception>
    /// <exception cref="FileNotFoundException">An index without its <c>.sub</c> file.</exception>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static SubtitleDocument ReadFile(string path, BitmapSubtitleOptions? options = null, DefectLog? defects = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        MediaLimits limits = options?.Limits ?? MediaLimits.Default;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] data = ReadAll(path, limits);
        if (data.Length >= 2 && data[0] == (byte)'P' && data[1] == (byte)'G')
        {
            return ReadSup(data, options, defects);
        }

        if (ext == ".idx" || LooksLikeIndex(data))
        {
            string subPath = Path.ChangeExtension(path, ".sub");
            return ReadVobSub(data, ReadAll(subPath, limits), options, defects);
        }

        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0xBA)
        {
            string idxPath = Path.ChangeExtension(path, ".idx");
            if (File.Exists(idxPath))
            {
                return ReadVobSub(ReadAll(idxPath, limits), data, options, defects);
            }

            return Document("vobsub", VobSubReader.ReadProgramStream(data, options, defects), options?.Language);
        }

        throw new UnsupportedMediaException("The file is not a PGS .sup, a VobSub .idx/.sub pair or a DVD sub-picture program stream.");
    }

    /// <summary>
    /// Decodes a bitmap subtitle track of an opened file: Matroska <c>S_HDMV/PGS</c>, <c>S_VOBSUB</c> and <c>S_DVBSUB</c>, MPEG-TS PGS (stream
    /// type 0x90) and DVB subtitle streams, and DVD sub-pictures in MPEG program streams. Cues carry the track's language unless
    /// <paramref name="options"/> sets one. A VOB has no palette of its own (the IFO file holds it): pass it in
    /// <see cref="BitmapSubtitleOptions.Palette"/> or the grey ramp is used.
    /// </summary>
    /// <param name="file">The opened file.</param>
    /// <param name="track">One of its tracks; see <see cref="CanDecode"/>.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <param name="cancellationToken">Checked before each packet.</param>
    /// <returns>A document of image cues, in packet order.</returns>
    /// <exception cref="UnsupportedMediaException">The track is not a bitmap subtitle track.</exception>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static SubtitleDocument ReadTrack(MediaFile file, MediaTrack track, BitmapSubtitleOptions? options = null, DefectLog? defects = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!CanDecode(track))
        {
            throw new UnsupportedMediaException($"Track {track.Index} is not a bitmap subtitle track this library decodes.");
        }

        options ??= new BitmapSubtitleOptions();
        options = options with { Language = options.Language ?? track.Language };
        defects ??= new DefectLog(options.Limits ?? MediaLimits.Default);
        var cues = new List<SubtitleCue>();
        string codec = track.Codec.Name;
        int width = 0, height = 0;
        if (track.Video is { } v)
        {
            width = v.Width;
            height = v.Height;
        }

        switch (codec)
        {
            case "pgs":
            case "hdmv-pgs":
                {
                    var decoder = new PgsDecoder(options, defects);
                    foreach (OwnedPacket packet in file.Packets(track))
                    {
                        using (packet)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            cues.AddRange(decoder.Feed(packet.Data.Span, packet.Info.Pts, packet.Info.Duration, packet.Info.SourceRange.Offset));
                        }
                    }

                    cues.AddRange(decoder.Flush());
                    break;
                }

            case "vobsub":
            case "dvd-subpicture":
                {
                    BitmapSubtitleOptions o = options;
                    if (codec == "vobsub" && track.Configuration.Data.Length > 0)
                    {
                        // Matroska keeps the index header (size, palette) in CodecPrivate.
                        VobSubIndex idx = VobSubIndex.Parse(track.Configuration.Data.Span, defects, options.Limits);
                        o = VobSubReader.WithIndex(options, idx, null);
                        width = idx.Width;
                        height = idx.Height;
                    }

                    var decoder = new VobSubDecoder(o, defects);
                    foreach (OwnedPacket packet in file.Packets(track))
                    {
                        using (packet)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            foreach (SubtitleCue cue in decoder.Decode(packet.Data.Span, packet.Info.Pts, 0, packet.Info.SourceRange.Offset))
                            {
                                // A block's duration bounds a picture that no stop command ends.
                                cues.Add(!cue.End.IsKnown && packet.Info.Duration.IsKnown ? cue with { End = BitmapSupport.Plus(cue.Start, packet.Info.Duration) } : cue);
                            }
                        }
                    }

                    break;
                }

            default:
                {
                    var decoder = new DvbSubtitleDecoder(options, defects);
                    foreach (OwnedPacket packet in file.Packets(track))
                    {
                        using (packet)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            cues.AddRange(decoder.Feed(packet.Data.Span, packet.Info.Pts, packet.Info.SourceRange.Offset));
                        }
                    }

                    cues.AddRange(decoder.Flush());
                    break;
                }
        }

        SubtitleDocument doc = Document(codec, cues, options.Language);
        if (cues.Count > 0 && cues[0].Images.Count > 0 && cues[0].Images[0].DisplayWidth > 0)
        {
            width = cues[0].Images[0].DisplayWidth;
            height = cues[0].Images[0].DisplayHeight;
        }

        return doc with { ReferenceWidth = width, ReferenceHeight = height };
    }

    private static SubtitleDocument Document(string format, IReadOnlyList<SubtitleCue> cues, string? language)
    {
        int w = 0, h = 0;
        foreach (SubtitleCue c in cues)
        {
            if (c.Images.Count > 0 && c.Images[0].DisplayWidth > 0)
            {
                w = c.Images[0].DisplayWidth;
                h = c.Images[0].DisplayHeight;
                break;
            }
        }

        return new SubtitleDocument
        {
            Format = format switch { "hdmv-pgs" => "pgs", "dvd-subpicture" => "vobsub", "dvb-subtitle" => "dvbsub", _ => format },
            Language = language,
            Cues = new SubtitleList<SubtitleCue>(cues),
            ReferenceWidth = w,
            ReferenceHeight = h,
        };
    }

    private static bool LooksLikeIndex(byte[] data)
    {
        // The index starts with comment lines and "size:" / "palette:" keys; look at the head only.
        string head = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 512));
        return head.Contains("VobSub index", StringComparison.OrdinalIgnoreCase) || head.Contains("palette:", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadAll(string path, MediaLimits limits)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        limits.CheckAllocation(stream.Length, 0);
        byte[] data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return data;
    }
}
