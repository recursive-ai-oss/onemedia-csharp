using System.Security.Cryptography;
using RecursiveAI.OneMedia.Repair;
using Xunit;

namespace RecursiveAI.OneMedia.Tests.Repair;

/// <summary>Repair of damaged files: each test damages a small healthy file in memory, repairs it, and checks the packets against the original.</summary>
public class RepairTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "repair", name);

    private static string Temp(string ext) => Path.Combine(Path.GetTempPath(), "repair-" + Guid.NewGuid().ToString("N") + ext);

    private static string Write(byte[] bytes, string ext)
    {
        string path = Temp(ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Dictionary<int, List<string>> Packets(string path)
    {
        var result = new Dictionary<int, List<string>>();
        using MediaFile f = Media.Open(path);
        IDemuxer d = f.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            if (!result.TryGetValue(v.Info.TrackIndex, out List<string>? list))
            {
                result[v.Info.TrackIndex] = list = [];
            }

            list.Add($"{v.Info.Size}:{Convert.ToHexString(SHA256.HashData(v.Data))[..12]}:{v.Info.Pts}:{v.Info.IsKeyframe}");
        }

        return result;
    }

    // Every packet of `repaired` is one of `original`'s, in order; returns how many original packets are missing.
    private static int AssertSubsequence(string original, string repaired)
    {
        var a = Packets(original);
        var b = Packets(repaired);
        int missing = 0;
        foreach (KeyValuePair<int, List<string>> kv in a)
        {
            List<string> y = b.TryGetValue(kv.Key, out List<string>? l) ? l : [];
            int j = 0;
            foreach (string p in y)
            {
                while (j < kv.Value.Count && kv.Value[j] != p)
                {
                    j++;
                }

                Assert.True(j < kv.Value.Count, $"track {kv.Key}: a repaired packet is not in the original");
                j++;
            }

            missing += kv.Value.Count - y.Count;
        }

        return missing;
    }

    private static RepairReport RepairAndCheck(string healthy, byte[] damaged, string ext, RepairStatus expected, out int missing, bool mustBeComplete = false)
    {
        string input = Write(damaged, ext);
        string output = Temp(ext);
        try
        {
            RepairReport r = Media.Repair(input, output);
            Assert.Equal(expected, r.Status);
            Assert.True(File.Exists(output));
            missing = AssertSubsequence(healthy, output);
            if (mustBeComplete)
            {
                Assert.Equal(0, missing);
            }

            // a repaired file is healthy: repairing it again changes nothing
            string again = Temp(ext);
            try
            {
                RepairReport r2 = Media.Repair(output, again);
                Assert.Equal(RepairStatus.NothingToRepair, r2.Status);
                Assert.Equal(File.ReadAllBytes(output), File.ReadAllBytes(again));
            }
            finally
            {
                File.Delete(again);
            }

            return r;
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData("tiny.mkv")]
    [InlineData("tiny.avi")]
    [InlineData("tiny.mp4")]
    [InlineData("tiny-frag.mp4")]
    [InlineData("tiny.ts")]
    public void HealthyFileNeedsNothingAndIsCopiedByteForByte(string name)
    {
        string output = Temp(Path.GetExtension(name));
        try
        {
            RepairReport r = Media.Repair(Data(name), output);
            Assert.Equal(RepairStatus.NothingToRepair, r.Status);
            Assert.Equal(File.ReadAllBytes(Data(name)), File.ReadAllBytes(output));
            Assert.Equal(0, r.DroppedBytes);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void OutputThatExistsIsRefused()
    {
        string output = Write([1], ".mkv");
        try
        {
            Assert.Throws<IOException>(() => Media.Repair(Data("tiny.mkv"), output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void NotMediaIsUnsupported()
    {
        string input = Write(new byte[2000], ".bin");
        try
        {
            Assert.Throws<UnsupportedMediaException>(() => Media.Repair(input, Temp(".bin")));
        }
        finally
        {
            File.Delete(input);
        }
    }

    // ---- Matroska

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.6)]
    [InlineData(0.97)]
    public void MatroskaCutAnywhereGetsSizesDurationSeekHeadAndCues(double fraction)
    {
        byte[] b = File.ReadAllBytes(Data("tiny.mkv"));
        RepairReport r = RepairAndCheck(Data("tiny.mkv"), b[..(int)(b.Length * fraction)], ".mkv", RepairStatus.Repaired, out int missing);
        Assert.Contains(r.Items, i => i.Structure == "Cues" && i.Action == RepairAction.Rebuilt);
        Assert.Contains(r.Items, i => i.Structure == "SeekHead");
        Assert.True(missing > 0);
    }

    [Fact]
    public void MatroskaDurationZeroIsSetFromTheBlocks()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.mkv"));
        int at = IndexOf(b, [0x44, 0x89, 0x88]); // Duration, 8-byte float
        Assert.True(at > 0);
        Array.Clear(b, at + 3, 8);
        RepairReport r = RepairAndCheck(Data("tiny.mkv"), b, ".mkv", RepairStatus.Repaired, out _, mustBeComplete: true);
        Assert.Contains(r.Items, i => i.Structure == "Info" && i.Action == RepairAction.Fixed);
    }

    [Fact]
    public void MatroskaWithUnknownSizesFromALiveRecordingGetsKnownSizes()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.mkv"));
        int seg = IndexOf(b, [0x18, 0x53, 0x80, 0x67]);
        Assert.True(seg > 0);
        b[seg + 4] = 0x01;
        for (int i = 5; i < 12; i++)
        {
            b[seg + i] = 0xFF;
        }

        RepairReport r = RepairAndCheck(Data("tiny.mkv"), b, ".mkv", RepairStatus.Repaired, out _, mustBeComplete: true);
        Assert.Contains(r.Items, i => i.Structure == "Segment" && i.Action == RepairAction.Fixed);
    }

    // ---- AVI

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.8)]
    public void AviCutGetsSizesCountsAndIndex(double fraction)
    {
        byte[] b = File.ReadAllBytes(Data("tiny.avi"));
        RepairReport r = RepairAndCheck(Data("tiny.avi"), b[..(int)(b.Length * fraction)], ".avi", RepairStatus.Repaired, out int missing);
        Assert.Contains(r.Items, i => i.Structure == "idx1" && i.Action == RepairAction.Rebuilt);
        Assert.True(missing > 0);
    }

    [Fact]
    public void AviWithoutIdx1GetsOneBack()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.avi"));
        int idx = LastIndexOf(b, "idx1"u8.ToArray());
        b[idx] = (byte)'J';
        b[idx + 1] = (byte)'U';
        b[idx + 2] = (byte)'N';
        b[idx + 3] = (byte)'K';
        RepairAndCheck(Data("tiny.avi"), b, ".avi", RepairStatus.Repaired, out _, mustBeComplete: true);
    }

    [Fact]
    public void AviGarbageBetweenChunksIsSkipped()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.avi"));
        int movi = IndexOf(b, "movi"u8.ToArray());
        int at = movi + 4 + 8 + 4000;
        while (b[at] != (byte)'0' || b[at + 1] is < (byte)'0' or > (byte)'9')
        {
            at++;
        }

        byte[] junk = new byte[53];
        Array.Fill(junk, (byte)0xEE);
        byte[] damaged = [.. b[..at], .. junk, .. b[at..]];
        RepairReport r = RepairAndCheck(Data("tiny.avi"), damaged, ".avi", RepairStatus.Repaired, out int missing);
        Assert.True(r.DroppedBytes >= 53);
        Assert.True(missing <= 1);
    }

    // ---- MP4

    [Fact]
    public void Mp4WithZeroedChunkOffsetsGetsThemBack()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.mp4"));
        int stco = IndexOf(b, "stco"u8.ToArray());
        int n = (b[stco + 8] << 24) | (b[stco + 9] << 16) | (b[stco + 10] << 8) | b[stco + 11];
        Array.Clear(b, stco + 12, 4 * n);
        RepairReport r = RepairAndCheck(Data("tiny.mp4"), b, ".mp4", RepairStatus.Repaired, out _, mustBeComplete: true);
        Assert.Contains(r.Items, i => i.Structure == "stco" && i.Action == RepairAction.Rebuilt);
    }

    [Fact]
    public void Mp4WithZeroDurationsGetsThemFromTheSampleTables()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.mp4"));
        int mdhd = IndexOf(b, "mdhd"u8.ToArray());
        Array.Clear(b, mdhd + 4 + 16, 4);
        RepairReport r = RepairAndCheck(Data("tiny.mp4"), b, ".mp4", RepairStatus.Repaired, out _, mustBeComplete: true);
        Assert.Contains(r.Items, i => i.Structure == "mdhd");
    }

    [Fact]
    public void Mp4WithoutMovieBoxIsLeftToReconstruction()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.mp4"));
        int moov = IndexOf(b, "moov"u8.ToArray()) - 4;
        string input = Write(b[..moov], ".mp4");
        string output = Temp(".mp4");
        try
        {
            RepairReport r = Media.Repair(input, output);
            Assert.Equal(RepairStatus.Unrepairable, r.Status);
            Assert.False(File.Exists(output));
            Assert.NotEmpty(r.Warnings);
        }
        finally
        {
            File.Delete(input);
        }
    }

    // ---- fragmented MP4

    [Fact]
    public void FragmentedMp4CutInsideAFragmentLosesOnlyThatFragmentAndGetsAnIndex()
    {
        byte[] b = File.ReadAllBytes(Data("tiny-frag.mp4"));
        int mfra = IndexOf(b, "mfra"u8.ToArray()) - 4;
        int lastMoof = LastIndexOf(b[..mfra], "moof"u8.ToArray()) - 4;
        RepairReport r = RepairAndCheck(Data("tiny-frag.mp4"), b[..(lastMoof + 300)], ".mp4", RepairStatus.Repaired, out int missing);
        Assert.Contains(r.Items, i => i.Structure == "moof" && i.Action == RepairAction.Dropped);
        Assert.Contains(r.Items, i => i.Structure == "mfra" && i.Action == RepairAction.Rebuilt);
        Assert.True(missing > 0);
    }

    [Fact]
    public void FragmentedMp4WithoutMfraGetsOne()
    {
        byte[] b = File.ReadAllBytes(Data("tiny-frag.mp4"));
        int mfra = IndexOf(b, "mfra"u8.ToArray()) - 4;
        RepairAndCheck(Data("tiny-frag.mp4"), b[..mfra], ".mp4", RepairStatus.Repaired, out _, mustBeComplete: true);
    }

    // ---- MPEG-TS

    [Fact]
    public void TransportStreamWithLostPacketsDropsBrokenPesAndRenumbers()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.ts"));
        var kept = new List<byte>();
        int packets = b.Length / 188;
        int removed = 0;
        for (int i = 0; i < packets; i++)
        {
            // remove three packets of the busiest PID in the middle
            int pid = ((b[(i * 188) + 1] & 0x1F) << 8) | b[(i * 188) + 2];
            if (i > packets / 2 && removed < 3 && pid == 0x100)
            {
                removed++;
                continue;
            }

            kept.AddRange(b.AsSpan(i * 188, 188).ToArray());
        }

        Assert.Equal(3, removed);
        RepairReport r = RepairAndCheck(Data("tiny.ts"), [.. kept], ".ts", RepairStatus.Repaired, out int missing);
        Assert.Contains(r.Items, i => i.Structure == "continuity_counter");
        Assert.True(missing >= 1);
    }

    [Fact]
    public void TransportStreamCutMidPacketAndStartingMidStreamIsAligned()
    {
        byte[] b = File.ReadAllBytes(Data("tiny.ts"));
        RepairAndCheck(Data("tiny.ts"), b[1234..(b.Length - 100)], ".ts", RepairStatus.Repaired, out _);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(75)]
    [InlineData(97)]
    public void FaststartFileCutInsideMediaDataGetsShorterSampleTables(int percent)
    {
        byte[] b = File.ReadAllBytes(Data("tiny.mp4"));
        int mdat = IndexOf(b, "mdat"u8.ToArray());
        int cut = mdat + (int)((b.Length - mdat) * (long)percent / 100);
        RepairReport r = RepairAndCheck(Data("tiny.mp4"), b[..cut], ".mp4", RepairStatus.Repaired, out int missing);
        Assert.True(missing > 0);
        Assert.Contains(r.Items, i => i.Structure == "stbl" && i.Action == RepairAction.Rebuilt);
        Assert.All(r.Tracks, t => Assert.True(t.DroppedPackets > 0 || t.Packets > 0));
    }

    // Inputs the fuzzer found that once escaped as untyped exceptions or reported Repaired with no output.
    [Theory]
    [InlineData("avi")]
    [InlineData("isobmff")]
    [InlineData("mpegts")]
    [InlineData("avi2")]
    [InlineData("isobmff2")]
    [InlineData("isobmff3")]
    public void FuzzReprosRaiseTypedErrorsOrConsistentReports(string format)
    {
        byte[] data = File.ReadAllBytes(Data("fuzz-" + format + ".bin"));
        using MemoryStream output = new();
        try
        {
            RepairReport r = Repairer.RunEngine(format.TrimEnd('2', '3'), new MemoryMediaSource(data), output, MediaLimits.Default, CancellationToken.None);
            Assert.Equal(r.OutputWritten, output.Length > 0);
        }
        catch (MediaException)
        {
        }
    }

    private static int IndexOf(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);

    private static int LastIndexOf(byte[] data, byte[] pattern) => data.AsSpan().LastIndexOf(pattern);
}
