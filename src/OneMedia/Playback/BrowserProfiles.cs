using System.Linq;

namespace RecursiveAI.OneMedia.Playback;

/// <summary>
/// The built-in browser profiles: static tables, read at start-up, never loaded from a file. Rows marked as observed were settled by
/// playing the generated verification matrix in the browser named by <see cref="BrowserProfile.VerifiedWith"/>; rows that no matrix file
/// exercises come from the browsers' public documentation. <c>docs/playability.md</c> lists both, with versions.
/// </summary>
public static class BrowserProfiles
{
    private const PlaybackMode P = PlaybackMode.Progressive;
    private const PlaybackMode M = PlaybackMode.MediaSource;
    private const PlaybackMode B = PlaybackMode.Both;

    private static ContainerSupport C(string container, PlaybackMode modes) => new(container, modes);

    private static CodecRule R(string codec, params string[] containers) => new(codec, containers);

    // H.264 profile_idc values (ITU-T H.264 Annex A): 66 Baseline, 77 Main, 88 Extended, 100 High, 110 High 10, 122 High 4:2:2, 244 High 4:4:4 Predictive.
    private static readonly int[] H264Common = [66, 77, 88, 100];
    private static readonly int[] H264Extended = [110, 122, 244];

    // HEVC general_profile_idc (ITU-T H.265 §A.3): 1 Main, 2 Main 10.
    private static readonly int[] HevcMain = [1, 2];

    /// <summary>
    /// Google Chrome on desktop with its proprietary codecs (H.264, AAC), as run on macOS. HEVC and the 10-bit/4:2:2/4:4:4 H.264 profiles
    /// play where the operating system or GPU decodes them, so they carry a warning rather than a block.
    /// </summary>
    public static BrowserProfile Chrome { get; } = new("chrome", "Chrome", "Windows, macOS, Linux",
        [C("mp4", B), C("mov", P), C("3gp", P), C("webm", B), C("matroska", P), C("ogg", P), C("mp3", P), C("adts", P), C("flac", P), C("wav", P)],
        [
            R("h264", "mp4", "mov", "3gp", "matroska") with { Profiles = H264Common, HardwareProfiles = H264Extended },
            R("hevc", "mp4", "mov", "matroska") with { HardwareProfiles = HevcMain },
            R("av1", "mp4", "webm", "matroska"),
            R("vp9", "mp4", "webm", "matroska"),
            R("vp8", "webm", "matroska"),
            R("aac", "mp4", "mov", "3gp", "matroska", "adts"),
            R("mp3", "mp3", "mp4"),
            R("opus", "mp4", "webm", "ogg", "matroska"),
            R("vorbis", "mp4", "webm", "ogg", "matroska"),
            R("flac", "flac", "ogg", "mp4"),
            R("pcm", "wav", "mov"),
        ])
    { VerifiedWith = "Google Chrome 156.0.8078.74 on macOS arm64, driven by Playwright 1.55.0" };

    /// <summary>
    /// Chromium built without proprietary codecs, as Playwright ships it and as most Linux distributions and embedded browsers do:
    /// no H.264, HEVC, AAC, AC-3 or ALAC anywhere.
    /// </summary>
    public static BrowserProfile Chromium { get; } = new("chromium", "Chromium (open-source build)", "any",
        [C("mp4", B), C("webm", B), C("matroska", P), C("ogg", P), C("mp3", P), C("flac", P), C("wav", P)],
        [
            R("av1", "mp4", "webm", "matroska"),
            R("vp9", "mp4", "webm", "matroska"),
            R("vp8", "webm", "matroska"),
            R("mp3", "mp3", "mp4"),
            R("opus", "mp4", "webm", "ogg", "matroska"),
            R("vorbis", "webm", "ogg", "matroska"),
            R("flac", "flac", "ogg", "mp4"),
            R("pcm", "wav"),
        ])
    { VerifiedWith = "Chromium 140.0.7339.16 on Linux x64, driven by Playwright 1.55.0" };

