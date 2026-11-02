using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.Repair.Reconstruction;

namespace RecursiveAI.OneMedia.Tests.Audio;

// AAC has no self-delimiting frames; the .Audio probe finds them. A cut-off recording's AAC packets must be the original's first packets.
public sealed class AacReconstructionTests
{
    private static byte[] Cut(byte[] file, double fraction)
    {
        int pos = 0;
        int ftypEnd = 0;
        while (pos + 8 <= file.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            if (type == "ftyp")
            {
                ftypEnd = pos + (int)size;
            }

            if (type == "mdat")
            {
                int keep = (int)((size - 8) * fraction) + 5;
                using MemoryStream ms = new();
                ms.Write(file, 0, ftypEnd);
                ms.Write(new byte[] { 0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'a', (byte)'t' });
                ms.Write(file, pos + 8, keep);
                return ms.ToArray();
            }

            pos += (int)size;
        }

        throw new InvalidOperationException("no mdat");
    }

    private static List<(TrackKind Kind, byte[] Data)> Packets(byte[] file)
    {
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        Dictionary<int, TrackKind> kinds = d.Tracks.ToDictionary(t => t.Index, t => t.Kind);
        List<(TrackKind, byte[])> all = [];
        while (d.TryReadPacket(out PacketView v))
        {
            all.Add((kinds[v.Info.TrackIndex], v.Data.ToArray()));
        }

        return all;
    }

    [Fact]
    public void AacPacketsAreRecoveredThroughTheProbeWithAReference()
    {
        byte[] healthy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "isobmff", "bframes_aac.mp4"));
        byte[] cut = Cut(healthy, 0.7);
        using MemoryStream output = new();
        ReconstructionReport report = IsoReconstructor.Reconstruct(new MemoryMediaSource(cut), output, new MemoryMediaSource(healthy), AacReconstructionProbe.Options());

        var original = Packets(healthy).Where(p => p.Kind == TrackKind.Audio).ToList();
        var rebuilt = Packets(output.ToArray()).Where(p => p.Kind == TrackKind.Audio).ToList();
        Assert.NotEmpty(rebuilt);
        Assert.True(rebuilt.Count <= original.Count);
        for (int i = 0; i < rebuilt.Count; i++)
        {
            Assert.True(original[i].Data.AsSpan().SequenceEqual(rebuilt[i].Data), $"AAC packet {i}");
        }

        Assert.Contains(report.Tracks, t => t.Codec == CodecId.Aac && t.Samples == rebuilt.Count);
    }

    [Fact]
    public void WithoutTheProbeTheAacTrackIsReportedAsLost()
    {
        byte[] healthy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "isobmff", "bframes_aac.mp4"));
        byte[] cut = Cut(healthy, 0.7);
        using MemoryStream output = new();
        ReconstructionReport report = IsoReconstructor.Reconstruct(new MemoryMediaSource(cut), output, new MemoryMediaSource(healthy));
        Assert.DoesNotContain(report.Tracks, t => t.Codec == CodecId.Aac && t.Samples > 0);
        Assert.NotEmpty(report.Notes);
    }

    // A movie box that was zeroed (its place kept) leaves the box chain unreadable in front of the media data.
    [Fact]
    public void AZeroedMovieBoxInFrontOfTheMediaDataIsStepped()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "isobmff", "faststart.mp4"));
        byte[] damaged = (byte[])original.Clone();
        int pos = 0;
        while (pos + 8 <= damaged.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(damaged.AsSpan(pos));
            if (System.Text.Encoding.ASCII.GetString(damaged, pos + 4, 4) == "moov")
            {
                Array.Clear(damaged, pos, (int)size);
                break;
            }

            pos += (int)size;
        }

        using MemoryStream output = new();
        ReconstructionReport report = IsoReconstructor.Reconstruct(new MemoryMediaSource(damaged), output, new MemoryMediaSource(original), AacReconstructionProbe.Options());
        var o = Packets(original);
        var r = Packets(output.ToArray());
        foreach (TrackKind kind in new[] { TrackKind.Video, TrackKind.Audio })
        {
            var ok = o.Where(p => p.Kind == kind).ToList();
            var rk = r.Where(p => p.Kind == kind).ToList();
            Assert.Equal(ok.Count, rk.Count);
            for (int i = 0; i < rk.Count; i++)
            {
                Assert.True(ok[i].Data.AsSpan().SequenceEqual(rk[i].Data), $"{kind} packet {i}");
            }
        }

        Assert.True(report.RecoveredFraction > 0.95);
    }
}
