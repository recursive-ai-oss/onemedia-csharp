using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.MpegTs;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Writes Annex B video and ADTS audio as a transport stream (188- and 192-byte packets) through <see cref="RemuxSession"/>, with a segment
/// switch on the direct muxer, and reads the result back. Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class MpegTsMuxSmoke
{
    private static readonly byte[] Sps = Convert.FromHexString("6764000cacd941419f9f011000000300100000030320f1429960");
    private static readonly byte[] Pps = Convert.FromHexString("68ebe3cb22c0");

    public static void Run(Action<string, bool> check)
    {
        MediaTrack video = new() { Index = 0, Kind = TrackKind.Video, Codec = CodecId.H264, Timescale = 90000, Video = new VideoTrackInfo(320, 180) };
        MediaTrack audio = new() { Index = 1, Kind = TrackKind.Audio, Codec = CodecId.Aac, Timescale = 90000, Audio = new AudioTrackInfo(44100, 2) };
        byte[] raw = new byte[40];
        byte[] adts = new byte[47];
        AdtsHeader.TryWrite(2, 4, 2, raw.Length, adts);
        raw.CopyTo(adts, 7);

        foreach ((RemuxFormat format, int size) in new[] { (RemuxFormat.MpegTs, 188), (RemuxFormat.M2ts, 192) })
        {
            using MemoryStream output = new();
            using (RemuxSession session = new([video, audio], output, format))
            {
                for (int i = 0; i < 12; i++)
                {
                    long t = 90000L + (i * 3600L);
                    List<byte> au = [];
                    if (i == 0)
                    {
                        au.AddRange([0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps]);
                    }

                    au.AddRange([0, 0, 0, 1, i == 0 ? (byte)0x65 : (byte)0x41, 0x88, (byte)i, 0x11, 0x22]);
                    session.WritePacket(new PacketInfo(0, new MediaTime(t, 90000), new MediaTime(t, 90000), new MediaTime(3600, 90000), i == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, au.Count), au.ToArray());
                    long at = 90000L + (i * 2090L);
                    session.WritePacket(new PacketInfo(1, new MediaTime(at, 90000), new MediaTime(at, 90000), new MediaTime(2090, 90000), PacketAttributes.Keyframe, ByteRange.None, adts.Length), adts);
                }

                session.Finish();
            }

            byte[] file = output.ToArray();
            using MediaFile back = Media.Open(file);
            int video2 = 0, audio2 = 0;
            IDemuxer d = back.Demuxer!;
            while (d.TryReadPacket(out PacketView v))
            {
                if (v.Info.TrackIndex == 0)
                {
                    video2++;
                }
                else if (v.Data.Length == 47)
                {
                    audio2++;
                }
            }

            check($"mux transport stream {size}", file.Length % size == 0 && video2 == 12 && audio2 == 12 && back.Format == MediaFormatId.MpegTs);
        }

        // Segments: the direct muxer, switching the output at the second keyframe.
        using MemoryStream first = new();
        using MemoryStream second = new();
        using (MpegTsMuxer mux = new(first))
        {
            mux.AddTrack(video);
            byte[] idr = [0, 0, 0, 1, 0x65, 1, 2, 3];
            for (int i = 0; i < 4; i++)
            {
                PacketInfo info = new(0, new MediaTime(i * 3600L, 90000), new MediaTime(i * 3600L, 90000), new MediaTime(3600, 90000), PacketAttributes.Keyframe, ByteRange.None, idr.Length);
                if (i == 2 && mux.CanStartSegmentAt(info))
                {
                    mux.StartSegment(second);
                }

                mux.WritePacket(info, idr);
            }

            mux.Finish();
        }

        byte[] s2 = second.ToArray();
        check("mux transport stream segments", first.Length > 0 && s2.Length > 0 && s2[0] == 0x47 && s2[1] == 0x40 && s2[2] == 0 && s2.Length % 188 == 0);
    }
}
