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

if (args[0] == "seeds" && args.Length >= 3)
{
    // Fuzz seeds in the texttrack-* input layout: selector, config length (16 bit), config, additional length, additional, packet.
    using MediaFile seedFile = Media.Open(args[1]);
    string stem = System.IO.Path.GetFileNameWithoutExtension(args[1]);
    int n = 0;
    foreach (MediaTrack t in seedFile.Tracks)
    {
        if (TextTracks.FormatOf(t) == TextTrackFormat.Unknown)
        {
            seedFile.Demuxer!.SetTrackEnabled(t.Index, false);
        }
    }

    while (seedFile.Demuxer!.TryReadPacket(out PacketView p))
    {
        MediaTrack t = seedFile.Tracks[p.Info.TrackIndex];
        TextTrackFormat f = TextTracks.FormatOf(t);
        (string dir, byte sel) = f switch
        {
            TextTrackFormat.Tx3g => ("texttrack-tx3g", (byte)0),
            TextTrackFormat.QuickTimeText => ("texttrack-tx3g", (byte)1),
            TextTrackFormat.WebVttIso => ("texttrack-wvtt", (byte)0),
            TextTrackFormat.XmlSubtitleIso => ("texttrack-stpp", (byte)0),
            TextTrackFormat.SubRip => ("texttrack-matroska", (byte)0),
            TextTrackFormat.Ass => ("texttrack-matroska", (byte)1),
            TextTrackFormat.WebVtt => ("texttrack-matroska", (byte)2),
            _ => ("texttrack-matroska", (byte)3),
        };
        ReadOnlySpan<byte> config = t.Configuration.Data.Span;
        ReadOnlySpan<byte> add = seedFile.Demuxer is RecursiveAI.OneMedia.Matroska.MatroskaDemuxer m ? m.LastBlockAdditional : default;
        using System.IO.MemoryStream ms = new();
        ms.WriteByte(sel);
        ms.WriteByte((byte)(config.Length >> 8));
        ms.WriteByte((byte)config.Length);
        ms.Write(config[..Math.Min(config.Length, 0xFFFF)]);
        ms.WriteByte((byte)Math.Min(add.Length, 255));
        ms.Write(add[..Math.Min(add.Length, 255)]);
        ms.Write(p.Data);
        string folder = System.IO.Path.Combine(args[2], dir);
        System.IO.Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, $"{stem}-{n++}.seed"), ms.ToArray());
    }

    return 0;
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
