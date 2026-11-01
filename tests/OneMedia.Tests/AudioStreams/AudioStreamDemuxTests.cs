using RecursiveAI.OneMedia.AudioStreams;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Sniffing;
using static RecursiveAI.OneMedia.Tests.AudioStreams.AudioStreamTestData;

namespace RecursiveAI.OneMedia.Tests.AudioStreams;

public class AudioStreamDemuxTests
{
    private static MpegAudioDemuxer OpenMp3(byte[] data) => MpegAudioDemuxer.Open(new MemoryMediaSource(data));

    [Fact]
    public void Mp3FramesAreOnePacketEach()
    {
        using MpegAudioDemuxer d = OpenMp3(Mp3(50));
        List<(PacketInfo Info, byte[] Data)> packets = Packets(d);
        Assert.Equal(50, packets.Count);
        Assert.All(packets, p => Assert.Equal(Mp3FrameBytes, p.Data.Length));
        for (int i = 0; i < packets.Count; i++)
        {
            Assert.Equal(new MediaTime(i * Mp3Samples, 44100), packets[i].Info.Pts);
            Assert.Equal(i * Mp3FrameBytes, packets[i].Info.SourceRange.Offset);
            Assert.True(packets[i].Info.IsKeyframe);
        }

        MediaTrack t = Assert.Single(d.Tracks);
        Assert.Equal(CodecId.Mp3, t.Codec);
        Assert.Equal(44100, t.Audio!.Value.SampleRate);
        Assert.Equal(2, t.Audio.Value.Channels);
        Assert.True(d.IsDurationEstimated);
        Assert.Equal(new MediaTime(50 * Mp3Samples, 44100), d.Duration);
        Assert.Empty(d.Defects);
    }

    [Fact]
    public void XingFrameIsNotAPacketAndGivesTheExactDurationAndGaplessValues()
    {
        byte[] audio = Mp3(40);
        byte[] file = Concat(XingFrame(40, audio.Length, lame: true, delay: 576, padding: 1500), audio);
        using MpegAudioDemuxer d = OpenMp3(file);
        Assert.Equal(40, Packets(d).Count);
        Assert.False(d.IsDurationEstimated);
        MediaTrack t = d.Tracks[0];
        // LAME documents a decoder delay of 528+1 samples on top of the encoder delay, and the padding it states includes the decoder's tail.
        Assert.Equal(new MediaTime(576 + 529, 44100), t.Edit.Priming);
        Assert.Equal(new MediaTime(1500 - 529, 44100), t.Edit.EndPadding);
        Assert.Equal(new MediaTime((40 * Mp3Samples) - (576 + 529) - (1500 - 529), 44100), t.Duration);
        Assert.Equal("mpeg-audio-info-frame", t.Configuration.Format);
        Assert.Equal("LAME3.100", d.Encoder);
    }

    [Fact]
    public void InfoHeaderWithoutLameExtensionStillSkipsTheFrame()
    {
        byte[] audio = Mp3(10);
        using MpegAudioDemuxer d = OpenMp3(Concat(XingFrame(10, audio.Length, lame: false, info: true), audio));
        Assert.Equal(10, Packets(d).Count);
        Assert.Equal(new MediaTime(10 * Mp3Samples, 44100), d.Duration);
        Assert.Equal(default, d.Tracks[0].Edit.Priming);
    }

    [Fact]
    public void PaddedFramesKeepTheirOwnLength()
    {
        byte[] file = Concat(Mp3Frame(0), Mp3Frame(1, padded: true), Mp3Frame(2), Mp3Frame(3, padded: true));
        using MpegAudioDemuxer d = OpenMp3(file);
        Assert.Equal([417, 418, 417, 418], Packets(d).Select(p => p.Data.Length).ToArray());
    }

    [Fact]
    public void JunkBetweenFramesIsSkippedAndReported()
    {
        byte[] junk = new byte[100];
        Array.Fill(junk, (byte)0x42);
        byte[] file = Concat(Mp3(10), junk, Mp3(10));
        using MpegAudioDemuxer d = OpenMp3(file);
        List<(PacketInfo Info, byte[] Data)> packets = Packets(d);
        Assert.Equal(20, packets.Count);
        Assert.Equal((10 * Mp3FrameBytes) + 100, packets[10].Info.SourceRange.Offset);
        MediaDefect defect = Assert.Single(d.Defects);
        Assert.Equal("audio.junk", defect.Code);
        Assert.Equal(10 * Mp3FrameBytes, defect.Offset);
    }

