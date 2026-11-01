using System.Text;

namespace RecursiveAI.OneMedia.Tags.Id3;

/// <summary>
/// Decodes the data of one ID3v2 frame into a <see cref="NativeTag"/> (id3v2.4.0-frames §4 for the layouts, id3v2.3.0 §4 and id3v2-00 §4 for the
/// older identifiers). Anything it does not understand is left to the caller, which keeps the frame as an opaque binary tag.
/// </summary>
internal sealed class Id3FrameDecoder(TagIo io, int major, Id3Frame frame, long dataOffset, long dataLength, byte[]? loaderData, bool prefixOnly)
{
    // id3v2-00 §4: the three-character identifiers of 2.2 and the four-character ones that replaced them in 2.3.
    private static readonly Dictionary<string, string> s_v22 = new()
    {
        ["BUF"] = "RBUF", ["CNT"] = "PCNT", ["COM"] = "COMM", ["CRA"] = "AENC", ["CRM"] = "CRM", ["ETC"] = "ETCO", ["EQU"] = "EQUA", ["GEO"] = "GEOB",
        ["IPL"] = "IPLS", ["LNK"] = "LINK", ["MCI"] = "MCDI", ["MLL"] = "MLLT", ["PIC"] = "APIC", ["POP"] = "POPM", ["REV"] = "RVRB", ["RVA"] = "RVAD",
        ["SLT"] = "SYLT", ["STC"] = "SYTC", ["TAL"] = "TALB", ["TBP"] = "TBPM", ["TCM"] = "TCOM", ["TCO"] = "TCON", ["TCR"] = "TCOP", ["TDA"] = "TDAT",
        ["TDY"] = "TDLY", ["TEN"] = "TENC", ["TFT"] = "TFLT", ["TIM"] = "TIME", ["TKE"] = "TKEY", ["TLA"] = "TLAN", ["TLE"] = "TLEN", ["TMT"] = "TMED",
        ["TOA"] = "TOPE", ["TOF"] = "TOFN", ["TOL"] = "TOLY", ["TOR"] = "TORY", ["TOT"] = "TOAL", ["TP1"] = "TPE1", ["TP2"] = "TPE2", ["TP3"] = "TPE3",
        ["TP4"] = "TPE4", ["TPA"] = "TPOS", ["TPB"] = "TPUB", ["TRC"] = "TSRC", ["TRD"] = "TRDA", ["TRK"] = "TRCK", ["TSI"] = "TSIZ", ["TSS"] = "TSSE",
        ["TT1"] = "TIT1", ["TT2"] = "TIT2", ["TT3"] = "TIT3", ["TXT"] = "TEXT", ["TXX"] = "TXXX", ["TYE"] = "TYER", ["UFI"] = "UFID", ["ULT"] = "USLT",
        ["WAF"] = "WOAF", ["WAR"] = "WOAR", ["WAS"] = "WOAS", ["WCM"] = "WCOM", ["WCP"] = "WCOP", ["WPB"] = "WPUB", ["WXX"] = "WXXX",
        // iTunes' additions to 2.2.
        ["TCP"] = "TCMP", ["TST"] = "TSOT", ["TSA"] = "TSOA", ["TSP"] = "TSOP", ["TS2"] = "TSO2", ["TSC"] = "TSOC", ["PCS"] = "PCST", ["GP1"] = "GRP1", ["MVN"] = "MVNM", ["MVI"] = "MVIN",
    };

    public void Decode(ReadOnlySpan<byte> d)
    {
        string id = major == 2 && s_v22.TryGetValue(frame.Id, out string? c) ? c : frame.Id;
        if (d.IsEmpty)
        {
            io.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, frame.DataRange.Offset, $"The ID3v2 frame '{frame.Id}' has no data.", "id3.empty-frame");
            return;
        }

        switch (id)
        {
            case "TXXX":
                DescribedText(d, "TXXX", terminatedUrl: false, alwaysColon: true);
                return;
            case "WXXX":
                DescribedText(d, "WXXX", terminatedUrl: true, alwaysColon: true);
                return;
            case "COMM" or "USLT":
                Comment(d, id);
                return;
            case "APIC":
                Picture(d);
                return;
            case "POPM":
                Popularimeter(d);
                return;
            case "PCNT":
                Counter(d);
                return;
            case "UFID" or "PRIV":
                Owned(d);
                return;
            case "GEOB":
                GeneralObject(d);
                return;
            case "SYLT":
                SynchronisedLyrics(d);
                return;
            case "CHAP" or "CTOC":
                ElementFrame(d, id);
                return;
        }

