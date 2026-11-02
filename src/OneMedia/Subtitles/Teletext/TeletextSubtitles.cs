using System.Text;

namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>What a teletext page lost when it became cues: things the cue model has no member for (ETS 300 706 mosaics, flashing, concealment).</summary>
public sealed class TeletextConversionReport
{
    /// <summary>Pages turned into cues or clears.</summary>
    public int Pages { get; internal set; }

    /// <summary>Boxed block-mosaic cells dropped (they read as spaces).</summary>
    public int MosaicCells { get; internal set; }

    /// <summary>Boxed cells that flash; they are shown steady.</summary>
    public int FlashCells { get; internal set; }

    /// <summary>Boxed cells concealed by the page; they are shown as spaces.</summary>
    public int ConcealedCells { get; internal set; }

    /// <summary>Rows with double-height or double-width text; carried as an approximate font size.</summary>
    public int DoubleSizeRows { get; internal set; }

    /// <summary>Cells with a teletext colour approximated by an RGB value (every coloured cell: teletext colours are the eight fixed ones).</summary>
    public int ColouredCells { get; internal set; }

    /// <summary>Characters that have no mapping in the character set and came out as U+FFFD.</summary>
    public int UnmappedCharacters { get; internal set; }

    /// <summary>True when nothing was dropped or approximated.</summary>
    public bool IsLossless => MosaicCells == 0 && FlashCells == 0 && ConcealedCells == 0 && DoubleSizeRows == 0 && UnmappedCharacters == 0;
}

/// <summary>The cues of one teletext subtitle page.</summary>
/// <param name="Page">The page address.</param>
/// <param name="Language">ISO 639-2 language from the PMT descriptor that announces this page, or null.</param>
/// <param name="Declared">True when a PMT teletext descriptor announced the page as a subtitle page.</param>
/// <param name="Document">The cues, in time order.</param>
/// <param name="Report">What the conversion dropped or approximated.</param>
/// <param name="Pages">Every transmission of the page, in time order: the page model the cues were made from.</param>
public sealed record TeletextSubtitleTrack(TeletextPageAddress Page, string? Language, bool Declared, SubtitleDocument Document, TeletextConversionReport Report, IReadOnlyList<TeletextPage> Pages);

/// <summary>
/// Turns the PES packets of a teletext stream into subtitle cues. A page is shown from its header until the next transmission of the
/// same page (or the packet that ended it); a transmission with no visible text clears the screen.
/// </summary>
public sealed class TeletextSubtitleReader
{
    private readonly TeletextPageDecoder _decoder;
    private readonly IReadOnlyList<TeletextService> _services;
    private readonly Dictionary<TeletextPageAddress, List<TeletextPage>> _pages = [];
    private readonly List<TeletextPageAddress> _order = [];
    private readonly TeletextOptions _options;

    /// <summary>Creates a reader.</summary>
    /// <param name="options">Page selection; by default every page that sets the subtitle bit C6, plus the pages <paramref name="services"/> announce as subtitles.</param>
    /// <param name="services">The pages the stream's PMT descriptor announces, or null.</param>
    /// <param name="defects">Where problems are reported, or null.</param>
    public TeletextSubtitleReader(TeletextOptions? options = null, IReadOnlyList<TeletextService>? services = null, DefectLog? defects = null)
    {
        _services = services ?? [];
        _options = options ?? new TeletextOptions();
        Defects = defects;

        if (_options.Pages is not { Count: > 0 } && !_options.AllPages)
        {
            // Subtitle pages are the ones that set C6; a page the PMT calls a subtitle page counts even when its header forgets the bit.
            List<TeletextPageAddress> pages = [];
            foreach (TeletextService s in _services)
            {
                if (s.IsSubtitle)
                {
                    pages.Add(s.Page);
                }
            }

            _decoder = new TeletextPageDecoder(WithFallbacks(_options with { Pages = null, AllPages = false }, _services), defects);
            _announced = pages;
        }
        else
        {
            _decoder = new TeletextPageDecoder(WithFallbacks(_options, _services), defects);
            _announced = [];
        }
    }

    private readonly List<TeletextPageAddress> _announced;