    [Fact]
    public void TruncatedLastFrameIsDeliveredAsCorrupt()
    {
        byte[] file = Mp3(5);
        using MpegAudioDemuxer d = OpenMp3(file.AsSpan(0, file.Length - 100).ToArray());
        List<(PacketInfo Info, byte[] Data)> packets = Packets(d);
        Assert.Equal(5, packets.Count);
        Assert.Equal(Mp3FrameBytes - 100, packets[4].Data.Length);
        Assert.NotEqual(0, (int)(packets[4].Info.Flags & PacketAttributes.Corrupt));
        Assert.Contains(d.Defects, x => x.Code == "audio.truncated-frame");
    }

    [Fact]
    public void TrailingTagsAreKeptOutOfTheAudio()
    {
        byte[] audio = Mp3(30);
        byte[] v1 = Id3v1("Song", "Singer", "Disc", "1999", "hi", track: 7);
        byte[] ape = ApeBytes(ApeItem("Title", "From APE"));
        using MpegAudioDemuxer d = OpenMp3(Concat(audio, ape, v1));
        Assert.Equal(30, Packets(d).Count);
        Assert.Equal(audio.Length, d.AudioEnd);
        Assert.Empty(d.Defects);
    }

    [Fact]
    public void LeadingId3v2IsSkipped()
    {
        byte[] tag = Id3Bytes(3, TextFrame(3, "TIT2", "Hello", encoding: 0), padding: 300);
        using MpegAudioDemuxer d = OpenMp3(Concat(tag, Mp3(12)));
        List<(PacketInfo Info, byte[] Data)> packets = Packets(d);
        Assert.Equal(12, packets.Count);
        Assert.Equal(tag.Length, packets[0].Info.SourceRange.Offset);
        Assert.Equal(tag.Length, d.AudioStart);
    }

