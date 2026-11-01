using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Tags.Id3;

/// <summary>
/// The logical bytes of an ID3v2 tag body: read straight from the source through a window, or held in memory after tag-wide
/// de-unsynchronisation (with the positions of the removed bytes, so a frame can still be located in the file).
/// </summary>
internal sealed class Id3Body
{
    private readonly TagWindow? _window;
    private readonly byte[]? _memory;
    private readonly List<int>? _removed;
    private readonly long _fileStart;

    private Id3Body(TagWindow? window, byte[]? memory, List<int>? removed, long fileStart, long length)
    {
        _window = window;
        _memory = memory;
        _removed = removed;
        _fileStart = fileStart;
        Length = length;
    }

    public long Length { get; private set; }

    public static Id3Body Direct(TagWindow window, long fileStart) => new(window, null, null, fileStart, window.Length);

    /// <summary>An in-memory body whose file offsets are one-to-one with the bytes (used for frames embedded in a frame).</summary>
    public static Id3Body Memory(byte[] bytes, long fileStart) => new(null, bytes, null, fileStart, bytes.Length);

    /// <summary>Undoes unsynchronisation of one frame's data.</summary>
    public static byte[] Deunsynchronise(ReadOnlySpan<byte> raw) => FromUnsynchronised(raw, 0).Get(0, raw.Length).ToArray();

    /// <summary>Undoes unsynchronisation (id3v2.4.0-structure §6.1): a 0x00 that follows 0xFF is dropped.</summary>
    public static Id3Body FromUnsynchronised(ReadOnlySpan<byte> raw, long fileStart)
    {
        byte[] output = new byte[raw.Length];
        List<int> removed = [];
        int n = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0 && raw[i] == 0 && raw[i - 1] == 0xFF)
            {
                removed.Add(i);
                continue;
            }

            output[n++] = raw[i];
        }

        return new Id3Body(null, output, removed, fileStart, n);
    }

    public ReadOnlySpan<byte> Get(long relative, int count)
    {
        if (_memory is not null)
        {
            if (relative < 0 || relative >= Length)
            {
                return default;
            }

            return _memory.AsSpan((int)relative, (int)Math.Min(count, Length - relative));
        }

        ReadOnlySpan<byte> span = _window!.Get(relative, count);
        Length = Math.Min(Length, _window.Length);
        return span;
    }

    /// <summary>The file offset of the logical byte at <paramref name="relative"/>.</summary>
    public long ToFile(long relative)
    {
        long raw = relative;
        if (_removed is not null)
        {
            foreach (int k in _removed)
            {
                if (k <= raw)
                {
                    raw++;
                }
                else
                {
                    break;
                }
            }
        }

        return _fileStart + raw;
    }

    public ByteRange ToFile(long relative, long length) =>
        length <= 0 ? new ByteRange(ToFile(relative), 0) : new ByteRange(ToFile(relative), ToFile(relative + length - 1) + 1 - ToFile(relative));
}

/// <summary>Walks the frames of one ID3v2 tag and decodes each into a <see cref="NativeTag"/>.</summary>
internal sealed class Id3FrameReader(TagIo io, int major, bool tagUnsynchronised, bool loaderMode = false)
{
    private const int EagerBytes = 64 * 1024;
    private const int PrefixBytes = 2048;

    private readonly DefectLog _defects = io.Defects;

    /// <summary>Skips the extended header and returns the position of the first frame.</summary>
    public long SkipExtendedHeader(Id3Body body, Id3Tag tag)
    {
        ReadOnlySpan<byte> h = body.Get(0, 6);
        if (h.Length < 4)
        {
            return 0;
        }

        // 2.3: size excludes its own four bytes and is a plain integer; 2.4: size includes them and is synchsafe.
        long size = major == 3 ? 4L + BinaryPrimitives.ReadUInt32BigEndian(h) : Synchsafe(h);
        if (size < 6 || size > body.Length)
        {
            _defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, body.ToFile(0), "The ID3v2 extended header has an impossible size; it is ignored.", "id3.extended-header");
            return 0;
        }

