using System.Buffers.Binary;
using RecursiveAI.OneMedia.Input;

namespace RecursiveAI.OneMedia.Ogg;

/// <summary>The flags of an Ogg page (RFC 3533 §6, header_type_flag).</summary>
[Flags]
public enum OggPageType : byte
{
    /// <summary>None of the flags.</summary>
    None = 0,

    /// <summary>The first packet on the page continues a packet from the previous page (0x01).</summary>
    Continued = 1,

    /// <summary>The page is the first of a logical stream (0x02).</summary>
    BeginOfStream = 2,

    /// <summary>The page is the last of a logical stream (0x04).</summary>
    EndOfStream = 4,
}

/// <summary>The header of one Ogg page (RFC 3533 §6).</summary>
/// <param name="Offset">Offset of the page's <c>OggS</c> capture pattern in the source.</param>
/// <param name="Flags">The header type flags.</param>
/// <param name="GranulePosition">The granule position, whose meaning depends on the codec; -1 when no packet ends on the page.</param>
/// <param name="SerialNumber">The logical stream the page belongs to.</param>
/// <param name="SequenceNumber">The page's sequence number within its logical stream.</param>
/// <param name="Checksum">The CRC-32 stored in the page.</param>
/// <param name="ChecksumValid">Whether the stored checksum matches the page's bytes; always true when the page was not verified.</param>
/// <param name="SegmentCount">The number of lacing values.</param>
/// <param name="BodySize">The sum of the lacing values: the size of the page body.</param>
public readonly record struct OggPageHeader(long Offset, OggPageType Flags, long GranulePosition, uint SerialNumber, uint SequenceNumber, uint Checksum, bool ChecksumValid, int SegmentCount, int BodySize)
{
    /// <summary>The size of the header including the segment table.</summary>
    public int HeaderSize => OggPageReader.FixedHeaderSize + SegmentCount;

    /// <summary>The size of the whole page.</summary>
    public int TotalSize => HeaderSize + BodySize;

    /// <summary>True when this is the first page of a logical stream.</summary>
    public bool IsFirst => (Flags & OggPageType.BeginOfStream) != 0;

    /// <summary>True when this is the last page of a logical stream.</summary>
    public bool IsLast => (Flags & OggPageType.EndOfStream) != 0;
}

/// <summary>An Ogg page as a node of the structure tree. Pages have no children; the body is not read.</summary>
public sealed class OggPageNode : IStructureNode
{
    internal OggPageNode(OggPageHeader header) => Header = header;

    /// <summary>The page header.</summary>
    public OggPageHeader Header { get; }

    /// <summary>The ASCII bytes <c>OggS</c> as a number.</summary>
    public ulong TypeKey => 0x4F676753;

    /// <inheritdoc />
    public long Offset => Header.Offset;

    /// <inheritdoc />
    public int HeaderSize => Header.HeaderSize;

    /// <inheritdoc />
    public long PayloadSize => Header.BodySize;

    /// <inheritdoc />
    public bool IsSizeInferred => false;

    /// <inheritdoc />
    public IReadOnlyList<IStructureNode> Children => [];
}

