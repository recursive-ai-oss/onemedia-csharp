using System;
using System.Collections.Generic;
using System.Linq;
using System.Buffers;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Ac3;
using RecursiveAI.OneMedia.Codecs.Amr;
using RecursiveAI.OneMedia.Codecs.Av1;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Codecs.H264;
using RecursiveAI.OneMedia.Codecs.Hevc;
using RecursiveAI.OneMedia.Codecs.Mpeg2Video;
using RecursiveAI.OneMedia.Codecs.MpegAudio;
using RecursiveAI.OneMedia.Codecs.Opus;
using RecursiveAI.OneMedia.Codecs.Vorbis;
using RecursiveAI.OneMedia.Codecs.Vpx;
using RecursiveAI.OneMedia.Codecs.Vvc;

using AvcPps = RecursiveAI.OneMedia.Codecs.H264.PictureParameterSet;
using AvcSps = RecursiveAI.OneMedia.Codecs.H264.SequenceParameterSet;
using HevcPps = RecursiveAI.OneMedia.Codecs.Hevc.PictureParameterSet;
using HevcSps = RecursiveAI.OneMedia.Codecs.Hevc.SequenceParameterSet;
using VvcSps = RecursiveAI.OneMedia.Codecs.Vvc.SequenceParameterSet;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>A fuzz target: must return normally for every input. An exception escaping it is a finding.</summary>
/// <param name="data">The input.</param>
internal delegate void FuzzTarget(ReadOnlySpan<byte> data);

