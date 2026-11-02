using RecursiveAI.OneMedia.Playback;
using RecursiveAI.OneMedia.Remuxing;
using RecursiveAI.OneMedia.Tests.Avi;

namespace RecursiveAI.OneMedia.Tests.Remuxing;

// A remux that leaves out the picture (or all the sound) must say so as more than a loss line, and MakePlayable must not call such an output made.
public sealed class LostMediaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lostmedia-" + Guid.NewGuid().ToString("N"));

    public LostMediaTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "avi", name);

    private string Out(string name) => Path.Combine(_dir, name);

    // Track 0: H.264 as Annex B with no parameter sets anywhere, so no sample entry can be built and the track is dropped.
    // Track 1: PCM audio. Track 2: video with a FourCC no MP4 entry exists for, refused by the muxer.
    private string Damaged(bool unknownVideo, bool withAudio = true)
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "H264", 1, 10, 0, AviBuilder.Bitmap(64, 48, "H264")));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, [0, 0, 0, 1, 0x65, 1, 2, 3, 4, 5, 6, 7, 8]));
        int next = 1;
        if (withAudio)
        {
            b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1, 8000, 2, AviBuilder.Wave(1, 1, 8000, 2, 16)));
            b.Chunks.Add(new AviBuilder.ChunkSpec(next, new byte[1600], Kind: "wb"));
            next++;
        }

        if (unknownVideo)
        {
            b.Streams.Add(new AviBuilder.StreamSpec("vids", "ZZZZ", 1, 10, 0, AviBuilder.Bitmap(64, 48, "ZZZZ")));
            b.Chunks.Add(new AviBuilder.ChunkSpec(next, [1, 2, 3, 4]));
        }

        string path = Out($"damaged-{unknownVideo}-{withAudio}.avi");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public void AnAviWithAnOddLengthAvcCRecordRemuxesToMp4WithItsVideoAndEveryPacket()
    {
        string output = Out("odd.mp4");
        using (MediaFile input = Media.Open(Fixture("avcc-odd.avi")))
        {
            Assert.NotNull(input.Video!.Rfc6381Codec());
            using FileStream fs = File.Create(output);
            RemuxReport report = Remuxer.Remux(input, fs, RemuxFormat.Mp4);
            Assert.Empty(report.LostMediaTracks);
            Assert.False(report.HasLosses);
        }

        using MediaFile source = Media.Open(Fixture("avcc-odd.avi"));
        using MediaFile result = Media.Open(output);
        Assert.Equal(source.Video!.Rfc6381Codec(), result.Video!.Rfc6381Codec());
        Assert.NotNull(result.Audio);
        Assert.Equal(Payloads(source, TrackKind.Video).Count, Payloads(result, TrackKind.Video).Count);
        Assert.Equal(Payloads(source, TrackKind.Video), Payloads(result, TrackKind.Video));
    }

    private static List<byte[]> Payloads(MediaFile f, TrackKind kind)
    {
        int index = f.Tracks.First(t => t.Kind == kind).Index;
        List<byte[]> all = [];
        IDemuxer d = f.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            if (v.Info.TrackIndex == index)
            {
                all.Add(v.Data.ToArray());
            }
        }

        return all;
    }

    [Fact]
    public void ADroppedVideoTrackIsListedAsLostMedia()
    {
        using MediaFile input = Media.Open(Damaged(unknownVideo: false));
        using MemoryStream ms = new();
        RemuxReport report = Remuxer.Remux(input, ms, RemuxFormat.Mp4);
        Assert.Equal([0], report.LostMediaTracks);
        Assert.Contains(report.Losses, l => l.InputTrack == 0 && l.Reason == LossReason.MissingConfiguration);
    }

    [Fact]
    public void ADeselectedTrackIsTheCallersChoiceNotALoss()
    {
        using MediaFile input = Media.Open(Damaged(unknownVideo: false));
        using MemoryStream ms = new();
        RemuxReport report = Remuxer.Remux(input, ms, RemuxFormat.Mp4, new RemuxOptions { Select = t => t.Kind == TrackKind.Audio });
        Assert.Empty(report.LostMediaTracks);
        RemuxOptions drop = new() { Edits = new Dictionary<int, TrackEdit> { [0] = new TrackEdit { Drop = true } } };
        using MemoryStream ms2 = new();
        Assert.Empty(Remuxer.Remux(input, ms2, RemuxFormat.Mp4, drop).LostMediaTracks);
    }

    [Fact]
    public void TheOnlyAudioTrackLeftOutIsLostMediaButOneOfTwoIsNot()
    {
        // Two audio tracks, one of them a codec MP4 cannot carry: the other still plays, so nothing media-wide is lost.
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1, 8000, 2, AviBuilder.Wave(1, 1, 8000, 2, 16)));
        b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1, 8000, 2, AviBuilder.Wave(0x7777, 1, 8000, 2, 16)));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, new byte[1600], Kind: "wb"));
        b.Chunks.Add(new AviBuilder.ChunkSpec(1, new byte[1600], Kind: "wb"));
        using MediaFile two = Media.Open(b.Build());
        using MemoryStream ms = new();
        RemuxReport report = Remuxer.Remux(two, ms, RemuxFormat.Mp4);
        Assert.Contains(report.Losses, l => l.InputTrack == 1 && l.Kind == LossKind.Track);
        Assert.Empty(report.LostMediaTracks);

        AviBuilder only = new();
        only.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1, 8000, 2, AviBuilder.Wave(0x7777, 1, 8000, 2, 16)));
        only.Chunks.Add(new AviBuilder.ChunkSpec(0, new byte[1600], Kind: "wb"));
        only.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1, 8000, 2, AviBuilder.Wave(1, 1, 8000, 2, 16)));
        only.Chunks.Add(new AviBuilder.ChunkSpec(1, new byte[1600], Kind: "wb"));
        using MediaFile mixed = Media.Open(only.Build());
        using MemoryStream ms2 = new();
        RemuxReport r2 = Remuxer.Remux(mixed, ms2, RemuxFormat.Mp4);
        Assert.Empty(r2.LostMediaTracks);
    }

    [Fact]
    public void LossDetailNamesTheInputTrackNotTheMuxersOwnNumber()
    {
        // Input track 0 is dropped before the muxer sees it and input track 2 is refused by it as the muxer's second track: the loss
        // must say 2, the number the caller sees in the input.
        using MediaFile input = Media.Open(Damaged(unknownVideo: true));
        using MemoryStream ms = new();
        RemuxReport report = Remuxer.Remux(input, ms, RemuxFormat.Mp4);
        RemuxLoss refused = Assert.Single(report.Losses, l => l.Kind == LossKind.Track && l.InputTrack == 2);
        Assert.StartsWith("Input track 2:", refused.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Track 1", refused.Detail, StringComparison.Ordinal);
        Assert.Equal([0, 2], report.LostMediaTracks.OrderBy(i => i));
    }

    [Fact]
    public void MakePlayableFailsWhenTheVideoCouldNotBeCarried()
    {
        string input = Damaged(unknownVideo: false);
        string output = Out("made.mp4");
        MakePlayableReport r = PlayableMaker.MakePlayable(input, output, BrowserProfiles.Chrome);
        Assert.NotEqual(MakePlayableStatus.Made, r.Status);
        Assert.NotEqual(MakePlayableStatus.AlreadyPlayable, r.Status);
        Assert.False(r.OutputWritten);
        Assert.False(File.Exists(output));
        Assert.NotEmpty(r.Remaining);
    }

    [Fact]
    public void MakePlayableStillMakesAFileWhoseVideoIsCarried()
    {
        string output = Out("odd-made.mp4");
        MakePlayableReport r = PlayableMaker.MakePlayable(Fixture("avcc-odd.avi"), output, BrowserProfiles.Chrome);
        Assert.Equal(MakePlayableStatus.Made, r.Status);
        using MediaFile result = Media.Open(output);
        Assert.NotNull(result.Video);
        Assert.NotNull(result.Audio);
    }
}