/// <summary>Lists the pages of an Ogg file from the headers alone, jumping over page bodies.</summary>
public static class OggPages
{
    /// <summary>
    /// Enumerates the pages of a seekable source in file order. Only the 27-byte headers and segment tables are read, so a page body
    /// costs no I/O and its checksum is not verified (<see cref="OggPageHeader.ChecksumValid"/> is true).
    /// Bytes that are not a page are skipped and reported as a defect.
    /// </summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="limits">Ceilings; the page count is held to <see cref="MediaLimits.MaxElementCount"/>.</param>
    /// <param name="defects">Where skipped bytes are reported; may be null.</param>
    /// <param name="cancellationToken">Observed between pages.</param>
    /// <returns>The page nodes, produced lazily.</returns>
    public static IEnumerable<OggPageNode> Enumerate(IMediaSource source, MediaLimits? limits = null, DefectLog? defects = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= MediaLimits.Default;
        long position = 0;
        long count = 0;
        byte[] head = new byte[OggPageReader.FixedHeaderSize + 255];
        while (position < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int got = source.Read(position, head, cancellationToken);
            if (got < OggPageReader.FixedHeaderSize || !OggPageReader.TryParseHeader(head.AsSpan(0, got), position, out OggPageHeader h) || position + h.TotalSize > source.Length)
            {
                // Not a page here: look for the next capture pattern in the bytes at hand.
                int next = got > 1 ? head.AsSpan(1, got - 1).IndexOf("OggS"u8) : -1;
                long skip = next >= 0 ? next + 1 : Math.Max(1, got - 3);
                defects?.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, position, "Bytes that do not form an Ogg page are skipped.", "ogg.skipped");
                position += skip;
                continue;
            }

            limits.CheckElementCount(++count, position);
            yield return new OggPageNode(h);
            position += h.TotalSize;
        }
    }
}

/// <summary>Reads Ogg pages one after another, checking each page's CRC and finding the next page after damage.</summary>
internal sealed class OggPageReader
{
    // RFC 3533 §6: 27 fixed bytes, then up to 255 lacing values, then a body of at most 255 * 255 bytes.
    public const int FixedHeaderSize = 27;
    public const int MaxPageSize = FixedHeaderSize + 255 + (255 * 255);

    private static readonly uint[] CrcTable = BuildTable();

    private readonly SequentialInput _input;
    private readonly DefectLog _defects;
    private readonly bool _verify;
    // Rented, because a reader that only looks at the headers would otherwise allocate 64 KiB per open.
    private byte[] _page = System.Buffers.ArrayPool<byte>.Shared.Rent(MaxPageSize);
    private OggPageHeader _header;

    public OggPageReader(SequentialInput input, DefectLog defects, bool verifyChecksum = true)
    {
        _input = input;
        _defects = defects;
        _verify = verifyChecksum;
    }

