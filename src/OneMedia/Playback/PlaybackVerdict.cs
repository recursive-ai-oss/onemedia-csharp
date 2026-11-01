using System.Linq;
using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia.Playback;

/// <summary>Why a file does not play, as a class a program can act on.</summary>
public enum PlaybackBlockerKind
{
    /// <summary>The browser does not accept the container in this mode.</summary>
    Container = 0,

    /// <summary>The browser plays the codec, but not inside this container.</summary>
    CodecInContainer,

    /// <summary>The browser plays no such codec at all.</summary>
    Codec,

    /// <summary>The codec plays, but not this profile (for example H.264 High 4:4:4).</summary>
    CodecProfile,

    /// <summary>The codec and profile play, but not this level.</summary>
    CodecLevel,

    /// <summary>The codec plays, but not at this bit depth.</summary>
    BitDepth,

    /// <summary>The codec plays, but not with this chroma format.</summary>
    ChromaFormat,

    /// <summary>The codec plays, but the browser wants another sample-entry form (<c>hvc1</c> rather than <c>hev1</c>).</summary>
    CodecTag,

    /// <summary>The file is not fragmented MP4 or WebM, so it cannot be appended to a MediaSource.</summary>
    NotSegmented,

    /// <summary>The samples are encrypted.</summary>
    Protected,

    /// <summary>The file has no video or audio track a browser could play.</summary>
    NoPlayableTracks,
}

/// <summary>What can be done about a blocker without decoding video. <c>MakePlayable</c> acts on this.</summary>
public enum PlaybackRemedy
{
    /// <summary>Nothing this library can do (encrypted content).</summary>
    None = 0,

    /// <summary>Copying the same samples into another container, or rewriting the sample entry, fixes it.</summary>
    Remux,

    /// <summary>The audio track must be re-encoded; OneMedia.Audio can do that. Only audio blockers carry this.</summary>
    TranscodeAudio,

    /// <summary>A video track would have to be re-encoded, which this library never does.</summary>
    VideoBlocker,
}

/// <summary>One reason a file does not play in a profile and mode.</summary>
/// <param name="Kind">The class of reason.</param>
/// <param name="Code">A stable lowercase code, for example <c>codec.profile</c>. The codes are listed in <c>docs/playability.md</c>.</param>
/// <param name="TrackIndex">The track concerned (<see cref="MediaTrack.Index"/>), or -1 when the blocker concerns the container.</param>
/// <param name="Remedy">What fixes it.</param>
/// <param name="Message">A sentence for a person, in English.</param>
public sealed record PlaybackBlocker(PlaybackBlockerKind Kind, string Code, int TrackIndex, PlaybackRemedy Remedy, string Message);

/// <summary>A condition that does not stop playback everywhere but is worth telling the caller about.</summary>
/// <param name="Code">A stable lowercase code: <c>progressive.movie-at-end</c> or <c>hardware-dependent</c>.</param>
/// <param name="TrackIndex">The track concerned, or -1.</param>
/// <param name="Remedy">What would remove the condition.</param>
/// <param name="Message">A sentence for a person.</param>
public sealed record PlaybackWarning(string Code, int TrackIndex, PlaybackRemedy Remedy, string Message);

/// <summary>The outcome of one <see cref="PlaybackMode"/>.</summary>
/// <param name="Mode">The mode judged.</param>
/// <param name="Blockers">Why the mode fails; empty when it works.</param>
/// <param name="Warnings">Conditions that do not block.</param>
public sealed record PlaybackModeVerdict(PlaybackMode Mode, IReadOnlyList<PlaybackBlocker> Blockers, IReadOnlyList<PlaybackWarning> Warnings)
{
    /// <summary>True when there is no blocker.</summary>
    public bool Plays => Blockers.Count == 0;
}

/// <summary>Whether a file plays in a browser profile, and if not, why not, per mode.</summary>
public sealed record PlaybackVerdict
{
    internal PlaybackVerdict(string profileId, PlaybackDescription description, PlaybackModeVerdict progressive, PlaybackModeVerdict mediaSource)
    {
        ProfileId = profileId;
        Description = description;
        Progressive = progressive;
        MediaSource = mediaSource;
    }

    /// <summary>The <see cref="BrowserProfile.Id"/> judged against.</summary>
    public string ProfileId { get; }

    /// <summary>The facts the verdict rests on.</summary>
    public PlaybackDescription Description { get; }

    /// <summary>The verdict for handing the file to a media element as a URL.</summary>
    public PlaybackModeVerdict Progressive { get; }

