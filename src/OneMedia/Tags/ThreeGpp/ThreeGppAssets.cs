using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Tags.QuickTime;

namespace RecursiveAI.OneMedia.Tags.ThreeGpp;

/// <summary>The location information box (<c>loci</c>), 3GPP TS 26.244 §8.</summary>
/// <param name="Language">ISO 639-2 language of <paramref name="Name"/> and <paramref name="Notes"/>.</param>
/// <param name="Name">Place name.</param>
/// <param name="Role">0 shooting location, 1 real location, 2 fictional location; other values are reserved.</param>
/// <param name="Longitude">Degrees east, from a 16.16 fixed-point field (a display value).</param>
/// <param name="Latitude">Degrees north, from a 16.16 fixed-point field.</param>
/// <param name="Altitude">Metres above the reference ellipsoid, from a 16.16 fixed-point field.</param>
/// <param name="AstronomicalBody">Name of the body the position refers to (normally Earth); may be empty.</param>
/// <param name="Notes">Additional notes; may be empty.</param>
/// <param name="Range">The box in the file.</param>
public sealed record ThreeGppLocation(string Language, string Name, int Role, double Longitude, double Latitude, double Altitude, string AstronomicalBody, string Notes, ByteRange Range);

/// <summary>
/// The 3GPP asset information boxes inside <c>udta</c> (3GPP TS 26.244 §8): <c>titl</c>, <c>dscp</c>, <c>cprt</c>, <c>perf</c>, <c>auth</c>, <c>gnre</c>,
/// <c>rtng</c>, <c>clsf</c>, <c>kywd</c>, <c>albm</c>, <c>yrrc</c> and <c>loci</c>. Every box is a full box with a packed language code; text is
/// UTF-8 or UTF-16 with a byte order mark and ends with a NUL. Tags are keyed by the box type; <c>kywd</c> yields one tag per keyword
/// and <c>albm</c> adds an <c>albm:track</c> integer tag when the box carries a track number.
/// </summary>
public sealed class ThreeGppAssets : ITagContainer
{
    private ThreeGppAssets(ByteRange range, long trackId, IReadOnlyList<NativeTag> tags, ThreeGppLocation? location)
    {
        Range = range;
        TrackId = trackId;
        Tags = tags;
        Location = location;
    }

    /// <inheritdoc />
    public string System => TagSystems.ThreeGpp;

    /// <inheritdoc />
    public long TrackId { get; }

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>The first <c>loci</c> box, or <c>null</c>.</summary>
    public ThreeGppLocation? Location { get; }

    /// <summary>
    /// Reads the 3GPP boxes of a <c>udta</c> box. A box too short for its fields, or a string that is not terminated, is a defect
    /// (<c>tags.3gpp-short</c>, <c>tags.3gpp-unterminated</c>) and what could be read is kept.
    /// </summary>
    /// <param name="udta">The user data box.</param>
    /// <param name="source">The source the box was read from; tag values are read from it later.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="trackId">The track the box belongs to (0 for the movie).</param>
    /// <returns>The asset boxes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ThreeGppAssets Read(BoxNode udta, IMediaSource source, MediaLimits limits, DefectLog defects, long trackId = 0)
    {
        ArgumentNullException.ThrowIfNull(udta);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        ThreeGppLocation? location = null;
        foreach (BoxNode box in udta.EnumerateChildren())
        {
            byte[] head = box.ReadPayload(Math.Min(box.PayloadSize, 8));
            if (UserDataClassifier.Classify(box, head) != UserDataLayout.ThreeGpp)
            {
                continue;
            }

            if (box.PayloadSize > limits.MaxTagBytes)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, box.Offset, $"The '{box.TypeName}' box is larger than the tag limit and is not read.", "tags.value-too-large");
                continue;
            }

            byte[] p = box.ReadPayload(limits.MaxTagBytes);
            Cursor c = new(p, box.Header.PayloadOffset, box, io);
            ByteRange range = new(box.Offset, box.Header.HeaderSize + box.PayloadSize);
            c.Skip(4); // version and flags
            switch (box.TypeName)
            {
                case "yrrc":
                    if (c.Remaining >= 2)
                    {
                        long at = c.Position;
                        NativeTag year = new(io, TagSystems.ThreeGpp, "yrrc", trackId, TagValueKind.Number, 0, 0, null, range, new ByteRange(at, 2)) { Number = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(c.Index)) };
                        tags.Add(year);
                    }
                    else
                    {
                        c.Short();
                    }

                    break;
                case "rtng":
                    c.Skip(8); // rating entity and criteria
                    AddText(c, tags, "rtng", range, trackId);
                    break;
                case "clsf":
                    c.Skip(6); // classification entity and table
                    AddText(c, tags, "clsf", range, trackId);
                    break;
                case "kywd":
                    ReadKeywords(c, tags, range, trackId);
                    break;
                case "albm":
                    AddText(c, tags, "albm", range, trackId, readTrack: true);
                    break;
                case "loci":
                    location ??= ReadLocation(c, range);
                    if (location is not null)
                    {
                        NativeTag place = new(io, TagSystems.ThreeGpp, "loci", trackId, TagValueKind.Text, 1, 0, location.Language, range, range);
                        place.SetText(location.Name);
                        tags.Add(place);
                    }

