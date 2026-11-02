using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs.Captions;
using RecursiveAI.OneMedia.Subtitles;
using RecursiveAI.OneMedia.Subtitles.Cea608;
using RecursiveAI.OneMedia.Subtitles.Cea708;
using RecursiveAI.OneMedia.Subtitles.Scc;
using RecursiveAI.OneMedia.Remuxing;

// Usage:
//   CaptionCheck srt <file> <outdir>   decodes the first video track's captions; writes cc1.srt .. cc4.srt, text1.srt .., svc1.srt .. (non-empty only)
//   CaptionCheck triplets <file>       counts cc_data triplets by type
if (args[0] == "mkscc" && args.Length >= 2)
{
    // Writes a test SCC: three pop-on captions with plain, coloured and accented text.
    MediaTime T(int ms) => new(ms, 1000);
    var sample = new List<Cea608Caption>
    {
        new(Cea608Channel.CC1, T(1000), T(3000), [new Cea608Row(14, 1, "HELLO WORLD", []), new Cea608Row(15, 1, "second line here", [])]),
        new(Cea608Channel.CC1, T(4000), T(6000), [new Cea608Row(15, 9, "Caf\u00e9 \u266a ni\u00f1o", [new Cea608Span(0, 4, Cea608Color.Yellow, false, false, false)])]),
        new(Cea608Channel.CC1, T(7000), T(9500), [new Cea608Row(13, 5, "Italic and underline", [new Cea608Span(7, 3, Cea608Color.White, true, false, false), new Cea608Span(11, 9, Cea608Color.White, false, true, false)])]),
    };
    File.WriteAllText(args[1], SccDocument.FromCaptions(sample).Write());
    return 0;
}

if (args[0] == "remux" && args.Length >= 3)
{
    RemuxReport rr = Remuxer.Remux(args[1], args[2], new RemuxOptions { Overwrite = true });
    Console.WriteLine($"remuxed; losses={rr.Losses.Count}");
    return 0;
}

if (args[0] == "scc2srt" && args.Length >= 3)
{
    SccDocument doc = SccDocument.Parse(File.ReadAllText(args[1]));
    Write(args[2], doc.Decode().OrderBy(c => c.Start).Select(c => (c.Start, c.End, c.Text)));
    return 0;
}

if (args[0] == "writeback" && args.Length >= 4)
{
    // writeback <in> <scc> <out>: inserts the SCC's pairs as caption SEI into the input's H.264/HEVC track and remuxes.
    using MediaFile first = Media.Open(args[1]);
    using MediaFile second = Media.Open(args[1]);
    MediaTrack v1 = first.Tracks.First(t => t.Kind == TrackKind.Video);
    MediaTrack v2 = second.Tracks.First(t => t.Kind == TrackKind.Video);
    var times = new List<MediaTime>();
    foreach (OwnedPacket p in first.Packets(v1))
    {
        using (p)
        {
            times.Add(p.Info.Pts.IsKnown ? p.Info.Pts : p.Info.Dts);
        }
    }

    MediaTime first0 = times.Min();
    SccDocument doc = SccDocument.Parse(File.ReadAllText(args[2]));
    IReadOnlyList<CaptionFrame> placed = CaptionPlacement.Place(times, doc.Pairs(first0));
    using var feed = new CaptionSeiFeed(second, v2, placed, args.Contains("--remove-all") ? ExistingCaptionPolicy.RemoveAll : ExistingCaptionPolicy.Replace);
    RemuxReport report = Remuxer.Remux(args[1], args[3], new RemuxOptions { Replacements = [new TrackReplacement(feed)], Overwrite = true });
    Console.WriteLine($"frames with captions={placed.Count} inserted={feed.InsertedCount} removed={feed.RemovedCount} losses={report.Losses.Count}");
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CaptionCheck srt <file> <outdir> | triplets <file>");
    return 2;
}

using MediaFile media = Media.Open(args[1]);
MediaTrack? video = media.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
if (video is null)
{
    Console.Error.WriteLine("no video track");
    return 1;
}

