using System.Buffers.Binary;
using RecursiveAI.OneMedia.Tests.IsoBmffSupport;
using static RecursiveAI.OneMedia.Tests.IsoBmffSupport.Mp4Builder;

namespace RecursiveAI.OneMedia.Tests;

// The file duration ignores data tracks, and an AAC track's channel count comes from its AudioSpecificConfig.
public class IsoDurationAndChannelTests
{
    private static void SetDuration(byte[] file, string box, int occurrence, uint value)
    {
        int at = -1;
        for (int i = 0; i <= occurrence; i++)
        {
            at = file.AsSpan(at + 1).IndexOf(System.Text.Encoding.Latin1.GetBytes(box)) + at + 1;
        }

        // version 0 header: type, version/flags, creation, modification, timescale, duration
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(at + 20), value);
    }

    // An mp4a entry with a well-formed esds: ES descriptor, decoder configuration (object type 0x40) and the two-byte AudioSpecificConfig 12 10.
    private static byte[] Mp4aEntry(int channels) =>
        Box("mp4a", new byte[6], U16(1), new byte[8], U16((ushort)channels), U16(16), U16(0), U16(0), U32(44100u << 16),
            Full("esds", 0, 0, [3, 0x19, 0, 1, 0, 4, 0x11, 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 2, 0x12, 0x10, 6, 1, 2]));

    [Fact]
    public void ALongerDataTrackDoesNotSetTheFileDuration()
    {
        TrackSpec video = new() { Id = 1, Handler = "vide", Timescale = 1000, SampleCount = 2 };
        TrackSpec telemetry = new() { Id = 2, Handler = "meta", Timescale = 1000, SampleCount = 7, SampleEntries = Box("priv", new byte[6], U16(1)) };
        byte[] file = Build([video, telemetry], true).ToArray([video, telemetry]);
        SetDuration(file, "mvhd", 0, 2000);
        SetDuration(file, "mdhd", 0, 2000);
        SetDuration(file, "mdhd", 1, 7000);

        using MediaFile media = MediaFile.Open(new MemoryMediaSource(file));
        Assert.Equal(TrackKind.Data, media.Tracks[1].Kind);
        Assert.Equal(2000, media.TracksDuration.ToTicks(1000, MediaRounding.NearestAwayFromZero));
        Assert.Equal(2000, media.ExactDuration.ToTicks(1000, MediaRounding.NearestAwayFromZero));
    }

    [Fact]
    public void ADataTrackAloneStillStatesTheDuration()
    {
        TrackSpec telemetry = new() { Id = 1, Handler = "meta", Timescale = 1000, SampleCount = 7, SampleEntries = Box("priv", new byte[6], U16(1)) };
        byte[] file = Build([telemetry], true).ToArray([telemetry]);
        SetDuration(file, "mdhd", 0, 7000);

        using MediaFile media = MediaFile.Open(new MemoryMediaSource(file));
        Assert.Equal(7000, media.TracksDuration.ToTicks(1000, MediaRounding.NearestAwayFromZero));
    }

    [Fact]
    public void AacChannelCountComesFromTheAudioSpecificConfigWhenTheSampleEntryDisagrees()
    {
        // The entry says one channel; the configuration (object type 2, 44.1 kHz, channel configuration 2) says two.
        TrackSpec audio = new() { Id = 1, Handler = "soun", Timescale = 44100, SampleCount = 3, SampleEntries = Mp4aEntry(channels: 1) };
        byte[] file = Build([audio], true).ToArray([audio]);
        using MediaFile media = MediaFile.Open(new MemoryMediaSource(file));
        Assert.Equal(2, media.Audio!.Channels);
    }
}