    // Streams that carry no X/28/0 or M/29/0 designation and a header with C12 to C14 all zero read as English; where the PMT announces a
    // language for the page, that language's usual set replaces the guess.
    private static TeletextOptions WithFallbacks(TeletextOptions options, IReadOnlyList<TeletextService> services)
    {
        Dictionary<TeletextPageAddress, TeletextCharacterSet> map = [];
        string? only = null;
        bool single = true;
        foreach (TeletextService s in services)
        {
            if (s.Language is null)
            {
                continue;
            }

            if (only is not null && only != s.Language)
            {
                single = false;
            }

            only ??= s.Language;
            if (TeletextCharacterSet.ForLanguage(s.Language) is { } set)
            {
                map[s.Page] = set;
            }
        }

        return options with
        {
            FallbackCharacterSets = map.Count == 0 ? null : map,
            FallbackCharacterSet = single && only is not null ? TeletextCharacterSet.ForLanguage(only) : null,
        };
    }

    /// <summary>Where problems are reported.</summary>
    public DefectLog? Defects { get; }

    /// <summary>
    /// Feeds one PES packet's data bytes.
    /// </summary>
    /// <param name="payload">The PES payload (data_identifier and data units).</param>
    /// <param name="pts">The PES presentation time.</param>
    /// <param name="offset">The source offset, or -1.</param>
    public void Push(ReadOnlySpan<byte> payload, MediaTime pts, long offset = -1)
    {
        TeletextPes.Push(payload, _decoder, pts, offset, Defects);
        Drain();
    }

    /// <summary>Ends the stream at <paramref name="end"/> and returns the cues per subtitle page, in order of first appearance.</summary>
    /// <param name="end">The end of the stream (the last presentation time in the file, or the end of this track): the last cue of each page lasts until then. When it is not after the cue's start, <see cref="TeletextOptions.LastCueDuration"/> applies.</param>
    public IReadOnlyList<TeletextSubtitleTrack> Finish(MediaTime end)
    {
        _decoder.Flush(end);
        Drain();

        List<TeletextSubtitleTrack> result = [];
        foreach (TeletextPageAddress address in _order)
        {
            List<TeletextPage> pages = _pages[address];
            pages.Sort(static (a, b) => a.Time.CompareTo(b.Time));
            TeletextService? service = null;
            foreach (TeletextService s in _services)
            {
                if (s.Page == address)
                {
                    service = s;
                    break;
                }
            }

            TeletextConversionReport report = new();
            SubtitleDocument doc = TeletextCues.Build(pages, service?.Language, report, end, _options.LastCueDuration, _options.Progressive);
            result.Add(new TeletextSubtitleTrack(address, service?.Language, service?.IsSubtitle == true, doc, report, pages));
        }

        return result;
    }

    private void Drain()
    {
        while (_decoder.TryDequeue(out TeletextPage? page))
        {
            TeletextPageAddress address = new(page.Magazine, page.Number);
            bool keep = _options.Pages is { Count: > 0 } || _options.AllPages
                || (page.Flags & TeletextControlBits.Subtitle) != 0 || _announced.Contains(address);
            if (!keep)
            {
                continue;
            }

            if (!_pages.TryGetValue(address, out List<TeletextPage>? list))
            {
                list = [];
                _pages[address] = list;
                _order.Add(address);
            }

            list.Add(page);
        }
    }
}

/// <summary>Builds cue documents from page transmissions.</summary>
internal static class TeletextCues
{
    public static SubtitleDocument Build(List<TeletextPage> pages, string? language, TeletextConversionReport report, MediaTime streamEnd, MediaTime lastCueDuration, bool progressive)
    {
        List<SubtitleCue> cues = [];
        SubtitleCue? open = null;
        TeletextCaption? openCaption = null;
        MediaTime openStart = default;
        for (int i = 0; i < pages.Count; i++)
        {
            TeletextPage page = pages[i];
            report.Pages++;
            TeletextCaption? caption = Extract(page, report);

            if (open is not null)
            {
                // Live subtitles arrive word by word: each transmission repeats the lines so far with more text. Unless asked for every
                // state, a transmission that only extends what is on screen continues the same cue and replaces its text.
                if (caption is not null && openCaption is not null && (caption.SameAs(openCaption) || (!progressive && caption.Extends(openCaption))))
                {
                    if (!caption.SameAs(openCaption))
                    {
                        open = caption.ToCue(page, language) with { Start = openStart };
                        openCaption = caption;
                    }

                    continue;
                }

                // The new transmission replaces whatever was shown.
                cues.Add(open with { End = page.Time });
                open = null;
                openCaption = null;
            }

            if (caption is null)
            {
                continue;
            }

            open = caption.ToCue(page, language);
            openCaption = caption;
            openStart = page.Time;
        }

        if (open is not null)
        {
            TeletextPage last = pages[^1];
            // A page stays up until something replaces or clears it, so the last cue lasts to the end of the stream.
            MediaTime end = streamEnd.IsKnown && streamEnd > openStart ? streamEnd : last.Time.Add(lastCueDuration, MediaRounding.NearestAwayFromZero);
            cues.Add(open with { End = end });
        }

        return new SubtitleDocument
        {
            Format = "teletext",
            Language = language,
            Cues = SubtitleList.From(cues),
        };
    }

