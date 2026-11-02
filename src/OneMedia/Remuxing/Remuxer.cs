using System.Linq;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>
/// Copies the tracks of a media file into another container without touching the compressed data: remux. Payload bytes are copied; the
/// only changes are the container framing (Annex B to length-prefixed NAL units, ADTS to raw AAC) and the codec configuration records the
/// target stores. The report lists everything that did not carry over.
/// </summary>
public static class Remuxer
{
    private static readonly HashSet<string> s_carriedEntryBoxes = ["colr", "clap", "fiel", "gama", "btrt", "chnl", "srat", "mdcv", "clli"];

    // Boxes of a sample entry that the muxer writes itself from the track's configuration and parameters, so dropping the source's copy loses nothing.
    private static readonly HashSet<string> s_regeneratedEntryBoxes =
        ["avcC", "hvcC", "vvcC", "av1C", "vpcC", "esds", "dOps", "dfLa", "dac3", "dec3", "damr", "pcmC", "pasp", "alac", "wave"];

    /// <summary>Chooses the output container from a file name's extension.</summary>
    /// <param name="path">A path or file name.</param>
    /// <returns>The format: <c>.mp4</c> MP4, <c>.m4a</c>/<c>.m4b</c> M4A, <c>.m4v</c> M4V, <c>.mov</c> QuickTime, <c>.3gp</c>, <c>.3g2</c>, <c>.mkv</c>/<c>.mka</c> Matroska, <c>.webm</c> WebM; null for anything else.</returns>
    public static RemuxFormat? FormatForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => RemuxFormat.Mp4,
            ".m4a" or ".m4b" => RemuxFormat.M4a,
            ".m4v" => RemuxFormat.M4v,
            ".mov" or ".qt" => RemuxFormat.Mov,
            ".3gp" => RemuxFormat.ThreeGp,
            ".3g2" => RemuxFormat.ThreeG2,
            ".mkv" or ".mka" or ".mk3d" => RemuxFormat.Matroska,
            ".webm" => RemuxFormat.WebM,
            _ => null,
        };
    }

    /// <summary>Remuxes an open file into a stream.</summary>
    /// <param name="input">The opened input. It is read from its start; a file that was read before needs a source that can seek.</param>
    /// <param name="output">The destination. Not closed.</param>
    /// <param name="format">The container to write.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the remux; the output is then incomplete.</param>
    /// <returns>What was written and what did not carry over.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="MediaReadStateException">The file was opened from an asynchronous source (use <see cref="RemuxAsync"/>) or cannot be read again.</exception>
    /// <exception cref="UnsupportedMediaException">The target container cannot be written yet.</exception>
    /// <exception cref="MuxRefusedException">No track can be written, or (with <see cref="RemuxOptions.AllowDrop"/> false) one cannot; or packets are out of order.</exception>
    /// <exception cref="MediaFormatException">The input holds data that cannot be repackaged.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static RemuxReport Remux(MediaFile input, Stream output, RemuxFormat format, RemuxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        IDemuxer demuxer = input.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "This file was opened from an asynchronous source; use RemuxAsync.");
        using TextTrackRemux text = TextTrackRemux.Plan(input, format, options);
        options = text.Options;
        PacketFeeds feeds = PacketFeeds.Of(options);
        using RemuxSession session = Begin(input, output, format, options, feeds);
        text.Report(session);
        ApplySelection(demuxer, session, feeds);
        int generation = input.BeginRead();
        try
        {
            while (demuxer.TryReadPacket(out PacketView view))
            {
                input.EnsureCurrentRead(generation);
                if (feeds.Any)
                {
                    feeds.Drain(session, view.Info.Dts.IsKnown ? view.Info.Dts : view.Info.Pts, false, cancellationToken);
                }

                session.WritePacket(view.Info, view.Data, demuxer is Matroska.MatroskaDemuxer matroska ? matroska.LastBlockAdditional : default, cancellationToken);
                if (session.TakeWantedChange())
                {
                    ApplySelection(demuxer, session, feeds);
                }
            }

            feeds.Drain(session, MediaTime.Unknown, true, cancellationToken);
            return session.Finish(cancellationToken) with { InputDefects = input.Defects.Count };
        }
        finally
        {
            foreach (MediaTrack t in demuxer.Tracks)
            {
                demuxer.SetTrackEnabled(t.Index, true);
            }
        }
    }

    /// <summary>Remuxes an open file into a stream, reading from either kind of source.</summary>
    /// <param name="input">The opened input.</param>
    /// <param name="output">The destination. Not closed.</param>
    /// <param name="format">The container to write.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the remux; the output is then incomplete.</param>
    /// <returns>What was written and what did not carry over.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="MediaReadStateException">The file was read before and its source cannot seek.</exception>
    /// <exception cref="UnsupportedMediaException">The target container cannot be written yet.</exception>
    /// <exception cref="MuxRefusedException">As for <see cref="Remux(MediaFile, Stream, RemuxFormat, RemuxOptions?, CancellationToken)"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async ValueTask<RemuxReport> RemuxAsync(MediaFile input, Stream output, RemuxFormat format, RemuxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (input.AsyncDemuxer is not { } demuxer)
        {
            return Remux(input, output, format, options, cancellationToken);
        }

        using TextTrackRemux text = await TextTrackRemux.PlanAsync(input, format, options, cancellationToken).ConfigureAwait(false);
        options = text.Options;
        PacketFeeds feeds = PacketFeeds.Of(options);
        using RemuxSession session = Begin(input, output, format, options, feeds);
        text.Report(session);
        ApplySelection(demuxer, session, feeds);
        int generation = await input.BeginReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (await demuxer.ReadPacketAsync(cancellationToken).ConfigureAwait(false))
            {
                input.EnsureCurrentRead(generation);
                PacketView view = demuxer.Current;
                if (feeds.Any)
                {
                    feeds.Drain(session, view.Info.Dts.IsKnown ? view.Info.Dts : view.Info.Pts, false, cancellationToken);
                }

                session.WritePacket(view.Info, view.Data, demuxer is Matroska.MatroskaAsyncDemuxer matroska ? matroska.LastBlockAdditional : default, cancellationToken);
                if (session.TakeWantedChange())
                {
                    ApplySelection(demuxer, session, feeds);
                }
            }

            feeds.Drain(session, MediaTime.Unknown, true, cancellationToken);
            return session.Finish(cancellationToken) with { InputDefects = input.Defects.Count };
        }
        finally
        {
            foreach (MediaTrack t in demuxer.Tracks)
            {
                demuxer.SetTrackEnabled(t.Index, true);
            }
        }
    }

    /// <summary>Remuxes a file into a new file whose container is chosen by the output's extension (see <see cref="FormatForPath"/>).</summary>
    /// <param name="inputPath">The input; its format is recognised by content.</param>
    /// <param name="outputPath">The output; refused if it exists unless <see cref="RemuxOptions.Overwrite"/> is set. A failed or cancelled remux deletes it.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the remux.</param>
    /// <returns>What was written and what did not carry over.</returns>
    /// <exception cref="ArgumentException">The extension names no container; pass a <see cref="RemuxFormat"/>.</exception>
    /// <exception cref="IOException">The output exists and may not be replaced, or a file cannot be read or written.</exception>
    /// <exception cref="UnsupportedMediaException">The input is not recognised, or the target cannot be written yet.</exception>
    /// <exception cref="MuxRefusedException">No track can be written, or (with <see cref="RemuxOptions.AllowDrop"/> false) one cannot.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static RemuxReport Remux(string inputPath, string outputPath, RemuxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        RemuxFormat format = FormatForPath(outputPath) ?? throw new ArgumentException($"The extension of '{outputPath}' names no container; pass a RemuxFormat.", nameof(outputPath));
        return Remux(inputPath, outputPath, format, options, cancellationToken);
    }

    /// <summary>Remuxes a file into a new file.</summary>
    /// <param name="inputPath">The input; its format is recognised by content.</param>
    /// <param name="outputPath">The output. Created new; an existing file is refused unless <see cref="RemuxOptions.Overwrite"/> is set. A failed or cancelled remux deletes it.</param>
    /// <param name="format">The container to write.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the remux.</param>
    /// <returns>What was written and what did not carry over.</returns>
    /// <exception cref="IOException">The output exists and may not be replaced, or a file cannot be read or written.</exception>
    /// <exception cref="UnsupportedMediaException">The input is not recognised, or the target cannot be written yet.</exception>
    /// <exception cref="MuxRefusedException">See <see cref="Remux(MediaFile, Stream, RemuxFormat, RemuxOptions?, CancellationToken)"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static RemuxReport Remux(string inputPath, string outputPath, RemuxFormat format, RemuxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        using MediaFile input = Media.Open(inputPath);
        FileStream output = Create(outputPath, options?.Overwrite ?? false);
        bool done = false;
        try
        {
            RemuxReport report;
            using (output)
            {
                report = Remux(input, output, format, options, cancellationToken);
            }

            if (options?.CarryTags ?? true)
            {
                report = CarryTags(input, outputPath, report, cancellationToken);
            }

            done = true;
            return report;
        }
        finally
        {
            if (!done)
            {
                output.Dispose();
                TryDelete(outputPath);
            }
        }
    }

    /// <summary>
    /// Moves the movie box of a progressive MP4/MOV/3GP file in front of its media data, so playback can start before the whole file has arrived.
    /// Every other box and every byte of media data is copied by range; only the chunk offset tables inside the movie box change.
    /// </summary>
    /// <remarks>
    /// This edits the file rather than remuxing it: the sample tables, edit lists, tags, unknown boxes and sample entries come through exactly
    /// as they were, and media data is never re-chunked. A file whose movie box is already in front is copied unchanged
    /// (<see cref="FaststartReport.Relocated"/> false). Fragmented files are refused: they have no single movie box to move.
    /// </remarks>
    /// <param name="input">The source; random access.</param>
    /// <param name="output">The destination; written front to back. Not closed.</param>
    /// <param name="limits">Ceilings for the movie box held in memory; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>What was moved.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="UnsupportedMediaException">The input is not an ISO base media file, is fragmented, has a compressed movie box (<c>cmov</c>) or no movie box.</exception>
    /// <exception cref="MediaLimitExceededException">The movie box is larger than <see cref="MediaLimits.MaxAllocationBytes"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static FaststartReport Faststart(IMediaSource input, Stream output, MediaLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        return FaststartWriter.Write(input, output, limits ?? MediaLimits.Default, cancellationToken);
    }

    /// <summary>Moves the movie box of a progressive MP4/MOV/3GP file to the front, into a new file. See <see cref="Faststart(IMediaSource, Stream, MediaLimits?, CancellationToken)"/>.</summary>
    /// <param name="inputPath">The input.</param>
    /// <param name="outputPath">The output. Created new; refused if it exists unless <paramref name="overwrite"/>. Deleted on failure.</param>
    /// <param name="overwrite">Replace an existing output file.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>What was moved.</returns>
    /// <exception cref="IOException">The output exists and may not be replaced, or a file cannot be read or written.</exception>
    /// <exception cref="UnsupportedMediaException">See <see cref="Faststart(IMediaSource, Stream, MediaLimits?, CancellationToken)"/>.</exception>
    public static FaststartReport Faststart(string inputPath, string outputPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        using FileMediaSource source = FileMediaSource.Open(inputPath);
        using FileStream output = Create(outputPath, overwrite);
        bool done = false;
        try
        {
            FaststartReport report = Faststart(source, output, null, cancellationToken);
            done = true;
            return report;
        }
        finally
        {
            if (!done)
            {
                output.Dispose();
                TryDelete(outputPath);
            }
        }
    }

    private static FileStream Create(string path, bool overwrite) =>
        new(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.None);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The partial output is left behind only when the file system refuses; the original failure is the one to report.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ApplySelection(IMediaContainer container, RemuxSession session, PacketFeeds feeds)
    {
        IReadOnlyList<bool> wanted = session.Wanted;
        for (int i = 0; i < wanted.Count; i++)
        {
            container.SetTrackEnabled(i, wanted[i] && !feeds.Replaces(i));
        }
    }

    private static RemuxSession Begin(MediaFile input, Stream output, RemuxFormat format, RemuxOptions? options, PacketFeeds feeds)
    {
        RemuxSession session = new(feeds.Apply(input.Tracks), output, format, options);
        try
        {
            if (input.Demuxer is IsoBmffDemuxer iso)
            {
                session.IsoOptions = t => feeds.Replaces(t.Index) ? null : IsoTrackOptionsFor(iso, t, session);
            }
            else
            {
                object? demuxer = (object?)input.Demuxer ?? input.AsyncDemuxer;
                session.IsoOptions = t => feeds.Replaces(t.Index) ? null : PcmOptionsFor(demuxer, t);
            }

            Inspect(input, session);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // The PCM layout (integer or float, width, byte order) is not in the neutral track; the audio-file readers and FLV's codec tag state it.
    internal static IsoTrackOptions? PcmOptionsFor(object? demuxer, MediaTrack t)
    {
        if (t.Codec != CodecId.Pcm || t.Kind != TrackKind.Audio)
        {
            return null;
        }

        PcmSampleFormat? pcm = demuxer switch
        {
            Riff.WavDemuxer w => w.Pcm,
            Aiff.AiffDemuxer a => a.Pcm,
            Caf.CafDemuxer c => c.Pcm,
            _ => null,
        };
        pcm ??= t.CodecTag switch
        {
            "pcm_s16be" => new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, true),
            "pcm_s16le" => new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, false),
            "pcm_u8" => new PcmSampleFormat(PcmEncoding.UnsignedInteger, 8, 8, false),
            _ => null,
        };
        return pcm is { } p ? new IsoTrackOptions { Pcm = p } : null;
    }

    /// <summary>Sample entry boxes of an ISO input track that the ISO muxer does not write itself and that can be passed through (<c>colr clap fiel gama btrt chnl srat mdcv clli</c>); adds a loss to <paramref name="session"/> for the ones that cannot, and for sample descriptions after the first.</summary>
    /// <param name="iso">The open ISO input.</param>
    /// <param name="t">One of its tracks.</param>
    /// <param name="session">Receives the losses.</param>
    /// <returns>Options to give the ISO muxer, or null when there is nothing to pass.</returns>
    public static IsoTrackOptions? IsoTrackOptionsFor(IsoBmffDemuxer iso, MediaTrack t, RemuxSession session)
    {
        if ((uint)t.Index >= (uint)iso.Movie.Tracks.Count)
        {
            return null;
        }

        IsoTrack track = iso.Movie.Tracks[t.Index];
        if (track.SampleEntries.Count == 0)
        {
            return null;
        }

        if (track.SampleEntries.Count > 1)
        {
            session.AddLoss(new RemuxLoss(LossKind.CodecField, LossReason.NotRepresentable, t.Index, "stsd",
                $"The track has {track.SampleEntries.Count} sample descriptions; only the first is written."));
        }

        List<ReadOnlyMemory<byte>> boxes = [];
        List<string> dropped = [];
        foreach (ExtensionBox e in track.SampleEntries[0].Extensions)
        {
            if (s_carriedEntryBoxes.Contains(e.TypeName))
            {
                boxes.Add(BoxBuilder.Create(e.Type, e.Payload.Span));
            }
            else if (!s_regeneratedEntryBoxes.Contains(e.TypeName))
            {
                dropped.Add(e.TypeName);
            }
        }

        if (dropped.Count > 0)
        {
            session.AddLoss(new RemuxLoss(LossKind.CodecField, LossReason.NotWritten, t.Index, string.Join(' ', dropped), "Sample entry boxes the output does not write."));
        }

        return boxes.Count == 0 ? null : new IsoTrackOptions { SampleEntryBoxes = boxes };
    }

    // Writes the input's common-view tags and pictures into the finished output with the tag writer, and replaces the muxer-level "not written"
    // tag and picture losses with what the writer reports. Tags with no common field have no counterpart to carry and stay losses.
    private static RemuxReport CarryTags(MediaFile input, string outputPath, RemuxReport report, CancellationToken cancellationToken)
    {
        List<RemuxLoss> losses = [.. report.Losses.Where(l => l.Kind is not (LossKind.Tag or LossKind.Picture) || l.Reason != LossReason.NotWritten)];
        try
        {
            Tags.CommonTags tags = input.Metadata.Tags;
            Tags.TagEdits edits = new();
            HashSet<NativeTag> mapped = [];
            foreach (Tags.CommonField field in Enum.GetValues<Tags.CommonField>())
            {
                IReadOnlyList<Tags.CommonValue> values = tags.Values(field);
                if (values.Count == 0)
                {
                    continue;
                }

                edits.Set(field, values.Select(v => v.Text));
                foreach (Tags.CommonValue v in values)
                {
                    mapped.Add(v.Source);
                }
            }

            int pictures = 0;
            foreach (Tags.TagPicture p in tags.Pictures)
            {
                edits.SetPicture(p.ReadData(), p.MimeType, p.Type);
                mapped.Add(p.Source);
                pictures++;
            }

            if (!edits.IsEmpty)
            {
                Tags.TagWriteReport written = Tags.TagWriter.Save(outputPath, edits, null, cancellationToken);
                foreach (Tags.SystemWriteReport s in written.Systems)
                {
                    foreach ((Tags.CommonField field, string reason) in s.NotRepresentable)
                    {
                        losses.Add(new RemuxLoss(LossKind.Tag, LossReason.NotRepresentable, -1, $"{s.System}:{field}", reason));
                    }
                }

                foreach (string note in written.Notes)
                {
                    losses.Add(new RemuxLoss(LossKind.Tag, LossReason.NotRepresentable, -1, "tags", note));
                }

                if (written.Systems.Count == 0)
                {
                    losses.Add(new RemuxLoss(LossKind.Tag, LossReason.NotWritten, -1, "tags", "The output has no tag system the common fields could be written to."));
                }
            }

            foreach (IGrouping<string, NativeTag> g in input.Metadata.NativeTags.Where(t => !mapped.Contains(t)).GroupBy(t => t.System))
            {
                string keys = string.Join(", ", g.Select(t => t.Key).Distinct().Take(8));
                losses.Add(new RemuxLoss(LossKind.Tag, LossReason.NotRepresentable, -1, g.Key, $"{g.Count()} native tags with no common field: {keys}{(g.Select(t => t.Key).Distinct().Count() > 8 ? ", …" : string.Empty)}."));
            }

            report = report with { Changes = [.. report.Changes, $"tags carried through the common view ({edits.Edits.Count} edits, {pictures} pictures)"] };
        }
        catch (Exception e) when (e is MediaException or MediaReadStateException or InvalidOperationException or NotSupportedException)
        {
            losses.Add(new RemuxLoss(LossKind.Tag, LossReason.NotWritten, -1, "tags", "The tags could not be carried: " + e.Message));
        }

        return report with { Losses = losses };
    }

    /// <summary>Lists the tags, pictures and chapters of the input; the muxers do not write any of them yet, so each is a loss.</summary>
    private static void Inspect(MediaFile input, RemuxSession session)
    {
        try
        {
            Metadata.MediaMetadata m = input.Metadata;
            foreach (IGrouping<string, NativeTag> g in m.NativeTags.GroupBy(t => t.System))
            {
                if (g.Key == TagSystems.Xmp && !g.Any())
                {
                    continue;
                }

                string keys = string.Join(", ", g.Select(t => t.Key).Distinct().Take(8));
                session.AddLoss(new RemuxLoss(LossKind.Tag, LossReason.NotWritten, -1, g.Key, $"{g.Count()} tags: {keys}{(g.Select(t => t.Key).Distinct().Count() > 8 ? ", …" : string.Empty)}."));
            }

            int pictures = m.Tags.Pictures.Count;
            if (pictures > 0)
            {
                session.AddLoss(new RemuxLoss(LossKind.Picture, LossReason.NotWritten, -1, "pictures", $"{pictures} embedded pictures."));
            }

            if (m.Chapters.Count > 0)
            {
                session.AddLoss(new RemuxLoss(LossKind.Chapter, LossReason.NotWritten, -1, "chapters", $"{m.Chapters.Count} chapters."));
            }

            // In-band event messages (emsg) sit between the segments, not in a track, so no track loss would name them. Timecode tracks and
            // transport stream event PIDs are tracks and are reported with the other tracks.
            foreach (TimedEvents.EventChannel c in input.Events)
            {
                if (c.Track is null)
                {
                    session.AddLoss(new RemuxLoss(LossKind.Structure, LossReason.NotWritten, -1, "emsg", $"{c.Events.Count} event messages of scheme {c.Scheme} (value \"{c.Value}\"); the muxers do not write emsg boxes."));
                }
            }
        }
        catch (Exception e) when (e is MediaException or MediaReadStateException or InvalidOperationException)
        {
            session.AddLoss(new RemuxLoss(LossKind.Tag, LossReason.NotInspected, -1, "metadata", "Tags, pictures and chapters could not be read from the input: " + e.Message));
        }
    }
}
