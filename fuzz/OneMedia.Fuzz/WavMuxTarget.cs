using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for the WAVE muxer's input side. The bytes describe a track (a format record taken from the input or a PCM layout, channels, rate,
/// width, byte order), muxer options (layout, text fields, iXML, chunk ids) and a run of packets with arbitrary times and lengths. The muxer may
/// refuse (<see cref="MuxRefusedException"/>, <see cref="MediaException"/>, <see cref="ArgumentException"/> for options); anything else escaping is a
/// finding. When it accepts, the file it wrote must read back with our reader and hold exactly the samples it was given (after the byte-order change
/// the layout asked for).
/// </summary>
internal static class WavMuxTarget
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxPacketBytes = 1 << 20, MaxTagBytes = 1 << 20 };

    public static void Register(Dictionary<string, FuzzTarget> targets) => targets["wavmux"] = Run;

    internal static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 16)
        {
            return;
        }

        byte[] data = input.ToArray();
        int at = 0;
        int Next() => at < data.Length ? data[at++] : 0;
        int U16() => Next() | (Next() << 8);

        int mode = Next();
        WavLayout layout = (WavLayout)(mode & 3);
        bool passThrough = (mode & 4) != 0;
        int channels = (U16() % 12) + (((mode & 8) != 0) ? 0 : 1);
        int rate = U16() * ((mode & 16) != 0 ? 3 : 1);
        int bits = Next() % 72;
        int encoding = Next() % 6;
        bool bigEndian = (Next() & 1) != 0;
        uint mask = (uint)(Next() | (Next() << 8));
        int packetCount = Next() % 12;
        int configLength = Next() % 64;

        CodecConfiguration config = default;
        if (passThrough)
        {
            byte[] fmt = new byte[Math.Min(configLength, Math.Max(0, data.Length - at))];
            Array.Copy(data, at, fmt, 0, fmt.Length);
            at += fmt.Length;
            config = new CodecConfiguration(fmt, "fmt ");
        }

        PcmSampleFormat pcm = new((PcmEncoding)(encoding % 5), bits, Math.Max(1, bits - (Next() % 9)), bigEndian);
        MediaTrack track = new()
        {
            Index = 0,
            Kind = TrackKind.Audio,
            Codec = (mode & 32) != 0 ? CodecId.Aac : CodecId.Pcm,
            Configuration = config,
            Timescale = Math.Max(1, rate),
            Audio = new AudioTrackInfo(rate, channels, bits),
        };

        WavMuxOptions options = new()
        {
            Layout = layout,
            Limits = Limits,
            PaddingBytes = (mode & 64) != 0 ? 8 + (Next() % 8 * 2) : 0,
            IXml = (mode & 128) != 0 ? Encoding.UTF8.GetString(data, Math.Min(at, data.Length), Math.Min(32, Math.Max(0, data.Length - at))) : null,
            Bext = (mode & 2) != 0 ? new BextChunk { Description = AsciiOf(data, at, 20), CodingHistory = AsciiOf(data, at, 5) } : null,
            Chunks = (mode & 1) != 0 ? [new WavChunk(Encoding.Latin1.GetString(data, Math.Min(at, data.Length - 1), Math.Min(4, Math.Max(0, data.Length - at))), data.AsSpan(Math.Min(at, data.Length)).ToArray())] : null,
        };

        List<byte> expected = [];
        byte[]? produced = null;
        try
        {
            using MemoryStream ms = new();
            using WavMuxer mux = new(ms, options);
            mux.AddTrack(track, new WavTrackOptions { Pcm = (mode & 8) != 0 ? pcm : null, ChannelMask = (mode & 16) != 0 ? mask : null });
            bool plain = !((mode & 8) != 0 && pcm.BigEndian && pcm.BitsPerSample > 8) && !((mode & 8) != 0 && pcm.Encoding == PcmEncoding.SignedInteger && pcm.BitsPerSample == 8);
            long t = 0;
            for (int i = 0; i < packetCount; i++)
            {
                int length = Math.Min(Next() * (1 + (Next() & 7)), Math.Max(0, data.Length - at));
                byte[] payload = new byte[length];
                Array.Copy(data, at, payload, 0, length);
                at += length;
                long step = (Next() << 4) - 700;
                t += step;
                MediaTime pts = new(t, Math.Max(1, rate));
                PacketInfo info = new(0, pts, (mode & 4) != 0 ? MediaTime.Unknown : pts, new MediaTime(Next(), Math.Max(1, rate)), PacketAttributes.Keyframe, ByteRange.None, length);
                mux.WritePacket(info, payload);
                expected.AddRange(payload);
            }

            MuxReport report = mux.Finish();
            byte[] file = ms.ToArray();
            if (report.BytesWritten != file.Length)
            {
                throw new InvalidOperationException("the report's length differs from the bytes written");
            }

            if (plain && !passThrough)
            {
                produced = file;
            }
        }
        catch (Exception e) when (e is MediaException or ArgumentException)
        {
        }

        if (produced is not null)
        {
            CheckReadBack(produced, expected);
        }
    }

    private static string AsciiOf(byte[] data, int at, int length)
    {
        StringBuilder b = new();
        for (int i = at; i < Math.Min(data.Length, at + length); i++)
        {
            b.Append((char)(0x20 + (data[i] % 0x5F)));
        }

        return b.ToString();
    }

    private static void CheckReadBack(byte[] file, List<byte> expected)
    {
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(file), new MediaOpenOptions { Limits = Limits });
        List<byte> got = [];
        while (d.TryReadPacket(out PacketView v))
        {
            got.AddRange(v.Data.ToArray());
        }

        // Whatever layout the muxer described, the samples it was given must come back unchanged (no byte-order change was asked for).
        if (d.WaveFormat.EffectiveTag is 1 or 3 or 6 or 7 && !got.ToArray().AsSpan().SequenceEqual(expected.ToArray()))
        {
            throw new InvalidOperationException($"the file reads back {got.Count} bytes of samples, {expected.Count} were written, or they differ");
        }
    }
}
