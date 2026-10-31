using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Where a movie fragment box lies in the file.</summary>
internal readonly record struct FragmentLocation(long Offset, long End)
{
    public bool IsNone => Offset < 0;

    public static FragmentLocation None => new(-1, -1);
}

/// <summary>Finds and loads the next movie fragment box at or after a file position.</summary>
internal interface IFragmentLoader
{
    ValueTask<FragmentLocation> LoadNextAsync(long position, MovieFragment into, CancellationToken cancellationToken);
}

internal sealed class SeekableFragmentLoader(IMediaSource source, MediaLimits limits, DefectLog defects) : IFragmentLoader
{
    public ValueTask<FragmentLocation> LoadNextAsync(long position, MovieFragment into, CancellationToken cancellationToken) =>
        new(LoadNext(position, into, cancellationToken));

    public FragmentLocation LoadNext(long position, MovieFragment into, CancellationToken cancellationToken)
    {
        long length = source.Length;
        while (position < length && BoxReader.TryRead(source, position, length, true, defects, out BoxHeader h, cancellationToken))
        {
            if (h.Type == BoxTypes.Moof)
            {
                int total = FragmentSizing.Check(h, limits);
                byte[] buffer = into.RentBuffer(total);
                if (source.Read(h.Offset, buffer.AsSpan(0, total), cancellationToken) < total)
                {
                    defects.Add(DefectKind.Truncated, DefectSeverity.Error, h.Offset, "The source ends inside a movie fragment header box.");
                    return FragmentLocation.None;
                }

                return new FragmentLocation(h.Offset, h.End);
            }

            position = h.End;
        }

        return FragmentLocation.None;
    }
}

internal sealed class AsyncFragmentLoader(IAsyncMediaSource source, MediaLimits limits, DefectLog defects) : IFragmentLoader
{
    public async ValueTask<FragmentLocation> LoadNextAsync(long position, MovieFragment into, CancellationToken cancellationToken)
    {
        long length = source.Length;
        byte[] head = new byte[BoxReader.MaxHeaderBytes];
        while (position < length)
        {
            int n = await source.ReadAsync(position, head.AsMemory(0, (int)Math.Min(head.Length, length - position)), cancellationToken).ConfigureAwait(false);
            if (!BoxReader.TryParse(head.AsSpan(0, n), position, length, true, defects, out BoxHeader h))
            {
                break;
            }

            if (h.Type == BoxTypes.Moof)
            {
                int total = FragmentSizing.Check(h, limits);
                byte[] buffer = into.RentBuffer(total);
                if (await source.ReadAsync(h.Offset, buffer.AsMemory(0, total), cancellationToken).ConfigureAwait(false) < total)
                {
                    defects.Add(DefectKind.Truncated, DefectSeverity.Error, h.Offset, "The source ends inside a movie fragment header box.");
                    return FragmentLocation.None;
                }

                return new FragmentLocation(h.Offset, h.End);
            }

            position = h.End;
        }

        return FragmentLocation.None;
    }
}

internal static class FragmentSizing
{
    public static int Check(BoxHeader h, MediaLimits limits)
    {
        long total = h.HeaderSize + h.PayloadSize;
        limits.CheckAllocation(total, h.Offset);
        if (total > Array.MaxLength)
        {
            throw new MediaLimitExceededException("MaxAllocationBytes", Array.MaxLength, total, h.Offset);
        }

        return (int)total;
    }
}

/// <summary>
/// Keyframe seeking in a fragmented file. Without an index every fragment's header is read from the start until the target is passed
/// (one header box per fragment, never the media data); the landing fragment is then reloaded and its earlier samples skipped.
/// </summary>
internal static class FragmentSeeker
{
    public static async ValueTask<MediaTime> SeekAsync(IsoBmffEngine engine, IFragmentLoader loader, long firstFragmentPosition, MediaTime target, SeekMode mode,
        Action<long> setNextPosition, CancellationToken cancellationToken)
    {
        TrackState[] states = engine.States;
        TrackState? primary = null;
        foreach (TrackState s in states)
        {
            if (!s.Enabled)
            {
                continue;
            }

            if (s.Info.Kind == TrackKind.Video)
            {
                primary = s;
                break;
            }

            primary ??= s;
        }

        foreach (TrackState s in states)
        {
            s.NextFragmentDts = 0;
        }

        if (primary is null)
        {
            return MediaTime.Unknown;
        }

        long[] snapshot = new long[states.Length];
        long[]? bestSnapshot = null;
        long bestMoof = -1, bestOffset = -1;
        MediaTime bestPts = MediaTime.Unknown;
        long position = firstFragmentPosition;
        bool done = false;
        while (!done)
        {
            for (int i = 0; i < states.Length; i++)
            {
                snapshot[i] = states[i].NextFragmentDts;
            }

            FragmentLocation loc = await loader.LoadNextAsync(position, engine.Fragment, cancellationToken).ConfigureAwait(false);
            if (loc.IsNone)
            {
                break;
            }

            position = loc.End;
            engine.BeginFragment(loc.Offset);
            while (engine.TryNextFragmentSample(out PacketInfo p))
            {
                if (p.TrackIndex != primary.Info.Index || !p.IsKeyframe)
                {
                    continue;
                }

                if (mode == SeekMode.PreviousKeyframe)
                {
                    if (p.Pts > target && bestMoof >= 0)
                    {
                        done = true;
                        break;
                    }

                    if (p.Pts <= target || bestMoof < 0)
                    {
                        bestMoof = loc.Offset;
                        bestOffset = p.SourceRange.Offset;
                        bestPts = p.Pts;
                        bestSnapshot = (long[])snapshot.Clone();
                    }
                }
                else if (p.Pts >= target)
                {
                    bestMoof = loc.Offset;
                    bestOffset = p.SourceRange.Offset;
                    bestPts = p.Pts;
                    bestSnapshot = (long[])snapshot.Clone();
                    done = true;
                    break;
                }
            }
        }

        if (bestMoof < 0 || bestSnapshot is null)
        {
            setNextPosition(long.MaxValue);
            return MediaTime.Unknown;
        }

        for (int i = 0; i < states.Length; i++)
        {
            states[i].NextFragmentDts = bestSnapshot[i];
        }

        FragmentLocation again = await loader.LoadNextAsync(bestMoof, engine.Fragment, cancellationToken).ConfigureAwait(false);
        if (again.IsNone)
        {
            setNextPosition(long.MaxValue);
            return MediaTime.Unknown;
        }

        engine.BeginFragment(again.Offset);
        setNextPosition(again.End);
        engine.Fragment.SkipBefore(states, bestOffset);
        return bestPts;
    }
}

/// <summary>Builds the raw bytes of a box header from a parsed one (used by the forward-only reader, which has already consumed them).</summary>
internal static class HeaderBytes
{
    public static int Write(BoxHeader h, Span<byte> destination)
    {
        if (h.HeaderSize == 16)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, 1);
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], h.Type);
            BinaryPrimitives.WriteUInt64BigEndian(destination[8..], h.DeclaredSize);
            return 16;
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(h.IsSizeInferred ? 0 : h.DeclaredSize));
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], h.Type);
        return 8;
    }
}
