using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>Rewrites the Segment-relative positions stored in SeekHead and Cues entries (RFC 9559 §6.5 and §6.9).</summary>
internal static class EbmlPositions
{
    private const uint CueCodecState = 0xEA;
    private const uint CueRefCluster = 0x97;
    private const uint CueRefCodecState = 0xEB;
    private const uint CueReference = 0xDB;
    private const uint Seek = 0x4DBB;
    private const uint SeekPosition = 0x53AC;

    // Elements whose value is a position relative to the Segment's payload.
    private static bool IsPosition(uint id) => id is SeekPosition or MatroskaIds.CueClusterPosition or CueCodecState or CueRefCluster or CueRefCodecState;

    // Masters on the way down to those elements.
    private static bool IsPath(uint id) => id is MatroskaIds.SeekHead or Seek or MatroskaIds.CuePoint or MatroskaIds.CueTrackPositions or CueReference;

    /// <summary>
    /// Rebuilds one element (a SeekHead or a CuePoint) with every position mapped. Elements without a position are copied byte for byte; a
    /// position keeps its integer width when the new value fits. A leading CRC-32 is recomputed.
    /// </summary>
    public static byte[] Rebuild(ReadOnlyMemory<byte> element, Func<long, long> map, ref bool changed)
    {
        ReadOnlySpan<byte> span = element.Span;
        if (!Ebml.TryReadId(span, out uint id, out int idLen) || !Ebml.TryReadSize(span[idLen..], out long size, out int sizeLen) || size < 0 || idLen + sizeLen + size > span.Length)
        {
            return span.ToArray();
        }

        bool inner = false;
        byte[] rebuilt = RebuildMaster(id, sizeLen, element.Slice(idLen + sizeLen, (int)size), map, ref inner);
        if (!inner)
        {
            return span.ToArray();
        }

        changed = true;
        return rebuilt;
    }

    // Parts that are not touched stay slices of the input until the result is joined, so a SeekHead of many entries costs one array, not one per element.
    private static byte[] RebuildMaster(uint id, int originalWidth, ReadOnlyMemory<byte> payloadMemory, Func<long, long> map, ref bool changed)
    {
        ReadOnlySpan<byte> payload = payloadMemory.Span;
        List<ReadOnlyMemory<byte>> parts = [];
        long total = 0;
        bool hasCrc = false;
        int at = 0;
        while (at < payload.Length)
        {
            if (!Ebml.TryReadId(payload[at..], out uint cid, out int idLen)
                || !Ebml.TryReadSize(payload[(at + idLen)..], out long size, out int sizeLen)
                || size < 0 || at + idLen + sizeLen + size > payload.Length)
            {
                ReadOnlyMemory<byte> tail = payloadMemory[at..]; // damaged tail: kept as it is
                parts.Add(tail);
                total += tail.Length;
                break;
            }

            int whole = idLen + sizeLen + (int)size;
            ReadOnlyMemory<byte> raw = payloadMemory.Slice(at, whole);
            ReadOnlySpan<byte> body = payload.Slice(at + idLen + sizeLen, (int)size);
            ReadOnlyMemory<byte> part;
            if (at == 0 && cid == MatroskaIds.Crc32 && size == 4)
            {
                hasCrc = true; // dropped here and recomputed below
                at += whole;
                continue;
            }

            if (IsPosition(cid) && size <= 8 && Ebml.TryReadUnsigned(body, out ulong old))
            {
                long mapped = map((long)old);
                if (mapped != (long)old)
                {
                    changed = true;
                    part = EbmlWriter.UnsignedElement(cid, (ulong)mapped, (int)size);
                }
                else
                {
                    part = raw;
                }
            }
            else if (IsPath(cid))
            {
                bool sub = false;
                byte[] rebuilt = RebuildMaster(cid, sizeLen, payloadMemory.Slice(at + idLen + sizeLen, (int)size), map, ref sub);
                if (sub)
                {
                    changed = true;
                    part = rebuilt;
                }
                else
                {
                    part = raw;
                }
            }
            else
            {
                part = raw;
            }

            parts.Add(part);
            total += part.Length;
            at += whole;
        }

        byte[] joined = new byte[total];
        int w = 0;
        foreach (ReadOnlyMemory<byte> p in parts)
        {
            p.CopyTo(joined.AsMemory(w));
            w += p.Length;
        }

        if (hasCrc)
        {
            byte[] withCrc = new byte[6 + joined.Length];
            withCrc[0] = (byte)MatroskaIds.Crc32;
            withCrc[1] = 0x84;
            BinaryPrimitives.WriteUInt32LittleEndian(withCrc.AsSpan(2), EbmlCrc32.Compute(joined));
            joined.CopyTo(withCrc, 6);
            joined = withCrc;
        }

        int width = originalWidth is >= 1 and <= 8 && joined.Length < (1L << (7 * originalWidth)) - 1 ? originalWidth : 0;
        return EbmlWriter.Element(id, joined, width);
    }
}

