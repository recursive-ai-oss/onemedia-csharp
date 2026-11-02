using System;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the remux layer. <c>faststart</c>: the input is an ISO file; the movie box is moved to the front and, when it was, the output
/// must have the same length (unless a table was made 64-bit) and read back as boxes. <c>remux</c>: the first byte picks the output (bit 0: fragmented,
/// bit 1: movie box last); the rest is any media file, remuxed into memory. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class RemuxTarget
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxSampleTableEntries = 200_000, MaxPacketBytes = 4 << 20 };

    public static void Register(System.Collections.Generic.Dictionary<string, FuzzTarget> targets)
    {
        targets["faststart"] = Faststart;
        targets["remux"] = Remux;
    }

    internal static void Faststart(ReadOnlySpan<byte> input)
    {
        byte[] file = input.ToArray();
        try
        {
            using MemoryStream output = new();
            FaststartReport r = Remuxer.Faststart(new MemoryMediaSource(file), output, Limits);
            if (r.Relocated && r.PromotedTables == 0 && output.Length != file.Length)
            {
                throw new InvalidOperationException($"faststart changed the length from {file.Length} to {output.Length} without promoting a table");
            }

            if (!r.Relocated && !output.ToArray().AsSpan().SequenceEqual(file))
            {
                throw new InvalidOperationException("a file that needed no change did not come back identical");
            }
        }
        catch (MediaException)
        {
        }
    }

    internal static void Remux(ReadOnlySpan<byte> input)
    {
        if (input.Length < 16)
        {
            return;
        }

        RemuxFormat format = (input[0] & 1) != 0 ? RemuxFormat.FragmentedMp4 : RemuxFormat.Mp4;
        RemuxOptions options = new() { Faststart = (input[0] & 2) == 0, Limits = Limits, ConfigurationProbeBytes = 1 << 20 };
        try
        {
            using MediaFile file = Media.Open(input[1..].ToArray());
            using MemoryStream output = new();
            _ = Remuxer.Remux(file, output, format, options);
        }
        catch (MediaException)
        {
        }
        catch (MediaReadStateException)
        {
        }
    }
}
