using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Captions.Teletext;
using RecursiveAI.OneMedia.MpegPs;
using RecursiveAI.OneMedia.MpegTs;
using RecursiveAI.OneMedia.Tests;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the teletext decoder. <c>teletext-pes</c> takes an option byte (bit 0: every page; bit 1: every state of a live page;
/// bit 2: announce a language by PMT descriptor; bit 3: split the payload in two PES packets) and a PES payload, which it pushes three
/// times at increasing times so pages complete and merge. <c>teletext-file</c> takes an option byte (bit 0: program stream instead of
/// transport stream; bit 1: every page) and a whole file. Neither may throw anything but a <see cref="MediaException"/>, and the cues
/// they return must have non-negative durations.
/// </summary>
internal static class TeletextTarget
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["teletext-pes"] = Pes;
        targets["teletext-file"] = File;
        targets["teletext-seeds"] = Seeds;
    }

    private static void Check(IReadOnlyList<TeletextSubtitleTrack> tracks)
    {
        foreach (TeletextSubtitleTrack track in tracks)
        {
            MediaTime previousStart = MediaTime.Unknown;
            foreach (SubtitleCue cue in track.Document.Cues)
            {
                if (cue.End < cue.Start || (previousStart.IsKnown && cue.Start < previousStart))
                {
                    throw new InvalidOperationException("cue times out of order");
                }

                previousStart = cue.Start;
            }
        }
    }

    private static void Pes(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        byte flags = input[0];
        ReadOnlySpan<byte> payload = input[1..];
        TeletextOptions options = new() { AllPages = (flags & 1) != 0, Progressive = (flags & 2) != 0 };
        TeletextService[] services = (flags & 4) != 0 ? [new TeletextService("pol", TeletextPageType.Subtitle, new TeletextPageAddress(7, 0x77))] : [];
        DefectLog defects = new();
        TeletextSubtitleReader reader = new(options, services, defects);
        for (int round = 0; round < 3; round++)
        {
            MediaTime time = new(round * 90_000L, 90_000);
            if ((flags & 8) != 0)
            {
                int half = payload.Length / 2;
                reader.Push(payload[..half], time, round);
                reader.Push(payload[half..], time + new MediaTime(1, 90_000), round);
            }
            else
            {
                reader.Push(payload, time, round);
            }
        }

        Check(reader.Finish(new MediaTime(10 * 90_000L, 90_000)));
    }

    private static void File(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        bool ps = (input[0] & 1) != 0;
        byte[] file = input[1..].ToArray();
        MediaOpenOptions open = new() { Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxPacketBytes = 1 << 20 } };
        TeletextOptions options = new() { AllPages = (input[0] & 2) != 0 };
        try
        {
            using IDemuxer d = ps ? MpegPsDemuxer.Open(new MemoryMediaSource(file), open) : MpegTsDemuxer.Open(new MemoryMediaSource(file), open);
            foreach (TeletextTrackSubtitles t in TeletextExtractor.ReadAll(d, options, new DefectLog()))
            {
                Check(t.Subtitles);
            }
        }
        catch (MediaException)
        {
        }
    }

    // Writes the seed corpus: one file per input of the two targets. The output directory is in ONEMEDIA_TELETEXT_SEED_DIR.
    private static void Seeds(ReadOnlySpan<byte> input)
    {
        _ = input;
        string dir = Environment.GetEnvironmentVariable("ONEMEDIA_TELETEXT_SEED_DIR") ?? throw new InvalidOperationException("ONEMEDIA_TELETEXT_SEED_DIR is not set");
        Directory.CreateDirectory(Path.Combine(dir, "teletext-pes"));
        Directory.CreateDirectory(Path.Combine(dir, "teletext-file"));
        long clock = TeletextBuilder.Clock;
        byte[][] hello = TeletextBuilder.Page(7, 0x77, true, true, 0b110, (20, TeletextBuilder.BoxedRow("citt{")), (21, TeletextBuilder.BoxedRow("sera", 3)));
        byte[][] accent = [.. hello, TeletextBuilder.DataUnit(7, 26, TeletextBuilder.Enhancement(0, (40 + 20, 0x04, 0), (6, 0x12, 'e')))];
        byte[][] designated = [hello[0], TeletextBuilder.DataUnit(7, 28, TeletextBuilder.Designation(0b0100100)), .. hello[1..]];
        byte[][] clear = TeletextBuilder.Page(7, 0x77, true, true, 0);
        byte[][] plain = TeletextBuilder.Page(1, 0x00, false, true, 0, (1, TeletextBuilder.Row("ABC \u0007DEF")));
        byte[][][] pages = [hello, accent, designated, clear, plain];
        for (int i = 0; i < pages.Length; i++)
        {
            System.IO.File.WriteAllBytes(Path.Combine(dir, "teletext-pes", $"page{i}.seed"), [(byte)(i == 4 ? 1 : 0), .. TeletextBuilder.PesPayload(pages[i])]);
        }

        (long, byte[][])[] pes = [(clock, hello), (3 * clock, clear), (4 * clock, accent), (6 * clock, designated)];
        System.IO.File.WriteAllBytes(Path.Combine(dir, "teletext-file", "stream.ts.seed"), [0, .. TeletextBuilder.TransportStream(pes)]);
        System.IO.File.WriteAllBytes(Path.Combine(dir, "teletext-file", "stream.mpg.seed"), [1, .. TeletextBuilder.ProgramStream(pes)]);
    }
}