/// <summary>The codec header layer's targets, one per family. Each also checks a few invariants the parsers promise.</summary>
internal static class CodecFuzz
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["codecs-nal"] = Nal;
        targets["codecs-av1-vpx-mpeg2"] = VideoOther;
        targets["codecs-aac"] = AacTarget;
        targets["codecs-audio"] = AudioOther;
        targets["codecs-config"] = Config;
    }

    // Consumes a parser result so that analyzers see it used; the targets only care that nothing throws.
    private static void Sink(bool value) => GC.KeepAlive(value ? "y" : "n");

    // H.264, H.265, H.266: Annex B walking, parameter sets, SEI framing, configuration records and the two framings.
    private static void Nal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return;
        }

        int selector = data[0];
        ReadOnlySpan<byte> body = data[1..];
        byte[] copy = body.ToArray();
        AnnexBReader reader = new(body);
        int units = 0;
        while (units++ < 256 && reader.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
        {
            Sink(AvcSps.TryParse(nal, out _));
            Sink(AvcPps.TryParse(nal, out _));
            Sink(HevcSps.TryParse(nal, out _));
            Sink(HevcPps.TryParse(nal, out _));
            Sink(VvcSps.TryParse(nal, out _));
            if (nal.Length > 3)
            {
                SeiMessageReader sei = new(nal[2..]);
                int n = 0;
                while (n++ < 64 && sei.TryRead(out _, out _))
                {
                }
            }
        }

        Sink(AvcDecoderConfigurationRecord.TryParse(copy, out AvcDecoderConfigurationRecord? avc));
        Sink(HevcDecoderConfigurationRecord.TryParse(copy, out HevcDecoderConfigurationRecord? hevc));
        Sink(VvcDecoderConfigurationRecord.TryParse(copy, out VvcDecoderConfigurationRecord? vvc));
        ArrayBufferWriter<byte> w = new();
        if (avc is not null)
        {
            avc.Write(w);
            Sink(AvcDecoderConfigurationRecord.TryParse(w.WrittenMemory, out _));
            Sink(avc.TryDescribe(out _));
        }

        w.ResetWrittenCount();
        hevc?.Write(w);
        hevc?.TryDescribe(out _);
        w.ResetWrittenCount();
        vvc?.Write(w);
        vvc?.TryDescribe(out _);

        Sink(AvcDecoderConfigurationRecord.TryCreateFromAnnexB(copy, out _));
        Sink(HevcDecoderConfigurationRecord.TryCreateFromAnnexB(copy, out _));

        NalCodec codec = (NalCodec)(selector % 3);
        int lengthSize = 1 + ((selector >> 2) & 3);
        Span<byte> destination = new byte[Math.Min(body.Length * 2, 4096)];
        NalRepackager.LengthPrefixedToAnnexB(codec, body, lengthSize, destination, out _, new AnnexBOptions(body.Length > 8 ? copy.AsMemory(0, 8) : default));
        NalRepackager.AnnexBToLengthPrefixed(body, lengthSize, destination, out _, (selector & 0x80) != 0, codec);
        EmulationPrevention.Remove(body, destination);
        Sink(CodecParameterReader.TryDescribeAnnexB(codec, copy, out _));
    }

    private static void VideoOther(ReadOnlySpan<byte> data)
    {
        byte[] copy = data.ToArray();
        int pos = 0;
        int guard = 0;
        while (pos < data.Length && guard++ < 256 && ObuHeader.TryParse(data[pos..], out ObuHeader h))
        {
            if (h.Type == ObuType.SequenceHeader)
            {
                Sink(SequenceHeader.TryParse(data.Slice(pos + h.PayloadOffset, h.PayloadSize), out _));
            }

            pos += Math.Max(h.TotalSize, 1);
        }

        Sink(SequenceHeader.TryFind(data, out _, out _));
        Av1Detection.DetectFraming(data);
        if (Av1CodecConfigurationRecord.TryParse(copy, out Av1CodecConfigurationRecord? av1))
        {
            ArrayBufferWriter<byte> w = new();
            av1!.Write(w);
            Sink(av1.TryDescribe(out _));
        }

        Sink(Vp8KeyFrameHeader.TryParse(data, out _));
        Vp8KeyFrameHeader.IsKeyFrame(data);
        Sink(Vp9FrameHeader.TryParse(data, out _));
        Span<int> sizes = stackalloc int[Vp9Superframe.MaxFrames]; // fixed: a superframe holds at most eight frames
        Sink(Vp9Superframe.TryRead(data, sizes, out _));
        if (VpCodecConfigurationRecord.TryParse(data, out VpCodecConfigurationRecord? vp))
        {
            ArrayBufferWriter<byte> w = new();
            vp!.Write(w);
            vp.Describe();
        }

        Sink(Vp9CodecPrivate.TryParse(data, out _, out _, out _, out _));

        Sink(Mpeg2SequenceHeader.TryFind(data, out _));
        Mpeg2StartCodeReader sc = new(data);
        int n = 0;
        while (n++ < 256 && sc.TryRead(out int code, out ReadOnlySpan<byte> body, out _))
        {
            if (code == Mpeg2StartCode.Picture)
            {
                Sink(Mpeg2PictureHeader.TryParse(body, out _));
            }
            else if (code == Mpeg2StartCode.GroupOfPictures)
            {
                Sink(Mpeg2GroupOfPicturesHeader.TryParse(body, out _));
            }
        }
    }

    private static void AacTarget(ReadOnlySpan<byte> data)
    {
        if (AudioSpecificConfig.TryParse(data, out AudioSpecificConfig asc))
        {
            asc.Describe();
            Span<byte> frame = new byte[64];
            AdtsRepackager.WrapRaw(asc, data[..Math.Min(data.Length, 32)], frame, out _);
        }

        if (AdtsHeader.TryParse(data, out AdtsHeader h))
        {
            ArrayBufferWriter<byte> w = new();
            Sink(AdtsRepackager.TryCreateConfig(data, w));
            Sink(AdtsRepackager.TryGetRawData(data, out _));
            // the config we derive must parse
            if (w.WrittenCount > 0 && !AudioSpecificConfig.TryParse(w.WrittenSpan, out _))
            {
                throw new InvalidOperationException("ADTS-derived AudioSpecificConfig does not parse");
            }

            _ = h.PayloadSize;
        }

        byte[] copy = data.ToArray();
        if (EsDescriptor.TryParse(copy, out EsDescriptor? es))
        {
            ArrayBufferWriter<byte> w = new();
            es!.WriteBoxPayload(w);
            if (!EsDescriptor.TryParse(w.WrittenMemory, out EsDescriptor? again) || !again!.DecoderSpecificInfo.Span.SequenceEqual(es.DecoderSpecificInfo.Span))
            {
                throw new InvalidOperationException("esds round trip lost the decoder-specific info");
            }
        }
    }

    private static void AudioOther(ReadOnlySpan<byte> data)
    {
        byte[] copy = data.ToArray();
        if (OpusHead.TryParseOpusHead(copy, out OpusHead? head))
        {
            ArrayBufferWriter<byte> w = new();
            head!.WriteDOps(w);
            head.WriteOpusHead(new ArrayBufferWriter<byte>());
            head.Describe();
        }

        if (OpusHead.TryParseDOps(copy, out OpusHead? dops))
        {
            dops!.WriteOpusHead(new ArrayBufferWriter<byte>());
        }

        Sink(OpusToc.TryGetSamples(data, out _));

        if (VorbisIdentificationHeader.TryParse(data, out VorbisIdentificationHeader id))
        {
            id.Describe();
        }

        if (VorbisXiphLacing.TrySplit(copy, out ReadOnlyMemory<byte> a, out ReadOnlyMemory<byte> b, out ReadOnlyMemory<byte> c))
        {
            if (VorbisIdentificationHeader.TryParse(a.Span, out VorbisIdentificationHeader vid) && VorbisModes.TryParse(c.Span, vid, out VorbisModes? modes))
            {
                modes!.TryGetBlockSize(data, out _);
            }

            ArrayBufferWriter<byte> w = new();
            VorbisXiphLacing.Build(a.Span, b.Span, c.Span, w);
        }

        VorbisIdentificationHeader fake = new(2, 48000, 0, 0, 0, 256, 2048);
        Sink(VorbisModes.TryParse(data, fake, out _));

        Sink(FlacStreamInfo.TryParse(data, out _));
        Sink(FlacBlockHeader.TryParse(data, out _));
        Sink(FlacFrameHeader.TryParse(data, out _));
        Sink(FlacConfig.TryGetStreamInfo(data, out _));

        Sink(Ac3SyncFrame.TryParse(data, out Ac3SyncFrame ac3));
        Sink(ac3.TryWriteDac3(new ArrayBufferWriter<byte>()));
        Sink(ac3.TryWriteDec3(640, new ArrayBufferWriter<byte>()));
        Sink(Ac3Config.TryDescribeDac3(data, out _));
        Sink(Ac3Config.TryDescribeDec3(data, out _));

        if (MpegAudioFrameHeader.TryParse(data, out MpegAudioFrameHeader mp))
        {
            _ = mp.FrameLength;
            mp.Describe();
        }

        Sink(MpegAudioVbrHeader.TryParse(data, out _));

        Sink(AmrFrame.TryReadMagic(data, out _, out _));
        if (data.Length > 0)
        {
            Sink(AmrFrame.TryGetFrameSize(data[0], false, out _));
            Sink(AmrFrame.TryGetFrameSize(data[0], true, out _));
        }

        Sink(AmrSpecificBox.TryParse(data, out _));
    }

    // The summary layer and the Matroska/ISO mapper, driven by a codec selector and a layout selector in the first two bytes.
    private static readonly string[] CodecNames =
    [
        "h264", "hevc", "vvc", "av1", "vp9", "vp8", "mpeg2video", "aac", "opus", "vorbis", "flac", "ac3", "eac3", "mp3", "amr-nb", "amr-wb",
    ];

    private static readonly string?[] Formats = [null, "avcC", "hvcC", "vvcC", "av1C", "vpcC", "esds", "asc", "dOps", "dfLa", "dac3", "dec3", "damr", "CodecPrivate"];

    private static void Config(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        CodecId codec = new(CodecNames[data[0] % CodecNames.Length]);
        string? format = Formats[data[1] % Formats.Length];
        byte[] body = data[2..].ToArray();
        Sink(CodecParameterReader.TryDescribe(codec, new CodecConfiguration(body, format), out _));
        Sink(CodecParameterReader.TryDescribe(codec, new CodecConfiguration(body, format), body, out _));
        Sink(CodecParameterReader.TryDescribeSample(codec, body, out _));
        ArrayBufferWriter<byte> w = new();
        CodecConfigMapper.MatroskaToIso(codec, body, body, w, out _);
        w.ResetWrittenCount();
        CodecConfigMapper.IsoToMatroska(codec, body, w);
    }
}
