using RecursiveAI.OneMedia.Fuzz;

namespace RecursiveAI.OneMedia.Tests.AudioStreams;

/// <summary>
/// Saved fuzz findings and a deterministic mutation pass over the fixtures: no input may make a tag or stream reader throw anything but a
/// <see cref="MediaException"/>, which the fuzz targets swallow, so an escaping exception fails the test.
/// </summary>
public class AudioStreamRobustnessTests
{
    private static readonly string[] Targets = ["id3", "ape", "mpa-es", "adts-ac3-amr-es"];

    private static Dictionary<string, FuzzTarget> Registered()
    {
        Dictionary<string, FuzzTarget> targets = new(StringComparer.Ordinal);
        AudioStreamFuzz.Register(targets);
        return targets;
    }

    private static string Root => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata");

    [Fact]
    public void SavedFuzzFindingsNoLongerEscape()
    {
        Dictionary<string, FuzzTarget> targets = Registered();
        int replayed = 0;
        foreach (string target in Targets)
        {
            string dir = System.IO.Path.Combine(Root, "fuzz", target);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir))
            {
                targets[target](File.ReadAllBytes(file));
                replayed++;
            }
        }

        Assert.True(replayed > 0);
    }

    [Fact]
    public void MutatedFixturesOnlyEverRaiseTypedErrors()
    {
        Dictionary<string, FuzzTarget> targets = Registered();
        Random random = new(12345);
        List<string> files = [.. Directory.GetFiles(System.IO.Path.Combine(Root, "audio-es")), .. Directory.GetFiles(System.IO.Path.Combine(Root, "audio-es", "tagged"))];
        foreach (string file in files)
        {
            byte[] original = File.ReadAllBytes(file);
            for (int round = 0; round < 12; round++)
            {
                byte[] data = (byte[])original.Clone();
                int flips = 1 + random.Next(6);
                for (int i = 0; i < flips; i++)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }

                if (round % 3 == 0)
                {
                    Array.Resize(ref data, random.Next(1, data.Length));
                }

                foreach ((string name, FuzzTarget target) in targets)
                {
                    for (int mode = 0; mode < 8; mode++)
                    {
                        byte[] input = new byte[data.Length + 1];
                        input[0] = (byte)(name == "adts-ac3-amr-es" ? mode | ((round % 3) << 4) : mode);
                        data.CopyTo(input, 1);
                        target(input);
                    }
                }
            }
        }
    }
}
