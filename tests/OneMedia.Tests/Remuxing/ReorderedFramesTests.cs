using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Tests.Remuxing;

// Frames that are stored out of presentation order make the first decode time earlier than the first presentation time. The edit list
// that hides that lead must still reach the end of the last frame shown, or players drop the last frames (ISO/IEC 14496-12 section 8.6.6).
public class ReorderedFramesTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "isobmff", name);

    private static MediaTime EditedDuration(MediaTrack t)
    {
        MediaTime total = MediaTime.Zero;
        foreach (EditSegment e in t.Edit.Edits!)
        {
            total = total.Add(e.Duration, MediaRounding.NearestAwayFromZero);
        }

        return total;
    }

    [Theory]
    [InlineData(RemuxFormat.Mp4)]
    [InlineData(RemuxFormat.Mov)]
    public void MatroskaWithBFramesRemuxesToAnEditListThatCoversEveryFrame(RemuxFormat format)
    {
        // 25 frames at 25 fps: one second.
        using MediaFile input = Media.Open(Data("bframes.mkv"));
        using MemoryStream ms = new();
        Remuxer.Remux(input, ms, format);
        using MediaFile output = Media.Open(ms.ToArray());
        MediaTrack video = output.Video!;
        Assert.NotNull(video.Edit.Edits);
        Assert.Equal(0, EditedDuration(video).CompareTo(new MediaTime(1, 1)));

        // The last frame shown ends where the edit ends.
        MediaTime first = MediaTime.Unknown, lastEnd = MediaTime.Zero;
        IDemuxer d = output.Demuxer!;
        while (d.TryReadPacket(out PacketView v))
        {
            if (v.Info.TrackIndex != video.Index)
            {
                continue;
            }

            if (!first.IsKnown || v.Info.Pts < first)
            {
                first = v.Info.Pts;
            }

            MediaTime end = v.Info.Pts.Add(v.Info.Duration, MediaRounding.NearestAwayFromZero);
            if (end > lastEnd)
            {
                lastEnd = end;
            }
        }

        Assert.Equal(0, lastEnd.Subtract(first, MediaRounding.NearestAwayFromZero).CompareTo(EditedDuration(video)));
    }
}
