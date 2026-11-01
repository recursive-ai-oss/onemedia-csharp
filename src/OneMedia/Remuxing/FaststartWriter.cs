using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>
/// Moves the movie box of a progressive ISO base media file in front of its media data. ISO/IEC 14496-12 §8.7.5 (<c>stco</c>) and §8.7.5
/// (<c>co64</c>) store absolute file offsets of chunks, so every offset that points past the new position of the movie box and before its old
/// position moves forward by the size of the movie box. Nothing else in the file holds such an offset in a progressive file.
/// </summary>
internal static class FaststartWriter
{
    private const int CopyBuffer = 1 << 20;

    public static FaststartReport Write(IMediaSource source, Stream output, MediaLimits limits, CancellationToken cancellationToken)
    {
        BoxTree tree = new(source, limits);
        IReadOnlyList<BoxNode> top = tree.TopLevel;
        BoxNode? moov = null;
        BoxNode? firstMdat = null;
        foreach (BoxNode b in top)
        {
            if (b.Type == BoxTypes.Moov && moov is null)
            {
                moov = b;
            }
            else if (b.Type == BoxTypes.Mdat && firstMdat is null)
            {
                firstMdat = b;
            }
            else if (b.Type == BoxTypes.Moof)
            {
                throw new UnsupportedMediaException("The file is fragmented; it has no single movie box to move.");
            }
        }

        if (moov is null)
        {
            throw new UnsupportedMediaException("The file has no movie box (moov).");
        }

        long length = source.Length;
        if (firstMdat is null || moov.Offset < firstMdat.Offset)
        {
            CopyRange(source, output, 0, length, cancellationToken);
            return new FaststartReport(false, moov.Header.End - moov.Offset, length, 0, 0);
        }

        if (moov.Find(BoxTypes.Mvex) is not null)
        {
            throw new UnsupportedMediaException("The movie box declares fragments (mvex); the file is fragmented.");
        }

        long moovStart = moov.Offset;
        long moovEnd = moov.Header.End;
        long moovLength = moovEnd - moovStart;
        limits.CheckAllocation(moovLength, moovStart);
        byte[] original = new byte[moovLength];
        if (source.Read(moovStart, original, cancellationToken) != original.Length)
        {
            throw new MediaFormatException("The movie box runs past the end of the file.") { Offset = moovStart };
        }

        // The movie box goes right after the file type box (or first, when there is none): the position every other writer uses.
        long insertAt = top.Count > 0 && top[0].Type == BoxTypes.Ftyp && top[0].Offset == 0 ? top[0].Header.End : 0;
        if (insertAt > moovStart)
        {
            insertAt = 0;
        }

        HashSet<int> promoted = [];
        long delta = moovLength;
        byte[] rewritten;
        long shifted;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rewritten = Rewrite(original, delta, insertAt, moovStart, promoted, out shifted, out bool addedPromotion);
            if (!addedPromotion && rewritten.Length == delta)
            {
                break;
            }

            delta = rewritten.Length;
        }

