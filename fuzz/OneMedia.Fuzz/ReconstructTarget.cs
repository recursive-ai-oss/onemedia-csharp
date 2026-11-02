using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.H264;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Repair.Reconstruction;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the reconstruction of cut-off ISO files: the media data walker and the framing detectors (NAL units and slice headers,
/// OBUs, AMR, MPEG audio, AC-3, GPMF, PCM runs). The input is a file; the first byte picks the mode. <c>reconstruct-iso</c> has no
/// reference, so video is found from in-stream parameter sets and audio from its frame headers. <c>reconstruct-iso-ref</c> uses a small
/// reference with H.264, PCM and a GoPro-style metadata track. Anything other than a <see cref="MediaException"/> escaping is a finding, and so
/// is an output that does not come back through the ISO reader.
/// </summary>
internal static class ReconstructTarget
{
    private static readonly MediaLimits Limits = new()
    {
        MaxAllocationBytes = 8 << 20,
        MaxPacketBytes = 1 << 20,
        MaxSampleTableEntries = 200_000,
        MaxElementCount = 100_000,
        MaxStructureDepth = 24,
    };

    private static readonly Lazy<byte[]> s_reference = new(BuildReference);

    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["reconstruct-iso"] = span => Run(span, false);
        targets["reconstruct-iso-ref"] = span => Run(span, true);
    }

    internal static void Run(ReadOnlySpan<byte> input, bool withReference)
    {
        if (input.Length < 16)
        {
            return;
        }

        byte[] file = input.ToArray();
        try
        {
            using MemoryStream output = new();
            MemoryMediaSource? reference = withReference ? new MemoryMediaSource(s_reference.Value) : null;
            ReconstructionReport report = IsoReconstructor.Reconstruct(new MemoryMediaSource(file), output, reference, new ReconstructionOptions { Limits = Limits });
            if (report.UsedBytes > report.ScanEnd - report.ScanStart)
            {
                throw new InvalidOperationException("more bytes used than scanned");
            }

            long previousEnd = -1;
            foreach (ByteRange r in report.UsedRanges)
            {
                if (r.Offset < previousEnd || r.Length <= 0 || r.End > file.Length)
                {
                    throw new InvalidOperationException("used ranges overlap or leave the input");
                }

                previousEnd = r.End;
            }

            // What was written must be a readable file with as many samples as the report says.
            using IsoBmffDemuxer back = IsoBmffDemuxer.Open(new MemoryMediaSource(output.ToArray()), new MediaOpenOptions { Limits = Limits });
            long packets = 0;
            while (back.TryReadPacket(out PacketView _))
            {
                packets++;
            }

            long expected = 0;
            foreach (ReconstructedTrack t in report.Tracks)
            {
                expected += t.Samples;
            }

            if (packets != expected)
            {
                throw new InvalidOperationException($"the report counts {expected} samples but the output holds {packets}");
            }
        }
        catch (MediaException)
        {
        }
    }

    // H.264 (one SPS, one PPS) + 16-bit mono PCM + a metadata track, one sample each.
    private static byte[] BuildReference()
    {
        byte[] sps = Convert.FromHexString("6764000dacd94141fb0110000003001000000303 20f1429960".Replace(" ", string.Empty, StringComparison.Ordinal));
        byte[] pps = Convert.FromHexString("68ef8fcb");
        byte[] annexB = NalRepackager.ParameterSetsToAnnexB([sps, pps]);
        AvcDecoderConfigurationRecord.TryCreateFromAnnexB(annexB, out AvcDecoderConfigurationRecord? avc);
        System.Buffers.ArrayBufferWriter<byte> cfg = new();
        avc!.Write(cfg);
        using MemoryStream ms = new();
        using (IsoMuxer mux = new(ms, new IsoMuxOptions { Layout = IsoLayout.MoovLast }))
        {
            int v = mux.AddTrack(new MediaTrack
            {
                Index = 0,
                Kind = TrackKind.Video,
                Codec = CodecId.H264,
                CodecTag = "avc1",
                Configuration = new CodecConfiguration(cfg.WrittenMemory.ToArray(), "avcC"),
                Timescale = 25000,
                Video = new VideoTrackInfo(320, 240),
            });
            int a = mux.AddTrack(new MediaTrack
            {
                Index = 1,
                Kind = TrackKind.Audio,
                Codec = CodecId.Pcm,
                CodecTag = "sowt",
                Timescale = 8000,
                Audio = new AudioTrackInfo(8000, 1, 16),
            });
            byte[] idr = [0, 0, 0, 5, 0x65, 0x88, 0x84, 0x00, 0x10];
            byte[] pcm = new byte[640];
            for (int i = 0; i < 3; i++)
            {
                mux.WritePacket(new PacketInfo(v, new MediaTime(i * 1000, 25000), new MediaTime(i * 1000, 25000), new MediaTime(1000, 25000), i == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, idr.Length), idr);
                mux.WritePacket(new PacketInfo(a, new MediaTime(i * 320, 8000), new MediaTime(i * 320, 8000), new MediaTime(320, 8000), PacketAttributes.Keyframe, ByteRange.None, pcm.Length), pcm);
            }

            mux.Finish();
        }

        return ms.ToArray();
    }
}
