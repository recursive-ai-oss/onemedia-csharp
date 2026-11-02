using System.Diagnostics;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Riff;

// Usage:
//   WavCheck meta <out.wav>                      a two-second stereo file with bext, cart, iXML and INFO chunks (for ExifTool and MediaInfo read-back)
//   WavCheck big <out.wav> <GiB> [--atomic] [--rf64]
//                                                24-bit 48 kHz stereo sine-like data of the given size in 1 MiB packets; prints bytes, duration, elapsed
//                                                seconds and peak working set. --atomic writes under a temporary name and renames at the end.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: WavCheck meta <out> | WavCheck big <out> <GiB> [--atomic] [--rf64]");
    return 2;
}

MediaTrack track = new()
{
    Index = 0,
    Kind = TrackKind.Audio,
    Codec = CodecId.Pcm,
    Timescale = 48000,
    Audio = new AudioTrackInfo(48000, 2, 24),
};

switch (args[0])
{
    case "meta": return Meta(args[1]);
    case "big" when args.Length >= 3: return Big(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args.Contains("--atomic"), args.Contains("--rf64"));
    default: Console.Error.WriteLine("unknown command"); return 2;
}

static byte[] Fill(byte[] buffer, long seed)
{
    for (int i = 0; i < buffer.Length; i++)
    {
        buffer[i] = (byte)((seed + i) * 31 >> 3);
    }

    return buffer;
}

int Meta(string path)
{
    WavMuxOptions options = new()
    {
        Bext = new BextChunk
        {
            Description = "Scene 12, take 4",
            Originator = "OneMedia WavCheck",
            OriginatorReference = "WC0000000001",
            OriginationDate = new DateOnly(2026, 3, 4),
            OriginationTime = new TimeOnly(13, 5, 59),
            TimeReference = 4_800_000,
            CodingHistory = "A=PCM,F=48000,W=24,M=stereo,T=OneMedia\r\n",
        },
        Cart = new CartChunk { Title = "Jingle", Artist = "Station", CutId = "0042", ClientId = "Client", Category = "IDS", StartDate = "2026/03/04", StartTime = "00:00:00", ProducerAppId = "OneMedia", ProducerAppVersion = "1", LevelReference = 12000 },
        IXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><BWFXML><IXML_VERSION>1.5</IXML_VERSION><PROJECT>Pilot</PROJECT><SCENE>12</SCENE><TAKE>4</TAKE><TAPE>T1</TAPE></BWFXML>",
        Info = [new("INAM", "Pilot"), new("IART", "Someone"), new("ICMT", "A comment"), new("ICRD", "2026-03-04"), new("ISFT", "OneMedia")],
        PaddingBytes = 512,
    };
    using WavMuxer mux = WavMuxer.CreateFile(path, options, overwrite: true);
    mux.AddTrack(track);
    byte[] chunk = Fill(new byte[6 * 4800], 0);
    for (int i = 0; i < 20; i++)
    {
        mux.WritePacket(new PacketInfo(0, new MediaTime(i * 4800L, 48000), new MediaTime(i * 4800L, 48000), new MediaTime(4800, 48000), PacketAttributes.Keyframe, ByteRange.None, chunk.Length), chunk);
    }

    Console.WriteLine(mux.Finish().BytesWritten);
    return 0;
}

int Big(string path, double gib, bool atomic, bool rf64)
{
    long total = (long)(gib * (1L << 30));
    long perPacket = 1 << 20;
    perPacket -= perPacket % 6;
    WavMuxOptions options = new() { Layout = rf64 ? WavLayout.Rf64 : WavLayout.Auto };
    Stopwatch sw = Stopwatch.StartNew();
    using WavMuxer mux = atomic ? WavMuxer.CreateFile(path, options, overwrite: true) : new WavMuxer(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16), options, ownsOutput: true);
    mux.AddTrack(track);
    byte[] buffer = new byte[perPacket];
    long written = 0;
    while (written < total)
    {
        int n = (int)Math.Min(perPacket, total - written);
        n -= n % 6;
        if (n == 0)
        {
            break;
        }

        Fill(buffer, written);
        long frame = written / 6;
        mux.WritePacket(new PacketInfo(0, new MediaTime(frame, 48000), new MediaTime(frame, 48000), new MediaTime(n / 6, 48000), PacketAttributes.Keyframe, ByteRange.None, n), buffer.AsSpan(0, n));
        written += n;
    }

    MuxReport report = mux.Finish();
    Console.WriteLine($"bytes={report.BytesWritten} data={report.Tracks[0].PayloadBytes} frames={report.Tracks[0].Duration.Ticks} seconds={sw.Elapsed.TotalSeconds:F1} peakWorkingSetMB={Process.GetCurrentProcess().PeakWorkingSet64 >> 20}");
    return 0;
}
