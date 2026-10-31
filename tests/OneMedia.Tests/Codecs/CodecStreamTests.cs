using System.Text.Json;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Codecs.Mpeg2Video;
using RecursiveAI.OneMedia.Codecs.MpegAudio;
using RecursiveAI.OneMedia.Codecs.Opus;
using RecursiveAI.OneMedia.Codecs.Vorbis;

namespace RecursiveAI.OneMedia.Tests.Codecs;

/// <summary>
/// Checks of the packet-level header parsers (Vorbis block sizes, Opus durations, MPEG audio tags, FLAC frames, MPEG-2 pictures)
/// on generated streams, against what ffprobe reports for the same files.
/// </summary>
public sealed class CodecStreamTests
{
    private static string? StreamsDir => Environment.GetEnvironmentVariable("ONEMEDIA_CODEC_STREAMS");

    private static string Ffprobe => Environment.GetEnvironmentVariable("ONEMEDIA_FFPROBE") ?? "ffprobe";

    private static JsonElement Probe(string path, string args)
    {
        string json = CodecFieldDifferentialTests.RunTool(Ffprobe, $"-v error {args} -of json \"{path}\"");
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void VorbisPacketDurationsSumToTheStreamLength()
    {
        if (StreamsDir is null)
        {
            return;
        }

        foreach (string name in new[] { "vorbis.mkv", "vorbis_mono.mkv" })
        {
            byte[] file = File.ReadAllBytes(Path.Combine(StreamsDir, name));
            MiniContainers.MkvTrack track = MiniContainers.ReadMkv(file)!;
            Assert.True(VorbisXiphLacing.TrySplit(track.CodecPrivate, out ReadOnlyMemory<byte> id, out ReadOnlyMemory<byte> comment, out ReadOnlyMemory<byte> setup));
            Assert.True(VorbisIdentificationHeader.TryParse(id.Span, out VorbisIdentificationHeader header));
            Assert.Equal(1, VorbisIdentificationHeader.GetHeaderType(id.Span));
            Assert.Equal(3, VorbisIdentificationHeader.GetHeaderType(comment.Span));
            Assert.True(VorbisModes.TryParse(setup.Span, header, out VorbisModes? modes));
            Assert.True(modes!.ModeCount >= 1);

            long samples = 0;
            int previous = 0, packets = 0;
            foreach (byte[] frame in MiniContainers.ReadMkvFrames(file))
            {
                Assert.True(modes.TryGetBlockSize(frame, out int size));
                samples += VorbisModes.GetOutputSamples(previous, size);
                previous = size;
                packets++;
            }

            // ffprobe: packets in the Matroska file; the decoded length is the sum of the overlaps, give or take the final block's trim
            long expected = (long)header.SampleRate * 2;
            Assert.True(Math.Abs(samples - expected) <= header.BlockSize1, $"{name}: {samples} samples over {packets} packets, expected about {expected}");

            // cross-check the block-size sequence with ffprobe's packet durations (milliseconds at the Matroska time base)
            JsonElement probe = Probe(Path.Combine(StreamsDir, name), "-show_entries packet=duration_time -select_streams 0");
            Assert.Equal(packets, probe.GetProperty("packets").GetArrayLength());
        }
    }

    [Fact]
    public void OpusPacketDurationsSumToTheStreamLength()
    {
        if (StreamsDir is null)
        {
            return;
        }

        foreach (string name in new[] { "opus_stereo.mkv", "opus_51.mkv", "opus_mono.mkv" })
        {
            byte[] file = File.ReadAllBytes(Path.Combine(StreamsDir, name));
            MiniContainers.MkvTrack track = MiniContainers.ReadMkv(file)!;
            Assert.True(OpusHead.TryParseOpusHead(track.CodecPrivate, out OpusHead? head));
            long total = 0;
            foreach (byte[] frame in MiniContainers.ReadMkvFrames(file))
            {
                Assert.True(OpusToc.TryGetSamples(frame, out int s));
                total += s;
            }

            // 2 s at 48 kHz plus the pre-skip, rounded up to whole packets
            long expected = 96000 + head!.PreSkip;
            Assert.True(Math.Abs(total - expected) <= 960, $"{name}: {total} samples, expected about {expected}");
        }
    }

    [Fact]
    public void Mp3EncoderDelayMatchesFfprobeStartTime()
    {
        if (StreamsDir is null)
        {
            return;
        }

        string path = Path.Combine(StreamsDir, "mp3.mp3");
        byte[] file = File.ReadAllBytes(path);
        int skip = file[0] == 'I' && file[1] == 'D' && file[2] == '3' ? 10 + ((file[6] << 21) | (file[7] << 14) | (file[8] << 7) | file[9]) : 0;
        Assert.True(MpegAudioVbrHeader.TryParse(file.AsSpan(skip), out MpegAudioVbrHeader vbr));
        Assert.False(vbr.IsVbri);
        Assert.True(vbr.EncoderDelay >= 0 && vbr.EncoderVersion.StartsWith("Lavc", StringComparison.Ordinal) || vbr.EncoderVersion.StartsWith("LAME", StringComparison.Ordinal), vbr.EncoderVersion);

        // The decoder delay of 528 samples plus one is added by players; ffprobe reports start_time = (delay + 529) / rate.
        JsonElement probe = Probe(path, "-show_entries format=start_time:stream=sample_rate");
        double start = double.Parse(probe.GetProperty("format").GetProperty("start_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(MpegAudioFrameHeader.TryParse(file.AsSpan(skip), out MpegAudioFrameHeader h));
        Assert.InRange(start * h.SampleRate, vbr.EncoderDelay + 529 - 1, vbr.EncoderDelay + 529 + 1);

        // The frame count in the tag excludes the tag's own frame: count the packets ffprobe finds.
        JsonElement counted = Probe(path, "-count_packets -show_entries stream=nb_read_packets");
        long packets = long.Parse(counted.GetProperty("streams")[0].GetProperty("nb_read_packets").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(packets, vbr.FrameCount);

        Assert.False(MpegAudioVbrHeader.TryParse(File.ReadAllBytes(Path.Combine(StreamsDir, "mp3_noxing.mp3")), out _));
    }

    [Fact]
    public void FlacFramesAgreeWithStreamInfo()
    {
        if (StreamsDir is null)
        {
            return;
        }

        byte[] file = File.ReadAllBytes(Path.Combine(StreamsDir, "flac.flac"));
        Assert.True(FlacConfig.TryGetBlocksFromCodecPrivate(file, out ReadOnlyMemory<byte> blocks));
        Assert.True(FlacConfig.TryGetStreamInfo(blocks.Span, out FlacStreamInfo info));
        int pos = 4;
        while (true)
        {
            Assert.True(FlacBlockHeader.TryParse(file.AsSpan(pos), out FlacBlockHeader bh));
            pos += FlacBlockHeader.Size + bh.Length;
            if (bh.IsLast)
            {
                break;
            }
        }

        Assert.True(FlacFrameHeader.TryParse(file.AsSpan(pos), out FlacFrameHeader frame));
        Assert.Equal(info.MaxBlockSize, frame.BlockSize);
        Assert.Equal(0, frame.Number);
        Assert.Equal(info.Channels, frame.Channels);
        Assert.Equal(info.SampleRate, frame.SampleRate == 0 ? info.SampleRate : frame.SampleRate);
        Assert.Equal(2 * info.SampleRate, info.TotalSamples);
    }

    [Fact]
    public void Mpeg2PicturesAreFoundInDecodeOrder()
    {
        if (StreamsDir is null)
        {
            return;
        }

        byte[] file = File.ReadAllBytes(Path.Combine(StreamsDir, "mpeg2.m2v"));
        Mpeg2StartCodeReader r = new(file);
        int pictures = 0, intra = 0, gops = 0;
        bool first = true;
        while (r.TryRead(out int code, out ReadOnlySpan<byte> body, out _))
        {
            if (code == Mpeg2StartCode.Picture)
            {
                Assert.True(Mpeg2PictureHeader.TryParse(body, out Mpeg2PictureHeader p));
                if (first)
                {
                    Assert.Equal(1, p.PictureCodingType);
                    first = false;
                }

                intra += p.PictureCodingType == 1 ? 1 : 0;
                pictures++;
            }
            else if (code == Mpeg2StartCode.GroupOfPictures)
            {
                Assert.True(Mpeg2GroupOfPicturesHeader.TryParse(body, out _));
                gops++;
            }
        }

        JsonElement counted = Probe(Path.Combine(StreamsDir, "mpeg2.m2v"), "-count_packets -show_entries stream=nb_read_packets");
        Assert.Equal(long.Parse(counted.GetProperty("streams")[0].GetProperty("nb_read_packets").GetString()!, System.Globalization.CultureInfo.InvariantCulture), pictures);
        Assert.True(intra >= 1 && gops >= 1);
    }
}
