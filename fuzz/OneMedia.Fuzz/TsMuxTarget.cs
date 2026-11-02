using System;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Remuxing;
using RecursiveAI.OneMedia.MpegTs;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the transport stream muxer. <c>ts-mux</c> reads its input as a script of track declarations and packets (hostile
/// timestamps, sizes, orders, framing and segment switches); <c>remux-ts</c> remuxes any media file into a transport stream. The property:
/// only a <see cref="MediaException"/> (a typed refusal) may escape, and what was written is a whole number of packets that each start
/// with the sync byte.
/// </summary>
internal static class TsMuxTarget
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxSampleTableEntries = 200_000, MaxPacketBytes = 1 << 20 };

    // avcC with the parameter sets of the smoke clips: version, profile, compatibility, level, 4-byte lengths, one SPS, one PPS.
    private static readonly byte[] AvcC = BuildAvcC(Convert.FromHexString("6764000cacd941419f9f011000000300100000030320f1429960"), Convert.FromHexString("68ebe3cb22c0"));

    private static byte[] BuildAvcC(byte[] sps, byte[] pps) => [1, 0x64, 0, 0x0c, 0xff, 0xe1, 0, (byte)sps.Length, .. sps, 1, 0, (byte)pps.Length, .. pps];

    public static void Register(System.Collections.Generic.Dictionary<string, FuzzTarget> targets)
    {
        targets["ts-mux"] = Script;
        targets["remux-ts"] = RemuxTs;
    }

    private static MediaTrack Track(int selector, int index)
    {
        TrackKind kind = selector switch { 0 or 1 or 2 or 10 => TrackKind.Video, 3 or 4 or 5 or 6 => TrackKind.Audio, 9 => TrackKind.Subtitle, _ => TrackKind.Data };
        string codec = selector switch
        {
            0 or 1 => "h264",
            2 => "hevc",
            3 or 4 => "aac",
            5 => "mp3",
            6 => "ac3",
            7 => "timed-id3",
            8 => "scte-35",
            9 => "dvb-subtitle",
            10 => "mpeg2video",
            _ => "opus",
        };
        return new MediaTrack
        {
            Index = index,
            Kind = kind,
            Codec = new CodecId(codec),
            Timescale = selector == 1 ? 12800 : 90000,
            Configuration = selector switch
            {
                1 => new CodecConfiguration(AvcC, "avcC"),
                3 => new CodecConfiguration(new byte[] { 0x12, 0x10 }, "asc"),
                _ => default,
            },
            Audio = kind == TrackKind.Audio ? new AudioTrackInfo(44100, 2) : null,
            Language = "eng",
        };
    }

    internal static void Script(ReadOnlySpan<byte> input)
    {
        if (input.Length < 4)
        {
            return;
        }

        MpegTsMuxOptions options = new() { Layout = (input[0] & 1) != 0 ? TsPacketLayout.M2ts192 : TsPacketLayout.Ts188, Limits = Limits, InsertParameterSets = (input[0] & 2) == 0 };
        int tracks = (input[0] >> 2 & 3) + 1;
        int at = 1;
        MemoryStream output = new();
        try
        {
            using MpegTsMuxer mux = new(output, options);
            long[] pts = new long[8];
            for (int i = 0; i < tracks && at < input.Length; i++)
            {
                try
                {
                    mux.AddTrack(Track(input[at++] % 12, i));
                }
                catch (MuxRefusedException)
                {
                }
            }

            while (at + 3 <= input.Length)
            {
                byte control = input[at++];
                int track = control & 7;
                int size = input[at++];
                int step = (sbyte)input[at++] * 300;
                if (at + size > input.Length)
                {
                    size = input.Length - at;
                }

                ReadOnlySpan<byte> payload = input.Slice(at, size);
                at += size;
                if ((control & 0x40) != 0 && (control & 0x10) != 0)
                {
                    mux.StartSegment(new MemoryStream());
                }

                pts[track] += step;
                MediaTime time = new(pts[track], (control & 0x20) != 0 ? 1000 : 90000);
                MediaTime dts = (control & 0x80) != 0 ? MediaTime.Unknown : time;
                PacketInfo info = new(track, time, dts, new MediaTime(3600, 90000), (control & 8) != 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, size);
                try
                {
                    mux.WritePacket(info, payload);
                }
                catch (MuxRefusedException)
                {
                }
            }

            try
            {
                mux.Finish();
            }
            catch (MuxRefusedException)
            {
            }
        }
        catch (MediaException)
        {
        }

        byte[] bytes = output.ToArray();
        int size2 = options.Layout == TsPacketLayout.M2ts192 ? 192 : 188;
        if (bytes.Length % size2 != 0)
        {
            throw new InvalidOperationException($"the output is {bytes.Length} bytes, not a whole number of {size2}-byte packets");
        }

        for (int i = size2 - 188; i < bytes.Length; i += size2)
        {
            if (bytes[i] != 0x47)
            {
                throw new InvalidOperationException($"packet at {i} does not start with the sync byte");
            }
        }
    }

    internal static void RemuxTs(ReadOnlySpan<byte> input)
    {
        if (input.Length < 16)
        {
            return;
        }

        RemuxFormat format = (input[0] & 1) != 0 ? RemuxFormat.M2ts : RemuxFormat.MpegTs;
        RemuxOptions options = new() { Limits = Limits, ConfigurationProbeBytes = 1 << 20 };
        try
        {
            using MediaFile file = Media.Open(input[1..].ToArray());
            using MemoryStream output = new();
            _ = Remuxer.Remux(file, output, format, options);
        }
        catch (MediaException)
        {
        }
    }
}
