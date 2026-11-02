using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Remuxes Annex B video and ADTS audio into MP4 through <see cref="RemuxSession"/>, reads the result back, and moves the movie box of a moov-last
/// file to the front. Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class RemuxSmoke
{
    private static readonly byte[] Sps = Convert.FromHexString("6764000cacd941419f9f011000000300100000030320f1429960");
    private static readonly byte[] Pps = Convert.FromHexString("68ebe3cb22c0");

    public static void Run(Action<string, bool> check)
    {
        MediaTrack video = new()
        {
            Index = 0,
            Kind = TrackKind.Video,
            Codec = CodecId.H264,
            Timescale = 90000,
            Video = new VideoTrackInfo(320, 180),
        };
        MediaTrack audio = new()
        {
            Index = 1,
            Kind = TrackKind.Audio,
            Codec = CodecId.Aac,
            Timescale = 90000,
            Audio = new AudioTrackInfo(44100, 2),
        };

        using MemoryStream output = new();
        RemuxReport report;
        using (RemuxSession session = new([video, audio], output, RemuxFormat.Mp4, new RemuxOptions { Faststart = false }))
        {
            byte[] raw = new byte[40];
            byte[] adts = new byte[47];
            AdtsHeader.TryWrite(2, 4, 2, raw.Length, adts);
            raw.CopyTo(adts, 7);
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

            report = session.Finish();
        }

        byte[] file = output.ToArray();
        using MemoryMediaSource source = new(file);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(source);
        int n = 0;
        int rawAudio = 0;
        while (d.TryReadPacket(out PacketView v))
        {
            n++;
            if (v.Info.TrackIndex == 1 && v.Data.Length == 40)
            {
                rawAudio++;
            }
        }

        check("remux annexb+adts to mp4", n == 24 && rawAudio == 12 && d.Tracks[0].Configuration.Format == "avcC" && report.Tracks[0].Repackaging == "annexb-to-length-prefixed" && report.Tracks[1].Repackaging == "adts-to-raw");

        // The movie box of the moov-last file above goes to the front; the packets stay the same.
        using MemoryStream fast = new();
        FaststartReport fr = Remuxer.Faststart(source, fast);
        using MemoryMediaSource fastSource = new(fast.ToArray());
        using IsoBmffDemuxer d2 = IsoBmffDemuxer.Open(fastSource);
        int n2 = 0;
        while (d2.TryReadPacket(out _))
        {
            n2++;
        }

        check("faststart", fr.Relocated && fast.Length == file.Length && n2 == 24 && !d2.MediaDataPrecedesMovie);
    }
}
