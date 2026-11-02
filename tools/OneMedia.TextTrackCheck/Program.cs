using System;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Captions;

// Usage:
//   TextTrackCheck raw <file>
//       One line per packet of every subtitle track: track index, codec tag, pts and duration in the track's ticks, size, escaped bytes.
//   TextTrackCheck cues <file>
//       Decodes every text track to cues: a header line per track, then <track>\t<start ms>\t<end ms>\t<text, line feeds escaped>.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: TextTrackCheck raw <file>");
    return 2;
}

if (args[0] == "cues")
{
    using MediaFile cueFile = Media.Open(args[1]);
    foreach (MediaTrack t in cueFile.Tracks)
    {
        if (TextTracks.FormatOf(t) == TextTrackFormat.Unknown)
        {
            continue;
        }

        SubtitleDocument doc = TextTrackDecoder.Read(cueFile, t);
        Console.WriteLine($"# track {t.Index} format={TextTracks.FormatOf(t)} lang={t.Language} cues={doc.Cues.Count} defects={cueFile.Defects.Count}");
        foreach (SubtitleCue c in doc.Cues)
        {
            Console.WriteLine($"{t.Index}\t{Ms(c.Start)}\t{Ms(c.End)}\t{c.Text.Replace("\n", "\\n", StringComparison.Ordinal)}");
        }
    }

    return 0;
}

using MediaFile media = Media.Open(args[1]);
foreach (MediaTrack t in media.Tracks)
{
    if (t.Kind != TrackKind.Subtitle)
    {
        media.Demuxer!.SetTrackEnabled(t.Index, false);
        continue;
    }

    Console.WriteLine($"# track {t.Index} id={t.Id} codec={t.Codec} tag={t.CodecTag} timescale={t.Timescale} lang={t.Language} disp={t.Disposition} config={t.Configuration.Format}:{t.Configuration.Data.Length}");
    Console.WriteLine($"#   config {Escape(t.Configuration.Data.Span)}");
}

while (media.Demuxer!.TryReadPacket(out PacketView p))
{
    MediaTrack t = media.Tracks[p.Info.TrackIndex];
    Console.WriteLine($"{t.Index}\t{p.Info.Pts.Ticks}\t{p.Info.Duration.Ticks}\t{p.Info.Size}\t{Escape(p.Data)}");
}

return 0;

static string Ms(MediaTime t) => ((decimal)t.Ticks * 1000m / t.Timescale).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

static string Escape(ReadOnlySpan<byte> data)
{
    StringBuilder sb = new();
    foreach (byte b in data)
    {
        if (b is >= 0x20 and < 0x7f && b != '\\')
        {
            sb.Append((char)b);
        }
        else
        {
            sb.Append("\\x").Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    return sb.ToString();
}
