using System.Linq;
using System.Collections.Frozen;

namespace RecursiveAI.OneMedia.Playback;

/// <summary>How a browser can be handed media: as a plain URL, or by appending segments to a MediaSource.</summary>
[Flags]
public enum PlaybackMode
{
    /// <summary>Neither.</summary>
    None = 0,

    /// <summary>The file is given to a <c>&lt;video&gt;</c> or <c>&lt;audio&gt;</c> element as a URL and the browser demuxes it.</summary>
    Progressive = 1,

    /// <summary>The file is appended to a Media Source Extensions <c>SourceBuffer</c>. Only fragmented MP4 and WebM can be, and the codec string must pass <c>isTypeSupported</c>.</summary>
    MediaSource = 2,

    /// <summary>Both modes.</summary>
    Both = Progressive | MediaSource,
}

/// <summary>
/// One container a browser accepts, in the key space of <see cref="PlaybackDescription.Container"/>:
/// <c>mp4</c>, <c>mov</c>, <c>3gp</c>, <c>webm</c>, <c>matroska</c>, <c>ogg</c>, <c>mp3</c>, <c>adts</c>, <c>flac</c>, <c>wav</c>, <c>aiff</c>, <c>caf</c>,
/// <c>avi</c>, <c>flv</c>, <c>mpegts</c>, <c>asf</c>, <c>ac3</c>, <c>amr</c>.
/// </summary>
/// <param name="Container">The container key.</param>
/// <param name="Modes">The modes in which the browser accepts it.</param>
public sealed record ContainerSupport(string Container, PlaybackMode Modes);

/// <summary>
/// What a browser accepts of one codec when it sits in the listed containers. A rule that is present means the codec plays; the
/// optional limits narrow it. A codec with no rule for a container does not play there.
/// </summary>
public sealed record CodecRule
{
    /// <summary>Creates a rule. <paramref name="codec"/> is a <see cref="CodecId"/> name; <paramref name="containers"/> are container keys.</summary>
    public CodecRule(string codec, params string[] containers)
    {
        Codec = codec;
        Containers = containers;
    }

    /// <summary>The <see cref="CodecId.Name"/> the rule is for, for example <c>h264</c>.</summary>
    public string Codec { get; }

    /// <summary>The container keys in which the codec plays.</summary>
    public IReadOnlyList<string> Containers { get; }

    /// <summary>The codec profile numbers that play unconditionally, in the codec's own numbering (H.264 <c>profile_idc</c>, HEVC <c>general_profile_idc</c>,
    /// AV1 <c>seq_profile</c>, VP9 profile, AAC audio object type with 5 standing for HE-AAC and 29 for HE-AAC v2). Null: any profile.</summary>
    public IReadOnlyList<int>? Profiles { get; init; }

    /// <summary>Profiles that play only on some hardware or operating-system builds. They are allowed with a warning, not blocked.</summary>
    public IReadOnlyList<int>? HardwareProfiles { get; init; }

    /// <summary>The highest level that plays, in the codec's own numbering (H.264 <c>level_idc</c>, HEVC <c>general_level_idc</c>, AV1 <c>seq_level_idx</c>,
    /// VP9 level ×10). 0: no limit.</summary>
    public int MaxLevel { get; init; }

    /// <summary>The highest luma bit depth that plays; 0: no limit.</summary>
    public int MaxBitDepth { get; init; }

    /// <summary>The highest chroma format that plays (4:2:0 &lt; 4:2:2 &lt; 4:4:4); <see cref="Codecs.ChromaFormat.Unknown"/>: no limit.</summary>
    public Codecs.ChromaFormat MaxChroma { get; init; }

    /// <summary>The sample-entry codes (<see cref="MediaTrack.CodecTag"/>) the browser accepts in ISO files, for example <c>hvc1</c>; null: any.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>The modes in which the rule applies. Default: both.</summary>
    public PlaybackMode Modes { get; init; } = PlaybackMode.Both;

    /// <summary>A plain-words note on where the rule comes from or what it leaves open.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// What one browser on one platform plays: containers, codecs and their limits, as data. Profiles are built from static tables
/// (<see cref="BrowserProfiles"/>); nothing is read at run time. <c>docs/playability.md</c> lists, for every row, the observation
/// or public document it comes from.
/// </summary>
public sealed class BrowserProfile
{
    /// <summary>Creates a profile.</summary>
    public BrowserProfile(string id, string displayName, string platform, IReadOnlyList<ContainerSupport> containers, IReadOnlyList<CodecRule> codecs)
    {
        Id = id;
        DisplayName = displayName;
        Platform = platform;
        Containers = containers;
        Codecs = codecs;
        _containers = containers.ToFrozenDictionary(c => c.Container, c => c.Modes);
        _rules = codecs.GroupBy(c => c.Codec).ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<CodecRule>)g.ToArray());
    }

    private readonly FrozenDictionary<string, PlaybackMode> _containers;
    private readonly FrozenDictionary<string, IReadOnlyList<CodecRule>> _rules;

    /// <summary>Short stable identifier, for example <c>chrome</c>.</summary>
    public string Id { get; }

    /// <summary>Name for display.</summary>
    public string DisplayName { get; }

    /// <summary>The operating systems the profile describes, in words.</summary>
    public string Platform { get; }

    /// <summary>What the profile was checked against, for example <c>playwright-chromium</c>; empty when it rests on public documentation only.</summary>
    public string VerifiedWith { get; init; } = string.Empty;

    /// <summary>The containers the browser accepts.</summary>
    public IReadOnlyList<ContainerSupport> Containers { get; }

    /// <summary>The codec rules.</summary>
    public IReadOnlyList<CodecRule> Codecs { get; }

    /// <summary>The modes in which the container is accepted; <see cref="PlaybackMode.None"/> when it is not.</summary>
    public PlaybackMode ModesFor(string container) => _containers.TryGetValue(container, out PlaybackMode m) ? m : PlaybackMode.None;

    /// <summary>The rules for a codec name, or an empty list when the browser plays none.</summary>
    public IReadOnlyList<CodecRule> RulesFor(string codec) => _rules.TryGetValue(codec, out IReadOnlyList<CodecRule>? r) ? r : [];
}
