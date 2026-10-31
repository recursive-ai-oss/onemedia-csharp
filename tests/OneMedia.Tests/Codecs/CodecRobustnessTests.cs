using RecursiveAI.OneMedia.Fuzz;

namespace RecursiveAI.OneMedia.Tests.Codecs;

/// <summary>
/// Every codec parser must answer random, truncated and corrupted input with a typed result and never an exception. The inputs are
/// deterministic: a fixed seed for the random data, and for the generated streams (when <c>ONEMEDIA_CODEC_STREAMS</c> is set) every
/// file cut at many points and with bytes flipped.
/// </summary>
public sealed class CodecRobustnessTests
{
    private static Dictionary<string, FuzzTarget> Targets()
    {
        Dictionary<string, FuzzTarget> t = new(StringComparer.Ordinal);
        CodecFuzz.Register(t);
        return t;
    }

    [Fact]
    public void RandomInputNeverThrows()
    {
        Random rng = new(12345);
        byte[] magic = [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0xff, 0xf1, 0x0b, 0x77, 0xfc, 0x80, 0x03, 0x47, 0x12];
        foreach ((string name, FuzzTarget target) in Targets())
        {
            for (int i = 0; i < 4000; i++)
            {
                byte[] data = new byte[rng.Next(0, 600)];
                rng.NextBytes(data);
                // sprinkle start codes, sync words and 0xff runs so parsers get past their first checks
                for (int k = 0; k < data.Length / 16; k++)
                {
                    data[rng.Next(data.Length)] = magic[rng.Next(magic.Length)];
                }

                target(data);
            }

            Assert.NotNull(name);
        }
    }

    [Fact]
    public void TruncatedAndCorruptedStreamsNeverThrow()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEMEDIA_CODEC_STREAMS");
        if (dir is null)
        {
            return;
        }

        Random rng = new(777);
        Dictionary<string, FuzzTarget> targets = Targets();
        int runs = 0;
        foreach (string path in Directory.GetFiles(dir))
        {
            if (path.EndsWith(".log", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] file = File.ReadAllBytes(path);
            // Parsers see the head of a stream or a configuration record; the middle of a file is just payload.
            byte[] head = file.AsSpan(0, Math.Min(file.Length, 4096)).ToArray();
            foreach (FuzzTarget target in targets.Values)
            {
                for (int cut = 0; cut <= head.Length; cut += Math.Max(1, head.Length / 64))
                {
                    target(head.AsSpan(0, cut));
                    runs++;
                }

                for (int flip = 0; flip < 40; flip++)
                {
                    byte[] copy = (byte[])head.Clone();
                    for (int k = 0; k < 1 + rng.Next(4); k++)
                    {
                        copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
                    }

                    target(copy);
                    runs++;
                }
            }
        }

        Assert.True(runs > 0);
    }
}
