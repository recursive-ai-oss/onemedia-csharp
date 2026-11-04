using System.Linq;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Same-length changes to a file: ranges overwritten with zeros and ranges overwritten with given bytes. Turned into a plan whose every
/// untouched range is a copy at its old position, so nothing moves and media payload is never read into memory.
/// </summary>
internal sealed class PatchSet
{
    private readonly List<(long Start, long End)> _zeros = [];
    private readonly List<(long Start, byte[] Data)> _writes = [];
    private static readonly byte[] ZeroBlock = new byte[64 * 1024];

    public int Count => _zeros.Count + _writes.Count;

    /// <summary>Overwrites a range with zeros.</summary>
    public void Zero(long offset, long length)
    {
        if (length > 0)
        {
            _zeros.Add((offset, offset + length));
        }
    }

    /// <summary>Overwrites a range with the bytes. Where two writes overlap, the earlier one stays.</summary>
    public void Write(long offset, byte[] data)
    {
        if (data.Length > 0)
        {
            _writes.Add((offset, data));
        }
    }

    /// <summary>Builds the plan for a file of <paramref name="length"/> bytes.</summary>
    public EditPlan ToPlan(long length)
    {
        List<(long Start, long End, byte[]? Data)> runs = BuildRuns();
        PieceList pieces = new();
        long pos = 0;
        foreach ((long start, long end, byte[]? data) in runs)
        {
            long s = Math.Max(start, pos);
            long e = Math.Min(end, length);
            if (e <= s)
            {
                continue;
            }

            pieces.AddCopy(pos, s - pos);
            if (data is not null)
            {
                pieces.AddData(data.AsMemory((int)(s - start), (int)(e - s)));
            }
            else
            {
                AddZeros(pieces, e - s);
            }

            pos = e;
        }

        pieces.AddCopy(pos, length - pos);
        return pieces.ToPlan(length, 0, 0, 0);
    }

    /// <summary>
    /// Applies the patches, which are in source coordinates, on top of another plan: every part of a copied source range that a patch covers becomes new
    /// bytes, and no piece changes its length, so the plan's change notes stay valid. This lets a patch (a camera record inside a stream header) combine with a
    /// tag rewrite that moves other bytes.
    /// </summary>
    /// <param name="basePlan">The plan to patch.</param>
    /// <param name="uncovered">Receives the patches' source ranges that no copied piece of the base plan covers in full (the base plan rewrote or dropped them), so they could not be applied.</param>
    /// <returns>The patched plan.</returns>
    public EditPlan Overlay(EditPlan basePlan, out List<ByteRange> uncovered)
    {
        List<(long Start, long End, byte[]? Data)> runs = BuildRuns();
        long[] applied = new long[runs.Count];
        List<OutputPiece> output = new(basePlan.Pieces.Count + (runs.Count * 2));
        foreach (OutputPiece piece in basePlan.Pieces)
        {
            if (piece.Kind != OutputPieceKind.Copy)
            {
                output.Add(piece);
                continue;
            }

            long from = piece.SourceRange.Offset, end = piece.SourceRange.End, pos = from;
            int first = FirstRunEndingAfter(runs, from);
            for (int i = first; i < runs.Count && runs[i].Start < end; i++)
            {
                long s = Math.Max(runs[i].Start, pos), e = Math.Min(runs[i].End, end);
                if (e <= s)
                {
                    continue;
                }

                if (s > pos)
                {
                    output.Add(OutputPiece.Copy(pos, s - pos));
                }

                output.Add(runs[i].Data is { } d ? OutputPiece.Data(d.AsMemory((int)(s - runs[i].Start), (int)(e - s))) : ZeroPiece(e - s));
                applied[i] += e - s;
                pos = e;
            }

            if (pos < end)
            {
                output.Add(OutputPiece.Copy(pos, end - pos));
            }
        }

        uncovered = [];
        for (int i = 0; i < runs.Count; i++)
        {
            if (applied[i] != runs[i].End - runs[i].Start)
            {
                uncovered.Add(new ByteRange(runs[i].Start, runs[i].End - runs[i].Start));
            }
        }

        return new EditPlan(output, basePlan.SourceLength, basePlan.Changes, basePlan.PaddingUsed, basePlan.PaddingAvailable, basePlan.PaddingRequired, basePlan.ShortfallIn);
    }

    private static int FirstRunEndingAfter(List<(long Start, long End, byte[]? Data)> runs, long offset)
    {
        int lo = 0, hi = runs.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (runs[mid].End <= offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static OutputPiece ZeroPiece(long n) => n <= ZeroBlock.Length ? OutputPiece.Data(ZeroBlock.AsMemory(0, (int)n)) : OutputPiece.Generated(n, new ZeroFill(n));

    private List<(long Start, long End, byte[]? Data)> BuildRuns()
    {
        // Disjoint, sorted writes (first one wins on overlap).
        List<(long Start, byte[] Data)> writes = [];
        foreach ((long Start, byte[] Data) w in _writes.OrderBy(w => w.Start))
        {
            if (writes.Count > 0 && writes[^1].Start + writes[^1].Data.Length > w.Start)
            {
                continue;
            }

            writes.Add(w);
        }

        // Disjoint, sorted zero runs, minus the writes.
        List<(long Start, long End)> zeros = [];
        foreach ((long Start, long End) z in _zeros.OrderBy(z => z.Start))
        {
            if (zeros.Count > 0 && z.Start <= zeros[^1].End)
            {
                zeros[^1] = (zeros[^1].Start, Math.Max(zeros[^1].End, z.End));
            }
            else
            {
                zeros.Add(z);
            }
        }

        List<(long Start, long End, byte[]? Data)> runs = [];
        int wi = 0;
        foreach ((long zs, long ze) in zeros)
        {
            long at = zs;
            while (wi < writes.Count && writes[wi].Start + writes[wi].Data.Length <= at)
            {
                wi++;
            }

            int k = wi;
            while (at < ze)
            {
                if (k < writes.Count && writes[k].Start < ze)
                {
                    long ws = writes[k].Start, we = ws + writes[k].Data.Length;
                    if (ws > at)
                    {
                        runs.Add((at, ws, null));
                    }

                    at = Math.Max(at, we);
                    k++;
                }
                else
                {
                    runs.Add((at, ze, null));
                    at = ze;
                }
            }
        }

        foreach ((long Start, byte[] Data) w in writes)
        {
            runs.Add((w.Start, w.Start + w.Data.Length, w.Data));
        }

        runs.Sort((a, b) => a.Start.CompareTo(b.Start));
        return runs;
    }

    private static void AddZeros(PieceList pieces, long n)
    {
        if (n <= ZeroBlock.Length)
        {
            pieces.AddData(ZeroBlock.AsMemory(0, (int)n));
        }
        else
        {
            pieces.AddZeros(n);
        }
    }
}