    // The visible text of a subtitle page: boxed cells of rows 1 to 24 (ETS 300 706 §12.2 code 0/B: only the boxed area of a subtitle
    // page is shown), the row below a double-height row skipped (§12.2 code 0/D).
    private static TeletextCaption? Extract(TeletextPage page, TeletextConversionReport report)
    {
        if ((page.Flags & TeletextControlBits.InhibitDisplay) != 0)
        {
            return null;
        }

        bool anyBoxed = false;
        for (int i = TeletextPage.Columns; i < TeletextPage.Rows * TeletextPage.Columns && !anyBoxed; i++)
        {
            anyBoxed = (page.Cells[i].Flags & TeletextCellAttributes.Boxed) != 0 && page.Cells[i].Character != ' ';
        }

        List<TeletextCaptionLine> lines = [];
        bool skip = false;
        for (int row = 1; row < TeletextPage.Rows; row++)
        {
            if (skip)
            {
                skip = false;
                continue;
            }

            TeletextCaptionLine? line = ExtractRow(page, row, boxedOnly: anyBoxed || (page.Flags & (TeletextControlBits.Subtitle | TeletextControlBits.Newsflash)) != 0, report);
            if (line is not null)
            {
                lines.Add(line);
                if (line.DoubleHeight)
                {
                    skip = true;
                    report.DoubleSizeRows++;
                }
            }
        }

        return lines.Count == 0 ? null : new TeletextCaption(lines);
    }

    private static TeletextCaptionLine? ExtractRow(TeletextPage page, int row, bool boxedOnly, TeletextConversionReport report)
    {
        int first = -1, last = -1;
        for (int c = 0; c < TeletextPage.Columns; c++)
        {
            TeletextCell cell = page[row, c];
            if (boxedOnly && (cell.Flags & TeletextCellAttributes.Boxed) == 0)
            {
                continue;
            }

            if (cell.Character != ' ')
            {
                if (first < 0)
                {
                    first = c;
                }

                last = c;
            }
        }

        if (first < 0)
        {
            return null;
        }

        StringBuilder sb = new();
        List<TeletextCell> kept = [];
        bool doubleHeight = false;
        for (int c = first; c <= last; c++)
        {
            TeletextCell cell = page[row, c];
            bool visible = !boxedOnly || (cell.Flags & TeletextCellAttributes.Boxed) != 0;
            char ch = visible ? cell.Character : ' ';
            if (visible)
            {
                if ((cell.Flags & TeletextCellAttributes.Mosaic) != 0)
                {
                    report.MosaicCells++;
                }

                if ((cell.Flags & TeletextCellAttributes.Flash) != 0)
                {
                    report.FlashCells++;
                }

                if ((cell.Flags & TeletextCellAttributes.Conceal) != 0)
                {
                    report.ConcealedCells++;
                    ch = ' ';
                }

                if (ch == '\uFFFD')
                {
                    report.UnmappedCharacters++;
                }

                if ((cell.Flags & TeletextCellAttributes.DoubleHeight) != 0 && ch != ' ')
                {
                    doubleHeight = true;
                }

                if (ch != ' ' && cell.Foreground != TeletextColor.White)
                {
                    report.ColouredCells++;
                }
            }

            sb.Append(ch);
            kept.Add(cell);
        }

        // Runs of equal colours and size over the visible characters; spaces between two characters of one style belong to that run.
        List<TeletextRun> runs = [];
        string text = sb.ToString();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ')
            {
                i++;
                continue;
            }

            TeletextCell start = kept[i];
            int lastSame = i;
            int j = i + 1;
            while (j < text.Length)
            {
                if (text[j] == ' ')
                {
                    j++;
                    continue;
                }

                TeletextCell cj = kept[j];
                if (cj.Foreground == start.Foreground && cj.Background == start.Background
                    && (cj.Flags & TeletextCellAttributes.DoubleHeight) == (start.Flags & TeletextCellAttributes.DoubleHeight))
                {
                    lastSame = j;
                    j++;
                }
                else
                {
                    break;
                }
            }

            runs.Add(new TeletextRun(i, lastSame + 1, start.Foreground, start.Background, (start.Flags & TeletextCellAttributes.DoubleHeight) != 0));
            i = lastSame + 1;
        }