        tag.SetExtendedHeader(body.ToFile(0, size));
        return size;
    }

    public void ReadFrames(Id3Body body, long position, List<Id3Frame> frames, Id3Tag? tag)
    {
        int headerSize = major == 2 ? 6 : 10;
        long end = body.Length;
        while (position + headerSize <= body.Length)
        {
            end = body.Length;
            ReadOnlySpan<byte> h = body.Get(position, headerSize);
            if (h.Length < headerSize)
            {
                break;
            }

            if (h[0] == 0)
            {
                // Padding: zeros to the end of the tag (id3v2.4.0-structure §3.3).
                tag?.SetPadding(body.ToFile(position, end - position));
                if (HasNonZero(body, position, end))
                {
                    _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, body.ToFile(position), "Non-zero bytes follow the start of the ID3v2 padding.", "id3.padding");
                }

                return;
            }

            if (!ValidId(h[..(major == 2 ? 3 : 4)]))
            {
                _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, body.ToFile(position), "An ID3v2 frame identifier holds characters other than A-Z and 0-9; the rest of the tag is not read.", "id3.frame-id");
                tag?.SetUnparsed(body.ToFile(position, end - position));
                return;
            }

            string id = Encoding.Latin1.GetString(h[..(major == 2 ? 3 : 4)]);
            long size;
            Id3FrameAttributes flags = Id3FrameAttributes.None;
            if (major == 2)
            {
                size = (h[3] << 16) | (h[4] << 8) | h[5];
            }
            else
            {
                size = FrameSize(body, position, h, end);
                flags = ReadFlags(h[8], h[9]);
            }

            if (size < 0 || position + headerSize + size > end)
            {
                _defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, body.ToFile(position), $"The ID3v2 frame '{id}' is larger than the rest of the tag; it and everything after it are not read.", "id3.frame-size");
                tag?.SetUnparsed(body.ToFile(position, end - position));
                return;
            }

            Id3Frame frame = new(id, body.ToFile(position, headerSize + size), body.ToFile(position + headerSize, size), flags);
            DecodeFrame(frame, body, position + headerSize, size);
            frames.Add(frame);
            position += headerSize + size;
        }

        if (position < body.Length && body.Length - position > 0 && position + headerSize > body.Length)
        {
            // Fewer bytes than a frame header remain: the writer's padding, in practice.
            tag?.SetPadding(body.ToFile(position, body.Length - position));
        }
    }

    private long FrameSize(Id3Body body, long position, ReadOnlySpan<byte> h, long end)
    {
        long plain = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
        if (major == 3)
        {
            return plain;
        }

        bool safe = ((h[4] | h[5] | h[6] | h[7]) & 0x80) == 0;
        long synchsafe = safe ? Synchsafe(h[4..]) : -1;
        if (safe && (synchsafe == plain || NextFrameLooksValid(body, position + 10 + synchsafe, end)))
        {
            return synchsafe;
        }

        // Some writers store 2.4 frame sizes as plain integers; take that reading when it leads to a frame (or the end) and the synchsafe one does not.
        if (NextFrameLooksValid(body, position + 10 + plain, end))
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, body.ToFile(position), "An ID3v2.4 frame size is not synchsafe; read as a plain integer.", "id3.frame-size-not-synchsafe");
            return plain;
        }

        return safe ? synchsafe : plain;
    }

    private static bool NextFrameLooksValid(Id3Body body, long position, long end)
    {
        if (position == end)
        {
            return true;
        }

        if (position < 0 || position + 10 > end)
        {
            return position <= end && position + 10 > end;
        }

        ReadOnlySpan<byte> n = body.Get(position, 4);
        return n.Length == 4 && (n[0] == 0 || ValidId(n));
    }

    private static bool HasNonZero(Id3Body body, long from, long to)
    {
        for (long p = from; p < to;)
        {
            ReadOnlySpan<byte> chunk = body.Get(p, (int)Math.Min(4096, to - p));
            if (chunk.IsEmpty)
            {
                return false;
            }

            if (chunk.IndexOfAnyExcept((byte)0) >= 0)
            {
                return true;
            }

            p += chunk.Length;
        }

        return false;
    }

    private Id3FrameAttributes ReadFlags(byte a, byte b)
    {
        Id3FrameAttributes f = Id3FrameAttributes.None;
        if (major == 3)
        {
            // id3v2.3.0 §3.3.1: %abc00000 %ijk00000; a tag alter, b file alter, c read only, i compression, j encryption, k grouping.
            f |= (a & 0x80) != 0 ? Id3FrameAttributes.TagAlterPreservation : 0;
            f |= (a & 0x40) != 0 ? Id3FrameAttributes.FileAlterPreservation : 0;
            f |= (a & 0x20) != 0 ? Id3FrameAttributes.ReadOnly : 0;
            f |= (b & 0x80) != 0 ? Id3FrameAttributes.Compressed : 0;
            f |= (b & 0x40) != 0 ? Id3FrameAttributes.Encrypted : 0;
            f |= (b & 0x20) != 0 ? Id3FrameAttributes.Grouping : 0;
            return f;
        }

        // id3v2.4.0-structure §4.1.1: %0abc0000 %0h00kmnp.
        f |= (a & 0x40) != 0 ? Id3FrameAttributes.TagAlterPreservation : 0;
        f |= (a & 0x20) != 0 ? Id3FrameAttributes.FileAlterPreservation : 0;
        f |= (a & 0x10) != 0 ? Id3FrameAttributes.ReadOnly : 0;
        f |= (b & 0x40) != 0 ? Id3FrameAttributes.Grouping : 0;
        f |= (b & 0x08) != 0 ? Id3FrameAttributes.Compressed : 0;
        f |= (b & 0x04) != 0 ? Id3FrameAttributes.Encrypted : 0;
        f |= (b & 0x02) != 0 || tagUnsynchronised ? Id3FrameAttributes.Unsynchronised : 0;
        f |= (b & 0x01) != 0 ? Id3FrameAttributes.DataLengthIndicator : 0;
        return f;
    }

    private static bool ValidId(ReadOnlySpan<byte> id)
    {
        foreach (byte c in id)
        {
            if (!(c is >= (byte)'A' and <= (byte)'Z' || c is >= (byte)'0' and <= (byte)'9'))
            {
                return false;
            }
        }

        return true;
    }

    internal static long Synchsafe(ReadOnlySpan<byte> b) => ((long)(b[0] & 0x7F) << 21) | ((long)(b[1] & 0x7F) << 14) | ((long)(b[2] & 0x7F) << 7) | (long)(b[3] & 0x7F);

    // The frame's data: apply the transformations its flags name, then decode.
    private void DecodeFrame(Id3Frame frame, Id3Body body, long dataStart, long size)
    {
        Id3FrameAttributes flags = frame.Flags;
        long offset = frame.DataRange.Offset;
        bool transformed = loaderMode || (flags & (Id3FrameAttributes.Compressed | Id3FrameAttributes.Unsynchronised)) != 0;
        bool structured = (flags & (Id3FrameAttributes.Compressed | Id3FrameAttributes.Encrypted | Id3FrameAttributes.Grouping | Id3FrameAttributes.DataLengthIndicator | Id3FrameAttributes.Unsynchronised)) != 0;
        byte[]? whole = null;
        ReadOnlySpan<byte> data;
        int prefix = 0;
        if (structured)
        {
            if (size > EagerBytes)
            {
                try
                {
                    io.Limits.CheckTag(size, offset);
                }
                catch (MediaLimitExceededException e)
                {
                    _defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, e.Offset, $"The flagged ID3v2 frame '{frame.Id}' is larger than the tag limit and is kept undecoded.", "id3.value-too-large");
                    frame.Native = Opaque(frame, frame.Id, 0);
                    return;
                }
            }

            whole = body.Get(dataStart, (int)size).ToArray();
            if ((flags & Id3FrameAttributes.Unsynchronised) != 0)
            {
                whole = Id3Body.Deunsynchronise(whole);
            }

            ReadOnlySpan<byte> w = whole;
            if (major == 3)
            {
                // id3v2.3.0 §3.3.1: decompressed size (4), then encryption method (1), then group (1), in that order.
                if ((flags & Id3FrameAttributes.Compressed) != 0 && w.Length >= 4)
                {
                    frame.DecodedLength = BinaryPrimitives.ReadUInt32BigEndian(w);
                    w = w[4..];
                    prefix += 4;
                }

                if ((flags & Id3FrameAttributes.Encrypted) != 0 && w.Length >= 1)
                {
                    frame.EncryptionMethod = w[0];
                    w = w[1..];
                    prefix++;
                }

                if ((flags & Id3FrameAttributes.Grouping) != 0 && w.Length >= 1)
                {
                    frame.GroupId = w[0];
                    w = w[1..];
                    prefix++;
                }
            }
            else
            {
                // id3v2.4.0-structure §4.1.2: group (1), encryption method (1), data length indicator (4, synchsafe).
                if ((flags & Id3FrameAttributes.Grouping) != 0 && w.Length >= 1)
                {
                    frame.GroupId = w[0];
                    w = w[1..];
                    prefix++;
                }

                if ((flags & Id3FrameAttributes.Encrypted) != 0 && w.Length >= 1)
                {
                    frame.EncryptionMethod = w[0];
                    w = w[1..];
                    prefix++;
                }

                if ((flags & Id3FrameAttributes.DataLengthIndicator) != 0 && w.Length >= 4)
                {
                    frame.DecodedLength = Synchsafe(w);
                    w = w[4..];
                    prefix += 4;
                }
            }

            if ((flags & Id3FrameAttributes.Encrypted) != 0)
            {
                _defects.Add(DefectKind.Unsupported, DefectSeverity.Info, offset, $"The ID3v2 frame '{frame.Id}' is encrypted; it is exposed as stored and not decrypted.", "id3.encrypted");
                frame.Native = Opaque(frame, frame.Id, prefix);
                return;
            }

            if ((flags & Id3FrameAttributes.Compressed) != 0)
            {
                // id3v2.3.0 §3.3.1 and 2.4 §4.1.2: zlib (RFC 1950). The decoded size is stated by the frame and checked against the tag limit before inflating.
                long expected = frame.DecodedLength ?? 0;
                if (expected <= 0)
                {
                    _defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, offset, $"The compressed ID3v2 frame '{frame.Id}' does not state its decompressed size; it is kept undecoded.", "id3.compressed-length");
                    frame.Native = Opaque(frame, frame.Id, prefix);
                    return;
                }

                try
                {
                    w = Id3Tag.Inflate(w, expected, io.Limits, offset);
                }
                catch (MediaLimitExceededException e)
                {
                    _defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, e.Offset, $"The decompressed ID3v2 frame '{frame.Id}' would exceed the tag limit; it is kept undecoded.", "id3.value-too-large");
                    frame.Native = Opaque(frame, frame.Id, prefix);
                    return;
                }
                catch (InvalidDataException)
                {
                    _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, $"The compressed ID3v2 frame '{frame.Id}' is not valid zlib data; it is kept undecoded.", "id3.compressed-data");
                    frame.Native = Opaque(frame, frame.Id, prefix);
                    return;
                }
            }

            whole = w.ToArray();
            data = whole;
        }
        else if (size <= EagerBytes || loaderMode)
        {
            whole = body.Get(dataStart, (int)size).ToArray();
            data = whole;
        }
        else
        {
            data = body.Get(dataStart, PrefixBytes);
        }

        Id3FrameDecoder decoder = new(io, major, frame, transformed ? frame.DataRange.Offset : body.ToFile(dataStart + prefix), size - prefix, transformed ? whole : null, whole is null && data.Length < size - prefix);
        decoder.Decode(data);
        if (frame.Native is null)
        {
            frame.Native = Opaque(frame, frame.Id, prefix);
        }

        if (frame.Id is "CHAP" && whole is not null)
        {
            ReadChapter(frame, whole, body.ToFile(dataStart + prefix));
        }
        else if (frame.Id is "CTOC" && whole is not null)
        {
            ReadTableOfContents(frame, whole, body.ToFile(dataStart + prefix));
        }
    }

    private NativeTag Opaque(Id3Frame frame, string key, int prefix) =>
        new(io, TagSystems.Id3v2, key, 0, TagValueKind.Binary, 0, 0, null, frame.Range, new ByteRange(frame.DataRange.Offset + prefix, Math.Max(0, frame.DataRange.Length - prefix)));

    private void ReadChapter(Id3Frame frame, byte[] data, long fileOffset)
    {
        // Chapter frame addendum §3.1: element ID (latin-1, NUL-terminated), start time, end time, start offset, end offset (each 32 bits), then sub-frames.
        int nul = Array.IndexOf(data, (byte)0);
        if (nul < 0 || data.Length < nul + 1 + 16)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, fileOffset, "A CHAP frame is too short for its fixed fields.", "id3.chap");
            return;
        }

        ReadOnlySpan<byte> f = data.AsSpan(nul + 1);
        List<Id3Frame> subs = [];
        ReadSubFrames(data, nul + 17, fileOffset, subs);
        frame.Chapter = new Id3Chapter(Encoding.Latin1.GetString(data, 0, nul), BinaryPrimitives.ReadUInt32BigEndian(f), BinaryPrimitives.ReadUInt32BigEndian(f[4..]), BinaryPrimitives.ReadUInt32BigEndian(f[8..]), BinaryPrimitives.ReadUInt32BigEndian(f[12..]), subs);
    }

    private void ReadTableOfContents(Id3Frame frame, byte[] data, long fileOffset)
    {
        // Chapter frame addendum §3.2: element ID, flags (top level = bit 1, ordered = bit 0), entry count, child IDs (each NUL-terminated), sub-frames.
        int nul = Array.IndexOf(data, (byte)0);
        if (nul < 0 || data.Length < nul + 3)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, fileOffset, "A CTOC frame is too short for its fixed fields.", "id3.ctoc");
            return;
        }

        int flags = data[nul + 1];
        int count = data[nul + 2];
        int p = nul + 3;
        List<string> children = [];
        for (int i = 0; i < count && p < data.Length; i++)
        {
            int end = Array.IndexOf(data, (byte)0, p);
            if (end < 0)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, fileOffset + p, "A CTOC child identifier is not terminated.", "id3.ctoc");
                p = data.Length;
                break;
            }

            children.Add(Encoding.Latin1.GetString(data, p, end - p));
            p = end + 1;
        }

        List<Id3Frame> subs = [];
        ReadSubFrames(data, p, fileOffset, subs);
        frame.TableOfContents = new Id3TableOfContents(Encoding.Latin1.GetString(data, 0, nul), (flags & 2) != 0, (flags & 1) != 0, children, subs);
    }

    private void ReadSubFrames(byte[] data, int start, long fileOffset, List<Id3Frame> into)
    {
        if (start >= data.Length)
        {
            return;
        }

        byte[] rest = data.AsSpan(start).ToArray();
        Id3Body inner = Id3Body.Memory(rest, fileOffset + start);
        Id3FrameReader reader = new(io, major, false, true);
        reader.ReadFrames(inner, 0, into, null);
    }
}
