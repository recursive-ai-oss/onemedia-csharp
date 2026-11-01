namespace RecursiveAI.OneMedia.Tags.Id3;

/// <summary>
/// Reads an ID3v2 tag that another container carries as a block of its own (a WAVE or AIFF <c>id3 </c> chunk, the bytes in front of an Ogg or
/// FLAC stream). The container keeps the block as a raw binary tag; the frames are added beside it as <see cref="TagSystems.Id3v2"/> tags.
/// </summary>
internal static class EmbeddedId3
{
    /// <summary>Reads the tag held in <paramref name="payload"/> of <paramref name="source"/>; null, with a defect, when the block holds none or the tag does not fit in it.</summary>
    public static Id3Tag? FromRange(IMediaSource source, ByteRange payload, MediaLimits limits, DefectLog defects)
    {
        if (payload.Length < Id3Header.HeaderSize)
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, payload.Offset, "An ID3 chunk is too short to hold an ID3v2 tag.", "tags.id3-chunk-not-id3");
            return null;
        }

        Span<byte> head = stackalloc byte[Id3Header.HeaderSize]; // the fixed tag header
        if (source.Read(payload.Offset, head) < head.Length || !Id3Header.TryParse(head, out Id3Header header))
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, payload.Offset, "An ID3 chunk does not hold an ID3v2 tag.", "tags.id3-chunk-not-id3");
            return null;
        }

        // A tag that claims more than its chunk holds would read the next chunk's bytes as frames.
        if (header.TotalLength > payload.Length)
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, payload.Offset, "The ID3v2 tag in a chunk is longer than the chunk; its frames are not read.", "tags.id3-chunk-overrun");
            return null;
        }

        return Id3Tag.TryRead(source, payload.Offset, out Id3Tag? tag, limits, defects) ? tag : null;
    }

    /// <summary>Reads the tag at the start of <paramref name="bytes"/>, which sit at <paramref name="baseOffset"/> in the file; null, with a defect, when they hold none.</summary>
    public static Id3Tag? FromMemory(ReadOnlyMemory<byte> bytes, long baseOffset, MediaLimits limits, DefectLog defects)
    {
        try
        {
            return Id3Tag.Read(bytes, baseOffset, limits, defects);
        }
        catch (MediaLimitExceededException e)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "id3.limit");
            return null;
        }
        catch (MediaFormatException)
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, baseOffset, "The bytes in front of the stream do not hold an ID3v2 tag.", "tags.id3-prefix-not-id3");
            return null;
        }
    }
}
