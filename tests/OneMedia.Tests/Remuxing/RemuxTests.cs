using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Tests.Remuxing;

public class RemuxTests
{
    private static string Data(string folder, string name) => Path.Combine(AppContext.BaseDirectory, "testdata", folder, name);

    private sealed record Pkt(int Track, MediaTime Pts, MediaTime Dts, bool Key, byte[] Data);

    private static List<Pkt> ReadAll(MediaFile f)
    {
        List<Pkt> list = [];
        IDemuxer d = f.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            list.Add(new Pkt(v.Info.TrackIndex, v.Info.Pts, v.Info.Dts, v.Info.IsKeyframe, v.Data.ToArray()));
        }

        return list;
    }

    private static List<Pkt> ReadAll(byte[] bytes)
    {
        using MediaFile f = Media.Open(bytes);
        return ReadAll(f);
    }

    private static (byte[] Output, RemuxReport Report) Remux(string path, RemuxFormat format = RemuxFormat.Mp4, RemuxOptions? options = null)
    {
        using MediaFile input = Media.Open(path);
        using MemoryStream ms = new();
        RemuxReport report = Remuxer.Remux(input, ms, format, options);
        return (ms.ToArray(), report);
    }

    // NAL units of an Annex B access unit, parameter sets left out.
    private static List<byte[]> AnnexBNals(NalCodec codec, byte[] data)
    {
        List<byte[]> list = [];
        AnnexBReader r = new(data);
        while (r.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
        {
            if (nal.Length > 0 && !NalUnit.IsParameterSet(codec, NalUnit.GetType(codec, nal)))
            {
                list.Add(nal.ToArray());
            }
        }

        return list;
    }

    private static List<byte[]> LengthPrefixedNals(NalCodec codec, byte[] data)
    {
        List<byte[]> list = [];
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            int len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            byte[] nal = data.AsSpan(pos, len).ToArray();
            pos += len;
            if (!NalUnit.IsParameterSet(codec, NalUnit.GetType(codec, nal)))
            {
                list.Add(nal);
            }
        }

        Assert.Equal(data.Length, pos);
        return list;
    }

    [Theory]
    [InlineData("mpeg", "h264-aac.ts", NalCodec.H264)]
    [InlineData("mpeg", "h264-aac.m2ts", NalCodec.H264)]
    [InlineData("mpeg", "h264.264", NalCodec.H264)]
    [InlineData("mpeg", "hevc.265", NalCodec.Hevc)]
    public void AnnexBVideoBecomesLengthPrefixedWithItsRecordBuiltFromTheStream(string folder, string name, NalCodec codec)
    {
        string path = Data(folder, name);
        (byte[] output, RemuxReport report) = Remux(path);
        using MediaFile source = Media.Open(path);
        List<Pkt> input = ReadAll(source);
        using MediaFile result = Media.Open(output);
        List<Pkt> written = ReadAll(result);
        MediaTrack video = result.Video!;
        Assert.Equal(codec == NalCodec.H264 ? "avcC" : "hvcC", video.Configuration.Format);
        Assert.Contains(report.Tracks, t => t.Repackaging == "annexb-to-length-prefixed");

        int vin = source.Tracks.First(t => t.Kind == TrackKind.Video).Index;
        List<Pkt> a = input.Where(p => p.Track == vin).ToList();
        List<Pkt> b = written.Where(p => p.Track == video.Index).ToList();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(AnnexBNals(codec, a[i].Data), LengthPrefixedNals(codec, b[i].Data));
            Assert.Equal(a[i].Key, b[i].Key);
        }
    }

    [Theory]
    [InlineData("mpeg", "h264-aac.ts")]
    [InlineData("audio-es", "lc.aac")]
    public void AdtsHeadersAreRemovedAndTheConfigurationIsStoredOnce(string folder, string name)
    {
        string path = Data(folder, name);
        (byte[] output, RemuxReport report) = Remux(path);
        using MediaFile source = Media.Open(path);
        List<Pkt> input = ReadAll(source);
        using MediaFile result = Media.Open(output);
        List<Pkt> written = ReadAll(result);
        Assert.Contains(report.Tracks, t => t.Repackaging == "adts-to-raw");
        int ain = source.Audio!.Index;
        List<Pkt> a = input.Where(p => p.Track == ain).ToList();
        List<Pkt> b = written.Where(p => p.Track == result.Audio!.Index).ToList();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.True(RecursiveAI.OneMedia.Codecs.Aac.AdtsRepackager.TryGetRawData(a[i].Data, out ReadOnlySpan<byte> raw));
            Assert.True(raw.SequenceEqual(b[i].Data));
        }
    }

    [Theory]
    [InlineData("xiph-flv", "h264-aac.flv")]
    [InlineData("audio", "two-audio.mp4")]
    [InlineData("isobmff", "bframes_aac.mov")]
    [InlineData("audio", "opus.webm")]
    [InlineData("xiph-flv", "tags.flac")]
    [InlineData("audio-es", "ac3.ac3")]
    [InlineData("audio-es", "plain.mp3")]
    public void PayloadBytesAreCopiedUnchanged(string folder, string name)
    {
        string path = Data(folder, name);
        (byte[] output, RemuxReport report) = Remux(path);
        using MediaFile source = Media.Open(path);
        List<Pkt> input = ReadAll(source);
        List<Pkt> written = ReadAll(output);
        foreach (RemuxTrackReport t in report.Tracks.Where(t => t.OutputTrack >= 0))
        {
            Assert.Equal("none", t.Repackaging);
            List<Pkt> a = input.Where(p => p.Track == t.InputTrack).ToList();
            List<Pkt> b = written.Where(p => p.Track == t.OutputTrack).ToList();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.True(a[i].Data.AsSpan().SequenceEqual(b[i].Data), $"{name} track {t.InputTrack} packet {i}");
            }
        }
    }

    [Fact]
    public void ATrackTheTargetCannotHoldIsReportedAndTheRestIsWritten()
    {
        (byte[] output, RemuxReport report) = Remux(Data("avi", "mjpeg-pcm.avi"));
        RemuxLoss loss = Assert.Single(report.Losses, l => l.Kind == LossKind.Track);
        Assert.Equal(LossReason.UnsupportedCodec, loss.Reason);
        Assert.Equal(0, loss.InputTrack);
        Assert.True(report.HasLosses);
        using MediaFile result = Media.Open(output);
        Assert.Single(result.Tracks);
        Assert.Equal(CodecId.Pcm, result.Tracks[0].Codec);
    }

    [Fact]
    public void RefusingToDropMakesTheMuxersRefusalEscape()
    {
        using MediaFile input = Media.Open(Data("avi", "mjpeg-pcm.avi"));
        using MemoryStream ms = new();
        MuxRefusedException e = Assert.Throws<MuxRefusedException>(() => Remuxer.Remux(input, ms, RemuxFormat.Mp4, new RemuxOptions { AllowDrop = false }));
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, e.Reason);
    }

    [Fact]
    public void NothingWritableIsAnError()
    {
        using MediaFile input = Media.Open(Data("xiph-flv", "tags.ogg"));
        using MemoryStream ms = new();
        Assert.Throws<MuxRefusedException>(() => Remuxer.Remux(input, ms, RemuxFormat.Mp4));
    }

    [Fact]
    public void SelectionAndEditsApply()
    {
        string path = Data("xiph-flv", "h264-aac.flv");
        using MediaFile input = Media.Open(path);
        int video = input.Video!.Index;
        int audio = input.Audio!.Index;
        RemuxOptions options = new()
        {
            Edits = new Dictionary<int, TrackEdit>
            {
                [video] = new TrackEdit { RotationDegrees = 90, Language = "fra", Name = "Main" },
                [audio] = new TrackEdit { Drop = true },
            },
        };
        using MemoryStream ms = new();
        RemuxReport report = Remuxer.Remux(input, ms, RemuxFormat.Mov, options);
        Assert.Contains(report.Losses, l => l.Reason == LossReason.Deselected && l.InputTrack == audio);
        using MediaFile result = Media.Open(ms.ToArray());
        MediaTrack t = Assert.Single(result.Tracks);
        Assert.Equal(90, t.Video!.Value.RotationDegrees);
        Assert.Equal("fra", t.Language);
        Assert.Equal("Main", t.Name);
    }

    [Fact]
    public void CmafHoldsOneTrackAndSaysSo()
    {
        (_, RemuxReport report) = Remux(Data("xiph-flv", "h264-aac.flv"), RemuxFormat.Cmaf);
        Assert.Equal(LossReason.TargetLimit, Assert.Single(report.Losses, l => l.Kind == LossKind.Track).Reason);
    }

    [Fact]
    public void FragmentedOutputReadsBack()
    {
        string path = Data("mpeg", "h264-aac.ts");
        (byte[] output, _) = Remux(path, RemuxFormat.FragmentedMp4);
        using MediaFile result = Media.Open(output);
        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(42, ReadAll(result).Count);
    }

    [Fact]
    public void OggOpusKeepsItsPrimingInTheEditInformation()
    {
        (byte[] output, RemuxReport report) = Remux(Data("xiph-flv", "tags.opus"));
        using MediaFile result = Media.Open(output);
        MediaTrack t = Assert.Single(result.Tracks);
        Assert.True(t.Edit.Priming.IsKnown && !t.Edit.Priming.IsZero);
        Assert.False(t.Edit.PacketTimesIncludePriming);
        Assert.Contains(report.Changes, c => c.Contains("priming", StringComparison.Ordinal));
        List<Pkt> packets = ReadAll(result);
        Assert.Equal(0, packets[0].Dts.Ticks);
    }

    [Fact]
    public void TagsAndChaptersAreListedAsNotWritten()
    {
        (_, RemuxReport report) = Remux(Data("xiph-flv", "tags.flac"));
        Assert.Contains(report.Losses, l => l.Kind == LossKind.Tag && l.Reason == LossReason.NotWritten);
    }

    [Fact]
    public void ObserverSeesEveryPacketAsWritten()
    {
        long count = 0;
        long bytes = 0;
        RemuxOptions options = new() { Observer = (in PacketInfo p, ReadOnlySpan<byte> d) => { count++; bytes += d.Length; } };
        (_, RemuxReport report) = Remux(Data("mpeg", "h264-aac.ts"), RemuxFormat.Mp4, options);
        Assert.Equal(report.Tracks.Sum(t => t.Packets), count);
        Assert.Equal(report.Mux.Tracks.Sum(t => t.PayloadBytes), bytes);
    }

    [Fact]
    public async Task AnAsynchronousSourceRemuxesToTheSameBytes()
    {
        string path = Data("mpeg", "h264-aac.ts");
        (byte[] expected, _) = Remux(path);
        await using MediaFile input = await MediaFile.OpenAsync(new MemoryMediaSource(File.ReadAllBytes(path)));
        using MemoryStream ms = new();
        await Remuxer.RemuxAsync(input, ms, RemuxFormat.Mp4);
        Assert.True(ms.ToArray().AsSpan().SequenceEqual(expected));
    }

    private sealed class ForwardOnly(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public void AForwardOnlyStreamRemuxes()
    {
        string path = Data("xiph-flv", "h264-aac.flv");
        (byte[] expected, _) = Remux(path);
        using MediaFile input = Media.Open(new ForwardOnly(File.ReadAllBytes(path)));
        using MemoryStream ms = new();
        Remuxer.Remux(input, ms, RemuxFormat.Mp4);
        Assert.Equal(expected.Length, ms.Length);
    }

    [Fact]
    public void ACancelledPathRemuxLeavesNoFile()
    {
        string output = Path.Combine(Path.GetTempPath(), "remux-cancel-" + Guid.NewGuid().ToString("N") + ".mp4");
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Remuxer.Remux(Data("mpeg", "h264-aac.ts"), output, RemuxFormat.Mp4, null, cts.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AnExistingOutputIsRefusedUnlessOverwriteIsSet()
    {
        string output = Path.Combine(Path.GetTempPath(), "remux-exists-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            File.WriteAllText(output, "x");
            Assert.Throws<IOException>(() => Remuxer.Remux(Data("audio-es", "lc.aac"), output));
            Assert.Equal("x", File.ReadAllText(output));
            _ = Remuxer.Remux(Data("audio-es", "lc.aac"), output, new RemuxOptions { Overwrite = true });
            Assert.True(new FileInfo(output).Length > 100);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void CancellationStopsTheRemux()
    {
        using MediaFile input = Media.Open(Data("mpeg", "h264-aac.ts"));
        using MemoryStream ms = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Remuxer.Remux(input, ms, RemuxFormat.Mp4, null, cts.Token));
    }

    [Fact]
    public void FormatFollowsTheExtension()
    {
        Assert.Equal(RemuxFormat.Mp4, Remuxer.FormatForPath("a/b.MP4"));
        Assert.Equal(RemuxFormat.M4a, Remuxer.FormatForPath("x.m4a"));
        Assert.Equal(RemuxFormat.WebM, Remuxer.FormatForPath("x.webm"));
        Assert.Null(Remuxer.FormatForPath("x.txt"));
    }

    [Theory]
    [InlineData("isobmff", "bframes_aac.mp4")]
    [InlineData("isobmff", "bframes_aac.mov")]
    [InlineData("isobmff", "video.3gp")]
    public void FaststartMovesOnlyTheMovieBoxAndKeepsEveryPacket(string folder, string name)
    {
        string path = Data(folder, name);
        // A moov-last copy to start from.
        using MediaFile source = Media.Open(path);
        using MemoryStream last = new();
        Remuxer.Remux(source, last, RemuxFormat.Mp4, new RemuxOptions { Faststart = false });
        byte[] lastBytes = last.ToArray();
        Assert.True(TopLevel(lastBytes).IndexOf("moov") > TopLevel(lastBytes).IndexOf("mdat"));

        using MemoryStream fast = new();
        FaststartReport r = Remuxer.Faststart(new MemoryMediaSource(lastBytes), fast);
        byte[] fastBytes = fast.ToArray();
        Assert.True(r.Relocated);
        Assert.True(TopLevel(fastBytes).IndexOf("moov") < TopLevel(fastBytes).IndexOf("mdat"));
        Assert.Equal(lastBytes.Length, fastBytes.Length);
        List<Pkt> a = ReadAll(lastBytes);
        List<Pkt> b = ReadAll(fastBytes);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Track, b[i].Track);
            Assert.Equal(a[i].Pts, b[i].Pts);
            Assert.True(a[i].Data.AsSpan().SequenceEqual(b[i].Data));
        }

        // Applying it again changes nothing.
        using MemoryStream again = new();
        FaststartReport second = Remuxer.Faststart(new MemoryMediaSource(fastBytes), again);
        Assert.False(second.Relocated);
        Assert.True(again.ToArray().AsSpan().SequenceEqual(fastBytes));
    }

    [Fact]
    public void FaststartRefusesAnOffsetTableThatClaimsMoreEntriesThanItHolds()
    {
        using MediaFile source = Media.Open(Data("isobmff", "bframes_aac.mp4"));
        using MemoryStream last = new();
        Remuxer.Remux(source, last, RemuxFormat.Mp4, new RemuxOptions { Faststart = false });
        byte[] bytes = last.ToArray();
        int at = bytes.AsSpan().IndexOf("stco"u8);
        Assert.True(at > 0);
        bytes[at + 8] = 0x7f; // after the type come the version/flags word and then the entry count
        Assert.ThrowsAny<MediaException>(() => Remuxer.Faststart(new MemoryMediaSource(bytes), new MemoryStream()));
    }

    [Fact]
    public void PacketsOfAnUndeclaredTrackAreSkippedAndReportedOnce()
    {
        using MediaFile input = Media.Open(Data("audio-es", "lc.aac"));
        using MemoryStream ms = new();
        using RemuxSession session = new(input.Tracks, ms, RemuxFormat.Mp4);
        PacketInfo stray = new(5, MediaTime.FromSeconds(0), MediaTime.FromSeconds(0), MediaTime.FromSeconds(1), PacketAttributes.Keyframe, default, 3, default);
        session.WritePacket(stray, new byte[3]);
        session.WritePacket(stray, new byte[3]);
        IDemuxer d = input.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            session.WritePacket(v.Info, v.Data);
        }

        RemuxReport report = session.Finish();
        Assert.Single(report.Losses, l => l.InputTrack == 5);
    }

    [Fact]
    public void FaststartRefusesFragmentedFiles()
    {
        byte[] data = File.ReadAllBytes(Data("isobmff", "frag.mp4"));
        Assert.Throws<UnsupportedMediaException>(() => Remuxer.Faststart(new MemoryMediaSource(data), new MemoryStream()));
    }

    [Fact]
    public void FloatingPointWaveSamplesGetAFloatingPointSampleEntry()
    {
        // WAVE_FORMAT_IEEE_FLOAT, one channel, 8 kHz, 64-bit samples: the layout is not in the neutral track, so the remuxer takes it from the reader.
        using MemoryStream wav = new();
        using (BinaryWriter w = new(wav))
        {
            w.Write("RIFF"u8);
            w.Write(36 + 64);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)3);
            w.Write((short)1);
            w.Write(8000);
            w.Write(64000);
            w.Write((short)8);
            w.Write((short)64);
            w.Write("data"u8);
            w.Write(64);
            for (int i = 0; i < 8; i++)
            {
                w.Write(i * 0.125);
            }
        }

        using MediaFile input = Media.Open(wav.ToArray());
        using MemoryStream ms = new();
        Remuxer.Remux(input, ms, RemuxFormat.Mp4);
        byte[] bytes = ms.ToArray();
        Assert.True(bytes.AsSpan().IndexOf("fpcm"u8) > 0);
        Assert.True(bytes.AsSpan().IndexOf("ipcm"u8) < 0);
    }

    private static List<string> TopLevel(byte[] data)
    {
        List<string> names = [];
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            long size = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            names.Add(System.Text.Encoding.ASCII.GetString(data, pos + 4, 4));
            if (size < 8)
            {
                break;
            }

            pos += (int)size;
        }

        return names;
    }
}
