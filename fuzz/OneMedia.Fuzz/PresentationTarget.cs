using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Streaming.Sources;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for HLS and DASH presentations as sources. The input is a flags byte followed by resources, each a big-endian 16-bit length and that many
/// bytes (the last one may be cut short): the first resource is the playlist or manifest and the k-th distinct location the presentation asks for after it
/// is served the (k+1)-th resource, wrapping around. Flags: bit 0 picks the second variant, bit 1 seeks around while reading. Reading is capped, and
/// anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class PresentationTarget
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20, MaxTagBytes = 1 << 20, MaxSampleTableEntries = 200_000, MaxSegments = 2_000, MaxSegmentBytes = 4 << 20 };

    public static void Register(Dictionary<string, FuzzTarget> targets) => targets["presentation"] = Run;

    public static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 4)
        {
            return;
        }

        byte flags = input[0];
        List<byte[]> resources = [];
        ReadOnlySpan<byte> rest = input[1..];
        while (rest.Length >= 2 && resources.Count < 64)
        {
            int length = Math.Min((rest[0] << 8) | rest[1], rest.Length - 2);
            resources.Add(rest.Slice(2, length).ToArray());
            rest = rest[(2 + length)..];
        }

        if (resources.Count == 0)
        {
            return;
        }

        Dictionary<string, int> seen = [];
        DelegatePresentationResolver resolver = new((uri, _) =>
        {
            string key = uri.AbsoluteUri;
            if (!seen.TryGetValue(key, out int k))
            {
                k = seen.Count;
                seen[key] = k;
            }

            return new ValueTask<IAsyncMediaSource>(new MemoryMediaSource(resources[k % resources.Count]));
        });
        PresentationOptions options = new() { VariantIndex = flags & 1 };
        MediaOpenOptions open = new() { Limits = Limits };
        try
        {
            using PresentationDemuxer demuxer = PresentationSource.OpenAsync(new Uri("http://fuzz.invalid/p/index"), resolver, options, open, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            int packets = 0;
            while (packets < 5_000 && demuxer.ReadPacketAsync().AsTask().GetAwaiter().GetResult())
            {
                packets++;
            }

            if ((flags & 2) != 0)
            {
                foreach (long ms in new long[] { 0, 1500, 60_000 })
                {
                    _ = demuxer.Seek(new MediaTime(ms, 1000));
                    for (int i = 0; i < 50 && demuxer.ReadPacketAsync().AsTask().GetAwaiter().GetResult(); i++)
                    {
                    }
                }
            }
        }
        catch (MediaException)
        {
        }
    }
}