if (args[0] == "userdata")
{
    // Prints the first bytes of each MPEG-2 user data section of the first 40 packets, distinct prefixes only.
    var seen = new HashSet<string>();
    int shown = 0;
    foreach (OwnedPacket p in media.Packets(video))
    {
        using (p)
        {
            var rd = new RecursiveAI.OneMedia.Codecs.Mpeg2Video.Mpeg2StartCodeReader(p.Data.Span);
            while (rd.TryRead(out int code, out ReadOnlySpan<byte> body, out _))
            {
                if (code == 0xb2)
                {
                    string hex = Convert.ToHexString(body[..Math.Min(24, body.Length)]);
                    if (seen.Add(hex[..Math.Min(8, hex.Length)]) && shown++ < 12)
                    {
                        Console.WriteLine($"len={body.Length} {hex}");
                    }
                }
            }
        }
    }

    return 0;
}

IReadOnlyList<CaptionFrame> frames = CaptionExtractor.ReadFrames(media, video);
if (args[0] == "pairs")
{
    int shownPairs = 0;
    foreach (CaptionFrame f in frames)
    {
        foreach (CcTriplet t in f.Triplets)
        {
            if (t.Valid && t.Type == 0 && (t.Data1 & 0x7f) != 0 && shownPairs++ < 120)
            {
                Console.Write($"{t.Data1 & 0x7f:x2}{t.Data2 & 0x7f:x2} ");
            }
        }
    }

    Console.WriteLine();
    return 0;
}

if (args[0] == "triplets")
{
    var counts = new int[4];
    var invalid = 0;
    foreach (CaptionFrame f in frames)
    {
        foreach (CcTriplet t in f.Triplets)
        {
            if (t.Valid) { counts[t.Type]++; } else { invalid++; }
        }
    }

    using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    foreach (CaptionFrame f in frames)
    {
        foreach (CcTriplet t in f.Triplets)
        {
            sha.AppendData([(byte)(t.Valid ? 1 : 0), t.Type, t.Data1, t.Data2]);
        }
    }

    Console.WriteLine($"frames={frames.Count} type0={counts[0]} type1={counts[1]} type2={counts[2]} type3={counts[3]} invalid={invalid} sha256={Convert.ToHexString(sha.GetHashAndReset())[..16]}");
    return 0;
}

// Other tools count captions from the first picture's time; --rebase does the same so SRT files line up.
MediaTime origin = MediaTime.Zero;
if (args.Contains("--rebase"))
{
    foreach (OwnedPacket p in media.Packets(video))
    {
        using (p)
        {
            MediaTime t = p.Info.Pts.IsKnown ? p.Info.Pts : p.Info.Dts;
            if (t.IsKnown && (origin.IsZero || t < origin))
            {
                origin = t;
            }
        }
    }
}

EmbeddedCaptions caps = EmbeddedCaptions.Decode(frames);
string outDir = args[2];
Directory.CreateDirectory(outDir);
foreach (IGrouping<Cea608Channel, Cea608Caption> g in caps.Cea608.GroupBy(c => c.Channel))
{
    string name = g.Key.ToString().ToLowerInvariant();
    Write(Path.Combine(outDir, name + ".srt"), g.OrderBy(c => c.Start).Select(c => (c.Start - origin, c.End - origin, c.Text)));
    Console.WriteLine($"{g.Key}: {g.Count()} captions");
}

foreach (IGrouping<int, Cea708Caption> g in caps.Cea708.GroupBy(c => c.Service))
{
    Write(Path.Combine(outDir, "svc" + g.Key.ToString(CultureInfo.InvariantCulture) + ".srt"), g.OrderBy(c => c.Start).Select(c => (c.Start - origin, c.End - origin, c.Text)));
    Console.WriteLine($"service {g.Key}: {g.Count()} captions");
}

foreach (XdsPacket x in caps.Xds)
{
    Console.WriteLine($"XDS class {x.Class} type {x.Type} len {x.Data.Length} ok={x.ChecksumValid} {x.ProgramName ?? x.NetworkName}");
}

return 0;

static string Stamp(MediaTime t)
{
    long ms = t.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks;
    return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:D2}:{ms / 60000 % 60:D2}:{ms / 1000 % 60:D2},{ms % 1000:D3}");
}

static void Write(string path, IEnumerable<(MediaTime Start, MediaTime End, string Text)> items)
{
    var sb = new StringBuilder();
    int n = 1;
    foreach ((MediaTime s, MediaTime e, string text) in items)
    {
        sb.Append(n++).Append('\n').Append(Stamp(s)).Append(" --> ").Append(Stamp(e)).Append('\n').Append(text).Append("\n\n");
    }

    File.WriteAllText(path, sb.ToString());
}
