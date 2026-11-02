using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Remuxing;
using RecursiveAI.OneMedia.Streaming;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Packages a small MP4 as CMAF with HLS and DASH manifests and as transport-stream segments, then reads the manifests back with the playlist and MPD
/// parsers. Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class PackagingSmoke
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
        using MemoryStream mp4 = new();
        using (RemuxSession session = new([video, audio], mp4, RemuxFormat.Mp4, new RemuxOptions { Faststart = false }))
        {
            for (int i = 0; i < 12; i++)
            {
                long t = 90000L + (i * 3600L);
                List<byte> au = [];
                if (i % 4 == 0)
                {
                    au.AddRange([0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps]);
                }

                au.AddRange([0, 0, 0, 1, i % 4 == 0 ? (byte)0x65 : (byte)0x41, 0x88, (byte)i, 0x11, 0x22]);
                session.WritePacket(new PacketInfo(0, new MediaTime(t, 90000), new MediaTime(t, 90000), new MediaTime(3600, 90000), i % 4 == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, au.Count), au.ToArray());
                long at = 90000L + (i * 2090L);
                session.WritePacket(new PacketInfo(1, new MediaTime(at, 90000), new MediaTime(at, 90000), new MediaTime(2090, 90000), PacketAttributes.Keyframe, ByteRange.None, adts.Length), adts);
            }

            session.Finish();
        }

        string root = Path.Combine(Path.GetTempPath(), "onemedia-smoke-pkg-" + Guid.NewGuid().ToString("N"));
        try
        {
            using MediaFile input = Media.Open(mp4.ToArray());
            PackagingOptions options = new() { SegmentDuration = new MediaTime(1, 10) };
            PackagingReport cmaf = Packager.Package(input, Path.Combine(root, "cmaf"), options);
            string master = File.ReadAllText(Path.Combine(root, "cmaf", "master.m3u8"));
            string mpd = File.ReadAllText(Path.Combine(root, "cmaf", "manifest.mpd"));
            HlsPlaylist hls = HlsPlaylist.Parse(master);
            bool segments = false;
            if (hls is HlsMultivariantPlaylist multivariant && multivariant.Variants.Count > 0)
            {
                HlsPlaylist media = HlsPlaylist.Parse(File.ReadAllText(Path.Combine(root, "cmaf", multivariant.Variants[0].Uri)));
                segments = media is HlsMediaPlaylist { EndList: true, Segments.Count: >= 2 } m && HlsPlaylist.Parse(media.ToString()).ToString() == media.ToString();
            }

            DashMpd parsed = DashMpd.Parse(mpd);
            check("package cmaf hls and dash", cmaf.Tracks.Count == 2 && segments && parsed.ToString().Length > 0 && DashMpd.Parse(parsed.ToString()).ToString() == parsed.ToString());

            PackagingReport ts = Packager.Package(input, Path.Combine(root, "ts"), options with { SegmentFormat = SegmentFormat.MpegTs, Formats = PackagingFormats.Hls });
            int tsFiles = Directory.GetFiles(Path.Combine(root, "ts"), "*.ts", SearchOption.AllDirectories).Length;
            check("package transport stream segments", ts.Manifests.Count > 0 && tsFiles >= 2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
