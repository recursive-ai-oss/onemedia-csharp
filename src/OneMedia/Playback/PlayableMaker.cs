using System.Linq;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Playback;

/// <summary>How a <see cref="MakePlayableReport"/> ended.</summary>
public enum MakePlayableStatus
{
    /// <summary>The file already plays as a downloaded file in the profile. It was copied (or its movie box moved to the front, see the actions); nothing was re-muxed.</summary>
    AlreadyPlayable = 0,

    /// <summary>The output was written and plays in the profile (the report's <c>After</c> verdict says so).</summary>
    Made,

    /// <summary>Every blocker can be removed, but one needs an audio transcode and no <see cref="IAudioTranscoder"/> was given. Nothing was written.</summary>
    NeedsAudioTranscode,

    /// <summary>A blocker cannot be removed without re-encoding video (or at all). Nothing was written; <see cref="MakePlayableReport.Remaining"/> lists the reasons with their codes.</summary>
    Refused,

    /// <summary>The conversion ran but the output still does not play in the profile, or the muxers could not carry the tracks. Nothing was kept.</summary>
    Failed,
}

/// <summary>A kind of step <see cref="PlayableMaker"/> took.</summary>
public enum MakePlayableActionKind
{
    /// <summary>The file was copied byte for byte: it already plays.</summary>
    Copy = 0,

    /// <summary>The movie box was moved in front of the media data, without remuxing (<see cref="Remuxer.Faststart(string, string, bool, CancellationToken)"/>).</summary>
    Faststart,

    /// <summary>The tracks were copied into another container (or the same container, rewritten); packets are the input's own.</summary>
    Remux,

    /// <summary>An input track was left out of the output.</summary>
    DropTrack,

    /// <summary>An audio track was decoded and encoded again.</summary>
    TranscodeAudio,
}

/// <summary>One step taken, in order.</summary>
/// <param name="Kind">The step.</param>
/// <param name="Track">The input track concerned, or -1.</param>
/// <param name="Detail">A sentence for a person.</param>
public sealed record MakePlayableAction(MakePlayableActionKind Kind, int Track, string Detail);

/// <summary>What <see cref="PlayableMaker.MakePlayable(string, string, BrowserProfile, MakePlayableOptions?, CancellationToken)"/> decided and did.</summary>
public sealed record MakePlayableReport
{
    /// <summary>The <see cref="BrowserProfile.Id"/> targeted.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The input judged against the profile. Playing as a downloaded file (<see cref="PlaybackVerdict.Progressive"/>) is the goal; MediaSource playback needs fragmented output, which is packaging.</summary>
    public required PlaybackVerdict Before { get; init; }

    /// <summary>The output judged the same way, reopened from disk; null when nothing was written.</summary>
    public PlaybackVerdict? After { get; init; }

    /// <summary>How it ended.</summary>
    public required MakePlayableStatus Status { get; init; }

    /// <summary>True when an output file exists.</summary>
    public bool OutputWritten => Status is MakePlayableStatus.AlreadyPlayable or MakePlayableStatus.Made;

    /// <summary>The container written, or the one that would be written for <see cref="MakePlayableStatus.NeedsAudioTranscode"/>; null when none was chosen.</summary>
    public RemuxFormat? Target { get; init; }

    /// <summary>The steps taken (or, for <see cref="MakePlayableStatus.NeedsAudioTranscode"/>, the ones that would be).</summary>
    public IReadOnlyList<MakePlayableAction> Actions { get; init; } = [];

    /// <summary>The audio tracks re-encoded, or those that would need it.</summary>
    public IReadOnlyList<AudioTranscodeInfo> AudioTranscodes { get; init; } = [];

    /// <summary>The remux's own report (losses, changes, per-track packet counts); null when no remux ran.</summary>
    public RemuxReport? Remux { get; init; }

    /// <summary>Blockers still standing: the reasons for <see cref="MakePlayableStatus.Refused"/>, <see cref="MakePlayableStatus.NeedsAudioTranscode"/> and <see cref="MakePlayableStatus.Failed"/>; empty otherwise.</summary>
    public IReadOnlyList<PlaybackBlocker> Remaining { get; init; } = [];

