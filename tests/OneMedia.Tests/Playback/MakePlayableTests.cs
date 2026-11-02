using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Playback;

namespace RecursiveAI.OneMedia.Tests.Playback;

// The decision logic of MakePlayable on the small generated fixtures: copy, remux, transcode audio, or refuse with the blocker codes.
public sealed class MakePlayableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "makeplayable-" + Guid.NewGuid().ToString("N"));

    public MakePlayableTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Data(string folder, string name) => Path.Combine(AppContext.BaseDirectory, "testdata", folder, name);

    private string Out(string name) => Path.Combine(_dir, name);

    private static MakePlayableOptions WithAudio => new() { AudioTranscoder = AudioTranscoder.Default };

    private static List<(int Track, byte[] Data, MediaTime Pts)> Packets(string path)
    {
        using MediaFile f = Media.Open(path);
        List<(int, byte[], MediaTime)> all = [];
        IDemuxer d = f.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            all.Add((v.Info.TrackIndex, v.Data.ToArray(), v.Info.Pts));
        }

        return all;
    }

    [Fact]
    public void AFileThatAlreadyPlaysIsCopiedUnchanged()
    {
        string src = Data("audio", "opus.webm");
        MakePlayableReport r = PlayableMaker.MakePlayable(src, Out("a.webm"), BrowserProfiles.Chromium);
        Assert.Equal(MakePlayableStatus.AlreadyPlayable, r.Status);
        Assert.True(r.OutputWritten);
        Assert.Equal(MakePlayableActionKind.Copy, Assert.Single(r.Actions).Kind);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Out("a.webm")));
    }

    [Fact]
    public void ARemuxAloneFixesAContainerBlockerAndLeavesEveryPacketUntouched()
    {
        // Firefox plays Vorbis in WebM but takes no Matroska.
        string src = Data("audio", "vorbis.mka");
        PlaybackVerdict before = Media.CanPlay(src, BrowserProfiles.Firefox);
        Assert.False(before.Progressive.Plays);
        MakePlayableReport r = PlayableMaker.MakePlayable(src, Out("v.webm"), BrowserProfiles.Firefox);
        Assert.Equal(MakePlayableStatus.Made, r.Status);
        Assert.Equal(global::RecursiveAI.OneMedia.Remuxing.RemuxFormat.WebM, r.Target);
        Assert.Empty(r.AudioTranscodes);
        Assert.True(r.After!.Progressive.Plays);
        Assert.Contains(r.Actions, a => a.Kind == MakePlayableActionKind.Remux);
        Assert.Equal(Packets(src).Select(p => p.Data), Packets(Out("v.webm")).Select(p => p.Data));
    }

    [Fact]
    public void AnAudioCodecThatNeedsAnotherContainerIsRemuxedNotTranscoded()
    {
        // Chromium plays MP3 in MP4 but not in Matroska.
        MakePlayableReport r = PlayableMaker.MakePlayable(Data("audio", "mp3.mka"), Out("m.mp4"), BrowserProfiles.Chromium, WithAudio);
        Assert.Equal(MakePlayableStatus.Made, r.Status);
        Assert.Equal(global::RecursiveAI.OneMedia.Remuxing.RemuxFormat.Mp4, r.Target);
        Assert.Empty(r.AudioTranscodes);
        Assert.Equal(Packets(Data("audio", "mp3.mka")).Select(p => p.Data), Packets(Out("m.mp4")).Select(p => p.Data));
    }

    [Fact]
    public void AudioThatNeedsATranscodeIsReportedAndNothingIsWrittenWithoutATranscoder()
    {
        MakePlayableReport r = PlayableMaker.MakePlayable(Data("audio", "aac.m4a"), Out("n.mp4"), BrowserProfiles.Chromium);
        Assert.Equal(MakePlayableStatus.NeedsAudioTranscode, r.Status);
        Assert.False(r.OutputWritten);
        Assert.False(File.Exists(Out("n.mp4")));
        AudioTranscodeInfo t = Assert.Single(r.AudioTranscodes);
        Assert.Equal(CodecId.Aac, t.From);
        Assert.Equal(CodecId.Opus, t.To);
        Assert.Equal("codec.unsupported", Assert.Single(r.Remaining).Code);
    }

    [Fact]
    public void AacIsTranscodedToOpusWithTheEncoderDelayDeclaredAndTheDurationKept()
    {
        string src = Data("audio", "aac.m4a");
        MakePlayableReport r = PlayableMaker.MakePlayable(src, Out("t.mp4"), BrowserProfiles.Chromium, WithAudio);
        Assert.Equal(MakePlayableStatus.Made, r.Status);
        Assert.True(r.After!.Progressive.Plays);
        AudioTranscodeInfo t = Assert.Single(r.AudioTranscodes);
        Assert.Equal(CodecId.Opus, t.To);
        Assert.Equal(48000, t.SampleRate);
        Assert.True(t.EncoderDelay.Ticks > 0);

        using MediaFile input = Media.Open(src);
        using MediaFile output = Media.Open(Out("t.mp4"));
        MediaTrack outTrack = output.Audio!;
        Assert.Equal(CodecId.Opus, outTrack.Codec);
        Assert.Equal(t.EncoderDelay.Ticks, outTrack.Edit.Priming.Rescale(48000, MediaRounding.NearestAwayFromZero).Ticks);

        // Decoded lengths agree within one Opus frame (20 ms).
        long inFrames = Frames(input.Decode(), out int inRate);
        long outFrames = Frames(output.Decode(), out int outRate);
        Assert.Equal(48000, outRate);
        long diffMs = Math.Abs(((outFrames * 1000) / outRate) - ((inFrames * 1000) / inRate));
        Assert.True(diffMs <= 21, $"decoded lengths differ by {diffMs} ms");

        static long Frames(OneAudio.IAudioDecoder d, out int rate)
        {
            using (d)
            {
                rate = d.Format.SampleRate;
                float[] buf = new float[4096 * d.Format.Channels];
                long n = 0;
                int got;
                while ((got = d.Read(buf)) > 0)
                {
                    n += got;
                }

                return n;
            }
        }
    }

    [Fact]
    public void PcmIsTranscodedToo()
    {
        MakePlayableReport r = PlayableMaker.MakePlayable(Data("pcm", "caf-info-chan.caf"), Out("p.mp4"), BrowserProfiles.Chromium, WithAudio);
        Assert.Equal(MakePlayableStatus.Made, r.Status);
        Assert.Equal(CodecId.Pcm, Assert.Single(r.AudioTranscodes).From);
    }

    [Fact]
    public void VideoBlockersAreRefusedWithTheirCodes()
    {
        MakePlayableReport r = PlayableMaker.MakePlayable(Data("isobmff", "bframes_aac.mp4"), Out("v.mp4"), BrowserProfiles.Chromium, WithAudio);
        Assert.Equal(MakePlayableStatus.Refused, r.Status);
        Assert.False(File.Exists(Out("v.mp4")));
        Assert.Contains(r.Remaining, b => b.Code == "codec.unsupported" && b.Remedy == PlaybackRemedy.VideoBlocker);
    }

    [Fact]
    public void AProfileThatPlaysNoCodecWeCanEncodeRefusesTheAudio()
    {
        // Generic MP4 takes AAC and MP3 only; the one encoder here makes Opus.
        MakePlayableReport r = PlayableMaker.MakePlayable(Data("audio", "opus.mka"), Out("g.mp4"), BrowserProfiles.GenericMp4, WithAudio);
        Assert.Equal(MakePlayableStatus.Refused, r.Status);
        Assert.Contains(r.Remaining, b => b.Code == "transcode.no-target");
    }

    [Fact]
    public void TheOutputMustDifferFromTheInputAndAnExistingFileIsRefused()
    {
        string src = Data("audio", "opus.webm");
        Assert.Throws<ArgumentException>(() => PlayableMaker.MakePlayable(src, src, BrowserProfiles.Chromium));
        File.WriteAllText(Out("x.webm"), "x");
        Assert.Throws<IOException>(() => PlayableMaker.MakePlayable(src, Out("x.webm"), BrowserProfiles.Chromium));
        Assert.Equal(MakePlayableStatus.AlreadyPlayable, PlayableMaker.MakePlayable(src, Out("x.webm"), BrowserProfiles.Chromium, new MakePlayableOptions { Overwrite = true }).Status);
    }

    [Fact]
    public void TheOutputExtensionsContainerIsTriedFirst()
    {
        MakePlayableReport r = PlayableMaker.MakePlayable(Data("audio", "aac.m4a"), Out("pref.mka"), BrowserProfiles.Chromium,
            new MakePlayableOptions { AudioTranscoder = AudioTranscoder.Default, PreferredTarget = global::RecursiveAI.OneMedia.Remuxing.RemuxFormat.Matroska });
        Assert.Equal(MakePlayableStatus.Made, r.Status);
        Assert.Equal(global::RecursiveAI.OneMedia.Remuxing.RemuxFormat.Matroska, r.Target);
        using MediaFile f = Media.Open(Out("pref.mka"));
        Assert.Equal(MediaFormatId.Matroska, f.Format);
    }
}