    /// <summary>The verdict for appending the file to a MediaSource.</summary>
    public PlaybackModeVerdict MediaSource { get; }

    /// <summary>True when the file plays in at least one mode.</summary>
    public bool CanPlay => Progressive.Plays || MediaSource.Plays;

    /// <summary>The modes in which the file plays.</summary>
    public PlaybackMode Modes => (Progressive.Plays ? PlaybackMode.Progressive : 0) | (MediaSource.Plays ? PlaybackMode.MediaSource : 0);

    /// <summary>
    /// The blockers of the better mode: empty when <see cref="CanPlay"/>, otherwise the progressive blockers unless the MediaSource mode has fewer.
    /// </summary>
    public IReadOnlyList<PlaybackBlocker> Blockers => MediaSource.Blockers.Count < Progressive.Blockers.Count ? MediaSource.Blockers : Progressive.Blockers;

    /// <summary>True when every blocker of <see cref="Blockers"/> can be removed without re-encoding video (a remux, or an audio transcode).</summary>
    public bool CanBeMadePlayable => !CanPlay && Blockers.All(b => b.Remedy is PlaybackRemedy.Remux or PlaybackRemedy.TranscodeAudio);
}

/// <summary>Judges a described file against a browser profile. <see cref="MediaFile.CanPlay"/> is this over an open file; call it directly to judge a description built by hand or read from elsewhere.</summary>
public static class Playability
{
    /// <summary>Judges a description against a profile in both modes.</summary>
    /// <param name="d">The facts about the file.</param>
    /// <param name="profile">The browser profile.</param>
    /// <returns>The verdict, with every blocker and warning. Never throws.</returns>
    public static PlaybackVerdict Check(PlaybackDescription d, BrowserProfile profile) =>
        new(profile.Id, d, Check(d, profile, PlaybackMode.Progressive), Check(d, profile, PlaybackMode.MediaSource));

    private static PlaybackModeVerdict Check(PlaybackDescription d, BrowserProfile profile, PlaybackMode mode)
    {
        List<PlaybackBlocker> blockers = [];
        List<PlaybackWarning> warnings = [];
        if (d.Tracks.Count == 0)
        {
            blockers.Add(new(PlaybackBlockerKind.NoPlayableTracks, "tracks.none", -1, PlaybackRemedy.None, "The file has no video or audio track."));
            return new(mode, blockers, warnings);
        }

        if ((profile.ModesFor(d.Container) & mode) == 0)
        {
            blockers.Add(new(PlaybackBlockerKind.Container, mode == PlaybackMode.Progressive ? "container.unsupported" : "container.mediasource",
                -1, PlaybackRemedy.Remux, $"{profile.DisplayName} does not accept {d.Container} as {(mode == PlaybackMode.Progressive ? "a downloaded file" : "MediaSource data")}."));
        }
        else if (mode == PlaybackMode.MediaSource && !d.IsSegmented)
        {
            // Media Source Extensions byte-stream formats: the ISO BMFF format needs an initialisation segment followed by fragments,
            // and WebM likewise; a plain MP4 with one movie box is neither.
            blockers.Add(new(PlaybackBlockerKind.NotSegmented, "mediasource.not-segmented", -1, PlaybackRemedy.Remux, "Only fragmented MP4 and WebM can be appended to a MediaSource."));
        }
        else if (mode == PlaybackMode.Progressive && !d.IsFastStart)
        {
            warnings.Add(new("progressive.movie-at-end", -1, PlaybackRemedy.Remux, "The movie box follows the media data: playback starts only after the browser has fetched the end of the file."));
        }

        if (mode == PlaybackMode.MediaSource && d.Tracks.Any(t => t.CodecString is null))
        {
            // MediaSource.isTypeSupported and addSourceBuffer need a complete codecs parameter; the caller must supply the missing one.
            warnings.Add(new("mediasource.codec-string", -1, PlaybackRemedy.None, "A track has no RFC 6381 codec string: the MIME type for the SourceBuffer is incomplete."));
        }

        foreach (PlaybackTrack t in d.Tracks)
        {
            if (t.IsProtected)
            {
                blockers.Add(new(PlaybackBlockerKind.Protected, "track.protected", t.TrackIndex, PlaybackRemedy.None, "The track's samples are encrypted."));
                continue;
            }

            CheckTrack(t, d.Container, profile, mode, blockers, warnings);
        }

        return new(mode, blockers, warnings);
    }