                    break;
                default:
                    AddText(c, tags, box.TypeName, range, trackId);
                    break;
            }
        }

        return new ThreeGppAssets(new ByteRange(udta.Offset, udta.Header.HeaderSize + udta.PayloadSize), trackId, tags, location);
    }

    private static void AddText(Cursor c, List<NativeTag> tags, string key, ByteRange range, long trackId, bool readTrack = false)
    {
        string? lang = c.Language();
        if (lang is null)
        {
            return;
        }

        string? text = c.String(out ByteRange valueRange, out TagTextEncoding enc);
        if (text is null)
        {
            return;
        }

        NativeTag tag = new(c.Io, TagSystems.ThreeGpp, key, trackId, TagValueKind.Text, enc == TagTextEncoding.Utf8 ? 1u : 2u, 0, lang == "und" ? null : lang, range, valueRange) { Encoding = enc };
        tag.SetText(text);
        tags.Add(tag);
        if (readTrack && c.Remaining >= 1)
        {
            long at = c.Position;
            tags.Add(new NativeTag(c.Io, TagSystems.ThreeGpp, key + ":track", trackId, TagValueKind.Number, 0, 0, null, range, new ByteRange(at, 1)) { Number = c.U8() });
        }
    }

    private static void ReadKeywords(Cursor c, List<NativeTag> tags, ByteRange range, long trackId)
    {
        string? lang = c.Language();
        if (lang is null || c.Remaining < 1)
        {
            c.Short();
            return;
        }

        int count = c.U8();
        for (int i = 0; i < count && c.Remaining >= 1; i++)
        {
            int size = c.U8();
            if (size > c.Remaining)
            {
                c.Short();
                return;
            }

            ByteRange vr = new(c.Position, size);
            ReadOnlySpan<byte> raw = c.Take(size);
            TagTextEncoding enc = raw.Length >= 2 && (raw[0] == 0xFE || raw[0] == 0xFF) ? TagTextEncoding.Utf16WithByteOrderMark : TagTextEncoding.Utf8;
            NativeTag tag = new(c.Io, TagSystems.ThreeGpp, "kywd", trackId, TagValueKind.Text, 1, 0, lang == "und" ? null : lang, range, vr) { Encoding = enc };
            tag.SetText(TextDecoder.Decode(raw, enc, vr.Offset, c.Io.Defects));
            tags.Add(tag);
        }
    }

    private static ThreeGppLocation? ReadLocation(Cursor c, ByteRange range)
    {
        string? lang = c.Language();
        string? name = lang is null ? null : c.String(out _, out _);
        if (name is null || c.Remaining < 13)
        {
            c.Short();
            return null;
        }

        int role = c.U8();
        double lon = c.Fixed16();
        double lat = c.Fixed16();
        double alt = c.Fixed16();
        string body = c.String(out _, out _) ?? string.Empty;
        string notes = c.String(out _, out _) ?? string.Empty;
        return new ThreeGppLocation(lang!, name, role, lon, lat, alt, body, notes, range);
    }

    /// <summary>A bounds-checked reader over one box payload.</summary>
    private sealed class Cursor(byte[] data, long baseOffset, BoxNode box, TagIo tagIo)
    {
        private int _pos;

        public TagIo Io { get; } = tagIo;

        public int Index => _pos;

        public int Remaining => data.Length - _pos;

        public long Position => baseOffset + _pos;

        public void Skip(int n) => _pos = Math.Min(data.Length, _pos + n);

        public void Short() => Io.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, box.Offset, $"The '{box.TypeName}' box is shorter than its fields.", "tags.3gpp-short");

        public int U8() => data[_pos++];

        public ReadOnlySpan<byte> Take(int n)
        {
            ReadOnlySpan<byte> s = data.AsSpan(_pos, n);
            _pos += n;
            return s;
        }

        public double Fixed16()
        {
            int v = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(_pos));
            _pos += 4;
            return v / 65536.0;
        }

        /// <summary>A pad bit and three 5-bit letters (ISO/IEC 14496-12 §8.4.2.3), as an ISO 639-2 code.</summary>
        public string? Language()
        {
            if (Remaining < 2)
            {
                Short();
                return null;
            }

            ushort code = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_pos));
            _pos += 2;
            return LanguageCodes.Decode(code);
        }

        public string? String(out ByteRange valueRange, out TagTextEncoding encoding)
        {
            int start = _pos;
            valueRange = new ByteRange(baseOffset + start, 0);
            encoding = TagTextEncoding.Utf8;
            bool utf16 = Remaining >= 2 && ((data[_pos] == 0xFE && data[_pos + 1] == 0xFF) || (data[_pos] == 0xFF && data[_pos + 1] == 0xFE));
            int end = -1;
            if (utf16)
            {
                encoding = TagTextEncoding.Utf16WithByteOrderMark;
                for (int i = start; i + 1 < data.Length; i += 2)
                {
                    if (data[i] == 0 && data[i + 1] == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }
            else
            {
                end = Array.IndexOf(data, (byte)0, start);
            }

            int next;
            if (end < 0)
            {
                Io.Defects.Add(DefectKind.Truncated, DefectSeverity.Info, baseOffset + start, $"A string in the '{box.TypeName}' box has no terminating NUL.", "tags.3gpp-unterminated");
                end = data.Length;
                next = end;
            }
            else
            {
                next = end + (utf16 ? 2 : 1);
            }

            valueRange = new ByteRange(baseOffset + start, end - start);
            string text = TextDecoder.Decode(data.AsSpan(start, end - start), encoding, baseOffset + start, Io.Defects);
            _pos = next;
            return text;
        }
    }
}
