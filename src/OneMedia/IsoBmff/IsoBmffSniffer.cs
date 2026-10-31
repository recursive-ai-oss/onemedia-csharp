using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Recognises the ISO base media file format family (MP4, MOV, 3GP, M4A, fragmented MP4) from its first box. Only the top-level
/// box headers in the head are inspected.
/// </summary>
public sealed class IsoBmffSniffer : IMediaFormatSniffer
{
    // ISO/IEC 14496-12 §4.2: a box is size(32) + type(32); size 1 means a 64-bit size follows; size 0 means "to end of file".
    private const uint Ftyp = 0x66747970; // 'ftyp'
    private const uint Styp = 0x73747970; // 'styp'

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.IsoBmff;

    /// <inheritdoc />
    public string DisplayName => "ISO base media file (MP4, MOV, 3GP)";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8)
        {
            return SniffResult.None;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(head);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        if (size is not (0 or 1) && size < 8)
        {
            return SniffResult.None;
        }

        if (type is Ftyp or Styp)
        {
            // The major brand follows the header; a file type box holds at least the brand and its version.
            if (size is not (0 or 1) && size < 16)
            {
                return SniffResult.None;
            }

            string? brand = head.Length >= 12 && IsPrintable(head.Slice(8, 4)) ? System.Text.Encoding.Latin1.GetString(head.Slice(8, 4)) : null;
            return new SniffResult(SniffConfidence.Certain, brand);
        }

        // Files that do not start with ftyp (older QuickTime movies, some recorders) start with one of these.
        if (type is 0x6D6F6F76 /* moov */ or 0x6D646174 /* mdat */ or 0x66726565 /* free */ or 0x736B6970 /* skip */
            or 0x77696465 /* wide */ or 0x706E6F74 /* pnot */ or 0x6D6F6F66 /* moof */ or 0x73696478 /* sidx */)
        {
            return NextBoxLooksValid(head, size) ? new SniffResult(SniffConfidence.Likely) : new SniffResult(SniffConfidence.Possible);
        }

        return SniffResult.None;
    }

    private static bool NextBoxLooksValid(ReadOnlySpan<byte> head, uint size)
    {
        if (size < 8 || size + 8UL > (ulong)head.Length)
        {
            return true; // The next header is not in the head, so there is nothing to contradict.
        }

        return IsPrintable(head.Slice((int)size + 4, 4));
    }

    private static bool IsPrintable(ReadOnlySpan<byte> fourcc)
    {
        foreach (byte b in fourcc)
        {
            // Box types are printable Latin-1; 0xA9 is the © of QuickTime metadata atoms.
            if (b < 0x20 || (b > 0x7E && b != 0xA9))
            {
                return false;
            }
        }

        return true;
    }
}