        CopyRange(source, output, 0, insertAt, cancellationToken);
        output.Write(rewritten);
        CopyRange(source, output, insertAt, moovStart - insertAt, cancellationToken);
        CopyRange(source, output, moovEnd, length - moovEnd, cancellationToken);
        return new FaststartReport(true, rewritten.Length, length - moovLength, shifted, promoted.Count);
    }

    /// <summary>Builds the movie box again with every chunk offset table moved; promotes a 32-bit table to 64-bit when an offset no longer fits.</summary>
    private static byte[] Rewrite(byte[] moov, long delta, long insertAt, long oldMoovStart, HashSet<int> promoted, out long shifted, out bool addedPromotion)
    {
        ByteSink w = new(moov.Length + 64);
        long count = 0;
        bool added = false;
        EmitBox(moov, 0, moov.Length, 0, w);
        shifted = count;
        addedPromotion = added;
        return w.ToArray();

        void EmitBox(byte[] src, int at, int end, int depth, ByteSink dst)
        {
            ReadOnlySpan<byte> s = src.AsSpan();
            while (at < end)
            {
                if (end - at < 8)
                {
                    dst.Write(s[at..end]);
                    return;
                }

                long size = BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
                uint type = BinaryPrimitives.ReadUInt32BigEndian(s[(at + 4)..]);
                int header = 8;
                if (size == 1 && end - at >= 16)
                {
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(s[(at + 8)..]);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = end - at;
                }

                if (size < header || at + size > end)
                {
                    dst.Write(s[at..end]);
                    return;
                }

                int next = at + (int)size;
                bool container = type is BoxTypes.Moov or BoxTypes.Trak or BoxTypes.Mdia or BoxTypes.Minf or BoxTypes.Stbl;
                if (container)
                {
                    // The header keeps the form it had (a 64-bit size stays one), so a movie box that needs no promotion keeps its length.
                    int sizeAt = dst.Count;
                    Span<byte> h = dst.Reserve(header);
                    BinaryPrimitives.WriteUInt32BigEndian(h, header == 16 ? 1u : 0u);
                    BinaryPrimitives.WriteUInt32BigEndian(h[4..], type);
                    EmitBox(src, at + header, next, depth + 1, dst);
                    if (header == 16)
                    {
                        dst.PatchUInt64(sizeAt + 8, (ulong)(dst.Count - sizeAt));
                    }
                    else
                    {
                        dst.PatchUInt32(sizeAt, (uint)(dst.Count - sizeAt));
                    }
                }
                else if (type == BoxTypes.Stco || type == BoxTypes.Co64)
                {
                    EmitOffsets(src, at, header, next, type == BoxTypes.Co64, dst);
                }
                else
                {
                    dst.Write(s[at..next]);
                }

                at = next;
            }
        }

        void EmitOffsets(byte[] src, int at, int header, int next, bool is64, ByteSink dst)
        {
            ReadOnlySpan<byte> s = src.AsSpan();
            int p = at + header;
            if (next - p < 8)
            {
                dst.Write(s[at..next]);
                return;
            }

            uint count32 = BinaryPrimitives.ReadUInt32BigEndian(s[(p + 4)..]);
            int entry = is64 ? 8 : 4;
            long available = (next - (p + 8)) / entry;
            if (count32 > available)
            {
                // The table cannot be moved correctly if its length is a guess; a file whose offsets cannot be trusted is not rewritten.
                throw new MediaFormatException("A chunk offset table claims more entries than its box holds.") { Offset = oldMoovStart + at };
            }

            long n = count32;
            int trailing = (int)(next - (p + 8) - (n * entry));
            bool promote = !is64 && promoted.Contains(at);
            if (!is64 && !promote)
            {
                // Does any shifted offset leave 32 bits?
                for (long i = 0; i < n; i++)
                {
                    long o = BinaryPrimitives.ReadUInt32BigEndian(s[(p + 8 + (int)(i * 4))..]);
                    if (Moves(o) && o + delta > uint.MaxValue)
                    {
                        promoted.Add(at);
                        added = true;
                        promote = true;
                        break;
                    }
                }
            }

            bool out64 = is64 || promote;
            int entrySize = out64 ? 8 : 4;
            int bodyLength = 8 + (int)(n * entrySize) + trailing;
            Span<byte> head = dst.Reserve(header + 8);
            if (header == 16)
            {
                BinaryPrimitives.WriteUInt32BigEndian(head, 1);
                BinaryPrimitives.WriteUInt32BigEndian(head[4..], out64 ? BoxTypes.Co64 : BoxTypes.Stco);
                BinaryPrimitives.WriteUInt64BigEndian(head[8..], (ulong)(header + bodyLength));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(head, (uint)(header + bodyLength));
                BinaryPrimitives.WriteUInt32BigEndian(head[4..], out64 ? BoxTypes.Co64 : BoxTypes.Stco);
            }

            BinaryPrimitives.WriteUInt32BigEndian(head[header..], BinaryPrimitives.ReadUInt32BigEndian(s[p..]));
            BinaryPrimitives.WriteUInt32BigEndian(head[(header + 4)..], (uint)n);
            Span<byte> body = dst.Reserve((int)(n * entrySize));
            for (long i = 0; i < n; i++)
            {
                long o = is64
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(s[(p + 8 + (int)(i * 8))..])
                    : BinaryPrimitives.ReadUInt32BigEndian(s[(p + 8 + (int)(i * 4))..]);
                if (Moves(o))
                {
                    o += delta;
                    count++;
                }

                if (out64)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(body[(int)(i * 8)..], (ulong)o);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(body[(int)(i * 4)..], (uint)o);
                }
            }

            if (trailing > 0)
            {
                dst.Write(s[(next - trailing)..next]);
            }
        }

        bool Moves(long offset) => offset >= insertAt && offset < oldMoovStart;
    }

    private static void CopyRange(IMediaSource source, Stream output, long offset, long count, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(CopyBuffer, Math.Max(count, 1)));
        try
        {
            long done = 0;
            while (done < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, count - done);
                int n = source.Read(offset + done, buffer.AsSpan(0, want), cancellationToken);
                if (n <= 0)
                {
                    throw new MediaFormatException("The file ended while it was being copied.") { Offset = offset + done };
                }

                output.Write(buffer, 0, n);
                done += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>A growable byte buffer that can be patched after the fact, for boxes whose size is known only once their children are written.</summary>
internal sealed class ByteSink(int capacity)
{
    private byte[] _buffer = new byte[Math.Max(capacity, 16)];

    public int Count { get; private set; }

    public void Write(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

    /// <summary>Appends <paramref name="count"/> bytes and returns them for the caller to fill.</summary>
    public Span<byte> Reserve(int count)
    {
        if (Count + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, (int)Math.Min(Math.Max(_buffer.Length * 2L, Count + (long)count), Array.MaxLength));
        }

        Span<byte> span = _buffer.AsSpan(Count, count);
        Count += count;
        return span;
    }

    public void PatchUInt32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(at), value);

    public void PatchUInt64(int at, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(at), value);

    public byte[] ToArray() => _buffer.AsSpan(0, Count).ToArray();
}