/// <summary>
/// Writes a Cues element with every position mapped, one CuePoint at a time, so a Cues element with hundreds of thousands of entries costs a
/// few kilobytes of memory.
/// </summary>
internal sealed class CuesGenerator(EbmlElement cues, Func<long, long> map, MediaLimits limits) : IPieceGenerator
{
    private const int MaxCuePointBytes = 1 << 20;
    // A Cues element up to this size is produced once, while it is measured, and written from memory; a larger one is streamed a CuePoint at a time.
    private const int MaxMaterialisedBytes = 64 * 1024;
    private bool _hasCrc;
    private long _contentLength;
    private byte[] _header = [];
    private byte[]? _element;

    /// <summary>The whole new element (header, CRC-32 and content) when it is small enough to be held, else null; set by <see cref="Measure"/>.</summary>
    public byte[]? Element => _element;

    /// <summary>Runs the transformation without keeping the output; returns the new element length and whether any position changed.</summary>
    public (long Length, bool Changed) Measure(IMediaSource source, CancellationToken ct)
    {
        bool hold = cues.Range.Length <= MaxMaterialisedBytes;
        using Stream sink = hold ? new MemoryStream() : new CountSink();
        bool changed = Content(source, sink, ct);
        _contentLength = sink.Length;
        long payload = _contentLength + (_hasCrc ? 6 : 0);
        int idLen = EbmlWriter.IdLength(cues.Id);
        int oldWidth = cues.HeaderSize - idLen;
        int width = oldWidth is >= 1 and <= 8 && payload < (1L << (7 * oldWidth)) - 1 ? oldWidth : EbmlWriter.SizeLength(payload);
        _header = new byte[idLen + width];
        EbmlWriter.WriteId(_header, cues.Id);
        EbmlWriter.WriteSize(_header.AsSpan(idLen), payload, width);
        if (hold && sink is MemoryStream content)
        {
            // The same bytes Write would produce: header, the CRC-32 element when the original had one, then the content.
            ReadOnlySpan<byte> body = content.GetBuffer().AsSpan(0, (int)content.Length);
            byte[] whole = new byte[_header.Length + (_hasCrc ? 6 : 0) + body.Length];
            _header.CopyTo(whole, 0);
            int at = _header.Length;
            if (_hasCrc)
            {
                whole[at] = (byte)MatroskaIds.Crc32;
                whole[at + 1] = 0x84;
                BinaryPrimitives.WriteUInt32LittleEndian(whole.AsSpan(at + 2), EbmlCrc32.Compute(body));
                at += 6;
            }

            body.CopyTo(whole.AsSpan(at));
            _element = whole;
        }

        return (_header.Length + payload, changed);
    }

    public void Write(Stream output, IMediaSource source, CancellationToken cancellationToken)
    {
        if (_element is not null)
        {
            output.Write(_element, 0, _element.Length);
            return;
        }

        output.Write(_header, 0, _header.Length);
        if (_hasCrc)
        {
            using CrcSink crc = new();
            Content(source, crc, cancellationToken);
            Span<byte> head = stackalloc byte[6]; // CRC-32 element: ID 0xBF, size 4, value little-endian
            head[0] = (byte)MatroskaIds.Crc32;
            head[1] = 0x84;
            BinaryPrimitives.WriteUInt32LittleEndian(head[2..], EbmlCrc32.Finish(crc.State));
            output.Write(head);
        }

        Content(source, output, cancellationToken);
    }

    private bool Content(IMediaSource source, Stream sink, CancellationToken ct)
    {
        bool changed = false;
        long pos = cues.Offset + cues.HeaderSize;
        long end = cues.Range.End;
        bool first = true;
        byte[] head = new byte[16];
        while (pos < end)
        {
            ct.ThrowIfCancellationRequested();
            int n = source.Read(pos, head.AsSpan(0, (int)Math.Min(head.Length, end - pos)), ct);
            if (n <= 0 || !Ebml.TryReadId(head.AsSpan(0, n), out uint id, out int idLen)
                || !Ebml.TryReadSize(head.AsSpan(idLen, n - idLen), out long size, out int sizeLen) || size < 0 || pos + idLen + sizeLen + size > end)
            {
                PieceWriter.CopyFrom(source, pos, end - pos, sink, ct); // damaged tail: kept as it is
                break;
            }

            long whole = idLen + sizeLen + size;
            if (first && id == MatroskaIds.Crc32 && size == 4)
            {
                _hasCrc = true;
            }
            else if (id == MatroskaIds.CuePoint)
            {
                if (whole > MaxCuePointBytes)
                {
                    throw new UnsupportedMediaException($"A CuePoint of {whole} bytes is too large to move its positions safely.") { Offset = pos };
                }

                limits.CheckAllocation(whole, pos);
                byte[] bytes = new byte[whole];
                source.ReadExactly(pos, bytes, ct);
                bool c = false;
                byte[] rebuilt = EbmlPositions.Rebuild(bytes, map, ref c);
                changed |= c;
                sink.Write(rebuilt, 0, rebuilt.Length);
            }
            else
            {
                PieceWriter.CopyFrom(source, pos, whole, sink, ct);
            }

            first = false;
            pos += whole;
        }

        return changed;
    }
}