        return new TeletextCaptionLine(row, first, sb.ToString(), runs, doubleHeight);
    }
}

internal readonly record struct TeletextRun(int Start, int End, TeletextColor Foreground, TeletextColor Background, bool DoubleHeight);

internal sealed record TeletextCaptionLine(int Row, int Column, string Text, List<TeletextRun> Runs, bool DoubleHeight);

internal sealed class TeletextCaption
{
    private readonly List<TeletextCaptionLine> _lines;

    public TeletextCaption(List<TeletextCaptionLine> lines) => _lines = lines;

    public bool SameAs(TeletextCaption other)
    {
        if (other._lines.Count != _lines.Count)
        {
            return false;
        }

        for (int i = 0; i < _lines.Count; i++)
        {
            TeletextCaptionLine a = _lines[i], b = other._lines[i];
            if (a.Row != b.Row || a.Column != b.Column || a.Text != b.Text || a.Runs.Count != b.Runs.Count)
            {
                return false;
            }

            for (int r = 0; r < a.Runs.Count; r++)
            {
                if (a.Runs[r] != b.Runs[r])
                {
                    return false;
                }
            }
        }

        return true;
    }

    // True when every line on screen in <paramref name="previous"/> is still there, at the same row, with at least the same text.
    public bool Extends(TeletextCaption previous)
    {
        foreach (TeletextCaptionLine old in previous._lines)
        {
            bool found = false;
            foreach (TeletextCaptionLine now in _lines)
            {
                if (now.Row == old.Row)
                {
                    found = now.Text.StartsWith(old.Text, StringComparison.Ordinal) && now.Column <= old.Column + 1 && now.Column >= old.Column - 1;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    public SubtitleCue ToCue(TeletextPage page, string? language)
    {
        StringBuilder text = new();
        List<SubtitleSpan> spans = [];
        foreach (TeletextCaptionLine line in _lines)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            int baseOffset = text.Length;
            text.Append(line.Text);
            foreach (TeletextRun run in line.Runs)
            {
                if (run.Foreground == TeletextColor.White && run.Background == TeletextColor.Black && !run.DoubleHeight)
                {
                    continue;
                }

                TextStyle style = new()
                {
                    Color = run.Foreground == TeletextColor.White ? null : Rgb(run.Foreground),
                    Background = run.Background == TeletextColor.Black ? null : Rgb(run.Background),
                    FontSize = run.DoubleHeight ? new SubtitleLength(200, SubtitleLengthUnit.Percent) : null,
                };
                spans.Add(new SubtitleSpan(baseOffset + run.Start, run.End - run.Start) { Style = style });
            }
        }

        TeletextCaptionLine firstLine = _lines[0];
        return new SubtitleCue
        {
            Start = page.Time,
            End = page.Time,
            Text = text.ToString(),
            Spans = SubtitleList.From(spans),
            Language = language,
            Placement = new CuePlacement { Row = firstLine.Row, Column = firstLine.Column },
            Extras = SubtitleList.Of(new SubtitleExtra("teletext", "page", $"{page.PageNumber}:{page.Subcode:X4}")),
        };
    }

    // ETS 300 706 §12.4 colour map, CLUT 0: the eight colours at full intensity.
    private static SubtitleColor Rgb(TeletextColor c) => c switch
    {
        TeletextColor.Red => new(255, 0, 0),
        TeletextColor.Green => new(0, 255, 0),
        TeletextColor.Yellow => new(255, 255, 0),
        TeletextColor.Blue => new(0, 0, 255),
        TeletextColor.Magenta => new(255, 0, 255),
        TeletextColor.Cyan => new(0, 255, 255),
        TeletextColor.White => new(255, 255, 255),
        _ => new(0, 0, 0),
    };
}
