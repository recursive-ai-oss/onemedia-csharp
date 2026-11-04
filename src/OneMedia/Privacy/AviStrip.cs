using System.Buffers.Binary;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// The parts of an AVI strip that the tag writer does not cover: camera data in the stream headers, and a list of what else the file holds that the strip does not
/// examine. Camera data of known layout (the Fujifilm <c>strd</c> chunk) is neutralised in place and nothing moves; a chunk of a layout the strip does not know is
/// reported in <see cref="StripReport.NotRemoved"/> by name, size and position, so that a vendor record is never left without a mention.
/// </summary>
internal static class AviStrip
{
    // Microsoft's AVI RIFF file reference and OpenDML AVI File Format Extensions 1.02: the chunks of the main header, the stream header lists and the movie data.
    private static readonly HashSet<string> KnownRoot = ["hdrl", "INFO", "movi", "idx1", "IDIT"];
    private static readonly HashSet<string> KnownHeaderList = ["avih", "strl", "odml", "IDIT"];
    private static readonly HashSet<string> KnownStreamList = ["strh", "strf", "strn", "indx", "vprp"];
    private const int MaxZeroScan = 16 * 1024 * 1024;

    public static void Scan(IMediaSource source, MediaLimits limits, StripCategories cats, StripSink sink, CancellationToken ct)
    {
        DefectLog defects = new(limits);
        RiffChunk? root = RiffChunk.Read(source, 0, source.Length, limits, defects, 1, ct);
        if (root is null || !root.IsList)
        {
            return;
        }

        int stream = -1;
        foreach (IStructureNode n in root.Children)
        {
            ct.ThrowIfCancellationRequested();
            if (n is not RiffChunk c)
            {
                continue;
            }

            if (c is { IsList: true, ListType: "hdrl" })
            {
                foreach (IStructureNode h in c.Children)
                {
                    if (h is not RiffChunk hc)
                    {
                        continue;
                    }

                    if (hc is { IsList: true, ListType: "strl" })
                    {
                        stream++;
                        ScanStreamList(source, hc, stream, limits, cats, sink, ct);
                    }
                    else
                    {
                        CheckUnknown(source, hc, KnownHeaderList, "hdrl", cats, sink, ct);
                    }
                }
            }
            else
            {
                CheckUnknown(source, c, KnownRoot, "", cats, sink, ct);
            }
        }

        CheckTrailer(source, root, cats, sink, ct);
    }

    private static void ScanStreamList(IMediaSource source, RiffChunk strl, int stream, MediaLimits limits, StripCategories cats, StripSink sink, CancellationToken ct)
    {
        foreach (IStructureNode n in strl.Children)
        {
            if (n is not RiffChunk c)
            {
                continue;
            }

            if (c.FourCc == "strd" && !c.IsList)
            {
                ScanStrd(source, c, stream, limits, cats, sink, ct);
            }
            else
            {
                CheckUnknown(source, c, KnownStreamList, "strl", cats, sink, ct);
            }
        }
    }

    // The strd chunk is "codec-specific driver data" in the AVI reference: the layout belongs to whoever wrote the file. Fujifilm cameras put the text AVIF and
    // an Exif directory there (observed with ExifTool in a FinePix movie). Anything else with content is reported.
    private static void ScanStrd(IMediaSource source, RiffChunk strd, int stream, MediaLimits limits, StripCategories cats, StripSink sink, CancellationToken ct)
    {
        long size = strd.PayloadSize;
        if (size <= 0 || IsAllZero(source, strd.Offset + strd.HeaderSize, size, ct))
        {
            return;
        }

        if (size <= limits.MaxTagBytes)
        {
            byte[] data = strd.ReadData((int)size, ct);
            if (data.Length >= 10 && data.AsSpan().StartsWith("AVIF"u8))
            {
                int before = sink.Patches.Count;
                if (TiffScrub.ScrubFujifilmAviStrd(data, strd.Offset + strd.HeaderSize, "riff:strd", stream, cats, sink, limits))
                {
                    if (sink.Patches.Count == before && cats != StripCategories.None)
                    {
                        sink.AddNote("The Fujifilm 'strd' chunk of stream " + stream.ToString(System.Globalization.CultureInfo.InvariantCulture) + " holds no position or serial entry to remove; its maker, model and settings stay.");
                    }

                    return;
                }
            }
        }

        sink.Leftover(cats, "riff:strd", "stream data", stream, new ByteRange(strd.Offset, strd.Range.Length),
            $"The 'strd' chunk of stream {stream} ({size} bytes) holds vendor data in a layout the strip does not know, so it was not inspected or edited.");
    }

    // A chunk or list the strip has no rule for. Free padding (JUNK/PAD with zero bytes) is not reported; anything with content is.
    private static void CheckUnknown(IMediaSource source, RiffChunk c, HashSet<string> known, string where, StripCategories cats, StripSink sink, CancellationToken ct)
    {
        string name = c.IsList ? "LIST " + c.ListType : c.FourCc;
        if (c.IsList ? known.Contains(c.ListType ?? "") : known.Contains(c.FourCc))
        {
            return;
        }

        if (!c.IsList && c.FourCc is "JUNK" or "PAD " or "junk")
        {
            if (IsAllZero(source, c.Offset + c.HeaderSize, c.PayloadSize, ct))
            {
                return;
            }
        }

        string place = where.Length == 0 ? "the file" : "the '" + where + "' list";
        sink.Leftover(cats, "riff", name, 0, new ByteRange(c.Offset, c.Range.Length),
            $"'{name}' ({c.PayloadSize} bytes) in {place} is a chunk the strip has no rule for, so it was not inspected or edited.");
    }

    // After the first RIFF form: further forms of type AVIX are OpenDML data and hold only frames and indexes; any other bytes are a trailer.
    private static void CheckTrailer(IMediaSource source, RiffChunk root, StripCategories cats, StripSink sink, CancellationToken ct)
    {
        long at = root.Range.End;
        byte[] head = new byte[12];
        for (int hops = 0; hops < 100_000 && at < source.Length; hops++)
        {
            ct.ThrowIfCancellationRequested();
            int got = source.Read(at, head, ct);
            if (got == 12 && head.AsSpan(0, 4).SequenceEqual("RIFF"u8) && head.AsSpan(8, 4).SequenceEqual("AVIX"u8))
            {
                long next = at + 8 + BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                if (next <= at)
                {
                    break;
                }

                at = next + (next & 1);
                continue;
            }

            if (IsAllZero(source, at, source.Length - at, ct))
            {
                return;
            }

            sink.Leftover(cats, "riff", "trailer", 0, new ByteRange(at, source.Length - at),
                $"{source.Length - at} bytes follow the last RIFF form and are not inspected.");
            return;
        }
    }

    private static bool IsAllZero(IMediaSource source, long offset, long length, CancellationToken ct)
    {
        byte[] buffer = new byte[Math.Min(length, 64 * 1024) is var n && n > 0 ? (int)n : 1];
        long done = 0;
        long cap = Math.Min(length, MaxZeroScan);
        while (done < cap)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, cap - done);
            int got = source.Read(offset + done, buffer.AsSpan(0, want), ct);
            if (got <= 0)
            {
                return true;
            }

            if (buffer.AsSpan(0, got).IndexOfAnyExcept((byte)0) >= 0)
            {
                return false;
            }

            done += got;
        }

        // Beyond the scan cap the rest is assumed to hold content: reporting is the safe side.
        return length <= MaxZeroScan;
    }
}
