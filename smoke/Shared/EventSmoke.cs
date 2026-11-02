using RecursiveAI.OneMedia.TimedEvents;
using RecursiveAI.OneMedia.Tests;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Reads SCTE 35 splice information and ID3 timed metadata from a small transport stream, parses an <c>emsg</c> box and a <c>tmcd</c> sample
/// entry, and counts drop-frame timecode, so the event parsers run under NativeAOT and in the browser WebAssembly runtime.
/// </summary>
internal static class EventSmoke
{
    public static void Run(Action<string, bool> check)
    {
        byte[] ts = EventBuilder.TransportStream(EventBuilder.SpliceInsert, EventBuilder.Id3Tag("k", "v"), 12 * 90000L);
        using MediaFile file = MediaFile.Open(new MemoryMediaSource(ts));
        EventChannel? scte = null;
        EventChannel? id3 = null;
        foreach (EventChannel c in file.Events)
        {
            if (c.Kind == MediaEventKind.Scte35)
            {
                scte = c;
            }
            else if (c.Kind == MediaEventKind.TimedId3)
            {
                id3 = c;
            }
        }

        check("events scte35", scte is { Events.Count: 1 } && scte.Events[0] is Scte35Event { Section: { CrcValid: true, Command: Scte35SpliceInsert { EventId: 0x12, OutOfNetwork: true } } } e
            && e.Time == new MediaTime(0x075C14B4A, 90000));
        check("events id3", id3 is { Events.Count: 1 } && id3.Events[0] is TimedId3Event { Tag.Frames.Count: 1 } t && t.Time == new MediaTime(12 * 90000L, 90000));

        check("events emsg", EventMessageBox.TryParse(EventBuilder.EmsgV0(EventMessageBox.Scte35Scheme, "1", 1000, 500, 0, 7, EventBuilder.SpliceInsert), 0, out EventMessageBox? box)
            && box!.Id == 7 && box.SchemeIdUri == EventMessageBox.Scte35Scheme && Scte35Section.TryParse(box.MessageData.Span, out _));

        check("events tmcd", TimecodeSampleEntry.TryParse(EventBuilder.TimecodeEntry(true, 30, 30000, 1001, "TAPE"), out TimecodeSampleEntry? entry)
            && entry!.SourceName == "TAPE" && entry.FromFrameNumber(1800).ToString() == "00:01:00;02" && entry.ToFrameNumber(entry.FromFrameNumber(107892)) == 107892);
    }
}
