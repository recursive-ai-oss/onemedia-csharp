namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>A teletext page address: magazine 1 to 8 and the two page digits.</summary>
/// <param name="Magazine">The magazine, 1 to 8.</param>
/// <param name="Number">The page number as two hex digits, tens in the high nibble: page 777 is magazine 7, number 0x77.</param>
public readonly record struct TeletextPageAddress(int Magazine, int Number)
{
    /// <summary>Parses "777" style page numbers (a magazine digit 1 to 8, then two hex digits).</summary>
    /// <param name="text">Three characters.</param>
    /// <param name="address">The address.</param>
    /// <returns>False when <paramref name="text"/> is not a page number.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out TeletextPageAddress address)
    {
        address = default;
        if (text.Length != 3 || text[0] is < '1' or > '8')
        {
            return false;
        }

        if (!int.TryParse(text[1..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out int n))
        {
            return false;
        }

        address = new TeletextPageAddress(text[0] - '0', n);
        return true;
    }

    /// <summary>The "777" form.</summary>
    public override string ToString() => $"{Magazine}{Number:X2}";
}

/// <summary>Settings for <see cref="TeletextPageDecoder"/>.</summary>
public sealed record TeletextOptions
{
    /// <summary>
    /// The pages to decode. Null or empty (the default) decodes every page whose header sets the subtitle bit C6, which is how
    /// broadcasters mark subtitle pages.
    /// </summary>
    public IReadOnlyList<TeletextPageAddress>? Pages { get; init; }

    /// <summary>True to decode every page, subtitle or not. Ignored when <see cref="Pages"/> is set. Default false.</summary>
    public bool AllPages { get; init; }

    /// <summary>The most queued, not yet taken pages the decoder holds before it drops the oldest and reports a defect. Default 4096.</summary>
    public int MaxQueuedPages { get; init; } = 4096;

    /// <summary>
    /// True to give every transmission of a subtitle page its own cue. The default, false, merges the word-by-word updates of live
    /// subtitles (each transmission repeats the lines so far with more text) into one cue with the final text.
    /// </summary>
    public bool Progressive { get; init; }

    /// <summary>
    /// The character set to assume for a page when the stream designates none (no X/28/0 or M/29/0) and the header's C12 to C14 are all
    /// zero, which also means "English". <see cref="TeletextSubtitleReader"/> fills this from the PMT language of each page.
    /// </summary>
    public IReadOnlyDictionary<TeletextPageAddress, TeletextCharacterSet>? FallbackCharacterSets { get; init; }

    /// <summary>The same assumption for pages not listed in <see cref="FallbackCharacterSets"/> (a stream whose descriptor names one language).</summary>
    public TeletextCharacterSet? FallbackCharacterSet { get; init; }

    /// <summary>How long the last subtitle of a stream stays up when nothing ends it. Default 3 seconds.</summary>
    public MediaTime LastCueDuration { get; init; } = MediaTime.FromSeconds(3);
}

/// <summary>
/// Assembles teletext packets (ETS 300 706) into pages. Feed it the packets of one teletext stream in order with their presentation
/// times, take the finished pages. A page ends at the next page header of its magazine (parallel mode) or of any magazine (serial
/// mode, header bit C11), or when <see cref="Flush"/> is called. Not thread-safe.
/// </summary>
public sealed class TeletextPageDecoder
{
    private readonly TeletextOptions _options;
    private readonly DefectLog? _defects;
    private readonly Queue<TeletextPage> _ready = new();
    private readonly PageBuilder?[] _current = new PageBuilder?[8];
    private readonly TeletextCharacterSet?[] _magazineDefault = new TeletextCharacterSet?[8];
    private int _activeMagazine = -1;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Page selection; null decodes subtitle pages.</param>
    /// <param name="defects">Where decoding problems are reported, or null to ignore them.</param>
    public TeletextPageDecoder(TeletextOptions? options = null, DefectLog? defects = null)
    {
        _options = options ?? new TeletextOptions();
        _defects = defects;
    }

    /// <summary>Number of corrupt bytes seen (failed parity or uncorrectable Hamming codes).</summary>
    public long ErrorCount { get; private set; }

    /// <summary>Number of pages waiting to be taken.</summary>
    public int Pending => _ready.Count;

    /// <summary>
    /// Feeds one teletext packet.
    /// </summary>
    /// <param name="magazine">The magazine bits of the packet address, 0 to 7 (0 is magazine 8).</param>
    /// <param name="row">The packet number (row) 0 to 31.</param>
    /// <param name="data">The 40 data bytes (bytes 6 to 45 of the teletext packet) in numeric bit order. Shorter spans are ignored.</param>
    /// <param name="time">The presentation time of the packet.</param>
    /// <param name="offset">Byte offset of the packet for defect reports, or -1.</param>
    public void Push(int magazine, int row, ReadOnlySpan<byte> data, MediaTime time, long offset = -1)
    {
        if ((uint)magazine > 7 || (uint)row > 31 || data.Length < 40)
        {
            return;
        }

        if (row == 0)
        {
            StartPage(magazine, data, time, offset);
            return;
        }

        if (row == 29)
        {
            ReadMagazineDesignation(magazine, data);
            return;
        }

        PageBuilder? b = _current[magazine];
        if (b is null)
        {
            return;
        }

        if (row <= 24)
        {
            data[..40].CopyTo(b.Raw.AsSpan(row * 40, 40));
            b.RowPresent[row] = true;
        }
        else if (row == 26)
        {
            if (b.Enhancement.Count < 16)
            {
                b.Enhancement.Add(data[..40].ToArray());
            }
        }
        else if (row == 28)
        {
            ReadPageDesignation(b, data);
        }
    }

    /// <summary>Ends every page being assembled, as if the stream stopped at <paramref name="time"/>.</summary>
    /// <param name="time">The time to record as the end of those pages.</param>
    public void Flush(MediaTime time)
    {
        for (int m = 0; m < 8; m++)
        {
            Finish(m, time);
        }
    }

    /// <summary>Takes the oldest finished page.</summary>
    /// <param name="page">The page.</param>
    /// <returns>False when none is waiting.</returns>
    public bool TryDequeue(out TeletextPage page) => _ready.TryDequeue(out page!);

    private void StartPage(int magazine, ReadOnlySpan<byte> data, MediaTime time, long offset)
    {
        int units = TeletextCodes.DecodeHamming84(data[0]);
        int tens = TeletextCodes.DecodeHamming84(data[1]);
        int s1 = TeletextCodes.DecodeHamming84(data[2]);
        int s2c4 = TeletextCodes.DecodeHamming84(data[3]);
        int s3 = TeletextCodes.DecodeHamming84(data[4]);
        int s4c56 = TeletextCodes.DecodeHamming84(data[5]);
        int c7to10 = TeletextCodes.DecodeHamming84(data[6]);
        int c11to14 = TeletextCodes.DecodeHamming84(data[7]);
        if ((units | tens | s1 | s2c4 | s3 | s4c56 | c7to10 | c11to14) < 0)
        {
            // An uncorrectable header cannot say which page it starts, so the page being assembled is closed and the rest is skipped.
            ErrorCount++;
            _defects?.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "Teletext page header has an uncorrectable Hamming 8/4 byte; the page is skipped.", "teletext.header-hamming");
            Finish(magazine, time);
            return;
        }

        int number = (tens << 4) | units;
        TeletextControlBits flags = TeletextControlBits.None;
        if ((s2c4 & 8) != 0)
        {
            flags |= TeletextControlBits.Erase;
        }

        if ((s4c56 & 4) != 0)
        {
            flags |= TeletextControlBits.Newsflash;
        }

        if ((s4c56 & 8) != 0)
        {
            flags |= TeletextControlBits.Subtitle;
        }

        if ((c7to10 & 1) != 0)
        {
            flags |= TeletextControlBits.SuppressHeader;
        }

        if ((c7to10 & 2) != 0)
        {
            flags |= TeletextControlBits.Update;
        }

        if ((c7to10 & 4) != 0)
        {
            flags |= TeletextControlBits.InterruptedSequence;
        }

        if ((c7to10 & 8) != 0)
        {
            flags |= TeletextControlBits.InhibitDisplay;
        }

        if ((c11to14 & 1) != 0)
        {
            flags |= TeletextControlBits.Serial;
        }

        // ETS 300 706 §7.2.1: a page ends at the next header of its magazine in parallel mode, or at any header in serial mode.
        if ((flags & TeletextControlBits.Serial) != 0)
        {
            for (int m = 0; m < 8; m++)
            {
                Finish(m, time);
            }
        }
        else
        {
            Finish(magazine, time);
        }

        // §7.3: page number FF is the "time filling header" that only closes the previous page.
        if (number == 0xFF)
        {
            return;
        }

        int mag = magazine == 0 ? 8 : magazine;
        if (!Wanted(mag, number, flags))
        {
            return;
        }

        int subcode = s1 | ((s2c4 & 7) << 4) | (s3 << 8) | ((s4c56 & 3) << 12);
        int nationalBits = c11to14 >> 1; // C12 in bit 0, C13 in bit 1, C14 in bit 2

        PageBuilder b = new(mag, number, subcode, flags, nationalBits, time, offset);
        data.Slice(8, 32).CopyTo(b.Raw.AsSpan(8, 32));
        b.RowPresent[0] = true;
        _current[magazine] = b;
        _activeMagazine = magazine;
    }

    private bool Wanted(int magazine, int number, TeletextControlBits flags)
    {
        if (_options.Pages is { Count: > 0 } pages)
        {
            foreach (TeletextPageAddress p in pages)
            {
                if (p.Magazine == magazine && p.Number == number)
                {
                    return true;
                }
            }

            return false;
        }

        return _options.AllPages || (flags & TeletextControlBits.Subtitle) != 0;
    }

    private void Finish(int magazine, MediaTime time)
    {
        PageBuilder? b = _current[magazine];
        if (b is null)
        {
            return;
        }

        _current[magazine] = null;
        if (_ready.Count >= _options.MaxQueuedPages)
        {
            _ready.Dequeue();
            _defects?.Add(DefectKind.LimitReached, DefectSeverity.Warning, b.Offset, "Teletext pages were not taken fast enough; the oldest was dropped.", "teletext.queue-full");
        }

        _ready.Enqueue(b.Render(_magazineDefault[magazine], time, this, _options));
    }

    private void ReadMagazineDesignation(int magazine, ReadOnlySpan<byte> data)
    {
        // ETS 300 706 §9.5.1: M/29/0 carries the same designation as X/28/0 format 1 for the whole magazine.
        if (TeletextCodes.DecodeHamming84(data[0]) != 0)
        {
            return;
        }

        int t1 = TeletextCodes.DecodeHamming2418(data[1], data[2], data[3]);
        if (t1 >= 0 && (t1 & 0x7F) == 0)
        {
            _magazineDefault[magazine] = TeletextCharacterSet.FromDesignation((t1 >> 7) & 0x7F);
        }
    }

    private static void ReadPageDesignation(PageBuilder b, ReadOnlySpan<byte> data)
    {
        // §9.4.2: X/28/0 format 1, triplet 1: bits 1-4 page function (0 = basic level 1 page), bits 5-7 coding, bits 8-14 designation.
        if (TeletextCodes.DecodeHamming84(data[0]) != 0)
        {
            return;
        }

        int t1 = TeletextCodes.DecodeHamming2418(data[1], data[2], data[3]);
        if (t1 >= 0 && (t1 & 0x7F) == 0)
        {
            b.Designation = TeletextCharacterSet.FromDesignation((t1 >> 7) & 0x7F);
        }
    }

    internal void CountError() => ErrorCount++;

    internal sealed class PageBuilder
    {
        public readonly byte[] Raw = new byte[TeletextPage.Rows * 40];
        public readonly bool[] RowPresent = new bool[TeletextPage.Rows];
        public readonly List<byte[]> Enhancement = [];

        public PageBuilder(int magazine, int number, int subcode, TeletextControlBits flags, int nationalBits, MediaTime time, long offset)
        {
            Magazine = magazine;
            Number = number;
            Subcode = subcode;
            Flags = flags;
            NationalBits = nationalBits;
            Time = time;
            Offset = offset;
        }

        public int Magazine { get; }

        public int Number { get; }

        public int Subcode { get; }

        public TeletextControlBits Flags { get; }

        public int NationalBits { get; }

        public MediaTime Time { get; }

        public long Offset { get; }

        public TeletextCharacterSet? Designation { get; set; }

        public TeletextPage Render(TeletextCharacterSet? magazineDefault, MediaTime completedAt, TeletextPageDecoder owner, TeletextOptions options)
            => TeletextRenderer.Render(this, magazineDefault, completedAt, owner, options);
    }
}