    /// <summary>Gives the page buffer back to the pool; the reader is not used afterwards.</summary>
    public void Release()
    {
        byte[] page = _page;
        _page = [];
        if (page.Length > 0)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(page);
        }
    }

    public OggPageHeader Header => _header;

    /// <summary>The lacing values of the current page.</summary>
    public ReadOnlySpan<byte> Lacing => _page.AsSpan(FixedHeaderSize, _header.SegmentCount);

    /// <summary>The body of the current page; valid until the next page is read.</summary>
    public ReadOnlySpan<byte> Body => _page.AsSpan(_header.HeaderSize, _header.BodySize);

    // RFC 3533 §6: the CRC uses the polynomial 0x04C11DB7, initial value 0, no reflection, no final XOR, over the whole page with the checksum field zeroed.
    private static uint[] BuildTable()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++)
            {
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            }

            t[i] = r;
        }

        return t;
    }

    public static uint Crc(ReadOnlySpan<byte> page)
    {
        // The four checksum bytes at offset 22 count as zero, so they are fed to the table as zeros.
        uint crc = Update(0, page[..22]);
        crc = Update(crc, [0, 0, 0, 0]);
        return Update(crc, page[26..]);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = CrcTable;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ table[(byte)(crc >> 24) ^ b];
        }

        return crc;
    }

    /// <summary>Reads the fixed header and segment table from the start of <paramref name="data"/>.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, long offset, out OggPageHeader header)
    {
        header = default;
        if (data.Length < FixedHeaderSize || !data[..4].SequenceEqual("OggS"u8) || data[4] != 0)
        {
            return false;
        }

        int nseg = data[26];
        if (data.Length < FixedHeaderSize + nseg)
        {
            return false;
        }

        int body = 0;
        for (int i = 0; i < nseg; i++)
        {
            body += data[FixedHeaderSize + i];
        }

        header = new OggPageHeader(offset, (OggPageType)(data[5] & 7), BinaryPrimitives.ReadInt64LittleEndian(data[6..]), BinaryPrimitives.ReadUInt32LittleEndian(data[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[18..]), BinaryPrimitives.ReadUInt32LittleEndian(data[22..]), true, nseg, body);
        return true;
    }

    /// <summary>
    /// Reads the next page. Bytes between pages are skipped with a defect; a page cut off by the end of the data ends the stream with a defect.
    /// A page whose checksum is wrong is still returned (the data may be fine apart from one bit) and is reported.
    /// </summary>
    public async ValueTask<bool> ReadNextAsync(CancellationToken ct)
    {
        long skippedFrom = -1;
        while (true)
        {
            long position = _input.Position;
            ReadOnlyMemory<byte> peekMemory = await _input.PeekAsync(FixedHeaderSize + 255, ct).ConfigureAwait(false);
            PageProbe probe = Probe(peekMemory.Span, position);
            if (probe.Kind == ProbeKind.End)
            {
                ReportSkipped(skippedFrom, position);
                return false;
            }

            if (probe.Kind == ProbeKind.ShortHeader)
            {
                ReportSkipped(skippedFrom, position);
                _defects.Add(DefectKind.Truncated, DefectSeverity.Error, position, "The data ends inside an Ogg page header.", "ogg.truncated");
                _input.Advance(peekMemory.Length);
                return false;
            }

            if (probe.Kind == ProbeKind.Page)
            {
                OggPageHeader h = probe.Header;
                int total = h.TotalSize;
                int got = await _input.ReadAsync(_page.AsMemory(0, total), ct).ConfigureAwait(false);
                if (got < total)
                {
                    ReportSkipped(skippedFrom, position);
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Error, position, $"The data ends inside an Ogg page: {got} of {total} bytes.", "ogg.truncated");
                    return false;
                }

                bool valid = true;
                if (_verify)
                {
                    valid = Crc(_page.AsSpan(0, total)) == h.Checksum;
                    if (!valid)
                    {
                        _defects.Add(DefectKind.ChecksumMismatch, DefectSeverity.Warning, position, "The CRC of an Ogg page does not match its bytes.", "ogg.crc");
                    }
                }

                ReportSkipped(skippedFrom, position);
                _header = h with { ChecksumValid = valid };
                return true;
            }

            // Not at a page: advance to the next capture pattern in the window (or most of the window if there is none).
            if (skippedFrom < 0)
            {
                skippedFrom = position;
            }

            ReadOnlyMemory<byte> window = await _input.PeekAsync(4096, ct).ConfigureAwait(false);
            int next = window.Length > 1 ? window.Span[1..].IndexOf("OggS"u8) : -1;
            _input.Advance(next >= 0 ? next + 1 : Math.Max(1, window.Length - 3));
        }
    }

    private enum ProbeKind
    {
        End,
        ShortHeader,
        Page,
        NotAPage,
    }

    private readonly record struct PageProbe(ProbeKind Kind, OggPageHeader Header);

    // Classifies the bytes at the current position: end of data, a header cut short, a page, or something else.
    private static PageProbe Probe(ReadOnlySpan<byte> peek, long position)
    {
        if (peek.Length == 0)
        {
            return new PageProbe(ProbeKind.End, default);
        }

        if (peek.Length >= 4 && peek[..4].SequenceEqual("OggS"u8) && (peek.Length < 5 || peek[4] == 0))
        {
            if (peek.Length < FixedHeaderSize || peek.Length < FixedHeaderSize + peek[26])
            {
                return new PageProbe(ProbeKind.ShortHeader, default);
            }

            TryParseHeader(peek, position, out OggPageHeader h);
            return new PageProbe(ProbeKind.Page, h);
        }

        return new PageProbe(ProbeKind.NotAPage, default);
    }

    private void ReportSkipped(long from, long to)
    {
        if (from >= 0 && to > from)
        {
            _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, from, $"{to - from} bytes before the next Ogg page are not part of any page.", "ogg.skipped");
        }
    }
}
