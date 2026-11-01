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
    public static byte[] Rebuild(ReadOnlySpan<byte> element, Func<long, long> map, ref bool changed)
    {
        if (!Ebml.TryReadId(element, out uint id, out int idLen) || !Ebml.TryReadSize(element[idLen..], out long size, out int sizeLen) || size < 0 || idLen + sizeLen + size > element.Length)
        {
            return element.ToArray();
        }

        bool inner = false;
        byte[] rebuilt = RebuildMaster(id, sizeLen, element.Slice(idLen + sizeLen, (int)size), map, ref inner);
        if (!inner)
        {
            return element.ToArray();
        }

        changed = true;
        return rebuilt;
    }

    private static byte[] RebuildMaster(uint id, int originalWidth, ReadOnlySpan<byte> payload, Func<long, long> map, ref bool changed)
    {
        List<byte[]> parts = [];
        long total = 0;
        bool hasCrc = false;
        int at = 0;
        while (at < payload.Length)
        {
            if (!Ebml.TryReadId(payload[at..], out uint cid, out int idLen)
                || !Ebml.TryReadSize(payload[(at + idLen)..], out long size, out int sizeLen)
                || size < 0 || at + idLen + sizeLen + size > payload.Length)
            {
                byte[] tail = payload[at..].ToArray(); // damaged tail: kept as it is
                parts.Add(tail);
                total += tail.Length;
                break;
            }

            int whole = idLen + sizeLen + (int)size;
            ReadOnlySpan<byte> raw = payload.Slice(at, whole);
            ReadOnlySpan<byte> body = payload.Slice(at + idLen + sizeLen, (int)size);
            byte[] part;
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
                    part = raw.ToArray();
                }
            }
            else if (IsPath(cid))
            {
                bool sub = false;
                byte[] rebuilt = RebuildMaster(cid, sizeLen, body, map, ref sub);
                if (sub)
                {
                    changed = true;
                    part = rebuilt;
                }
                else
                {
                    part = raw.ToArray();
                }
            }
            else
            {
                part = raw.ToArray();
            }

            parts.Add(part);
            total += part.Length;
            at += whole;
        }

        byte[] joined = new byte[total];
        int w = 0;
        foreach (byte[] p in parts)
        {
            p.CopyTo(joined, w);
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
    private bool _hasCrc;
    private long _contentLength;
    private byte[] _header = [];

    /// <summary>Runs the transformation without keeping the output; returns the new element length and whether any position changed.</summary>
    public (long Length, bool Changed) Measure(IMediaSource source, CancellationToken ct)
    {
        using CountSink sink = new();
        bool changed = Content(source, sink, ct);
        _contentLength = sink.Count;
        long payload = _contentLength + (_hasCrc ? 6 : 0);
        int idLen = EbmlWriter.IdLength(cues.Id);
        int oldWidth = cues.HeaderSize - idLen;
        int width = oldWidth is >= 1 and <= 8 && payload < (1L << (7 * oldWidth)) - 1 ? oldWidth : EbmlWriter.SizeLength(payload);
        _header = new byte[idLen + width];
        EbmlWriter.WriteId(_header, cues.Id);
        EbmlWriter.WriteSize(_header.AsSpan(idLen), payload, width);
        return (_header.Length + payload, changed);
    }

    public void Write(Stream output, IMediaSource source, CancellationToken cancellationToken)
    {
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
