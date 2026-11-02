using System.Text;
using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Tests.Telemetry;

public class DeviceFactsFromTelemetryTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "telemetry", name);

    [Fact]
    public void GoProModelAndMakeComeFromTheUserDataBlockAndTheDeviceName()
    {
        using MediaFile media = Media.Open(Fixture("gpmf-device.mp4"));

        // The model text sits in the udta GPMF atom, so a facts-only open already names the camera.
        Assert.Equal("HERO8 Black", media.Device.Model.Best!.Value);
        Assert.Equal("GoPro", media.Device.Make.Best!.Value);

        // Reading the track adds the device name ("Camera" and settings blocks are not models, "Fusion" is) behind the container's reading.
        _ = media.Telemetry;
        MediaFacts facts = media.Facts;
        Assert.Contains(facts.Model.Candidates, c => c.Source == "telemetry:gpmf:DVNM" && c.Value == "Fusion");
        Assert.Equal("udta:GPMF:MINF", facts.Model.Best!.Source);
        Assert.Contains(facts.Make.Candidates, c => c.Source == "telemetry:gpmf");
    }

    [Fact]
    public void DjiMakeAndModelComeFromTheProtobufMessage()
    {
        using MediaFile media = Media.Open(Fixture("djmd-real.mp4"));
        _ = media.Telemetry;
        Assert.Equal("Synthetic Model", media.Device.Model.Best!.Value);
        Assert.Equal("DJI", media.Device.Make.Best!.Value);
        Assert.NotEqual(ByteRange.None, media.Device.Model.Best.Range);
    }

    [Fact]
    public void ASidecarAndATrackShareOneParser()
    {
        byte[] srt = File.ReadAllBytes(Fixture("dji-formats.srt"));
        EvidenceSet e = Sidecars.ReadDjiSrt(srt);

        // The first block is bracketed, so it gives the position.
        Assert.Equal(59.302335, e.Location.Single().Value.Latitude);

        // The GPS(lat,lon,alt) layout gives evidence like the bracketed one.
        EvidenceSet gpsForm = Sidecars.ReadDjiSrt(Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:00,033\nGPS(39.906217,116.391305,69.800) BAROMETER(91.2)\n"));
        Assert.Equal(116.391305, gpsForm.Location.Single().Value.Longitude);
    }
}