    private static void CheckTrack(PlaybackTrack t, string container, BrowserProfile profile, PlaybackMode mode, List<PlaybackBlocker> blockers, List<PlaybackWarning> warnings)
    {
        bool audio = t.Kind == TrackKind.Audio;
        PlaybackRemedy codecRemedy = audio ? PlaybackRemedy.TranscodeAudio : PlaybackRemedy.VideoBlocker;
        string name = t.Codec.Name;
        IReadOnlyList<CodecRule> rules = profile.RulesFor(name);
        if (rules.Count == 0)
        {
            blockers.Add(new(PlaybackBlockerKind.Codec, "codec.unsupported", t.TrackIndex, codecRemedy, $"{profile.DisplayName} does not play {(name.Length == 0 ? "this codec" : name)}."));
            return;
        }

        List<CodecRule> here = [];
        foreach (CodecRule r in rules)
        {
            if ((r.Modes & mode) != 0 && (r.Containers.Contains(container) || r.Containers.Contains("*")))
            {
                here.Add(r);
            }
        }

        if (!rules.Any(r => (r.Modes & mode) != 0))
        {
            blockers.Add(new(PlaybackBlockerKind.Codec, "codec.mode", t.TrackIndex, codecRemedy,
                $"{profile.DisplayName} plays {name} only {(mode == PlaybackMode.Progressive ? "through MediaSource" : "as a plain URL")}."));
            return;
        }

        if (here.Count == 0)
        {
            // The codec is known to the browser, just not in this container (or not in this mode): another container fixes it.
            bool elsewhere = rules.Any(r => (r.Modes & mode) != 0 && r.Containers.Any(c => (profile.ModesFor(c) & mode) != 0));
            blockers.Add(new(PlaybackBlockerKind.CodecInContainer, "codec.container", t.TrackIndex, elsewhere ? PlaybackRemedy.Remux : codecRemedy,
                $"{profile.DisplayName} plays {name} but not in {container}."));
            return;
        }

        PlaybackBlocker? first = null;
        foreach (CodecRule r in here)
        {
            PlaybackBlocker? b = Violation(t, r, profile, codecRemedy);
            if (b is null)
            {
                if (r.HardwareProfiles is { } hw && hw.Contains(t.Profile))
                {
                    warnings.Add(new("hardware-dependent", t.TrackIndex, PlaybackRemedy.None, $"{profile.DisplayName} plays {name} profile {t.Profile} only on some hardware or operating-system builds."));
                }

                return;
            }

            first ??= b;
        }

        blockers.Add(first!);
    }

    private static PlaybackBlocker? Violation(PlaybackTrack t, CodecRule r, BrowserProfile profile, PlaybackRemedy codecRemedy)
    {
        string who = profile.DisplayName;
        if (r.Tags is { } tags && t.CodecTag is { } tag && !tags.Contains(tag))
        {
            return new(PlaybackBlockerKind.CodecTag, "codec.tag", t.TrackIndex, PlaybackRemedy.Remux, $"{who} wants the sample entry {string.Join(" or ", tags)}, the file has {tag}.");
        }

        if (r.Profiles is { } profiles && t.Profile >= 0 && !profiles.Contains(t.Profile) && !(r.HardwareProfiles?.Contains(t.Profile) ?? false))
        {
            return new(PlaybackBlockerKind.CodecProfile, "codec.profile", t.TrackIndex, codecRemedy, $"{who} does not play {t.Codec.Name} profile {t.Profile}.");
        }

        if (r.MaxLevel > 0 && t.Level > r.MaxLevel)
        {
            return new(PlaybackBlockerKind.CodecLevel, "codec.level", t.TrackIndex, codecRemedy, $"{who} plays {t.Codec.Name} up to level {r.MaxLevel}, the file is level {t.Level}.");
        }

        if (r.MaxBitDepth > 0 && t.BitDepth > r.MaxBitDepth)
        {
            return new(PlaybackBlockerKind.BitDepth, "codec.bit-depth", t.TrackIndex, codecRemedy, $"{who} plays {t.Codec.Name} up to {r.MaxBitDepth} bits, the file has {t.BitDepth}.");
        }

        if (r.MaxChroma != ChromaFormat.Unknown && t.Chroma > r.MaxChroma)
        {
            return new(PlaybackBlockerKind.ChromaFormat, "codec.chroma", t.TrackIndex, codecRemedy, $"{who} does not play {t.Codec.Name} with this chroma format.");
        }

        return null;
    }
}
