using System.Linq;

namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>
/// A source of packets for a track whose samples do not come from the input file's own reader: audio re-encoded by an add-on package, say.
/// The core never produces such packets; it only merges them into the output next to the input's own.
/// </summary>
public interface IPacketFeed : IDisposable
{
    /// <summary>
    /// The track as it is declared to the output: codec, configuration, time base, edit information. Its <see cref="MediaTrack.Index"/> must be the
    /// index of the input track the feed replaces; the packets carry the same number.
    /// </summary>
    MediaTrack Track { get; }

    /// <summary>Looks at the next packet's time without taking the packet.</summary>
    /// <param name="decodeTime">The next packet's decode time (its presentation time when it has none), in the time base of <see cref="Track"/>.</param>
    /// <returns>False at the end of the feed.</returns>
    bool TryPeek(out MediaTime decodeTime);

    /// <summary>Takes the next packet, in decode order.</summary>
    /// <param name="packet">The packet; its data is valid until the next call.</param>
    /// <returns>False at the end of the feed.</returns>
    bool TryRead(out PacketView packet);
}

/// <summary>Replaces the packets of one input track by those of a feed.</summary>
/// <param name="Feed">The packets to write instead; <see cref="IPacketFeed.Track"/> says how the track is declared.</param>
public sealed record TrackReplacement(IPacketFeed Feed)
{
    /// <summary>The input track that the feed stands in for (<c>Feed.Track.Index</c>).</summary>
    public int InputTrack => Feed.Track.Index;
}

// Merges the feeds into the stream of the input's own packets by decode time, so the output stays interleaved and holds one feed packet at a time.
internal sealed class PacketFeeds
{
    private readonly IPacketFeed[] _feeds;

    private PacketFeeds(IPacketFeed[] feeds) => _feeds = feeds;

    public static PacketFeeds None { get; } = new([]);

    public bool Any => _feeds.Length > 0;

    public static PacketFeeds Of(RemuxOptions? options)
    {
        if (options?.Replacements is not { Count: > 0 } r)
        {
            return None;
        }

        return new PacketFeeds([.. r.Select(x => x.Feed)]);
    }

    public bool Replaces(int inputTrack)
    {
        foreach (IPacketFeed f in _feeds)
        {
            if (f.Track.Index == inputTrack)
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<MediaTrack> Apply(IReadOnlyList<MediaTrack> tracks)
    {
        if (_feeds.Length == 0)
        {
            return tracks;
        }

        MediaTrack[] result = [.. tracks];
        foreach (IPacketFeed f in _feeds)
        {
            int i = f.Track.Index;
            if ((uint)i >= (uint)result.Length)
            {
                throw new ArgumentException($"A track replacement names input track {i}, which the input does not have.", nameof(tracks));
            }

            result[i] = f.Track;
        }

        return result;
    }

    // Writes every feed packet that is due at or before `upTo` (all of them when `upTo` is unknown and `all` is set).
    public void Drain(RemuxSession session, MediaTime upTo, bool all, CancellationToken cancellationToken)
    {
        foreach (IPacketFeed f in _feeds)
        {
            while (f.TryPeek(out MediaTime t) && (all || (upTo.IsKnown && t <= upTo)))
            {
                if (!f.TryRead(out PacketView p))
                {
                    break;
                }

                session.WritePacket(p.Info, p.Data, cancellationToken);
            }
        }
    }
}
