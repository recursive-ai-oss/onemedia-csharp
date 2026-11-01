using System.Text;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Playback;

namespace RecursiveAI.OneMedia.Tests.Playback;

// The profile tables are data, so they are pinned as text: a change to any row shows up as a diff of profiles.golden.txt. The reason codes
// and what can fix each are pinned with hand-made file descriptions, one case per code.
public class PlaybackTests
{
    private static string DataPath(string dir, string name) => Path.Combine(AppContext.BaseDirectory, "testdata", dir, name);

    private static PlaybackTrack Video(string codec, int profile = -1, int level = -1, string? tag = null, int depth = 8, ChromaFormat chroma = ChromaFormat.Yuv420) =>
        new(0, TrackKind.Video, new CodecId(codec), tag, null, profile, level, depth, chroma, false);

    private static PlaybackTrack Audio(string codec, int index = 1, int profile = -1) =>
        new(index, TrackKind.Audio, new CodecId(codec), null, null, profile, -1, 0, ChromaFormat.Unknown, false);

    private static PlaybackDescription File(string container, bool segmented, bool fastStart, params PlaybackTrack[] tracks) =>
        new(container, "video/" + container, "video/" + container, segmented, fastStart, tracks);

    private static PlaybackVerdict Judge(PlaybackDescription d, BrowserProfile p) => Playability.Check(d, p);

    // ---- the data ----

    [Fact]
    public void ProfileDataIsPinned()
    {
        StringBuilder sb = new();
        foreach (BrowserProfile p in BrowserProfiles.All)
        {
            sb.Append("profile ").Append(p.Id).Append(" | ").Append(p.DisplayName).Append(" | ").Append(p.Platform).Append(" | ").AppendLine(p.VerifiedWith);
            foreach (ContainerSupport c in p.Containers)
            {
                sb.Append("  container ").Append(c.Container).Append(' ').AppendLine(c.Modes.ToString());
            }

            foreach (CodecRule r in p.Codecs)
            {
                sb.Append("  codec ").Append(r.Codec).Append(" in ").Append(string.Join(',', r.Containers)).Append(' ').Append(r.Modes);
                Append(sb, "profiles", r.Profiles);
                Append(sb, "hardware", r.HardwareProfiles);
                Append(sb, "tags", r.Tags);
                if (r.MaxLevel > 0) { sb.Append(" maxLevel=").Append(r.MaxLevel); }
                if (r.MaxBitDepth > 0) { sb.Append(" maxBits=").Append(r.MaxBitDepth); }
                if (r.MaxChroma != ChromaFormat.Unknown) { sb.Append(" maxChroma=").Append(r.MaxChroma); }
                sb.AppendLine();
            }
        }

        string expected = System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "playback", "profiles.golden.txt")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string actual = sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (expected != actual)
        {
            string path = Path.Combine(Path.GetTempPath(), "profiles.actual.txt");
            System.IO.File.WriteAllText(path, actual);
            Assert.Fail($"The profile tables changed; the new text is in {path}. If the change is intended, replace tests/OneMedia.Tests/Playback/profiles.golden.txt with it.");
        }