    /// <summary>Remarks that do not change the outcome (candidate containers that were tried and why they were passed over).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Settings for <see cref="PlayableMaker"/>.</summary>
public sealed record MakePlayableOptions
{
    /// <summary>Re-encodes audio when that is what stands between the file and playback. Null (the default): such a file is reported as <see cref="MakePlayableStatus.NeedsAudioTranscode"/> and nothing is written, because the core decodes nothing.</summary>
    public IAudioTranscoder? AudioTranscoder { get; init; }

    /// <summary>Bits per second for re-encoded audio; 0 (the default) lets the transcoder choose.</summary>
    public long AudioBitRate { get; init; }

    /// <summary>Replace an existing output file (default false: refuse).</summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// The container to try first, usually the one the output's extension names (<see cref="Remuxer.FormatForPath(string)"/>). Default null: MP4, then WebM, Matroska, QuickTime.
    /// It is used only when the profile plays the result; otherwise the usual order applies and the report's <c>Target</c> says what was written.
    /// </summary>
    public RemuxFormat? PreferredTarget { get; init; }

    /// <summary>Settings for the remux (limits, tag carry-through, movie timescale). Its <see cref="RemuxOptions.Replacements"/> and <see cref="RemuxOptions.Overwrite"/> are set by <see cref="PlayableMaker"/>.</summary>
    public RemuxOptions? Remux { get; init; }
}

/// <summary>
/// Turns a file a browser profile cannot play into one it can, without decoding video: moves the movie box to the front, remuxes into a container
/// the profile accepts, and, when audio is the only other blocker and an <see cref="IAudioTranscoder"/> is given, re-encodes the audio.
/// Video blockers (codec, profile, level, bit depth, chroma) are reported with their codes and never solved.
/// </summary>
/// <remarks>The goal is playing as a downloaded file (<see cref="PlaybackVerdict.Progressive"/>). The output container is the first of MP4, WebM, Matroska, QuickTime that the profile plays with all the tracks' codecs, trying a plain copy of every track before any transcode.</remarks>
public static class PlayableMaker
{
    private static readonly (RemuxFormat Format, string Key)[] s_targets =
    [
        (RemuxFormat.Mp4, "mp4"), (RemuxFormat.WebM, "webm"), (RemuxFormat.Matroska, "matroska"), (RemuxFormat.Mov, "mov"),
    ];

    private static readonly CodecId[] s_assumedTargets = [CodecId.Opus];

