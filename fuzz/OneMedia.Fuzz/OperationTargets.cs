using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Editing;
using RecursiveAI.OneMedia.Playback;
using RecursiveAI.OneMedia.Streaming;
using RecursiveAI.OneMedia.Streaming.Sources;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the operations that run on a file path: <c>edit-trim</c> (first bytes pick the range and mode, the rest is the file),
/// <c>edit-split</c>, <c>edit-concat</c> (the file twice), <c>package</c> (first byte: bit 0 TS segments, bit 1 single file, bit 2 HLS only),
/// <c>playable</c> (first byte picks the browser profile) and <c>growing</c> (a file opened as a growing one, read to its current end).
/// Each run uses a scratch directory of its own; anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class OperationTargets
{
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "onemedia-fuzz-ops-" + Environment.ProcessId);

    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxSampleTableEntries = 200_000, MaxPacketBytes = 4 << 20 };

    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["edit-trim"] = Trim;
        targets["edit-split"] = Split;
        targets["edit-concat"] = Concat;
        targets["package"] = Package;
        targets["playable"] = Playable;
        targets["growing"] = Growing;
    }

    private static string Fresh(ReadOnlySpan<byte> file, out string dir, string name = "in.bin")
    {
        dir = Path.Combine(Work, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Guard(Action run)
    {
        try
        {
            run();
        }
        catch (MediaException)
        {
        }
        catch (MediaReadStateException)
        {
        }
    }

    private static TimeSpan Seconds(byte b) => TimeSpan.FromMilliseconds(b * 100);

    private static void Trim(ReadOnlySpan<byte> input)
    {
        if (input.Length < 19)
        {
            return;
        }

        TimeSpan from = Seconds(input[1]);
        TimeSpan to = from + Seconds(input[2]);
        TrimMode mode = (input[0] & 1) == 0 ? TrimMode.FrameAccurate : TrimMode.Keyframe;
        string path = Fresh(input[3..], out string dir, (input[0] & 2) == 0 ? "in.mp4" : "in.mkv");
        try
        {
            Guard(() => _ = Media.Trim(path, Path.Combine(dir, "out.mp4"), from, to, mode));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static void Split(ReadOnlySpan<byte> input)
    {
        if (input.Length < 19)
        {
            return;
        }

        string path = Fresh(input[3..], out string dir, (input[0] & 2) == 0 ? "in.mp4" : "in.mkv");
        try
        {
            string pattern = Path.Combine(dir, "part-{0}.mp4");
            byte b1 = input[1], b2 = input[2];
            if ((input[0] & 1) == 0)
            {
                TimeSpan a = Seconds((byte)(b1 + 1));
                Guard(() => _ = Media.Split(path, pattern, [a, a + Seconds(b2)]));
            }
            else
            {
                Guard(() => _ = Media.SplitBySize(path, pattern, 1 + b1 * 64L + b2));
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static void Concat(ReadOnlySpan<byte> input)
    {
        if (input.Length < 16)
        {
            return;
        }

        string path = Fresh(input, out string dir);
        try
        {
            Guard(() => _ = Media.Concat(Path.Combine(dir, "out.mp4"), path, path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static void Package(ReadOnlySpan<byte> input)
    {
        if (input.Length < 17)
        {
            return;
        }

        PackagingOptions options = new()
        {
            SegmentFormat = (input[0] & 1) != 0 ? SegmentFormat.MpegTs : SegmentFormat.Cmaf,
            SingleFile = (input[0] & 2) != 0,
            Formats = (input[0] & 4) != 0 ? PackagingFormats.Hls : PackagingFormats.Both,
            SegmentDuration = new MediaTime(1 + input[0] / 8, 2),
            Limits = Limits,
            Overwrite = true,
        };
        string path = Fresh(input[1..], out string dir);
        try
        {
            Guard(() => _ = Packager.Package(path, Path.Combine(dir, "out"), options));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static void Playable(ReadOnlySpan<byte> input)
    {
        if (input.Length < 17)
        {
            return;
        }

        IReadOnlyList<BrowserProfile> all = BrowserProfiles.All;
        BrowserProfile profile = all[input[0] % all.Count];
        string path = Fresh(input[1..], out string dir);
        try
        {
            Guard(() =>
            {
                _ = Media.CanPlay(path, profile);
                _ = PlayableMaker.MakePlayable(path, Path.Combine(dir, "out.mp4"), profile, new MakePlayableOptions { Overwrite = true, Remux = new Remuxing.RemuxOptions { Limits = Limits } });
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static void Growing(ReadOnlySpan<byte> input)
    {
        if (input.Length < 17)
        {
            return;
        }

        string path = Fresh(input, out string dir);
        try
        {
            Guard(() =>
            {
                using MediaFile file = Media.OpenGrowing(path, GrowthPolicy.NoWait);
                for (int i = 0; i < 20_000 && file.Demuxer is { } d && d.TryReadPacket(out PacketView _); i++)
                {
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
