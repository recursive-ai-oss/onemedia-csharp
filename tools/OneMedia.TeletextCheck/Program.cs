using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Captions.Teletext;

// Usage: TeletextCheck <file> [outdir] [--all] [--list] [--progressive] [--pages 777,778]
// Writes one <outdir>/<name>.pid<PID>.p<page>.srt per teletext subtitle page, and prints a summary line per page.
string path = args[0];
string outDir = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "";
bool all = Array.IndexOf(args, "--all") >= 0;
bool list = Array.IndexOf(args, "--list") >= 0;
bool progressive = Array.IndexOf(args, "--progressive") >= 0;
List<TeletextPageAddress> pages = [];
int pi = Array.IndexOf(args, "--pages");
if (pi >= 0 && pi + 1 < args.Length)
{
    foreach (string p in args[pi + 1].Split(','))
    {
        if (TeletextPageAddress.TryParse(p, out TeletextPageAddress a))
        {
            pages.Add(a);
        }
    }
}

using MediaFile file = MediaFile.Open(FileMediaSource.Open(path));
IDemuxer demux = file.Demuxer ?? throw new InvalidOperationException("not a synchronous demuxer");
DefectLog defects = new();
IReadOnlyList<TeletextTrackSubtitles> found = TeletextExtractor.ReadAll(
    demux,
    new TeletextOptions { Pages = pages.Count > 0 ? pages : null, AllPages = all, Progressive = progressive },
    defects);

string name = Path.GetFileNameWithoutExtension(path);
if (outDir.Length > 0)
{
    Directory.CreateDirectory(outDir);
}

foreach (TeletextTrackSubtitles t in found)
{
    foreach (TeletextSubtitleTrack s in t.Subtitles)
    {
        if (list)
        {
            foreach (TeletextPage pg in s.Pages)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{t.Pid} {s.Page}] t={pg.Time.ToTicks(1000, MediaRounding.NearestAwayFromZero)}ms..{pg.CompletedAt.ToTicks(1000, MediaRounding.NearestAwayFromZero)}ms sub={pg.Subcode:X4} {pg.Flags}"));
                for (int r = 1; r < TeletextPage.Rows; r++)
                {
                    string row = pg.RowText(r);
                    if (row.Length > 0)
                    {
                        Console.WriteLine($"      {r,2}: {row}");
                    }
                }
            }
        }

        string file2 = outDir.Length > 0 ? Path.Combine(outDir, $"{name}.pid{t.Pid}.p{s.Page}.srt") : "";
        if (file2.Length > 0)
        {
            File.WriteAllText(file2, ToSrt(s.Document), new UTF8Encoding(false));
        }

        TeletextPage first = s.Pages[0];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"track={t.TrackIndex} pid={t.Pid} page={s.Page} lang={s.Language ?? "-"} declared={s.Declared} cues={s.Document.Cues.Count} pages={s.Report.Pages} mosaic={s.Report.MosaicCells} unmapped={s.Report.UnmappedCharacters} set={first.CharacterSet.Alphabet}/{first.CharacterSet.National}/{first.CharacterSetSource}/hdr{first.HeaderNationalBits} {file2}"));
    }
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"teletext tracks={found.Count} defects={defects.Count}"));

// A minimal SRT rendering for comparison with other tools: index, times to the millisecond, text lines.
static string ToSrt(SubtitleDocument document)
{
    StringBuilder sb = new();
    int n = 0;
    foreach (SubtitleCue cue in document.Cues)
    {
        sb.Append(++n).Append('\n').Append(Stamp(cue.Start)).Append(" --> ").Append(Stamp(cue.End)).Append('\n').Append(cue.Text).Append("\n\n");
    }

    return sb.ToString();
}

static string Stamp(MediaTime t)
{
    long ms = t.ToTicks(1000, MediaRounding.NearestAwayFromZero);
    return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
}
