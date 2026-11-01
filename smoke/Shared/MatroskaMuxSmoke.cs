using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>Writes a small Matroska, WebM and live file in memory and reads each back, under NativeAOT and in the browser WebAssembly runtime.</summary>
internal static class MatroskaMuxSmoke
{
    public static void Run(Action<string, bool> check)
    {
        MediaTrack video = new() { Index = 0, Kind = TrackKind.Video, Codec = CodecId.Vp9, Timescale = 1000, Video = new VideoTrackInfo(64, 48) };
        MediaTrack audio = new()
        {
            Index = 1,
            Kind = TrackKind.Audio,
            Codec = CodecId.Opus,
            Timescale = 48000,
            Configuration = new CodecConfiguration(new byte[] { (byte)'O', (byte)'p', (byte)'u', (byte)'s', (byte)'H', (byte)'e', (byte)'a', (byte)'d', 1, 2, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0 }, "OpusHead"),
            Audio = new AudioTrackInfo(48000, 2),
        };

        foreach ((string name, MatroskaMuxOptions options) in new[]
        {
            ("matroska", new MatroskaMuxOptions()),
            ("webm", new MatroskaMuxOptions { Profile = MatroskaProfile.WebM, Crc32 = true }),
        })
        {
            using MemoryStream output = new();
            MatroskaMuxer mux = new(output, [video, audio], options);
            Feed(mux);
            mux.Finish();
            using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(output.ToArray()));
            check($"mux {name}: packets", Count(d) == 60 && d.Tracks.Count == 2);
        }

        using MemoryStream live = new();
        MatroskaMuxer liveMux = new(new ForwardOnly(live), [video, audio], new MatroskaMuxOptions { Live = true });
        Feed(liveMux);
        liveMux.Finish();
        using MatroskaDemuxer fwd = MatroskaDemuxer.Open(new StreamForwardSource(new MemoryStream(live.ToArray())));
        check("mux live: forward-only packets", Count(fwd) == 60);

        bool refused = false;
        try
        {
            _ = new MatroskaMuxer(new MemoryStream(), [new MediaTrack { Index = 0, Kind = TrackKind.Audio, Codec = CodecId.Aac, Timescale = 44100, Configuration = new CodecConfiguration(new byte[] { 0x12, 0x10 }, "asc") }], new MatroskaMuxOptions { Profile = MatroskaProfile.WebM });
        }
        catch (MuxRefusedException e)
        {
            refused = e.Reason == MuxRefusalReason.UnsupportedCodec;
        }

        check("mux webm refuses aac", refused);
    }

    private static void Feed(MatroskaMuxer mux)
    {
        byte[] data = new byte[40];
        for (int i = 0; i < 30; i++)
        {
            data[0] = (byte)i;
            mux.WritePacket(new PacketInfo(0, new MediaTime(i * 40, 1000), new MediaTime(i * 40, 1000), new MediaTime(40, 1000), i % 10 == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, data.Length), data);
            mux.WritePacket(new PacketInfo(1, new MediaTime(i * 1920, 48000), new MediaTime(i * 1920, 48000), new MediaTime(1920, 48000), PacketAttributes.Keyframe, ByteRange.None, 10), data.AsSpan(0, 10));
        }
    }

    private static int Count(MatroskaDemuxer d)
    {
        int n = 0;
        while (d.TryReadPacket(out _))
        {
            n++;
        }

        return n;
    }

    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