        static void Append<T>(StringBuilder sb, string name, IReadOnlyList<T>? values)
        {
            if (values is not null)
            {
                sb.Append(' ').Append(name).Append('=').Append(string.Join('/', values));
            }
        }
    }

    [Fact]
    public void ProfilesAreFoundByIdAndEveryRuleNamesAKnownContainer()
    {
        string[] known = ["mp4", "mov", "3gp", "webm", "matroska", "ogg", "mp3", "adts", "flac", "wav", "aiff", "caf", "avi", "flv", "mpegts", "asf", "ac3", "amr", "*"];
        foreach (BrowserProfile p in BrowserProfiles.All)
        {
            Assert.Same(p, BrowserProfiles.Find(p.Id));
            Assert.All(p.Containers, c => Assert.Contains(c.Container, known));
            Assert.All(p.Codecs, r => Assert.All(r.Containers, c => Assert.Contains(c, known)));
        }

        Assert.Null(BrowserProfiles.Find("netscape"));
    }

    // ---- reason codes ----

    [Fact]
    public void ACodecTheBrowserLacksBlocksWithTheRemedyForItsKind()
    {
        PlaybackVerdict v = Judge(File("mp4", false, true, Video("h264", 100, 40), Audio("aac", 1, 2)), BrowserProfiles.Chromium);
        Assert.False(v.CanPlay);
        Assert.Collection(v.Blockers,
            b => { Assert.Equal("codec.unsupported", b.Code); Assert.Equal(0, b.TrackIndex); Assert.Equal(PlaybackRemedy.VideoBlocker, b.Remedy); },
            b => { Assert.Equal("codec.unsupported", b.Code); Assert.Equal(1, b.TrackIndex); Assert.Equal(PlaybackRemedy.TranscodeAudio, b.Remedy); });
        Assert.False(v.CanBeMadePlayable);
    }

    [Fact]
    public void AnAudioOnlyBlockerCanBeMadePlayable()
    {
        // Chrome has no AC-3; the H.264 video is fine, so the file can be fixed by an audio transcode.
        PlaybackVerdict v = Judge(File("mp4", false, true, Video("h264", 100, 40), Audio("ac3")), BrowserProfiles.Chrome);
        Assert.Equal(["codec.unsupported"], v.Blockers.Select(b => b.Code));
        Assert.True(v.CanBeMadePlayable);
    }

    [Fact]
    public void AContainerTheBrowserLacksIsFixedByARemux()
    {
        PlaybackVerdict v = Judge(File("matroska", false, true, Video("h264", 100, 40), Audio("aac", 1, 2)), BrowserProfiles.Safari);
        PlaybackBlocker c = Assert.Single(v.Progressive.Blockers, b => b.Kind == PlaybackBlockerKind.Container);
        Assert.Equal("container.unsupported", c.Code);
        Assert.Equal(PlaybackRemedy.Remux, c.Remedy);
        Assert.Equal(-1, c.TrackIndex);
        // The codecs are known to Safari, just not inside Matroska: also a remux.
        Assert.All(v.Progressive.Blockers.Where(b => b.TrackIndex >= 0), b => { Assert.Equal("codec.container", b.Code); Assert.Equal(PlaybackRemedy.Remux, b.Remedy); });
        Assert.True(v.CanBeMadePlayable);
    }

    [Fact]
    public void TheHevcSampleEntryFormDecidesSafariDownloadsButNotMediaSource()
    {
        PlaybackVerdict hev1 = Judge(File("mp4", false, true, Video("hevc", 1, 120, "hev1"), Audio("aac", 1, 2)), BrowserProfiles.Safari);
        Assert.False(hev1.Progressive.Plays);
        PlaybackBlocker tag = Assert.Single(hev1.Progressive.Blockers);
        Assert.Equal(("codec.tag", PlaybackBlockerKind.CodecTag, PlaybackRemedy.Remux), (tag.Code, tag.Kind, tag.Remedy));

        PlaybackVerdict hvc1 = Judge(File("mp4", false, true, Video("hevc", 1, 120, "hvc1"), Audio("aac", 1, 2)), BrowserProfiles.Safari);
        Assert.True(hvc1.Progressive.Plays);

        // MediaSource takes either form (observed), but needs fragments.
        PlaybackVerdict frag = Judge(File("mp4", true, true, Video("hevc", 1, 120, "hev1"), Audio("aac", 1, 2)), BrowserProfiles.Safari);
        Assert.True(frag.MediaSource.Plays);
        Assert.Equal(PlaybackMode.MediaSource, frag.Modes);
    }

    [Fact]
    public void ProfileLevelDepthAndChromaAreReportedInThatOrder()
    {
        BrowserProfile g = BrowserProfiles.GenericMp4;
        PlaybackBlocker b = Assert.Single(Judge(File("mp4", false, true, Video("h264", 244, 40, "avc1", 8, ChromaFormat.Yuv444), Audio("aac", 1, 2)), g).Blockers);
        Assert.Equal(("codec.profile", PlaybackBlockerKind.CodecProfile, PlaybackRemedy.VideoBlocker), (b.Code, b.Kind, b.Remedy));

        b = Assert.Single(Judge(File("mp4", false, true, Video("h264", 100, 51, "avc1"), Audio("aac", 1, 2)), g).Blockers);
        Assert.Equal("codec.level", b.Code);

        b = Assert.Single(Judge(File("mp4", false, true, Video("h264", 100, 40, "avc1", 10), Audio("aac", 1, 2)), g).Blockers);
        Assert.Equal("codec.bit-depth", b.Code);

        b = Assert.Single(Judge(File("mp4", false, true, Video("h264", 100, 40, "avc1", 8, ChromaFormat.Yuv422), Audio("aac", 1, 2)), g).Blockers);
        Assert.Equal("codec.chroma", b.Code);

        b = Assert.Single(Judge(File("mp4", false, true, Video("h264", 100, 40, "avc3"), Audio("aac", 1, 2)), g).Blockers);
        Assert.Equal("codec.tag", b.Code);

        // HE-AAC is reported as profile 5, which the generic profile leaves out; the fix is an audio transcode.
        b = Assert.Single(Judge(File("mp4", false, true, Video("h264", 100, 40, "avc1"), Audio("aac", 1, 5)), g).Blockers);
        Assert.Equal(("codec.profile", PlaybackRemedy.TranscodeAudio), (b.Code, b.Remedy));
    }

    [Fact]
    public void HardwareDependentProfilesPlayWithAWarning()
    {
        PlaybackVerdict v = Judge(File("mp4", false, true, Video("h264", 110, 40, "avc1", 10), Audio("aac", 1, 2)), BrowserProfiles.Chrome);
        Assert.True(v.CanPlay);
        PlaybackWarning w = Assert.Single(v.Progressive.Warnings);
        Assert.Equal(("hardware-dependent", 0), (w.Code, w.TrackIndex));
    }

    [Fact]
    public void MediaSourceNeedsFragmentsAndAMovieAtTheEndOnlyWarnsForDownloads()
    {
        PlaybackDescription plain = File("mp4", false, false, Video("h264", 100, 40, "avc1"), Audio("aac", 1, 2));
        PlaybackVerdict v = Judge(plain, BrowserProfiles.Chrome);
        Assert.True(v.Progressive.Plays);
        Assert.Equal("progressive.movie-at-end", Assert.Single(v.Progressive.Warnings).Code);
        PlaybackBlocker seg = Assert.Single(v.MediaSource.Blockers);
        Assert.Equal(("mediasource.not-segmented", PlaybackRemedy.Remux), (seg.Code, seg.Remedy));
        Assert.True(v.CanPlay);
        Assert.Empty(v.Blockers);

        // A container that is not accepted through MediaSource at all says so with its own code.
        PlaybackVerdict mkv = Judge(File("matroska", false, true, Video("h264", 100, 40), Audio("aac", 1, 2)), BrowserProfiles.Chrome);
        Assert.Equal("container.mediasource", Assert.Single(mkv.MediaSource.Blockers, b => b.TrackIndex < 0).Code);
    }

    [Fact]
    public void ACodecThatPlaysOnlyOneWayReportsTheMode()
    {
        // Safari plays Opus in MP4 from a URL but rejects it for MediaSource; Opus in WebM goes through both, so a remux fixes it.
        PlaybackVerdict v = Judge(File("mp4", true, true, Audio("opus", 0)), BrowserProfiles.Safari);
        Assert.True(v.Progressive.Plays);
        PlaybackBlocker b = Assert.Single(v.MediaSource.Blockers);
        Assert.Equal(("codec.container", PlaybackBlockerKind.CodecInContainer, PlaybackRemedy.Remux), (b.Code, b.Kind, b.Remedy));

        // A codec with rules for one mode only says which mode it lacks.
        BrowserProfile oneWay = new("t", "T", "x", [new ContainerSupport("mp4", PlaybackMode.Both)], [new CodecRule("opus", "mp4") with { Modes = PlaybackMode.Progressive }]);
        PlaybackBlocker m = Assert.Single(Judge(File("mp4", true, true, Audio("opus", 0)), oneWay).MediaSource.Blockers);
        Assert.Equal(("codec.mode", PlaybackBlockerKind.Codec, PlaybackRemedy.TranscodeAudio), (m.Code, m.Kind, m.Remedy));
    }

    [Fact]
    public void EncryptedAndEmptyFilesHaveNoRemedy()
    {
        PlaybackTrack enc = Video("h264", 100, 40, "avc1") with { IsProtected = true };
        PlaybackBlocker p = Assert.Single(Judge(File("mp4", false, true, enc), BrowserProfiles.Chrome).Blockers);
        Assert.Equal(("track.protected", PlaybackRemedy.None), (p.Code, p.Remedy));

        PlaybackBlocker none = Assert.Single(Judge(File("mp4", false, true), BrowserProfiles.Chrome).Blockers);
        Assert.Equal("tracks.none", none.Code);
    }

    [Fact]
    public void AnIncompleteCodecStringIsReportedForMediaSource()
    {
        PlaybackDescription d = File("mp4", true, true, Video("h264", 100, 40, "avc1"), Audio("aac", 1, 2));
        Assert.Contains(Judge(d, BrowserProfiles.Chrome).MediaSource.Warnings, w => w.Code == "mediasource.codec-string");
    }

    // ---- from real files ----

    [Fact]
    public void DescribesFilesFromTheirContainersAndCodecs()
    {
        using MediaFile fast = Media.Open(DataPath("isobmff", "faststart.mp4"));
        PlaybackDescription d = fast.DescribePlayback();
        Assert.Equal("mp4", d.Container);
        Assert.True(d.IsFastStart);
        Assert.False(d.IsSegmented);
        Assert.StartsWith("video/mp4; codecs=\"avc1.", d.MimeType);

        using MediaFile frag = Media.Open(DataPath("isobmff", "frag.mp4"));
        Assert.True(frag.DescribePlayback().IsSegmented);

        using MediaFile mov = Media.Open(DataPath("isobmff", "bframes_aac.mov"));
        Assert.Equal("mov", mov.DescribePlayback().Container);

        using MediaFile gp = Media.Open(DataPath("isobmff", "video.3gp"));
        Assert.Equal("3gp", gp.DescribePlayback().Container);

        // MP3 in an MP4 has no configuration to read a codec string from; browsers name it mp4a.6B.
        using MediaFile mp3 = Media.Open(DataPath("isobmff", "mp3.mp4"));
        Assert.Contains("mp4a.6B", mp3.DescribePlayback().MimeType, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHappyPathJudgesAFileByPath()
    {
        string path = DataPath("isobmff", "faststart.mp4");
        PlaybackVerdict chromium = Media.CanPlay(path, BrowserProfiles.Chromium);
        Assert.False(chromium.CanPlay);
        Assert.Contains(chromium.Blockers, b => b.Code == "codec.unsupported" && b.Remedy == PlaybackRemedy.VideoBlocker);
        using MediaFile f = Media.Open(path);
        Assert.True(chromium.Blockers.SequenceEqual(f.CanPlay(BrowserProfiles.Chromium).Blockers));
    }
}