    [Fact]
    public void ForwardOnlyGivesTheSamePacketsAsSeekable()
    {
        byte[] file = Concat(Id3Bytes(4, TextFrame(4, "TIT2", "x")), XingFrame(60, 60 * Mp3FrameBytes, lame: true), Mp3(60), Id3v1("a", "b", "c", "2000", "d"));
        using MpegAudioDemuxer seekable = OpenMp3(file);
        using MpegAudioDemuxer forward = MpegAudioDemuxer.Open(new StreamForwardSource(new MemoryStream(file)));
        List<(PacketInfo Info, byte[] Data)> a = Packets(seekable);
        List<(PacketInfo Info, byte[] Data)> b = Packets(forward);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Data, b[i].Data);
            Assert.Equal(a[i].Info.Pts, b[i].Info.Pts);
            Assert.Equal(a[i].Info.SourceRange, b[i].Info.SourceRange);
        }

        Assert.False(forward.CanSeek);
    }

    [Fact]
    public void SeekLandsOnTheFrameAtOrBeforeTheTarget()
    {
        using MpegAudioDemuxer d = OpenMp3(Mp3(600));
        MediaTime landed = d.Seek(new MediaTime(400 * Mp3Samples, 44100) + new MediaTime(5, 44100));
        Assert.Equal(new MediaTime(400 * Mp3Samples, 44100), landed);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(400 * Mp3FrameBytes, p.Info.SourceRange.Offset);
        MediaTime next = d.Seek(new MediaTime(400 * Mp3Samples, 44100) + new MediaTime(5, 44100), SeekMode.NextKeyframe);
        Assert.Equal(new MediaTime(401 * Mp3Samples, 44100), next);
        Assert.Equal(new MediaTime(0, 44100), d.Seek(new MediaTime(0, 1)));
    }

    [Fact]
    public void Mp3PullPathAllocatesNothingPerPacket()
    {
        // The zero-allocation guarantee is for optimised builds (unoptimised code boxes and allocates state).
        Assert.SkipWhen(typeof(MpegAudioDemuxer).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false).Cast<System.Diagnostics.DebuggableAttribute>().Any(a => a.IsJITOptimizerDisabled), "needs an optimised build");
        for (int round = 0; round < 3; round++)
        {
            using MpegAudioDemuxer warm = OpenMp3(Mp3(2000));
            Drain(warm, out _); // warm-up: grows buffers and lets the tiered JIT settle
        }

        using MpegAudioDemuxer d = OpenMp3(Mp3(5000));
        Assert.True(d.TryReadPacket(out _));
        long allocated = Drain(d, out long count);
        Assert.Equal(4999, count);
        Assert.Equal(0, allocated);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static long Drain(MpegAudioDemuxer demuxer, out long count)
    {
        count = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (demuxer.TryReadPacket(out PacketView view))
        {
            count += view.Data.Length > 0 ? 1 : 0;
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void FreeFormatFrameLengthIsTheDistanceBetweenHeaders()
    {
        // Bit-rate index 0 (ISO/IEC 11172-3 §2.4.2.3): the length is not in the header. Frames of 500 bytes here.
        byte[] FreeFrame(int i)
        {
            byte[] f = new byte[500];
            f[0] = 0xFF;
            f[1] = 0xFB;
            f[2] = 0x00; // bit-rate index 0, 44.1 kHz, no padding
            for (int k = 4; k < f.Length; k++)
            {
                f[k] = (byte)(1 + ((k + i) % 100));
            }

            return f;
        }

        byte[] file = Concat(Enumerable.Range(0, 30).Select(FreeFrame).ToArray());
        using MpegAudioDemuxer d = OpenMp3(file);
        List<(PacketInfo Info, byte[] Data)> packets = Packets(d);
        Assert.Equal(30, packets.Count);
        Assert.All(packets, p => Assert.Equal(500, p.Data.Length));
    }

    [Fact]
    public void FreeFormatWithPaddingBitsKeepsEachFramesOwnLength()
    {
        // As a CBR 44.1 kHz stream laid out: most frames are 417 bytes and a padded one is 418; with bit-rate index 0 only the distances tell.
        List<byte[]> frames = [];
        for (int i = 0; i < 60; i++)
        {
            bool padded = i % 25 == 3;
            byte[] f = Mp3Frame(i, padded);
            f[2] &= 0x0F;
            frames.Add(f);
        }

        using MpegAudioDemuxer d = OpenMp3(Concat(frames.ToArray()));
        Assert.Equal(frames.Select(f => f.Length).ToArray(), Packets(d).Select(p => p.Data.Length).ToArray());
        Assert.True(MediaFormats.Default.TrySniff(Concat(frames.ToArray()), out FormatMatch m));
        Assert.Equal(MediaFormatId.MpegAudio, m.Format.Id);
    }

    [Fact]
    public void AdtsFramesKeepTheirHeaders()
    {
        byte[] file = Concat(AdtsFrame(100), AdtsFrame(120), AdtsFrame(90), AdtsFrame(100));
        using AdtsDemuxer d = AdtsDemuxer.Open(new MemoryMediaSource(file));
        List<(PacketInfo Info, byte[] Data)> packets = Packets(d);
        Assert.Equal([100, 120, 90, 100], packets.Select(p => p.Data.Length).ToArray());
        Assert.Equal(new MediaTime(1024, 44100), packets[1].Info.Pts);
        MediaTrack t = d.Tracks[0];
        Assert.Equal(CodecId.Aac, t.Codec);
        Assert.Equal("asc", t.Configuration.Format);
        Assert.Equal([0x12, 0x10], t.Configuration.Data.ToArray()); // AAC-LC, 44.1 kHz, stereo
        Assert.Equal(2, t.Audio!.Value.Channels);
    }

    [Fact]
    public void AdtsJunkIsSkipped()
    {
        byte[] file = Concat(AdtsFrame(100), AdtsFrame(100), [1, 2, 3, 4, 5], AdtsFrame(100), AdtsFrame(100));
        using AdtsDemuxer d = AdtsDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(4, Packets(d).Count);
        Assert.Single(d.Defects);
    }

    [Fact]
    public void Ac3SyncframesAreOnePacketEach()
    {
        byte[] file = Concat(Ac3Frame(), Ac3Frame(), Ac3Frame());
        using Ac3Demuxer d = Ac3Demuxer.Open(new MemoryMediaSource(file));
        List<(PacketInfo Info, byte[] Data)> packets = Packets(d);
        Assert.Equal(3, packets.Count);
        Assert.All(packets, p => Assert.Equal(768, p.Data.Length));
        Assert.Equal(CodecId.Ac3, d.Tracks[0].Codec);
        Assert.Equal(48000, d.Tracks[0].Audio!.Value.SampleRate);
        Assert.Equal(new MediaTime(3 * 1536, 48000).Rescale(d.Tracks[0].Timescale, MediaRounding.Floor), d.Duration.Rescale(d.Tracks[0].Timescale, MediaRounding.Floor));
    }

    [Fact]
    public void AmrNarrowbandFileHasAMagicAndOneFramePerPacket()
    {
        byte[] file = Concat("#!AMR\n"u8.ToArray(), AmrNbFrame(), AmrNbFrame(), AmrFrame(0x04, 12), AmrNbFrame());
        using AmrDemuxer d = AmrDemuxer.Open(new MemoryMediaSource(file));
        List<(PacketInfo Info, byte[] Data)> packets = Packets(d);
        Assert.Equal([32, 32, 13, 32], packets.Select(p => p.Data.Length).ToArray());
        Assert.Equal(CodecId.AmrNb, d.Tracks[0].Codec);
        Assert.Equal(8000, d.Tracks[0].Audio!.Value.SampleRate);
        Assert.Equal(new MediaTime(160, 8000), packets[1].Info.Pts);
        Assert.Equal(6, packets[0].Info.SourceRange.Offset);
    }

    [Fact]
    public void AmrWidebandFileIsRecognised()
    {
        byte[] file = Concat("#!AMR-WB\n"u8.ToArray(), AmrFrame(0x04, 17), AmrFrame(0x04, 17), AmrFrame(0x04, 17));
        using AmrDemuxer d = AmrDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(3, Packets(d).Count);
        Assert.Equal(CodecId.AmrWb, d.Tracks[0].Codec);
        Assert.Equal(16000, d.Tracks[0].Audio!.Value.SampleRate);
    }

    [Fact]
    public void RegisteredFormatsOpenEachStreamKind()
    {
        Assert.Equal(MediaFormatId.MpegAudio, Open(Mp3(5)));
        Assert.Equal(MediaFormatId.MpegAudio, Open(Concat(Id3Bytes(3, TextFrame(3, "TIT2", "t")), Mp3(5))));
        Assert.Equal(MediaFormatId.Adts, Open(Concat(AdtsFrame(100), AdtsFrame(100), AdtsFrame(100))));
        Assert.Equal(MediaFormatId.Ac3, Open(Concat(Ac3Frame(), Ac3Frame())));
        Assert.Equal(MediaFormatId.Amr, Open(Concat("#!AMR\n"u8.ToArray(), AmrNbFrame(), AmrNbFrame())));

        static MediaFormatId Open(byte[] data)
        {
            using IDemuxer d = MediaFormats.Default.Open(new MemoryMediaSource(data));
            return d.Format;
        }
    }

    [Fact]
    public void Id3AudioFormatStepsAsideForFlacWavAndOgg()
    {
        Id3AudioFormat format = new();
        byte[] tag = Id3Bytes(3, TextFrame(3, "TIT2", "t"));
        Assert.NotEqual(SniffConfidence.None, format.Sniff(Concat(tag, Mp3(3))).Confidence);
        Assert.Equal(SniffConfidence.None, format.Sniff(Concat(tag, "fLaC\0\0\0\0"u8.ToArray())).Confidence);
        Assert.Equal(SniffConfidence.None, format.Sniff(Concat(tag, "RIFF\0\0\0\0WAVE"u8.ToArray())).Confidence);
        Assert.Equal(SniffConfidence.None, format.Sniff(Concat(tag, "OggS\0\0\0\0"u8.ToArray())).Confidence);
    }

    [Fact]
    public async Task RangedSourceGivesTheSamePacketsAndMetadata()
    {
        byte[] audio = Mp3(2000);
        byte[] file = Concat(Id3Bytes(3, TextFrame(3, "TIT2", "Remote"), padding: 50_000), XingFrame(2000, audio.Length, lame: true), audio, Id3v1("t", "a", "b", "2001", "c"));
        using MpegAudioDemuxer sync = OpenMp3(file);
        List<(PacketInfo Info, byte[] Data)> expected = Packets(sync);

        int requests = 0;
        long bytes = 0;
        RangedMediaSource ranged = new(file.Length, (offset, destination, _) =>
        {
            requests++;
            int n = (int)Math.Min(destination.Length, file.Length - offset);
            file.AsMemory((int)offset, n).CopyTo(destination);
            bytes += n;
            return new ValueTask<int>(n);
        });
        await using MediaFile media = await MediaFile.OpenAsync(ranged);
        int openRequests = requests;
        Assert.True(openRequests <= 4, $"open took {openRequests} requests");
        Assert.True(bytes < 700_000, $"open read {bytes} bytes");
        MediaMetadata m = await media.ReadMetadataAsync();
        Assert.Equal("Remote", m.Tags.Title);
        int i = 0;
        await foreach (OwnedPacket p in media.PacketsAsync(media.Tracks[0]))
        {
            using (p)
            {
                Assert.Equal(expected[i].Data, p.Data.ToArray());
                Assert.Equal(expected[i].Info.Pts, p.Info.Pts);
                i++;
            }
        }

        Assert.Equal(expected.Count, i);
    }
}
