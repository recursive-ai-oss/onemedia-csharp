using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Tags.QuickTime;

/// <summary>How an atom directly under <c>udta</c> lays out its payload.</summary>
internal enum UserDataLayout
{
    /// <summary>QuickTime text: repeated records of a 16-bit size, a 16-bit language and the text.</summary>
    QuickTimeText,

    /// <summary>3GPP asset information: version and flags, then the box's own fields (3GPP TS 26.244 §8).</summary>
    ThreeGpp,

    /// <summary>Anything else; kept as bytes.</summary>
    Binary,
}

internal static class UserDataClassifier
{
    // Asset boxes that only 3GPP defines, and the camera maker and model strings (manu, modl) that some cameras write in the same layout:
    // version and flags, a packed language, a zero-terminated string (observed in a compact-camera MP4, where ExifTool lists them as Make and Model).
    private static readonly uint[] ThreeGppOnly = [BoxTypes.Make("loci"), BoxTypes.Make("yrrc"), BoxTypes.Make("kywd"), BoxTypes.Make("albm"), BoxTypes.Make("rtng"), BoxTypes.Make("clsf"), BoxTypes.Make("manu"), BoxTypes.Make("modl")];

    // Atoms both QuickTime and 3GPP define with different payloads: told apart by their first bytes.
    private static readonly uint[] Shared = [BoxTypes.Make("titl"), BoxTypes.Make("dscp"), BoxTypes.Make("cprt"), BoxTypes.Make("perf"), BoxTypes.Make("auth"), BoxTypes.Make("gnre")];

    public static UserDataLayout Classify(BoxNode atom, ReadOnlySpan<byte> head)
    {
        uint type = atom.Type;
        if ((type >> 24) == 0xA9)
        {
            return UserDataLayout.QuickTimeText;
        }

        if (Array.IndexOf(ThreeGppOnly, type) >= 0)
        {
            return UserDataLayout.ThreeGpp;
        }

        if (Array.IndexOf(Shared, type) >= 0)
        {
            // A 3GPP box starts with four zero bytes (version, flags) and a language word whose top bit is a pad bit (0).
            // A QuickTime text atom starts with the size of its first text record.
            if (atom.PayloadSize >= 6 && head.Length >= 6 && BinaryPrimitives.ReadUInt32BigEndian(head) == 0 && (head[4] & 0x80) == 0)
            {
                return UserDataLayout.ThreeGpp;
            }

            if (atom.PayloadSize >= 4 && head.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(head) <= atom.PayloadSize - 4)
            {
                return UserDataLayout.QuickTimeText;
            }
        }

        return UserDataLayout.Binary;
    }
}

/// <summary>
/// The QuickTime user data atoms of a <c>udta</c> box: the © text atoms (<c>©nam</c>, <c>©day</c>, <c>©xyz</c>, <c>©mak</c>, <c>©mod</c>, <c>©swr</c>…) with their
/// per-language records, and every other atom kept as opaque bytes. 3GPP asset boxes (<c>titl</c>, <c>loci</c>…) are read by
/// <see cref="RecursiveAI.OneMedia.Tags.ThreeGpp.ThreeGppAssets"/>; <c>meta</c> and XMP children are read by their own layers.
/// </summary>
public sealed class QuickTimeUserData : ITagContainer
{
    private QuickTimeUserData(ByteRange range, long trackId, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        TrackId = trackId;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.QuickTimeUserData;

    /// <inheritdoc />
    public long TrackId { get; }

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>
    /// Reads a <c>udta</c> box. A text atom yields one tag per language record; a record that overruns its atom is clamped and reported
    /// (<c>tags.udta-text-size</c>); an atom without the record header is read as plain text (<c>tags.udta-raw-text</c>).
    /// </summary>
    /// <param name="udta">The user data box.</param>
    /// <param name="source">The source the box was read from; tag values are read from it later.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="trackId">The track the box belongs to (0 for the movie).</param>
    /// <returns>The atoms.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static QuickTimeUserData Read(BoxNode udta, IMediaSource source, MediaLimits limits, DefectLog defects, long trackId = 0)
    {
        ArgumentNullException.ThrowIfNull(udta);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        foreach (BoxNode atom in udta.EnumerateChildren())
        {
            if (atom.Type is BoxTypes.Meta || atom.TypeName is "XMP_" or "chpl" || atom.Type == BoxTypes.Uuid)
            {
                continue;
            }

            byte[] head = atom.ReadPayload(Math.Min(atom.PayloadSize, 8));
            UserDataLayout layout = UserDataClassifier.Classify(atom, head);
            ByteRange range = new(atom.Offset, atom.Header.HeaderSize + atom.PayloadSize);
            ByteRange payload = new(atom.Header.PayloadOffset, atom.PayloadSize);
            switch (layout)
            {
                case UserDataLayout.QuickTimeText:
                    ReadTextRecords(atom, io, range, trackId, tags);
                    break;
                case UserDataLayout.Binary:
                    tags.Add(new NativeTag(io, TagSystems.QuickTimeUserData, atom.TypeName, trackId, TagValueKind.Binary, 0, 0, null, range, payload));
                    break;
            }
        }

        return new QuickTimeUserData(new ByteRange(udta.Offset, udta.Header.HeaderSize + udta.PayloadSize), trackId, tags);
    }

    private static void ReadTextRecords(BoxNode atom, TagIo io, ByteRange range, long trackId, List<NativeTag> tags)
    {
        if (atom.PayloadSize > io.Limits.MaxTagBytes)
        {
            io.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, atom.Offset, $"The '{atom.TypeName}' atom is larger than the tag limit and is not read.", "tags.value-too-large");
            return;
        }

        byte[] p = atom.ReadPayload(io.Limits.MaxTagBytes);
        long baseOffset = atom.Header.PayloadOffset;
        int pos = 0;
        int records = 0;
        while (pos + 4 <= p.Length)
        {
            int size = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
            ushort lang = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos + 2));
            int available = p.Length - pos - 4;
            if (size > available)
            {
                if (records == 0)
                {
                    // The first record overruns the atom: some writers put the bare text in the atom instead of records.
                    io.Defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, atom.Offset, $"The '{atom.TypeName}' atom has no text record header and is read as plain text.", "tags.udta-raw-text");
                    AddTag(io, atom, p.AsSpan(0), baseOffset, range, trackId, null, tags);
                    return;
                }

                io.Defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, baseOffset + pos, $"A text record of '{atom.TypeName}' declares {size} bytes but {available} remain.", "tags.udta-text-size");
                size = available;
            }

            AddTag(io, atom, p.AsSpan(pos + 4, size), baseOffset + pos + 4, range, trackId, lang == 0 ? null : LanguageCodes.Decode(lang), tags);
            records++;
            pos += 4 + size;
        }
    }

    private static void AddTag(TagIo io, BoxNode atom, ReadOnlySpan<byte> text, long valueOffset, ByteRange range, long trackId, string? language, List<NativeTag> tags)
    {
        bool utf16 = text.Length >= 2 && ((text[0] == 0xFE && text[1] == 0xFF) || (text[0] == 0xFF && text[1] == 0xFE));
        NativeTag tag = new(io, TagSystems.QuickTimeUserData, atom.TypeName, trackId, TagValueKind.Text, utf16 ? 2u : 1u, 0, language, range, new ByteRange(valueOffset, text.Length))
        {
            Encoding = utf16 ? TagTextEncoding.Utf16WithByteOrderMark : TagTextEncoding.Utf8,
        };
        tag.SetText(TextDecoder.Decode(text, tag.Encoding, valueOffset, io.Defects));
        tags.Add(tag);
    }
}
