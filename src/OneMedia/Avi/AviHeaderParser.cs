using System.Buffers.Binary;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>Reads the <c>hdrl</c> list of an AVI file into the header models.</summary>
internal static class AviHeaderParser
{
    // A strf this large is not a format description; the limit stops a hostile size from allocating.
    private const int MaxFormatBytes = 1 << 20;
    private const int MaxSuperIndexEntries = 1 << 20;

    private static readonly uint Avih = RiffChunk.Make("avih");
    private static readonly uint Strh = RiffChunk.Make("strh");
    private static readonly uint Strf = RiffChunk.Make("strf");
    private static readonly uint Strn = RiffChunk.Make("strn");
    private static readonly uint Indx = RiffChunk.Make("indx");
    private static readonly uint Vprp = RiffChunk.Make("vprp");
    private static readonly uint Dmlh = RiffChunk.Make("dmlh");

    public static AviHeaderList Parse(RiffChunk hdrl, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        AviMainHeader main = default;
        bool haveMain = false;
        OpenDmlHeader? odml = null;
        List<AviStreamInfo> streams = [];
        foreach (IStructureNode node in hdrl.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is not RiffChunk c)
            {
                continue;
            }

            if (c.Id == Avih && !haveMain)
            {
                byte[] data = c.ReadData(AviMainHeader.Size, cancellationToken);
                if (data.Length < AviMainHeader.Size)
                {
                    defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, c.Offset, $"The avih chunk has {c.PayloadSize} bytes; the structure is {AviMainHeader.Size}.", "avi.short-avih");
                }

                main = AviMainHeader.Parse(data);
                haveMain = true;
            }
            else if (c.IsList && c.ListType == "strl")
            {
                limits.CheckTracks(streams.Count + 1, c.Offset);
                AviStreamInfo? s = ParseStream(c, streams.Count, limits, defects, cancellationToken);
                if (s is not null)
                {
                    streams.Add(s);
                }
            }
            else if (c.IsList && c.ListType == "odml")
            {
                RiffChunk? dmlh = c.Find(Dmlh);
                if (dmlh is not null && dmlh.ReadData(4, cancellationToken) is { Length: 4 } d)
                {
                    odml = new OpenDmlHeader(BinaryPrimitives.ReadUInt32LittleEndian(d));
                }
            }
        }

        if (!haveMain)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, hdrl.Offset, "The hdrl list has no avih chunk.", "avi.missing-avih");
        }

        if (main.Streams != 0 && main.Streams != streams.Count)
        {
            defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, hdrl.Offset, $"avih states {main.Streams} streams; the header list has {streams.Count}.", "avi.stream-count");
        }

        return new AviHeaderList(main, streams, odml, hdrl.Range);
    }

    private static AviStreamInfo? ParseStream(RiffChunk strl, int index, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        AviStreamHeader? header = null;
        ByteRange formatRange = ByteRange.None;
        byte[] format = [];
        string? name = null;
        ByteRange nameRange = ByteRange.None;
        VideoPropertiesHeader? props = null;
        List<SuperIndexEntry>? superIndex = null;
        uint superId = 0;
        foreach (IStructureNode node in strl.Children)
        {
            if (node is not RiffChunk c)
            {
                continue;
            }

            if (c.Id == Strh && header is null)
            {
                byte[] data = c.ReadData(AviStreamHeader.Size, cancellationToken);
                if (data.Length < AviStreamHeader.Size)
                {
                    defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, c.Offset, $"The strh chunk has {data.Length} bytes; the structure is {AviStreamHeader.Size}.", "avi.short-strh");
                }

                header = AviStreamHeader.Parse(data);
            }
            else if (c.Id == Strf && formatRange.IsNone)
            {
                if (c.PayloadSize > MaxFormatBytes)
                {
                    defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, c.Offset, $"The strf chunk of stream {index} is {c.PayloadSize} bytes; only the first {MaxFormatBytes} are kept.", "avi.strf-too-large");
                }

                format = c.ReadData(MaxFormatBytes, cancellationToken);
                formatRange = new ByteRange(c.Offset + c.HeaderSize, c.PayloadSize);
            }
            else if (c.Id == Strn && name is null)
            {
                name = AviText.Latin1(c.ReadData(4096, cancellationToken));
                nameRange = c.Range;
            }
            else if (c.Id == Vprp && props is null)
            {
                if (VideoPropertiesHeader.TryParse(c.ReadData(36, cancellationToken), out VideoPropertiesHeader p))
                {
                    props = p;
                }
            }
            else if (c.Id == Indx && superIndex is null)
            {
                superIndex = ParseSuperIndex(c, index, limits, defects, cancellationToken, out superId);
            }
        }

        if (header is null)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, strl.Offset, $"Stream list {index} has no strh chunk; the stream is ignored.", "avi.missing-strh");
            return null;
        }

        if (formatRange.IsNone)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, strl.Offset, $"Stream {index} has no strf chunk.", "avi.missing-strf");
        }

        return new AviStreamInfo(index, header.Value, formatRange, format, name, nameRange, props, superIndex, superId, strl.Range);
    }

    // OpenDML AVI File Format Extensions, "AVI Super Index Chunk": a 24-byte header, then nEntriesInUse entries of
    // qwOffset (64-bit), dwSize and dwDuration.
    private static List<SuperIndexEntry>? ParseSuperIndex(RiffChunk indx, int stream, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken, out uint chunkId)
    {
        chunkId = 0;
        byte[] head = indx.ReadData(24, cancellationToken);
        if (head.Length < 24)
        {
            defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, indx.Offset, "The indx chunk is shorter than its header.", "avi.short-indx");
            return null;
        }

        ushort longsPerEntry = BinaryPrimitives.ReadUInt16LittleEndian(head);
        byte type = head[3];
        uint inUse = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        chunkId = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        if (type != 0 || longsPerEntry != 4)
        {
            // An index of chunks placed directly in the stream list (type 1) carries no pointers to other indexes.
            defects.Add(DefectKind.Unsupported, DefectSeverity.Info, indx.Offset, $"The indx chunk of stream {stream} is not an index of indexes (type {type}, {longsPerEntry} longs per entry); it is ignored.", "avi.indx-not-super");
            return null;
        }

        long fit = (indx.PayloadSize - 24) / 16;
        if (inUse > fit)
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, indx.Offset, $"The super index states {inUse} entries but has room for {fit}.", "avi.indx-entry-count");
        }

        long count = Math.Min(inUse, fit);
        limits.CheckSampleTable(count, indx.Offset);
        if (count > MaxSuperIndexEntries)
        {
            count = MaxSuperIndexEntries;
        }

        byte[] data = indx.ReadData((int)(24 + (count * 16)), cancellationToken);
        count = Math.Min(count, (data.Length - 24) / 16);
        List<SuperIndexEntry> entries = new((int)count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = data.AsSpan(24 + (i * 16), 16);
            long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(e);
            if (offset < 0)
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, indx.Offset + 24 + (i * 16), "A super index offset is beyond 2^63.", "avi.indx-offset");
                break;
            }

            entries.Add(new SuperIndexEntry(offset, BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..])));
        }

        return entries;
    }
}
