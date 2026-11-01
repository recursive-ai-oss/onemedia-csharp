using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Writes a small AAC-and-H.264 file with the ISO muxer in each layout (movie box first, last, fragmented with a segment index) and reads
/// it back with the demuxer. Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class MuxSmoke
{
    private static readonly byte[] AvcC = [1, 0x64, 0, 0x1F, 0xFF, 0xE0, 0];

    public static void Run(Action<string, bool> check)
    {
        foreach ((string name, IsoMuxOptions options) in new (string, IsoMuxOptions)[]
        {
            ("movie first", new IsoMuxOptions()),
            ("movie last", new IsoMuxOptions { Layout = IsoLayout.MoovLast }),
            ("fragmented", new IsoMuxOptions { Layout = IsoLayout.Fragmented, FragmentDuration = new MediaTime(1, 4), WriteSegmentIndex = true, WriteRandomAccess = true }),
        })
        {
            using MemoryStream output = new();
            long frames;
            using (IsoMuxer mux = new(output, options))
            {
                MediaTrack video = new()
                {
                    Index = 0,
                    Kind = TrackKind.Video,
                    Codec = CodecId.H264,
                    Configuration = new CodecConfiguration(AvcC, "avcC"),
                    Timescale = 12800,
                    Video = new VideoTrackInfo(320, 180),
                };
                MediaTrack audio = new()
                {
                    Index = 1,
                    Kind = TrackKind.Audio,
                    Codec = CodecId.Aac,
                    Configuration = new CodecConfiguration(new byte[] { 0x12, 0x10 }, "asc"),
                    Timescale = 44100,
                    Audio = new AudioTrackInfo(44100, 2),
                    Edit = new TrackEditInfo(Priming: new MediaTime(1024, 44100)),
                };
                int v = mux.AddTrack(video);
                int a = mux.AddTrack(audio);
                byte[] payload = new byte[64];
                for (int i = 0; i < 24; i++)
                {
                    MediaTime vt = new(i * 512L, 12800);
                    mux.WritePacket(new PacketInfo(v, vt, vt, new MediaTime(512, 12800), i % 8 == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, payload.Length), payload);
                    MediaTime at = new(i * 1024L, 44100);
                    mux.WritePacket(new PacketInfo(a, at, at, new MediaTime(1024, 44100), PacketAttributes.Keyframe, ByteRange.None, payload.Length), payload);
                }

                MuxReport report = mux.Finish();
                frames = report.Tracks[0].Packets + report.Tracks[1].Packets;
            }

            using MemoryMediaSource source = new(output.ToArray());
            using IsoBmffDemuxer d = IsoBmffDemuxer.Open(source);
            int n = 0;
            while (d.TryReadPacket(out _))
            {
                n++;
            }

            check($"mux {name}", frames == 48 && n == 48 && d.Tracks.Count == 2 && d.Tracks[1].Edit.Priming == new MediaTime(1024, 44100));
        }

        bool refused = false;
        try
        {
            using IsoMuxer mux = new(new MemoryStream());
            mux.AddTrack(new MediaTrack { Index = 0, Kind = TrackKind.Audio, Codec = CodecId.Vorbis, Timescale = 44100, Audio = new AudioTrackInfo(44100, 2) });
        }
        catch (MuxRefusedException e)
        {
            refused = e.Reason == MuxRefusalReason.UnsupportedCodec;
        }

        check("mux refuses vorbis", refused);
    }
}