    /// <summary>
    /// Firefox on a host whose operating system or system libraries supply the H.264, HEVC and AAC decoders (Firefox ships none of its
    /// own). No Matroska, 3GP, AC-3, ALAC or MPEG-TS.
    /// </summary>
    public static BrowserProfile Firefox { get; } = new("firefox", "Firefox", "Windows, macOS, Linux (system decoders)",
        [C("mp4", B), C("mov", P), C("webm", B), C("ogg", P), C("mp3", P), C("adts", P), C("flac", P), C("wav", P)],
        [
            R("h264", "mp4", "mov") with { Profiles = H264Common, HardwareProfiles = H264Extended },
            R("hevc", "mp4") with { HardwareProfiles = HevcMain },
            R("av1", "mp4", "webm"),
            R("vp9", "mp4", "webm"),
            R("vp8", "webm"),
            R("aac", "mp4", "mov", "adts"),
            R("mp3", "mp3", "mp4"),
            R("opus", "mp4", "webm", "ogg"),
            R("vorbis", "webm", "ogg"),
            R("flac", "flac", "ogg", "mp4"),
            R("pcm", "wav"),
        ])
    { VerifiedWith = "Firefox 141.0 on Linux x64 with system FFmpeg libraries, driven by Playwright 1.55.0" };

    /// <summary>
    /// The WebKit engine as Playwright builds it for macOS, which uses the system media stack Safari uses. Wants <c>hvc1</c> rather than
    /// <c>hev1</c> for downloaded HEVC; no Matroska, no AV1, no Opus or MP3 in MP4.
    /// </summary>
    public static BrowserProfile Safari { get; } = new("safari", "Safari (macOS)", "macOS",
        [C("mp4", B), C("mov", P), C("3gp", P), C("mpegts", P), C("webm", B), C("ogg", P), C("caf", P), C("aiff", P), C("wav", P), C("mp3", P), C("adts", P), C("flac", P), C("ac3", P)],
        [
            R("h264", "mp4", "mov", "3gp", "mpegts"),
            R("hevc", "mp4", "mov") with { Tags = ["hvc1", "dvh1"], Modes = P },
            R("hevc", "mp4") with { Modes = M },
            R("mpeg4", "mp4", "mov", "3gp"),
            R("vp8", "webm"),
            R("vp9", "mp4", "webm"),
            R("aac", "mp4", "mov", "3gp", "mpegts", "adts", "caf"),
            R("mp3", "mp3"),
            R("ac3", "mp4", "ac3"),
            R("eac3", "mp4"),
            R("alac", "mp4", "mov", "caf"),
            R("flac", "flac", "mp4"),
            R("opus", "webm", "ogg", "caf"),
            R("opus", "mp4") with { Modes = P },
            R("vorbis", "webm", "ogg"),
            R("pcm", "wav", "aiff", "caf", "mov"),
        ])
    { VerifiedWith = "WebKit 26.0 (Playwright build) on macOS arm64, driven by Playwright 1.55.0" };

    /// <summary>
    /// The WebKit engine as Playwright builds it for Linux (WPE), whose media pipeline is GStreamer: it plays whatever the host's
    /// GStreamer plug-ins decode, which on a host with the FFmpeg plug-in is nearly everything. Not a model of Safari.
    /// </summary>
    public static BrowserProfile WebKitLinux { get; } = new("webkit-linux", "WebKit (Linux, GStreamer)", "Linux (GStreamer with FFmpeg plug-ins)",
        [C("mp4", B), C("mov", P), C("3gp", P), C("matroska", P), C("webm", B), C("ogg", P), C("mp3", P), C("adts", P), C("flac", P), C("wav", P), C("aiff", P), C("ac3", P), C("avi", P), C("flv", P), C("mpegts", P), C("asf", P)],
        [
            R("h264", "*"), R("hevc", "*"), R("mpeg4", "*"), R("av1", "*"), R("vp8", "*"), R("vp9", "*"),
            R("aac", "*"), R("mp3", "*"), R("ac3", "*"), R("eac3", "*"), R("alac", "*"), R("flac", "*"), R("opus", "*"), R("vorbis", "*"), R("pcm", "*"),
            R("wmv2", "*"), R("wmav2", "*"),
        ])
    { VerifiedWith = "WebKit 26.0 (Playwright WPE build) on Linux x64, driven by Playwright 1.55.0" };

