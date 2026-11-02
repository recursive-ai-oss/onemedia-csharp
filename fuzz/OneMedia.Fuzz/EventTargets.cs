using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.TimedEvents;
using RecursiveAI.OneMedia.Tests;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the timed-event parsers. <c>scte35-section</c> takes a splice information section, <c>tmcd-entry</c> a timecode sample
/// entry body (and counts frames with it), <c>emsg-box</c> an event message box payload, and <c>events-file</c> a whole transport stream or
/// ISO file (read through <see cref="MediaFile.Events"/>). None may throw anything but a <see cref="MediaException"/>.
/// </summary>
internal static class EventTargets
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["scte35-section"] = Section;
        targets["tmcd-entry"] = TimecodeEntry;
        targets["emsg-box"] = Emsg;
        targets["events-file"] = File;
        targets["events-seeds"] = Seeds;
    }

    private static void Section(ReadOnlySpan<byte> input)
    {
        DefectLog log = new();
        if (!Scte35Section.TryParse(input, out Scte35Section? s, log, 0) || s is null)
        {
            return;
        }

        if (s.Data.Length > input.Length || s.Data.Length < Scte35Section.MinimumLength)
        {
            throw new InvalidOperationException("section data outside the input");
        }

        _ = s.CommandType;
        _ = s.SplicePts;
        if (s.AdjustedSplicePts is long a && (a < 0 || a > 0x1_FFFF_FFFFL))
        {
            throw new InvalidOperationException("adjusted PTS is not 33 bits");
        }

        foreach (Scte35Descriptor d in s.Descriptors)
        {
            if (d is Scte35SegmentationDescriptor g && g.Upid.Length > 255)
            {
                throw new InvalidOperationException("UPID longer than its length byte allows");
            }
        }
    }

    private static void TimecodeEntry(ReadOnlySpan<byte> input)
    {
        if (!TimecodeSampleEntry.TryParse(input, out TimecodeSampleEntry? e) || e is null)
        {
            return;
        }

        _ = e.FrameTime;
        long[] frames = [0, 1, 29, 30, 1799, 1800, 17982, 107892, 2_147_483_647, -1, -1800, -2_147_483_648L];
        foreach (long n in frames)
        {
            Timecode t = e.FromFrameNumber(n);
            // Counting round-trips except where hours wrap at 24 or a drop-frame rate other than 30 and 60 cannot drop.
            if ((e.Flags & TimecodeAttributes.Wraps24Hours) == 0 && t.Hours < 1_000_000 && e.ToFrameNumber(t) != n && n != 0)
            {
                throw new InvalidOperationException($"frame {n} became {t} and then {e.ToFrameNumber(t)}");
            }
        }

        _ = e.FromFrameNumber(123456).ToString();
    }

    private static void Emsg(ReadOnlySpan<byte> input)
    {
        if (EventMessageBox.TryParse(input, 0, out EventMessageBox? b, new DefectLog()) && b is not null)
        {
            _ = b.Duration;
            if (b.MessageData.Length > input.Length)
            {
                throw new InvalidOperationException("message longer than the box");
            }

            if (b.SchemeIdUri == EventMessageBox.Scte35Scheme)
            {
                _ = Scte35Section.TryParse(b.MessageData.Span, out _);
            }
        }
    }

    private static void File(ReadOnlySpan<byte> input)
    {
        byte[] file = input.ToArray();
        MediaOpenOptions open = new() { Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxPacketBytes = 1 << 20, MaxElementCount = 100_000 } };
        try
        {
            using MediaFile f = MediaFile.Open(new MemoryMediaSource(file), null, open);
            foreach (EventChannel c in MediaEvents.List(f, open.Limits))
            {
                foreach (MediaEvent ev in c.Events)
                {
                    if (ev is Scte35Event { Section: null })
                    {
                        throw new InvalidOperationException("event without a section");
                    }
                }
            }
        }
        catch (MediaException)
        {
        }
        catch (MediaReadStateException)
        {
        }
    }

    // Writes the seed files into testdata/fuzz/<target>/ (directory in ONEMEDIA_EVENTS_SEED_DIR); the files are checked in.
    private static void Seeds(ReadOnlySpan<byte> input)
    {
        _ = input;
        string dir = Environment.GetEnvironmentVariable("ONEMEDIA_EVENTS_SEED_DIR") ?? throw new InvalidOperationException("ONEMEDIA_EVENTS_SEED_DIR is not set");
        foreach (string sub in new[] { "scte35-section", "tmcd-entry", "emsg-box" })
        {
            Directory.CreateDirectory(Path.Combine(dir, sub));
        }

        System.IO.File.WriteAllBytes(Path.Combine(dir, "scte35-section", "splice-insert.seed"), EventBuilder.SpliceInsert);
        System.IO.File.WriteAllBytes(Path.Combine(dir, "tmcd-entry", "drop.seed"), EventBuilder.TimecodeEntry(true, 30, 30000, 1001, "TAPE"));
        System.IO.File.WriteAllBytes(Path.Combine(dir, "tmcd-entry", "non-drop.seed"), EventBuilder.TimecodeEntry(false, 25, 25, 1));
        System.IO.File.WriteAllBytes(Path.Combine(dir, "emsg-box", "scte35.seed"), EventBuilder.EmsgV0(EventMessageBox.Scte35Scheme, "1", 1000, 500, 0, 7, EventBuilder.SpliceInsert));
        System.IO.File.WriteAllBytes(Path.Combine(dir, "emsg-box", "id3.seed"), EventBuilder.EmsgV0(EventMessageBox.Id3Scheme, "1", 1000, 100, 0, 2, EventBuilder.Id3Tag("a", "b")));
    }
}
