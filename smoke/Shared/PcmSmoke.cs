using System.Linq;
using System.Buffers.Binary;
using RecursiveAI.OneMedia.Aiff;
using RecursiveAI.OneMedia.Caf;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Reads tiny hand-built WAVE, AIFF and CAF files: through the sniffing registry, forward-only and over a ranged source. Part of the NativeAOT
/// and browser-WebAssembly smoke runs.
/// </summary>
internal static class PcmSmoke
{
    private static byte[] Wav()
    {
        byte[] data = new byte[8000 * 4];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        byte[] file = new byte[44 + data.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), 16);
        "data"u8.CopyTo(file.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), (uint)data.Length);
        data.CopyTo(file.AsSpan(44));
        return file;
    }

    private static byte[] Aiff()
    {
        byte[] file = new byte[12 + 26 + 16 + 4000];
        "FORM"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)(file.Length - 8));
        "AIFFCOMM"u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), 18);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(20), 2);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(22), 1000);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(26), 16);
        new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 }.CopyTo(file, 28);
        "SSND"u8.CopyTo(file.AsSpan(38));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(42), 4008);
        return file;
    }

    private static byte[] Caf()
    {
        byte[] file = new byte[8 + 12 + 32 + 12 + 4 + 4000];
        "caff"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), 1);
        "desc"u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(12), 32);
        BinaryPrimitives.WriteDoubleBigEndian(file.AsSpan(20), 44100);
        "lpcm"u8.CopyTo(file.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(36), 4);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(44), 2);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(48), 16);
        "data"u8.CopyTo(file.AsSpan(52));
        BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(56), 4 + 4000);
        return file;
    }

    public static async Task RunAsync(Action<string, bool> check)
    {
        MediaFormatRegistry registry = MediaFormats.Create();
        foreach ((string name, byte[] file, MediaFormatId id, long frames, int rate) in new[]
        {
            ("wav", Wav(), MediaFormatId.Wav, 8000L, 8000),
            ("aiff", Aiff(), MediaFormatId.Aiff, 1000L, 44100),
            ("caf", Caf(), MediaFormatId.Caf, 1000L, 44100),
        })
        {
            long packets = 0, bytes = 0;
            using (IDemuxer d = registry.Open(new MemoryMediaSource(file)))
            {
                while (d.TryReadPacket(out PacketView p))
                {
                    packets++;
                    bytes += p.Data.Length;
                }

                check($"{name} format", d.Format == id && d.Tracks[0].Codec == CodecId.Pcm && d.Duration == new MediaTime(frames, rate));
            }

            check($"{name} packets", packets > 0 && bytes == frames * 4);

            await using RangedMediaSource ranged = new(file.Length, (offset, buffer, _) =>
            {
                int count = (int)Math.Min(buffer.Length, file.Length - offset);
                file.AsMemory((int)offset, count).CopyTo(buffer);
                return new ValueTask<int>(count);
            });
            IMediaFormat format = (IMediaFormat)registry.Formats.First(f => f.Id == id);
            await using IAsyncDemuxer a = await format.OpenAsync(ranged, MediaOpenOptions.Default with { LeaveOpen = true });
            long asyncPackets = 0, asyncBytes = 0;
            while (await a.ReadPacketAsync())
            {
                asyncPackets++;
                asyncBytes += a.Current.Data.Length;
            }

            check($"{name} ranged", asyncPackets == packets && asyncBytes == bytes);
        }

        using WavDemuxer forward = WavDemuxer.Open(new StreamForwardSource(new MemoryStream(Wav())));
        long n = 0;
        while (forward.TryReadPacket(out _))
        {
            n++;
        }

        check("wav forward-only", n > 0 && !forward.CanSeek);
    }
}