    /// <summary>
    /// Safari on iPhone and iPad (every iOS browser uses this engine), from Apple's and MDN's public documentation; not verified in a browser.
    /// Hardware decoders only: no AV1 before the A17 and M3 generation, no 4:2:2 or 4:4:4 H.264.
    /// </summary>
    public static BrowserProfile IosSafari { get; } = new("ios-safari", "Safari (iOS)", "iOS, iPadOS",
        [C("mp4", B), C("mov", P), C("3gp", P), C("mp3", P), C("adts", P), C("caf", P), C("aiff", P), C("wav", P), C("flac", P)],
        [
            R("h264", "mp4", "mov", "3gp") with { Profiles = H264Common },
            R("hevc", "mp4", "mov") with { Tags = ["hvc1", "dvh1"], Modes = P },
            R("hevc", "mp4") with { Modes = M },
            R("aac", "mp4", "mov", "3gp", "adts", "caf"),
            R("mp3", "mp3"),
            R("ac3", "mp4"), R("eac3", "mp4"), R("alac", "mp4", "mov", "caf"),
            R("flac", "flac", "mp4"),
            R("opus", "mp4", "caf"),
            R("pcm", "wav", "aiff", "caf", "mov"),
        ])
    { };

    /// <summary>
    /// Chrome on Android, from Chrome's and Android's public documentation; not verified in a browser. HEVC plays only where the device
    /// has a hardware decoder.
    /// </summary>
    public static BrowserProfile AndroidChrome { get; } = new("android-chrome", "Chrome (Android)", "Android",
        [C("mp4", B), C("3gp", P), C("webm", B), C("matroska", P), C("ogg", P), C("mp3", P), C("adts", P), C("flac", P), C("wav", P)],
        [
            R("h264", "mp4", "3gp", "matroska") with { Profiles = H264Common },
            R("hevc", "mp4", "matroska") with { HardwareProfiles = HevcMain },
            R("av1", "mp4", "webm", "matroska"),
            R("vp9", "mp4", "webm", "matroska"),
            R("vp8", "webm", "matroska"),
            R("aac", "mp4", "3gp", "matroska", "adts"),
            R("mp3", "mp3", "mp4"),
            R("opus", "mp4", "webm", "ogg", "matroska"),
            R("vorbis", "mp4", "webm", "ogg", "matroska"),
            R("flac", "flac", "ogg", "mp4"),
            R("amr-nb", "3gp"), R("amr-wb", "3gp"),
            R("pcm", "wav"),
        ])
    { };

    /// <summary>
    /// The least a browser needs to play an MP4 anywhere: H.264 Baseline, Main or High up to level 4.2 with AAC-LC or MP3 in MP4,
    /// plus plain MP3 files. What every profile above accepts; the safe target for <c>MakePlayable</c>.
    /// </summary>
    public static BrowserProfile GenericMp4 { get; } = new("generic-mp4", "Generic MP4", "any",
        [C("mp4", B), C("mp3", P)],
        [
            R("h264", "mp4") with { Profiles = [66, 77, 100], MaxLevel = 42, MaxBitDepth = 8, MaxChroma = Codecs.ChromaFormat.Yuv420, Tags = ["avc1"] },
            R("aac", "mp4") with { Profiles = [2] },
            R("mp3", "mp3", "mp4"),
        ])
    { };

    /// <summary>Every built-in profile, in a stable order.</summary>
    public static IReadOnlyList<BrowserProfile> All { get; } = [Chrome, Chromium, Firefox, Safari, WebKitLinux, IosSafari, AndroidChrome, GenericMp4];

    /// <summary>Finds a built-in profile by <see cref="BrowserProfile.Id"/> (ordinal comparison).</summary>
    /// <param name="id">For example <c>chrome</c>.</param>
    /// <returns>The profile, or null.</returns>
    public static BrowserProfile? Find(string id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}
