using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Repair.Reconstruction;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Writes a small AMR-NB 3GP file, cuts it in the media data so the movie box is lost, and rebuilds it with and without a reference file,
/// also constructing the AAC probe options. Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class ReconstructSmoke
{
    public static void Run(Action<string, bool> check)
    {
        MediaTrack track = new()
        {
            Index = 0,
            Kind = TrackKind.Audio,
            Codec = CodecId.AmrNb,
            CodecTag = "samr",
            Configuration = new CodecConfiguration(new byte[] { 0x46, 0x46, 0x4D, 0x50, 0, 0x81, 0xFF, 0, 1 }, "damr"),
            Timescale = 8000,
            Audio = new AudioTrackInfo(8000, 1),
        };
        using MemoryStream healthy = new();
        using (IsoMuxer mux = new(healthy, new IsoMuxOptions { Kind = IsoFileKind.ThreeGp, Layout = IsoLayout.MoovLast }))
        {
            mux.AddTrack(track);
            for (int i = 0; i < 120; i++)
            {
                byte[] frame = new byte[32];
                for (int k = 1; k < frame.Length; k++)
                {
                    frame[k] = (byte)((i * 31) + (k * 17));
                }

                frame[0] = (7 << 3) | 4; // RFC 4867 §5.3: mode 7, quality bit set
                mux.WritePacket(new PacketInfo(0, new MediaTime(i * 160L, 8000), new MediaTime(i * 160L, 8000), new MediaTime(160, 8000), PacketAttributes.Keyframe, ByteRange.None, frame.Length), frame);
            }

            mux.Finish();
        }

        byte[] file = healthy.ToArray();
        int mdat = file.AsSpan().IndexOf("mdat"u8) - 4;
        int cutAt = mdat + 8 + ((120 * 32) / 2) + 5;
        byte[] cut = file[..cutAt];
        cut[mdat] = cut[mdat + 1] = cut[mdat + 2] = cut[mdat + 3] = 0; // size 0: to the end of the file

        foreach (bool withReference in new[] { true, false })
        {
            using MemoryStream output = new();
            ReconstructionReport report = IsoReconstructor.Reconstruct(new MemoryMediaSource(cut), output, withReference ? new MemoryMediaSource(file) : null, AacReconstructionProbe.Options());
            using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(output.ToArray()));
            int n = 0;
            while (d.TryReadPacket(out _))
            {
                n++;
            }

            check($"reconstruct amr (reference: {withReference})", report.Tracks.Count == 1 && n == report.Tracks[0].Samples && n is > 55 and < 62);
        }
    }
}
