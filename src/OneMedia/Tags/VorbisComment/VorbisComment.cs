using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text;

namespace RecursiveAI.OneMedia.Tags.VorbisComment;

/// <summary>The fields of a FLAC PICTURE block ahead of the image bytes (RFC 9639 §8.8), which is also the layout inside an Ogg METADATA_BLOCK_PICTURE comment.</summary>
/// <param name="PictureType">The ID3v2 APIC picture type, 0 to 20.</param>
/// <param name="MimeType">The media type of the image, or <c>-&gt;</c> when <see cref="DataOffset"/> holds a URL.</param>
/// <param name="Description">A description of the picture in UTF-8.</param>
/// <param name="Width">Width in pixels; 0 when unknown.</param>
/// <param name="Height">Height in pixels; 0 when unknown.</param>
/// <param name="BitsPerPixel">Colour depth in bits per pixel; 0 when unknown.</param>
/// <param name="ColorCount">Number of colours of an indexed picture, 0 for a non-indexed one.</param>
/// <param name="DataOffset">Offset of the image bytes from the start of the block body.</param>
/// <param name="DataLength">Length of the image bytes.</param>
public readonly record struct PictureBlockHeader(int PictureType, string MimeType, string Description, int Width, int Height, int BitsPerPixel, int ColorCount, int DataOffset, int DataLength)
{
    /// <summary>Reads the fields of a PICTURE block body. Returns false when the body is too short or the fields run past its end; never throws.</summary>
    /// <param name="body">The block body (without the four-byte block header).</param>
    /// <param name="header">The fields on success.</param>
    public static bool TryParse(ReadOnlySpan<byte> body, out PictureBlockHeader header)
    {
        header = default;
        int p = 0;
        if (!ReadU32(body, ref p, out uint type) || !ReadU32(body, ref p, out uint mimeLength) || mimeLength > (uint)(body.Length - p))
        {
            return false;
        }

        string mime = Encoding.ASCII.GetString(body.Slice(p, (int)mimeLength));
        p += (int)mimeLength;
        if (!ReadU32(body, ref p, out uint descLength) || descLength > (uint)(body.Length - p))
        {
            return false;
        }

        string description = Encoding.UTF8.GetString(body.Slice(p, (int)descLength));
        p += (int)descLength;
        if (!ReadU32(body, ref p, out uint w) || !ReadU32(body, ref p, out uint h) || !ReadU32(body, ref p, out uint depth)
            || !ReadU32(body, ref p, out uint colors) || !ReadU32(body, ref p, out uint dataLength) || dataLength > (uint)(body.Length - p))
        {
            return false;
        }

        header = new PictureBlockHeader((int)Math.Min(type, int.MaxValue), mime, description, (int)Math.Min(w, int.MaxValue), (int)Math.Min(h, int.MaxValue),
            (int)Math.Min(depth, int.MaxValue), (int)Math.Min(colors, int.MaxValue), p, (int)dataLength);
        return true;
    }

    private static bool ReadU32(ReadOnlySpan<byte> data, ref int p, out uint value)
    {
        if (data.Length - p < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
        p += 4;
        return true;
    }
}

/// <summary>
/// One Vorbis comment header (Vorbis I §5, also the body of a FLAC VORBIS_COMMENT block and of an Ogg Opus OpusTags packet after its
/// eight-byte magic): a vendor string and a list of <c>NAME=value</c> fields. Each field is one <see cref="NativeTag"/> in
/// system <see cref="TagSystems.VorbisComment"/>, with its name as written (names compare case-insensitively in the common view).
/// </summary>
public sealed class VorbisCommentBlock : ITagContainer
{
    internal VorbisCommentBlock(ByteRange range, long trackId, string vendor, ByteRange vendorRange, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        TrackId = trackId;
        Vendor = vendor;
        VendorRange = vendorRange;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.VorbisComment;

    /// <inheritdoc />
    public long TrackId { get; }

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>The vendor string (the name of the writing library), possibly empty.</summary>
    public string Vendor { get; }

    /// <summary>Where the vendor string's bytes are, in the same coordinates as the tags' ranges.</summary>
    public ByteRange VendorRange { get; }
}

/// <summary>Reads Vorbis comment headers into native tags without copying their values.</summary>
public static class VorbisCommentReader
{
    private const string PictureKey = "METADATA_BLOCK_PICTURE";

    /// <summary>
    /// Reads a comment header that is held in memory, as the Ogg readers do after assembling the packet from its pages.
    /// Offsets in the returned tags are relative to the start of <paramref name="comment"/>; a METADATA_BLOCK_PICTURE field is
    /// decoded from base64 into a picture whose bytes follow the comment in the same coordinate space.
    /// </summary>
    /// <param name="comment">The comment header body: vendor length, vendor, count, fields. For Vorbis the trailing framing bit is tolerated; for Opus the <c>OpusTags</c> magic must already be removed.</param>
    /// <param name="trackId">The track the comment belongs to, or 0 for the file.</param>
    /// <param name="limits">The limits; field values over <see cref="MediaLimits.MaxTagBytes"/> are not read and a picture over <see cref="MediaLimits.MaxAttachmentBytes"/> is dropped.</param>
    /// <param name="defects">Where deviations are logged.</param>
    /// <param name="physicalRange">The bytes of the container that hold the header, for <see cref="ITagContainer.Range"/>.</param>
    /// <returns>The parsed block. A malformed header yields the fields read before the damage and a defect.</returns>
    public static VorbisCommentBlock Read(ReadOnlyMemory<byte> comment, long trackId, MediaLimits limits, DefectLog defects, ByteRange physicalRange = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        List<byte[]> pictures = [];
        // First pass over the fields to find pictures; they are appended after the comment bytes, so one source serves all values.
        byte[] virt = BuildVirtualBuffer(comment.Span, limits, defects, pictures);
        TagIo io = new(new MemoryMediaSource(virt), limits, defects);
        ByteRange range = physicalRange.IsNone || physicalRange.Length == 0 ? new ByteRange(0, comment.Length) : physicalRange;
        List<NativeTag> tags = [];
        string vendor = Parse(comment.Span, 0, io, trackId, limits, defects, tags, comment.Length, pictures, out ByteRange vendorRange, expandPictures: true);
        return new VorbisCommentBlock(range, trackId, vendor, vendorRange, tags);
    }

    /// <summary>
    /// Reads a comment header whose bytes are at <paramref name="offset"/> in the source behind <paramref name="source"/> (a FLAC VORBIS_COMMENT block),
    /// so that tag ranges are real file offsets.
    /// </summary>
    /// <param name="body">The block body, already read from <paramref name="source"/> at <paramref name="offset"/>.</param>
    /// <param name="source">The source the values are read from later.</param>
    /// <param name="offset">Offset of <paramref name="body"/> in the source.</param>
    /// <param name="trackId">The track the comment belongs to, or 0 for the file.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="defects">Where deviations are logged.</param>
    /// <returns>The parsed block.</returns>
    public static VorbisCommentBlock ReadAt(ReadOnlySpan<byte> body, IMediaSource source, long offset, long trackId, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        string vendor = Parse(body, offset, io, trackId, limits, defects, tags, body.Length, null, out ByteRange vendorRange, expandPictures: false);
        return new VorbisCommentBlock(new ByteRange(offset, body.Length), trackId, vendor, vendorRange, tags);
    }

    /// <summary>Makes a tag from a FLAC PICTURE block (RFC 9639 §8.8) whose body is at <paramref name="offset"/> in <paramref name="source"/>.</summary>
    /// <param name="body">The block body.</param>
    /// <param name="source">The source the image bytes are read from later.</param>
    /// <param name="offset">Offset of <paramref name="body"/> in the source.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="defects">Where deviations are logged.</param>
    /// <returns>The picture as an image tag with key <c>PICTURE</c> in system <see cref="TagSystems.Flac"/>, or null when the block is malformed.</returns>
    public static NativeTag? ReadPictureBlock(ReadOnlySpan<byte> body, IMediaSource source, long offset, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        if (!PictureBlockHeader.TryParse(body, out PictureBlockHeader h))
        {
            defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, offset, "A PICTURE block is shorter than its fields claim.", "flac.picture");
            return null;
        }

        TagIo io = new(source, limits, defects);
        return MakePicture(io, TagSystems.Flac, "PICTURE", h, 0, new ByteRange(offset, body.Length), offset);
    }

    private static NativeTag MakePicture(TagIo io, string system, string key, PictureBlockHeader h, long trackId, ByteRange range, long bodyOffset) =>
        new(io, system, key, trackId, TagValueKind.Image, 0, 0, null, range, new ByteRange(bodyOffset + h.DataOffset, h.DataLength))
        {
            MimeType = h.MimeType,
            PictureTypeNumber = h.PictureType,
            Description = h.Description,
        };

    private static byte[] BuildVirtualBuffer(ReadOnlySpan<byte> data, MediaLimits limits, DefectLog defects, List<byte[]> pictures)
    {
        // Decode every METADATA_BLOCK_PICTURE value up front: base64 text is ASCII, so it decodes straight from the bytes.
        long total = data.Length;
        if (!TryWalk(data, out List<(int Start, int Length)> fields))
        {
            return data.ToArray();
        }

        foreach ((int start, int length) in fields)
        {
            ReadOnlySpan<byte> field = data.Slice(start, length);
            int eq = field.IndexOf((byte)'=');
            if (eq != PictureKey.Length || !AsciiEqualsIgnoreCase(field[..eq], PictureKey))
            {
                pictures.Add([]);
                continue;
            }

            ReadOnlySpan<byte> b64 = field[(eq + 1)..];
            long decodedMax = (long)b64.Length / 4 * 3;
            if (decodedMax > limits.MaxAttachmentBytes)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, start, "A METADATA_BLOCK_PICTURE value is larger than the attachment limit and is not decoded.", "vorbis.picture-too-large");
                pictures.Add([]);
                continue;
            }

            byte[] decoded = new byte[decodedMax];
            if (Base64.DecodeFromUtf8(b64, decoded, out _, out int written) != System.Buffers.OperationStatus.Done)
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, start, "A METADATA_BLOCK_PICTURE value is not valid base64.", "vorbis.picture-base64");
                pictures.Add([]);
                continue;
            }

            byte[] exact = decoded.AsSpan(0, written).ToArray();
            pictures.Add(exact);
            total += exact.Length;
        }

        limits.CheckAllocation(total, 0);
        byte[] virt = new byte[total];
        data.CopyTo(virt);
        long at = data.Length;
        foreach (byte[] p in pictures)
        {
            p.CopyTo(virt, at);
            at += p.Length;
        }

        return virt;
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (char.ToUpperInvariant((char)a[i]) != b[i])
            {
                return false;
            }
        }

        return true;
    }

    // The field spans of a comment header, for the picture pre-pass. Stops quietly at damage; Parse reports it.
    private static bool TryWalk(ReadOnlySpan<byte> data, out List<(int Start, int Length)> fields)
    {
        fields = [];
        int p = 0;
        if (data.Length < 8)
        {
            return false;
        }

        uint vendor = BinaryPrimitives.ReadUInt32LittleEndian(data);
        p = 4;
        if (vendor > (uint)(data.Length - p - 4))
        {
            return false;
        }

        p += (int)vendor;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
        p += 4;
        for (uint i = 0; i < count && data.Length - p >= 4; i++)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            p += 4;
            if (len > (uint)(data.Length - p))
            {
                break;
            }

            fields.Add((p, (int)len));
            p += (int)len;
        }

        return true;
    }

    private static string Parse(ReadOnlySpan<byte> data, long baseOffset, TagIo io, long trackId, MediaLimits limits, DefectLog defects, List<NativeTag> tags,
        int commentLength, List<byte[]>? pictures, out ByteRange vendorRange, bool expandPictures)
    {
        vendorRange = new ByteRange(baseOffset, 0);
        // Vorbis I §5.2.1: vendor_length (32-bit little-endian), vendor string, user_comment_list_length, then length-prefixed fields.
        if (data.Length < 4)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, baseOffset, "The comment header is too short to hold a vendor length.", "vorbis.truncated");
            return string.Empty;
        }

        uint vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int p = 4;
        if (vendorLength > (uint)(data.Length - p))
        {
            defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, baseOffset, "The vendor string is longer than the comment header.", "vorbis.vendor-size");
            return string.Empty;
        }

        string vendor = Encoding.UTF8.GetString(data.Slice(p, (int)vendorLength));
        vendorRange = new ByteRange(baseOffset + p, vendorLength);
        p += (int)vendorLength;
        if (data.Length - p < 4)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, baseOffset + p, "The comment header ends before its field count.", "vorbis.truncated");
            return vendor;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
        p += 4;
        // Each field needs four bytes of length, so a count beyond that is a lie; reading stops at the real end.
        if (count > (uint)((data.Length - p) / 4) && count > 0)
        {
            defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, baseOffset + p - 4, $"The comment count ({count}) is larger than the header can hold.", "vorbis.count");
        }

        // Pictures sit after the comment bytes in the order the fields were walked; the walk and this loop stop at the same field.
        int pictureIndex = 0;
        long picturesAt = commentLength;

        for (uint i = 0; i < count; i++)
        {
            if (data.Length - p < 4)
            {
                if (data.Length - p > 0 || i < count)
                {
                    defects.Add(DefectKind.Truncated, DefectSeverity.Warning, baseOffset + p, $"The comment header ends after {i} of {count} fields.", "vorbis.truncated");
                }

                break;
            }

            uint len = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            int fieldStart = p;
            p += 4;
            if (len > (uint)(data.Length - p))
            {
                defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, baseOffset + fieldStart, $"Field {i} is longer than the comment header.", "vorbis.field-size");
                break;
            }

            ReadOnlySpan<byte> field = data.Slice(p, (int)len);
            int valueStart = p;
            p += (int)len;
            ByteRange fieldRange = new(baseOffset + fieldStart, 4 + (long)len);
            int eq = field.IndexOf((byte)'=');
            byte[]? picture = pictures is not null && pictureIndex < pictures.Count ? pictures[pictureIndex] : null;
            pictureIndex++;
            if (eq < 0)
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, baseOffset + valueStart, $"Field {i} has no '=' between its name and value.", "vorbis.no-equals");
                continue;
            }

            string key = Encoding.ASCII.GetString(field[..eq]);
            if (!IsValidName(field[..eq]))
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, baseOffset + valueStart, $"Field name '{key}' uses characters outside 0x20-0x7D.", "vorbis.name");
            }

            ByteRange valueRange = new(baseOffset + valueStart + eq + 1, len - eq - 1);
            if (expandPictures && picture is { Length: > 0 })
            {
                ReadOnlySpan<byte> virt = picture;
                if (PictureBlockHeader.TryParse(virt, out PictureBlockHeader h))
                {
                    tags.Add(MakePicture(io, TagSystems.VorbisComment, key, h, trackId, fieldRange, picturesAt));
                }
                else
                {
                    defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, baseOffset + valueStart, "A METADATA_BLOCK_PICTURE value is not a PICTURE block.", "vorbis.picture");
                }

                picturesAt += picture.Length;
                continue;
            }

            tags.Add(new NativeTag(io, TagSystems.VorbisComment, key, trackId, TagValueKind.Text, 0, 0, null, fieldRange, valueRange));
        }

        // Vorbis I §5.2.1 ends the header with a framing bit; Opus and FLAC have none. Anything else left over is reported.
        int left = data.Length - p;
        if (left > 1 || (left == 1 && (data[p] & 1) == 0 && data[p] != 0))
        {
            defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, baseOffset + p, $"{left} bytes follow the last comment field.", "vorbis.trailing");
        }

        return vendor;
    }

    // Vorbis I §5.2.1: a field name is ASCII 0x20 through 0x7D, not including '='.
    private static bool IsValidName(ReadOnlySpan<byte> name)
    {
        foreach (byte b in name)
        {
            if (b < 0x20 || b > 0x7D || b == (byte)'=')
            {
                return false;
            }
        }

        return true;
    }
}
