using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Metadata.Telemetry;
using RecursiveAI.OneMedia.Metadata.Telemetry.Dji;

namespace RecursiveAI.OneMedia.Tests.Telemetry;

// The fixtures are written by tools/gen-dji-fixtures.py and tools/gen-mdpm-gps-fixtures.py. Their layouts follow the published descriptions
// and the values were read back with ExifTool 13.60; nothing in them comes from a device.
[Collection(AllocationMeasurements.Name)]
public class DjiMdpmTelemetryTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "telemetry", name);

    private static TelemetryChannel Channel(IReadOnlyList<TelemetryChannel> all, string name) => all.Single(c => c.Name == name);

    private static void CheckTextChannels(IReadOnlyList<TelemetryChannel> all, string format, long timescale, long step)
    {
        TelemetryChannel gps = Channel(all, "dji gps");
        Assert.Equal(format, gps.Format);
        Assert.Equal(5, gps.Samples.Count);
        for (int i = 0; i < 5; i++)
        {
            GpsSample g = Assert.IsType<GpsSample>(gps.Samples[i]);
            Assert.Equal(52.5 + (i * 1e-4), g.Latitude, 9);
            Assert.Equal(13.4 + (i * 1e-4), g.Longitude, 9);
            Assert.Equal(142.76 + i, g.Altitude!.Value, 9);
            Assert.Equal(i * step, g.Time.Rescale(timescale, MediaRounding.NearestEven).Ticks);
        }

        CameraSettingsSample cam = Assert.IsType<CameraSettingsSample>(Channel(all, "dji camera").Samples[3]);
        Assert.Equal(103, cam.Iso);
        Assert.Equal(new MediaTime(1, 1003), cam.ExposureTime);
        Assert.Equal(2.8, cam.FNumber!.Value, 9);
        Assert.Equal(-0.7, cam.ExposureCompensationEv!.Value, 9);
        Assert.Equal(5500, cam.ColorTemperatureKelvin);
        Assert.Equal(24.0, cam.FocalLengthMillimetres!.Value, 9);

        ScalarSample alt = Assert.IsType<ScalarSample>(Channel(all, "dji altitude").Samples[2]);
        Assert.Equal([12.2, 144.76], alt.Values.Select(v => Math.Round(v, 3)).ToArray());

        // year, month, day, hour, minute, second, millisecond, frame counter, DiffTime
        ScalarSample clock = Assert.IsType<ScalarSample>(Channel(all, "dji clock").Samples[1]);
        Assert.Equal([2024, 1, 15, 14, 30, 22, 100, 2, 33], clock.Values.ToArray());
    }

    [Fact]
    public void ASidecarDecodesEveryFieldOfTheComprehensiveLayout()
    {
        IReadOnlyList<TelemetryChannel> channels = DjiSrt.Read(Fixture("dji-text.srt"));
        CheckTextChannels(channels, "dji-srt", 1000, 33);
        Assert.Equal(new MediaTime(33, 1000), channels[0].Samples[0].Duration);
    }

    [Fact]
    public void ASubtitleTrackDecodesLikeItsSidecar()
    {
        using MediaFile media = Media.Open(Fixture("dji-text.mp4"));
        Assert.Single(TelemetryChannels.TracksOf(media));
        CheckTextChannels(media.Telemetry, "dji-srt", 1000, 100);
        Assert.NotEmpty(TelemetryFacts.LocationCandidates(media.Telemetry));
    }

    [Fact]
    public void EveryDescribedLayoutFamilyDecodes()
    {
        IReadOnlyList<TelemetryChannel> all = DjiSrt.Read(Fixture("dji-formats.srt"));
        List<GpsSample> gps = [.. Channel(all, "dji gps").Samples.Cast<GpsSample>()];
        Assert.Equal(5, gps.Count);

        // Bracketed pairs; the f-number is the integer times 100.
        Assert.Equal((59.302335, 18.203059, 132.860), (gps[0].Latitude, gps[0].Longitude, gps[0].Altitude!.Value));
        List<CameraSettingsSample> cams = [.. Channel(all, "dji camera").Samples.Cast<CameraSettingsSample>()];
        Assert.Equal(2700, cams[0].Iso);
        Assert.Equal(new MediaTime(1, 30), cams[0].ExposureTime);
        Assert.Equal(1.7, cams[0].FNumber!.Value, 9);

        // GPS(lat,lon,alt) with BAROMETER; the unit letter after the altitude is ignored; HOME is not read.
        Assert.Equal((39.906217, 116.391305, 69.8), (gps[1].Latitude, gps[1].Longitude, gps[1].Altitude!.Value));
        Assert.Equal((36.6146, -6.1120, 0.0), (gps[2].Latitude, gps[2].Longitude, gps[2].Altitude!.Value));
        List<ScalarSample> baro = [.. Channel(all, "dji barometer").Samples.Cast<ScalarSample>()];
        Assert.Equal([91.2, 0.3], baro.Select(b => b.Values[0]).ToArray());

        // The compact line: shutter 1/400, ISO, EV, f-number and the two attitude triples (pitch, roll, yaw as written).
        Assert.Equal(-58.851745, gps[3].Latitude);
        Assert.Equal(new MediaTime(1, 400), cams[1].ExposureTime);
        Assert.Equal((100, 5.6, 0.0), (cams[1].Iso, cams[1].FNumber!.Value, cams[1].ExposureCompensationEv!.Value));
        OrientationSample aircraft = Assert.IsType<OrientationSample>(Channel(all, "dji aircraft").Samples.Single());
        Assert.Equal((110.1, 2.7, -7.0), (aircraft.YawDegrees!.Value, aircraft.PitchDegrees!.Value, aircraft.RollDegrees!.Value));
        List<OrientationSample> gimbal = [.. Channel(all, "dji gimbal").Samples.Cast<OrientationSample>()];
        Assert.Equal((110.4, -24.4, 0.0), (gimbal[0].YawDegrees!.Value, gimbal[0].PitchDegrees!.Value, gimbal[0].RollDegrees!.Value));

        // Decimal units: the f-number and focal length are written as they are; the tint after the colour temperature is skipped.
        Assert.Equal(1.8, cams[2].FNumber!.Value, 9);
        Assert.Equal(24.0, cams[2].FocalLengthMillimetres!.Value, 9);
        Assert.Equal(5262, cams[2].ColorTemperatureKelvin);
        Assert.Equal(new MediaTime(1, 6400), cams[2].ExposureTime);
        Assert.Equal((10.5, -20.25, 0.0), (gimbal[1].YawDegrees!.Value, gimbal[1].PitchDegrees!.Value, gimbal[1].RollDegrees!.Value));
        ScalarSample clock = Assert.IsType<ScalarSample>(Channel(all, "dji clock").Samples.Single());
        Assert.Equal([2026, 5, 27, 13, 14, 22, 911, 1, 40], clock.Values.ToArray());
    }

    [Fact]
    public void AMessageStateTheProtocolAndTheMappedFieldsDecode()
    {
        using MediaFile media = Media.Open(Fixture("djmd-real.mp4"));
        IReadOnlyList<TelemetryChannel> all = media.Telemetry;
        GpsSample g = Assert.IsType<GpsSample>(Channel(all, "djmd gps").Samples[1]);
        Assert.Equal(52.5001, g.Latitude, 9);
        Assert.Equal(13.4001, g.Longitude, 9);
        Assert.Equal(123.457, g.Altitude!.Value, 9);
        ScalarSample alt = Assert.IsType<ScalarSample>(Channel(all, "djmd altitude").Samples[1]);
        Assert.Equal(12.346, alt.Values[0], 3);
        OrientationSample drone = Assert.IsType<OrientationSample>(Channel(all, "djmd aircraft").Samples[1]);
        Assert.Equal((90.6, -2.5, 1.5), (drone.YawDegrees!.Value, drone.PitchDegrees!.Value, drone.RollDegrees!.Value));
        OrientationSample gimbal = Assert.IsType<OrientationSample>(Channel(all, "djmd gimbal").Samples[0]);
        Assert.Equal((45.2, -30.5, 0.5), (gimbal.YawDegrees!.Value, gimbal.PitchDegrees!.Value, gimbal.RollDegrees!.Value));
        Assert.Equal(101, Assert.IsType<CameraSettingsSample>(Channel(all, "djmd camera").Samples[1]).Iso);

        // The serial number is delivered as a raw item that knows its bytes in the file, and so is the whole message.
        RawTelemetrySample serial = Assert.IsType<RawTelemetrySample>(Channel(all, "serial number").Samples[0]);
        Assert.Equal("SYNTHETIC-SERIAL", System.Text.Encoding.UTF8.GetString(serial.Data.Span));
        byte[] file = File.ReadAllBytes(Fixture("djmd-real.mp4"));
        Assert.Equal("SYNTHETIC-SERIAL", System.Text.Encoding.UTF8.GetString(file, (int)serial.Range.Offset, (int)serial.Range.Length));
        Assert.Equal(5, Channel(all, "djmd message").Samples.Count);
    }

    [Fact]
    public void AnOsmoStyleProtocolUsesItsOwnPaths()
    {
        using MediaFile media = Media.Open(Fixture("djmd-osmo.mp4"));
        GpsSample g = Assert.IsType<GpsSample>(Channel(media.Telemetry, "djmd gps").Samples[1]);
        Assert.Equal((-33.8001, 151.2001, 21.501), (Math.Round(g.Latitude, 6), Math.Round(g.Longitude, 6), g.Altitude!.Value));
        CameraSettingsSample cam = Assert.IsType<CameraSettingsSample>(Channel(media.Telemetry, "djmd camera").Samples[1]);
        Assert.Equal((401, 5600), (cam.Iso, cam.ColorTemperatureKelvin));
    }

    [Fact]
    public void ProtobufReaderWalksFieldsAndStopsAtDamage()
    {
        // field 1 varint 300, field 2 string "hi", field 3 fixed32, then a length that runs past the end.
        byte[] msg = [0x08, 0xAC, 0x02, 0x12, 2, (byte)'h', (byte)'i', 0x1D, 1, 0, 0, 0, 0x22, 9, 1];
        ProtobufReader r = new(msg);
        Assert.True(r.TryRead(out int n, out ProtobufWireType wt, out ulong v, out _, out int start));
        Assert.Equal((1, ProtobufWireType.Varint, 300UL, 0), (n, wt, v, start));
        Assert.True(r.TryRead(out n, out wt, out _, out ReadOnlySpan<byte> p, out _));
        Assert.Equal((2, ProtobufWireType.LengthDelimited, "hi"), (n, wt, System.Text.Encoding.ASCII.GetString(p)));
        Assert.True(r.TryRead(out n, out wt, out v, out _, out _));
        Assert.Equal((3, ProtobufWireType.Fixed32, 1UL), (n, wt, v));
        Assert.False(r.TryRead(out _, out _, out _, out _, out _));
        Assert.False(r.TryRead(out _, out _, out _, out _, out _));
    }

    [Fact]
    public void MdpmGpsComesOutOfTheVideoOfATransportStream()
    {
        using MediaFile media = Media.Open(Fixture("mdpm-gps.mts"));
        TelemetryChannel gps = Channel(media.Telemetry, "mdpm gps");
        Assert.Equal("mdpm", gps.Format);
        Assert.Equal(3, gps.Samples.Count);
        for (int i = 0; i < 3; i++)
        {
            GpsSample g = Assert.IsType<GpsSample>(gps.Samples[i]);
            Assert.Equal(52 + (30 / 60.0) + ((1550 + i) / 100.0 / 3600), g.Latitude, 9);
            Assert.Equal(13 + (24 / 60.0) + ((2200 + i) / 100.0 / 3600), g.Longitude, 9);
            Assert.Equal((1234 + i) / 10.0, g.Altitude!.Value, 9);
            Assert.Equal(GpsFixKind.Fix3D, g.Fix);
            Assert.Equal(1.5, g.Dop);
            Assert.Equal(12.3 / 3.6, g.Speed2D!.Value, 9);
            Assert.Equal(new DateTime(2026, 11, 1, 12, 34, i, DateTimeKind.Utc), g.GpsTime);
            Assert.True(g.Time.IsKnown);
        }

        Assert.True(gps.Samples[1].Time > gps.Samples[0].Time);
        Assert.NotEmpty(TelemetryFacts.CaptureTimeCandidates(media.Telemetry));
    }

    [Fact]
    public void SouthAndWestReferencesNegateTheCoordinates()
    {
        using MediaFile media = Media.Open(Fixture("mdpm-south-west.mts"));
        GpsSample g = Assert.IsType<GpsSample>(Channel(media.Telemetry, "mdpm gps").Samples[0]);
        Assert.True(g.Latitude < -52 && g.Longitude < -13);
    }

    [Fact]
    public void ThePullPathAllocatesNothingPerDjiOrMdpmSample()
    {
        AllocationGuard.RequireOptimised();
        MediaTime pts = new(0, 1000), dur = new(33, 1000);

        // The same packet decoded over and over into one collector, as the reader does packet after packet.
        foreach (string name in new[] { "dji-text.mp4", "djmd-real.mp4", "djmd-osmo.mp4", "mdpm-gps.mts" })
        {
            using MediaFile media = Media.Open(Fixture(name));
            MediaTrack track = TelemetryChannels.TracksOf(media)[0];
            byte[] payload = [];
            long offset = 0;
            using (IEnumerator<OwnedPacket> packets = media.Packets(track).GetEnumerator())
            {
                // For the transport stream the first packets hold no SEI; take the first with the MDPM marker.
                while (packets.MoveNext())
                {
                    byte[] data = packets.Current.Data.ToArray();
                    offset = packets.Current.Info.SourceRange.Offset;
                    packets.Current.Dispose();
                    if (name != "mdpm-gps.mts" || data.AsSpan().IndexOf("MDPM"u8) >= 0)
                    {
                        payload = data;
                        break;
                    }
                }
            }

            TelemetryCollector collector = new(new DefectLog(), 1);
            DjiTextDecoder? text = name == "dji-text.mp4" ? new DjiTextDecoder(collector) : null;
            Metadata.Telemetry.Mdpm.MdpmGpsDecoder? mdpm = name == "mdpm-gps.mts" ? new Metadata.Telemetry.Mdpm.MdpmGpsDecoder(collector) : null;
            DjmdDecoder? djmd = name.StartsWith("djmd", StringComparison.Ordinal) ? new DjmdDecoder(collector) : null;
            long at = offset;
            Action<byte[]> decode = b =>
            {
                text?.Decode(b, at, pts, dur);
                mdpm?.Decode(b, at, pts, dur);
                djmd?.Decode(b, at, pts, dur);
            };
            collector.Clear();
            decode(payload);
            Assert.True(collector.Count > 0, name);
            long allocated = AllocationGuard.Best(() =>
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200; i++)
                {
                    collector.Clear();
                    decode(payload);
                }

                return GC.GetAllocatedBytesForCurrentThread() - before;
            });
            Assert.Equal(0, allocated);
        }
    }

    [Fact]
    public void HostileTextAndProtobufNeverThrow()
    {
        Random rng = new(43);
        byte[] buf = new byte[512];
        for (int round = 0; round < 300; round++)
        {
            rng.NextBytes(buf);
            Span<byte> data = buf.AsSpan(0, rng.Next(0, buf.Length));
            _ = DjiSrt.Read(data).Count;
            TelemetryCollector c = new(new DefectLog(), 1);
            new DjiTextDecoder(c).Decode(data, 0, new MediaTime(0, 1000), MediaTime.Unknown);
            new DjmdDecoder(c).Decode(data, 0, new MediaTime(0, 1000), MediaTime.Unknown);
        }
    }
}