    /// <summary>Makes a file play in a browser profile.</summary>
    /// <param name="inputPath">The file; its format is recognised by content.</param>
    /// <param name="outputPath">The output. Created new; an existing file is refused unless <see cref="MakePlayableOptions.Overwrite"/> is set. Must differ from the input. Not written for <see cref="MakePlayableStatus.NeedsAudioTranscode"/>, <see cref="MakePlayableStatus.Refused"/> and <see cref="MakePlayableStatus.Failed"/>.</param>
    /// <param name="profile">The browser to target, for example <see cref="BrowserProfiles.Chromium"/>.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the work; a partial output is deleted.</param>
    /// <returns>The verdicts before and after, the steps, the remux report and what still blocks. Refusals are results, not exceptions.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Input and output are the same file.</exception>
    /// <exception cref="IOException">The output exists and may not be replaced, or a file cannot be read or written.</exception>
    /// <exception cref="UnsupportedMediaException">The input is not recognised.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static MakePlayableReport MakePlayable(string inputPath, string outputPath, BrowserProfile profile, MakePlayableOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(profile);
        options ??= new MakePlayableOptions();
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.Ordinal))
        {
            throw new ArgumentException("The output must be a different file from the input.", nameof(outputPath));
        }

        if (File.Exists(outputPath) && !options.Overwrite)
        {
            throw new IOException($"'{outputPath}' exists.");
        }

        PlaybackVerdict before;
        PlaybackDescription description;
        bool fragmented;
        using (MediaFile input = Media.Open(inputPath))
        {
            before = input.CanPlay(profile);
            description = before.Description;
            fragmented = input.Demuxer is IsoBmff.IsoBmffDemuxer { Movie.IsFragmented: true };
        }

        PlaybackModeVerdict goal = before.Progressive;
        if (goal.Plays)
        {
            return AlreadyPlayable(inputPath, outputPath, profile, before, description, fragmented, options, cancellationToken);
        }

        // Blockers no remux or audio transcode removes: video, encryption, an empty file.
        List<PlaybackBlocker> fixedBlockers = [.. goal.Blockers.Where(b => b.Remedy is PlaybackRemedy.None or PlaybackRemedy.VideoBlocker)];
        if (fixedBlockers.Count > 0)
        {
            return new MakePlayableReport { ProfileId = profile.Id, Before = before, Status = MakePlayableStatus.Refused, Remaining = fixedBlockers };
        }

        IAudioTranscoder? transcoder = options.AudioTranscoder;
        List<string> notes = [];
        Plan? plan = null;
        List<PlaybackBlocker> unsolved = [];
        (RemuxFormat Format, string Key)[] order = s_targets;
        if (options.PreferredTarget is { } preferred && KeyOf(preferred) is { } preferredKey)
        {
            order = [(preferred, preferredKey), .. s_targets.Where(t => t.Key != preferredKey)];
        }

        foreach (bool allowTranscode in new[] { false, true })
        {
            foreach ((RemuxFormat format, string key) in order)
            {
                if ((profile.ModesFor(key) & PlaybackMode.Progressive) == 0)
                {
                    continue;
                }

                Plan candidate = Evaluate(description, profile, format, key, allowTranscode, transcoder, options.AudioBitRate);
                if (candidate.Blockers.Count == 0)
                {
                    plan = candidate;
                    break;
                }

                unsolved = candidate.Blockers;
                notes.Add($"{key}: {string.Join(", ", candidate.Blockers.Select(b => $"{b.Code}[{b.TrackIndex}]"))}{(allowTranscode ? " (with audio transcode)" : string.Empty)}");
            }

            if (plan is not null)
            {
                break;
            }
        }

        if (plan is null)
        {
            // Nothing reaches playback. Say what stands in the way in the terms of the best candidate (the last one tried allowing transcode).
            return new MakePlayableReport { ProfileId = profile.Id, Before = before, Status = MakePlayableStatus.Refused, Remaining = unsolved.Count > 0 ? unsolved : [.. goal.Blockers], Notes = notes };
        }

        if (plan.Transcodes.Count > 0 && transcoder is null)
        {
            return new MakePlayableReport
            {
                ProfileId = profile.Id,
                Before = before,
                Status = MakePlayableStatus.NeedsAudioTranscode,
                Target = plan.Format,
                Actions = [.. plan.Actions],
                AudioTranscodes = [.. plan.Transcodes.Select(t => t.Info)],
                Remaining = [.. goal.Blockers.Where(b => plan.Transcodes.Any(t => t.Info.InputTrack == b.TrackIndex))],
                Notes = notes,
            };
        }

        return Run(inputPath, outputPath, profile, before, plan, options, notes, cancellationToken);
    }

    // One audio track to re-encode in a plan. Info is what the report will say; Source is the track.
    private sealed record PlannedTranscode(AudioTranscodeInfo Info, MediaTrack? Source);

    private sealed record Plan(RemuxFormat Format, string Key, List<PlaybackBlocker> Blockers, List<MakePlayableAction> Actions, List<PlannedTranscode> Transcodes);

    private static Plan Evaluate(PlaybackDescription d, BrowserProfile profile, RemuxFormat format, string key, bool allowTranscode, IAudioTranscoder? transcoder, long bitRate)
    {
        List<MakePlayableAction> actions = [];
        List<PlannedTranscode> transcodes = [];
        PlaybackDescription hypo = ForContainer(d, key, null);
        PlaybackModeVerdict v = Playability.Check(hypo, profile).Progressive;
        List<PlaybackBlocker> remaining = [];
        if (v.Plays || !allowTranscode)
        {
            remaining.AddRange(v.Blockers);
        }
        else
        {
            Dictionary<int, PlaybackTrack> replaced = [];
            IReadOnlyList<CodecId> targets = transcoder?.Targets ?? s_assumedTargets;
            foreach (int track in v.Blockers.Where(b => b.TrackIndex >= 0).Select(b => b.TrackIndex).Distinct())
            {
                PlaybackTrack? t = d.Tracks.FirstOrDefault(x => x.TrackIndex == track);
                if (t is null || t.Kind != TrackKind.Audio)
                {
                    continue;
                }

                bool done = false;
                foreach (CodecId target in targets)
                {
                    PlaybackTrack asTarget = t with { Codec = target, CodecTag = null, CodecString = CodecStringOf(target), Profile = -1, Level = -1, BitDepth = 0 };
                    // The one track changed, the others as they are: does the profile play the result?
                    Dictionary<int, PlaybackTrack> trial = new(replaced) { [track] = asTarget };
                    PlaybackModeVerdict tv = Playability.Check(ForContainer(d, key, trial), profile).Progressive;
                    if (tv.Blockers.Any(b => b.TrackIndex == track))
                    {
                        continue;
                    }

                    replaced[track] = asTarget;
                    done = true;
                    break;
                }

                if (!done)
                {
                    remaining.Add(new PlaybackBlocker(PlaybackBlockerKind.Codec, "transcode.no-target", track, PlaybackRemedy.None,
                        $"{profile.DisplayName} plays none of the codecs an audio transcode can produce ({string.Join(", ", targets.Select(c => c.Name))}) in {key}."));
                }
            }

            PlaybackModeVerdict after = Playability.Check(ForContainer(d, key, replaced), profile).Progressive;
            remaining.AddRange(after.Blockers.Where(b => !remaining.Any(r => r.TrackIndex == b.TrackIndex && r.TrackIndex >= 0)));
            if (remaining.Count == 0)
            {
                foreach ((int track, PlaybackTrack to) in replaced)
                {
                    // The transcoder says whether it can handle the source; its refusal becomes a blocker so the report names the reason.
                    CodecId from = d.Tracks.First(x => x.TrackIndex == track).Codec;
                    transcodes.Add(new PlannedTranscode(new AudioTranscodeInfo(track, from, to.Codec, bitRate, 0, 0, MediaTime.Zero, "pending"), null));
                    actions.Add(new MakePlayableAction(MakePlayableActionKind.TranscodeAudio, track, $"Audio track {track}: {from.Name} to {to.Codec.Name}."));
                }
            }
        }

        if (remaining.Count == 0)
        {
            actions.Insert(0, new MakePlayableAction(MakePlayableActionKind.Remux, -1, $"Copy the tracks into {key}{(key is "mp4" or "mov" ? ", movie box first" : string.Empty)}."));
        }

        return new Plan(format, key, remaining, actions, transcodes);
    }

    private static string? KeyOf(RemuxFormat f) => f switch
    {
        RemuxFormat.Mp4 or RemuxFormat.M4a or RemuxFormat.M4v => "mp4",
        RemuxFormat.WebM => "webm",
        RemuxFormat.Matroska => "matroska",
        RemuxFormat.Mov => "mov",
        _ => null,
    };

    private static string? CodecStringOf(CodecId c) => c == CodecId.Opus ? "opus" : c == CodecId.Flac ? "flac" : null;

    // What the description would be if the same packets sat in `key`: container name, content type, fast start, and the sample-entry form the muxers write.
    private static PlaybackDescription ForContainer(PlaybackDescription d, string key, Dictionary<int, PlaybackTrack>? replaced)
    {
        List<PlaybackTrack> tracks = [];
        foreach (PlaybackTrack t in d.Tracks)
        {
            PlaybackTrack x = replaced is not null && replaced.TryGetValue(t.TrackIndex, out PlaybackTrack? r) ? r : t;
            // The ISO muxers write avc1/hvc1 unless the source said parameter sets travel in the stream; Matroska names no sample entry.
            string? tag = key is "mp4" or "mov"
                ? (x.Codec.Name, x.CodecTag) switch
                {
                    ("h264", "avc3") => "avc3",
                    ("hevc", "hev1") => "hev1",
                    ("h264", _) => "avc1",
                    ("hevc", _) => "hvc1",
                    _ => x.CodecTag is "mp4a" or "avc1" or "hvc1" or "hev1" or "avc3" ? x.CodecTag : null,
                }
                : null;
            tracks.Add(x with { CodecTag = tag });
        }

        bool video = tracks.Any(t => t.Kind == TrackKind.Video);
        string contentType = key switch
        {
            "mp4" => video ? "video/mp4" : "audio/mp4",
            "webm" => video ? "video/webm" : "audio/webm",
            "matroska" => video ? "video/x-matroska" : "audio/x-matroska",
            _ => video ? "video/quicktime" : "audio/x-m4a",
        };
        return d with { Container = key, ContentType = contentType, IsSegmented = key == "webm", IsFastStart = true, Tracks = tracks };
    }

    private static MakePlayableReport AlreadyPlayable(string inputPath, string outputPath, BrowserProfile profile, PlaybackVerdict before, PlaybackDescription d, bool fragmented, MakePlayableOptions options, CancellationToken ct)
    {
        bool moveMovie = !d.IsFastStart && !fragmented && d.Container is "mp4" or "mov" or "3gp";
        if (moveMovie && before.Progressive.Warnings.Any(w => w.Code == "progressive.movie-at-end"))
        {
            try
            {
                FaststartReport fs = Remuxer.Faststart(inputPath, outputPath, options.Overwrite, ct);
                using MediaFile result = Media.Open(outputPath);
                return new MakePlayableReport
                {
                    ProfileId = profile.Id,
                    Before = before,
                    After = result.CanPlay(profile),
                    Status = fs.Relocated ? MakePlayableStatus.Made : MakePlayableStatus.AlreadyPlayable,
                    Actions = [new MakePlayableAction(MakePlayableActionKind.Faststart, -1, fs.Relocated ? $"Moved the {fs.MovieBoxBytes}-byte movie box in front of the media data." : "The movie box was already first.")],
                };
            }
            catch (UnsupportedMediaException)
            {
                // A compressed movie box or a layout the editor does not move: the file plays as it is, so copy it.
            }
        }

        using (FileStream src = File.OpenRead(inputPath))
        using (FileStream dst = new(outputPath, options.Overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write))
        {
            src.CopyTo(dst);
        }

        return new MakePlayableReport
        {
            ProfileId = profile.Id,
            Before = before,
            After = before,
            Status = MakePlayableStatus.AlreadyPlayable,
            Actions = [new MakePlayableAction(MakePlayableActionKind.Copy, -1, "The file already plays in this profile; copied unchanged.")],
        };
    }

    private static MakePlayableReport Run(string inputPath, string outputPath, BrowserProfile profile, PlaybackVerdict before, Plan plan, MakePlayableOptions options, List<string> notes, CancellationToken ct)
    {
        List<IAudioTranscode> feeds = [];
        List<MakePlayableAction> actions = [.. plan.Actions.Where(a => a.Kind != MakePlayableActionKind.TranscodeAudio)];
        List<PlaybackBlocker> blockers = [];
        try
        {
            if (plan.Transcodes.Count > 0)
            {
                using MediaFile probe = Media.Open(inputPath);
                IAudioTranscoder transcoder = options.AudioTranscoder!;
                foreach (PlannedTranscode p in plan.Transcodes)
                {
                    MediaTrack source = probe.Tracks[p.Info.InputTrack];
                    if (!transcoder.CanTranscode(source, p.Info.To, out string code, out string message))
                    {
                        blockers.Add(new PlaybackBlocker(PlaybackBlockerKind.Codec, code, source.Index, PlaybackRemedy.None, message));
                        continue;
                    }

                    try
                    {
                        feeds.Add(transcoder.Begin(inputPath, source, p.Info.To, options.AudioBitRate, ct));
                    }
                    catch (MediaException e)
                    {
                        blockers.Add(new PlaybackBlocker(PlaybackBlockerKind.Codec, "transcode.failed", source.Index, PlaybackRemedy.None, e.Message));
                    }
                }

                if (blockers.Count > 0)
                {
                    return new MakePlayableReport { ProfileId = profile.Id, Before = before, Status = MakePlayableStatus.Refused, Target = plan.Format, Remaining = blockers, Notes = notes };
                }
            }

            RemuxOptions remuxOptions = (options.Remux ?? new RemuxOptions()) with
            {
                Replacements = [.. feeds.Select(f => new TrackReplacement(f))],
                Overwrite = options.Overwrite,
                Faststart = true,
            };
            RemuxReport remux;
            try
            {
                remux = Remuxer.Remux(inputPath, outputPath, plan.Format, remuxOptions, ct);
            }
            catch (Exception e) when (e is MuxRefusedException or UnsupportedMediaException || (feeds.Count > 0 && e is MediaException))
            {
                return new MakePlayableReport
                {
                    ProfileId = profile.Id,
                    Before = before,
                    Status = MakePlayableStatus.Failed,
                    Target = plan.Format,
                    Remaining = [new PlaybackBlocker(PlaybackBlockerKind.CodecInContainer, feeds.Count > 0 && e is not MuxRefusedException ? "transcode.failed" : "remux.refused", e is MuxRefusedException m ? m.TrackIndex : -1, PlaybackRemedy.None, e.Message)],
                    Notes = notes,
                };
            }

            foreach (RemuxLoss loss in remux.Losses.Where(l => l.Kind == LossKind.Track))
            {
                actions.Add(new MakePlayableAction(MakePlayableActionKind.DropTrack, loss.InputTrack, $"{loss.Subject}: {loss.Detail}"));
            }

            // A file that lost its picture (or all its sound) is not playable in any sense the caller asked for, even if what is left plays.
            if (remux.LostMediaTracks.Count > 0)
            {
                File.Delete(outputPath);
                return new MakePlayableReport
                {
                    ProfileId = profile.Id,
                    Before = before,
                    Status = MakePlayableStatus.Failed,
                    Target = plan.Format,
                    Actions = actions,
                    Remux = remux,
                    Remaining = [.. remux.LostMediaTracks.Select(i => new PlaybackBlocker(PlaybackBlockerKind.CodecInContainer, "remux.dropped-track", i, PlaybackRemedy.None,
                        remux.Losses.FirstOrDefault(l => l.Kind == LossKind.Track && l.InputTrack == i)?.Detail ?? $"Input track {i} could not be written."))],
                    Notes = notes,
                };
            }

            PlaybackVerdict after;
            using (MediaFile result = Media.Open(outputPath))
            {
                after = result.CanPlay(profile);
            }

            foreach (IAudioTranscode f in feeds)
            {
                actions.Add(new MakePlayableAction(MakePlayableActionKind.TranscodeAudio, f.Info.InputTrack,
                    $"Audio track {f.Info.InputTrack}: {f.Info.From.Name} to {f.Info.To.Name}, {f.Info.BitRate} bit/s, {f.Info.SampleRate} Hz, {f.Info.Channels} ch, encoder delay {f.Info.EncoderDelay.Ticks}/{f.Info.EncoderDelay.Timescale} s."));
            }

            if (!after.Progressive.Plays)
            {
                File.Delete(outputPath);
                return new MakePlayableReport
                {
                    ProfileId = profile.Id,
                    Before = before,
                    After = after,
                    Status = MakePlayableStatus.Failed,
                    Target = plan.Format,
                    Actions = actions,
                    Remux = remux,
                    Remaining = after.Progressive.Blockers,
                    Notes = notes,
                };
            }

            return new MakePlayableReport
            {
                ProfileId = profile.Id,
                Before = before,
                After = after,
                Status = MakePlayableStatus.Made,
                Target = plan.Format,
                Actions = actions,
                AudioTranscodes = [.. feeds.Select(f => f.Info)],
                Remux = remux,
                Notes = notes,
            };
        }
        finally
        {
            foreach (IAudioTranscode f in feeds)
            {
                f.Dispose();
            }
        }
    }
}