        if (id[0] == 'T' || id == "IPLS")
        {
            TextFrame(d);
        }
        else if (id[0] == 'W')
        {
            Url(d);
        }
    }

    private static bool IsFilled(ReadOnlySpan<byte> d, int need) => d.Length >= need;

    private NativeTag Make(string key, TagValueKind kind, long valueStart, long valueLength, TagTextEncoding encoding = TagTextEncoding.Latin1, string? language = null)
    {
        ByteRange valueRange = loaderData is not null ? frame.DataRange : new ByteRange(dataOffset + valueStart, valueLength);
        Func<byte[]>? loader = loaderData is null ? null : () => loaderData.AsSpan((int)valueStart, (int)Math.Min(valueLength, loaderData.Length - valueStart)).ToArray();
        NativeTag tag = new(io, TagSystems.Id3v2, key, 0, kind, kind == TagValueKind.Text ? 1u : 0u, 0, language, frame.Range, valueRange)
        {
            Encoding = encoding,
            Loader = kind == TagValueKind.Text ? null : loader,
        };
        frame.Native = tag;
        return tag;
    }

    private static TagTextEncoding EncodingOf(int enc) => enc switch { 1 => TagTextEncoding.Utf16WithByteOrderMark, 2 => TagTextEncoding.Utf16BigEndian, 3 => TagTextEncoding.Utf8, _ => TagTextEncoding.Latin1 };

    private int ReadEncoding(byte b)
    {
        // id3v2.4.0-structure §4: 0 ISO-8859-1, 1 UTF-16 with a byte order mark, 2 UTF-16BE (2.4), 3 UTF-8 (2.4).
        if (b > 3 || (major < 4 && b > 1))
        {
            io.Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, frame.DataRange.Offset, $"The ID3v2 frame '{frame.Id}' names text encoding {b}, which this tag version does not define; ISO-8859-1 is assumed.", "id3.text-encoding");
            return b > 3 ? 0 : b;
        }

        return b;
    }

    // Finds the end of the string that starts at `from`: a single zero byte for the one-byte encodings, an aligned pair of zeros for UTF-16.
    private static int FindTerminator(ReadOnlySpan<byte> d, int from, int enc)
    {
        if (enc is 1 or 2)
        {
            for (int i = from; i + 1 < d.Length; i += 2)
            {
                if (d[i] == 0 && d[i + 1] == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        int z = d[from..].IndexOf((byte)0);
        return z < 0 ? -1 : from + z;
    }

    private static int TerminatorLength(int enc) => enc is 1 or 2 ? 2 : 1;

    private string DecodeString(ReadOnlySpan<byte> bytes, int enc)
    {
        switch (enc)
        {
            case 1:
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    return Encoding.Unicode.GetString(bytes[2..]);
                }

                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    return Encoding.BigEndianUnicode.GetString(bytes[2..]);
                }

                if (bytes.Length > 0)
                {
                    io.Defects.Add(DefectKind.MissingRequired, DefectSeverity.Info, frame.DataRange.Offset, $"UTF-16 text in '{frame.Id}' has no byte order mark; little-endian is assumed.", "id3.utf16-bom");
                }

                return Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);
            case 2:
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    bytes = bytes[2..];
                }

                return Encoding.BigEndianUnicode.GetString(bytes[..(bytes.Length & ~1)]);
            case 3:
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    bytes = bytes[3..];
                }

                return Encoding.UTF8.GetString(bytes);
            default:
                return Encoding.Latin1.GetString(bytes);
        }
    }

    private List<string> SplitStrings(ReadOnlySpan<byte> bytes, int enc)
    {
        List<string> list = [];
        int p = 0;
        int step = TerminatorLength(enc);
        while (p <= bytes.Length)
        {
            int t = FindTerminator(bytes, p, enc);
            if (t < 0)
            {
                if (p < bytes.Length)
                {
                    list.Add(DecodeString(bytes[p..], enc));
                }

                break;
            }

            list.Add(DecodeString(bytes[p..t], enc));
            p = t + step;
            if (p >= bytes.Length)
            {
                break;
            }
        }

        if (list.Count == 0)
        {
            list.Add(string.Empty);
        }

        return list;
    }

    private void TextFrame(ReadOnlySpan<byte> d)
    {
        if (prefixOnly)
        {
            io.Defects.Add(DefectKind.LimitReached, DefectSeverity.Info, frame.DataRange.Offset, $"The text frame '{frame.Id}' is larger than {d.Length} bytes; it is kept undecoded.", "id3.value-too-large");
            return;
        }

        int enc = ReadEncoding(d[0]);
        List<string> values = SplitStrings(d[1..], enc);
        frame.Values = values;
        NativeTag tag = Make(frame.Id, TagValueKind.Text, 1, dataLength - 1, EncodingOf(enc));
        tag.SetText(string.Join("; ", values));
    }

    private void Url(ReadOnlySpan<byte> d)
    {
        if (prefixOnly)
        {
            return;
        }

        int z = d.IndexOf((byte)0);
        ReadOnlySpan<byte> url = z >= 0 ? d[..z] : d;
        frame.Values = [Encoding.Latin1.GetString(url)];
        Make(frame.Id, TagValueKind.Text, 0, url.Length).SetText(frame.Values[0]);
    }

    private void DescribedText(ReadOnlySpan<byte> d, string baseKey, bool terminatedUrl, bool alwaysColon)
    {
        if (prefixOnly)
        {
            return;
        }

        int enc = ReadEncoding(d[0]);
        int t = FindTerminator(d, 1, enc);
        if (t < 0)
        {
            io.Defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, frame.DataRange.Offset, $"The description in '{frame.Id}' is not terminated.", "id3.description");
            return;
        }

        string description = DecodeString(d[1..t], enc);
        int start = t + TerminatorLength(enc);
        string key = (frame.Id.Length == 3 ? frame.Id : baseKey) + (alwaysColon || description.Length > 0 ? ":" + description : string.Empty);
        if (terminatedUrl)
        {
            ReadOnlySpan<byte> url = d[start..];
            int z = url.IndexOf((byte)0);
            string text = Encoding.Latin1.GetString(z >= 0 ? url[..z] : url);
            frame.Values = [text];
            NativeTag w = Make(key, TagValueKind.Text, start, url.Length);
            w.SetText(text);
            w.Description = description;
            return;
        }

        List<string> values = SplitStrings(d[start..], enc);
        frame.Values = values;
        NativeTag tag = Make(key, TagValueKind.Text, start, dataLength - start, EncodingOf(enc));
        tag.SetText(string.Join("; ", values));
        tag.Description = description;
    }

    private static string? LanguageOf(ReadOnlySpan<byte> b)
    {
        // ISO 639-2 code; "XXX" (and blanks) mean the writer did not know.
        foreach (byte c in b)
        {
            if (!(c is >= (byte)'a' and <= (byte)'z' || c is >= (byte)'A' and <= (byte)'Z'))
            {
                return null;
            }
        }

        string s = Encoding.ASCII.GetString(b).ToLowerInvariant();
        return s == "xxx" ? null : s;
    }

    private void Comment(ReadOnlySpan<byte> d, string id)
    {
        if (prefixOnly || !IsFilled(d, 5))
        {
            return;
        }

        int enc = ReadEncoding(d[0]);
        string? lang = LanguageOf(d.Slice(1, 3));
        int t = FindTerminator(d, 4, enc);
        if (t < 0)
        {
            io.Defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, frame.DataRange.Offset, $"The description in '{frame.Id}' is not terminated.", "id3.description");
            return;
        }

        string description = DecodeString(d[4..t], enc);
        int start = t + TerminatorLength(enc);
        // The text of a comment or lyrics is one string (it may hold line breaks and, in 2.4, is not split on zeros).
        string text = DecodeString(d[start..], enc).TrimEnd('\0');
        frame.Values = [text];
        string key = frame.Id + (description.Length > 0 ? ":" + description : string.Empty);
        NativeTag tag = Make(key, TagValueKind.Text, start, dataLength - start, EncodingOf(enc), lang);
        tag.SetText(text);
        tag.Description = description;
    }

    private void SynchronisedLyrics(ReadOnlySpan<byte> d)
    {
        if (prefixOnly || !IsFilled(d, 7))
        {
            return;
        }

        int enc = ReadEncoding(d[0]);
        string? lang = LanguageOf(d.Slice(1, 3));
        int t = FindTerminator(d, 6, enc);
        string description = t < 0 ? string.Empty : DecodeString(d[6..t], enc);
        NativeTag tag = Make(frame.Id + (description.Length > 0 ? ":" + description : string.Empty), TagValueKind.Binary, 0, dataLength, TagTextEncoding.Latin1, lang);
        tag.Description = description;
    }

    private void Picture(ReadOnlySpan<byte> d)
    {
        // APIC (id3v2.4.0-frames §4.14): encoding, MIME type (latin-1, zero-terminated), picture type, description, picture data.
        // PIC (2.2): encoding, a three-character image format, picture type, description, picture data.
        int enc = ReadEncoding(d[0]);
        int p = 1;
        string mime;
        if (frame.Id.Length == 3)
        {
            if (d.Length < 5)
            {
                return;
            }

            string format = Encoding.Latin1.GetString(d.Slice(1, 3)).ToUpperInvariant();
            mime = format switch { "JPG" => "image/jpeg", "PNG" => "image/png", "GIF" => "image/gif", "BMP" => "image/bmp", "-->" => "-->", _ => "image/" + format.ToLowerInvariant() };
            p = 4;
        }
        else
        {
            int z = d[1..].IndexOf((byte)0);
            if (z < 0)
            {
                io.Defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, frame.DataRange.Offset, "The MIME type of an attached picture is not terminated.", "id3.apic");
                return;
            }

            mime = Encoding.Latin1.GetString(d.Slice(1, z)).Trim();
            p = 1 + z + 1;
        }

        if (p >= d.Length)
        {
            return;
        }

        int pictureType = d[p++];
        int t = FindTerminator(d, p, enc);
        if (t < 0)
        {
            io.Defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, frame.DataRange.Offset, "The description of an attached picture is not terminated.", "id3.apic");
            return;
        }

        string description = DecodeString(d[p..t], enc);
        int start = t + TerminatorLength(enc);
        if (pictureType > 20)
        {
            io.Defects.Add(DefectKind.ReservedValue, DefectSeverity.Info, frame.DataRange.Offset, $"The attached picture has type {pictureType}, which is not in the list; it is read as 'Other'.", "id3.picture-type");
            pictureType = 0;
        }

        if (mime is "" or "-->" || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            mime = mime == "-->" ? "-->" : Sniff(d[Math.Min(start, d.Length)..]) ?? (mime.Length > 0 ? mime : "application/octet-stream");
        }
        else if (mime.Equals("image/jpg", StringComparison.OrdinalIgnoreCase))
        {
            mime = "image/jpeg";
        }

        NativeTag tag = Make(frame.Id + (description.Length > 0 ? ":" + description : string.Empty), TagValueKind.Image, start, Math.Max(0, dataLength - start), TagTextEncoding.Latin1);
        tag.MimeType = mime;
        tag.PictureRole = (PictureType)pictureType;
        tag.Description = description;
    }

    private static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }

        if (b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
        {
            return "image/png";
        }

        if (b.StartsWith("GIF8"u8))
        {
            return "image/gif";
        }

        return b.StartsWith("BM"u8) ? "image/bmp" : null;
    }

    private void Popularimeter(ReadOnlySpan<byte> d)
    {
        // POPM (id3v2.4.0-frames §4.17): email (latin-1, zero-terminated), rating (1 byte), play counter (4 bytes or more).
        int z = d.IndexOf((byte)0);
        if (z < 0 || z + 1 >= d.Length)
        {
            return;
        }

        string email = Encoding.Latin1.GetString(d[..z]);
        NativeTag tag = Make(frame.Id + ":" + email, TagValueKind.Number, 0, dataLength);
        tag.Number = d[z + 1];
        tag.Description = email;
    }

    private void Counter(ReadOnlySpan<byte> d)
    {
        // PCNT: a big-endian counter of at least four bytes.
        if (d.Length is < 4 or > 8)
        {
            return;
        }

        ulong n = 0;
        foreach (byte b in d)
        {
            n = (n << 8) | b;
        }

        Make(frame.Id, TagValueKind.Number, 0, dataLength).Number = (long)Math.Min(n, long.MaxValue);
    }

    private void Owned(ReadOnlySpan<byte> d)
    {
        // UFID and PRIV: an owner identifier (latin-1, zero-terminated), then the identifier or private data.
        int z = d.IndexOf((byte)0);
        if (z < 0)
        {
            return;
        }

        string owner = Encoding.Latin1.GetString(d[..z]);
        Make(frame.Id + ":" + owner, TagValueKind.Binary, z + 1, Math.Max(0, dataLength - z - 1)).Description = owner;
    }

    private void GeneralObject(ReadOnlySpan<byte> d)
    {
        // GEOB: encoding, MIME type (latin-1), file name, description (both in the encoding), object data.
        if (prefixOnly || d.Length < 2)
        {
            return;
        }

        int enc = ReadEncoding(d[0]);
        int z = d[1..].IndexOf((byte)0);
        if (z < 0)
        {
            return;
        }

        string mime = Encoding.Latin1.GetString(d.Slice(1, z));
        int p = 1 + z + 1;
        int f = FindTerminator(d, p, enc);
        if (f < 0)
        {
            return;
        }

        int q = f + TerminatorLength(enc);
        int t = FindTerminator(d, q, enc);
        if (t < 0)
        {
            return;
        }

        string description = DecodeString(d[q..t], enc);
        int start = t + TerminatorLength(enc);
        NativeTag tag = Make(frame.Id + (description.Length > 0 ? ":" + description : string.Empty), TagValueKind.Binary, start, Math.Max(0, dataLength - start));
        tag.MimeType = mime.Length > 0 ? mime : null;
        tag.Description = description;
    }

    private void ElementFrame(ReadOnlySpan<byte> d, string id)
    {
        // CHAP and CTOC start with a latin-1 element identifier; the whole frame is the value, the chapter model comes from the reader.
        int z = d.IndexOf((byte)0);
        if (z < 0)
        {
            return;
        }

        Make(frame.Id + ":" + Encoding.Latin1.GetString(d[..z]), TagValueKind.Binary, 0, dataLength).Description = id;
    }
}
