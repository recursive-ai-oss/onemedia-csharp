using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Metadata.Telemetry;
using RecursiveAI.OneMedia.Metadata.Telemetry.Camm;
using RecursiveAI.OneMedia.Metadata.Telemetry.Gpmf;
using RecursiveAI.OneMedia.Metadata.Telemetry.Mebx;

namespace RecursiveAI.OneMedia.Tests.Telemetry;

// The fixtures are written by tools/gen-telemetry-fixtures.py from the published format descriptions; nothing in them comes from a device.
[Collection(AllocationMeasurements.Name)]
public class TelemetryTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "telemetry", name);

    private static List<(TelemetryItem Item, string Channel, byte[] Raw)> ReadAll(string file, out List<TelemetryChannelInfo> channels, out DefectLog defects)
    {
        using MediaFile media = Media.Open(Fixture(file));
        MediaTrack track = TelemetryChannels.TracksOf(media).Single();
        List<(TelemetryItem, string, byte[])> items = [];
        using TelemetryTrackReader reader = media.OpenTelemetry(track);
        while (reader.TryRead(out TelemetryItem item))
        {
            items.Add((item, reader.Channels[item.ChannelIndex].Name, item.IsRaw ? reader.RawData(item).ToArray() : []));
        }

        channels = [.. reader.Channels];
        defects = media.Defects;
        return items;
    }

    [Fact]
    public void KlvReaderWalksNestedBlocksAndReportsATruncatedTail()
    {
        // DEVC (nested) holding DVID 'L' 4 1 and a text DVNM, then a triplet whose data runs past the end.
        byte[] devc = [(byte)'D', (byte)'V', (byte)'I', (byte)'D', (byte)'L', 4, 0, 1, 0, 0, 0, 7, (byte)'D', (byte)'V', (byte)'N', (byte)'M', (byte)'c', 1, 0, 3, (byte)'A', (byte)'B', (byte)'C', 0];
        byte[] block = [(byte)'D', (byte)'E', (byte)'V', (byte)'C', 0, 4, 0, (byte)(devc.Length / 4), .. devc, (byte)'X', (byte)'Y', (byte)'Z', (byte)'W', (byte)'B', 1, 0, 200, 1, 2, 3, 4];
        GpmfKlvReader r = new(block);
        Assert.True(r.TryRead(out GpmfKlv devcKlv));
        Assert.True(devcKlv.IsNested);
        Assert.Equal(GpmfKlv.MakeKey("DEVC"), devcKlv.Key);
        Assert.Equal(devc.Length, devcKlv.Data.Length);
        GpmfKlvReader inner = new(devcKlv.Data);
        Assert.True(inner.TryRead(out GpmfKlv dvid));
        Assert.Equal(7, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(dvid.Data));
        Assert.True(inner.TryRead(out GpmfKlv dvnm));
        Assert.Equal(3 * 1, dvnm.StructSize * dvnm.Repeat);
        Assert.False(inner.TryRead(out _));
        Assert.False(inner.Truncated);
        Assert.False(r.TryRead(out _));
        Assert.True(r.Truncated);
    }

    [Fact]
    public void GpmfSyntheticFileDecodesEveryStream()
    {
        List<(TelemetryItem Item, string Channel, byte[] Raw)> items = ReadAll("gpmf-synthetic.mp4", out List<TelemetryChannelInfo> channels, out DefectLog defects);

        TelemetryItem a0 = items.First(i => i.Channel == "ACCL").Item;
        Assert.Equal(TelemetryKind.Accelerometer, a0.Kind);
        Assert.Equal([0.0, 200 / 418.0, 100 / 418.0], a0.Values.ToArray());
        Assert.Equal(new MediaTime(0, 1000 * 40), a0.Time);
        TelemetryChannelInfo accl = channels.Single(c => c.Name == "ACCL");
        Assert.Equal(["m/s²"], accl.Units);
        Assert.Equal("Accelerometer", accl.Label);
        Assert.Equal("ZXY", accl.AxisOrder);
        Assert.Equal("Camera", accl.DeviceName);

        // Samples spread evenly over the 1.001 s payload, exactly: sample 1 of 40 starts at 1001/40 ms.
        TelemetryItem a1 = items.Where(i => i.Channel == "ACCL").Skip(1).First().Item;
        Assert.Equal(new MediaTime(1001, 1000 * 40), a1.Time);
        Assert.Equal(0, a1.Time.CompareTo(new MediaTime(1001, 40000)));

        List<TelemetryItem> gps = [.. items.Where(i => i.Channel == "GPS5").Select(i => i.Item)];
        Assert.Equal(27, gps.Count); // nine per payload; the cut-off third payload keeps the streams before the damage
        Assert.Equal(33.1264968, gps[0].Values[0], 9);
        Assert.Equal(-117.3273541, gps[0].Values[1], 9);
        Assert.Equal(-20.102, gps[0].Values[2], 9);
        Assert.Equal(0.64, gps[0].Values[3], 9);
        Assert.Equal(6.84, gps[0].Values[4], 9);
        Assert.Equal(6.06, gps[0].Values[5], 9);
        Assert.Equal(GpsFixKind.Fix3D, gps[0].Fix);
        Assert.Equal(new DateTime(2017, 4, 17, 17, 31, 3, DateTimeKind.Utc).Ticks, gps[0].GpsTimeTicks);
        Assert.Equal(new DateTime(2017, 4, 17, 17, 31, 4, DateTimeKind.Utc).Ticks, gps[9].GpsTimeTicks);

        // GPS9 carries its own time: days since 2000-01-01 and seconds since midnight.
        TelemetryItem g9 = items.First(i => i.Channel == "GPS9").Item;
        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(8000).AddSeconds(5520).Ticks, g9.GpsTimeTicks);
        Assert.Equal(1.2, g9.Values[5], 9);
        Assert.Equal(GpsFixKind.Fix3D, g9.Fix);

        TelemetryItem shut = items.First(i => i.Channel == "SHUT").Item;
        Assert.Equal(TelemetryKind.CameraSettings, shut.Kind);
        Assert.Equal(1 / 1000.0, shut.Values[0], 6);

        // A nested block with its own TICK/TOCK holds the face samples; the two face rows of a payload come out as two samples; the cut-off payload lost its faces.
        Assert.Equal(4, items.Count(i => i.Channel == "FACE"));

        // Keys the decoder does not interpret are not lost: the camera model (a device field) stays as the bytes stored.
        (TelemetryItem item, _, byte[] raw) = items.First(i => i.Channel == "MINF");
        Assert.True(item.IsRaw);
        Assert.Equal("SYNTHETIC CAMERA", System.Text.Encoding.ASCII.GetString(raw));
        Assert.True(item.Range.Length == 16 && !item.Range.IsNone);

        // The third payload stops inside a triplet: what precedes the damage still decodes, and the defect carries an offset.
        Assert.Contains(defects, d => d.Message.Contains("GPMF", StringComparison.Ordinal) && d.Code == "gpmf.truncated");
    }

    [Fact]
    public void GpmfSamplesKnowTheirBytesInTheFile()
    {
        string path = Fixture("gpmf-synthetic.mp4");
        byte[] file = File.ReadAllBytes(path);
        List<(TelemetryItem Item, string Channel, byte[] Raw)> items = ReadAll("gpmf-synthetic.mp4", out _, out _);
        TelemetryItem first = items.First(i => i.Channel == "ACCL").Item;
        // One 6-byte structure: x = 0, y = 200, z = 100 as stored (big-endian shorts), at the range the sample reports.
        Assert.Equal(6, first.Range.Length);
        Assert.Equal([0, 0, 0, 200, 0, 100], file.AsSpan((int)first.Range.Offset, 6).ToArray());
    }

    [Fact]
    public void MediaFileExposesChannelsAndFactCandidates()
    {
        using MediaFile media = Media.Open(Fixture("gpmf-synthetic.mp4"));
        IReadOnlyList<TelemetryChannel> channels = media.Telemetry;
        Assert.Same(channels, media.Telemetry);
        TelemetryChannel gps = channels.Single(c => c.Name == "GPS5");
        Assert.Equal(TelemetryKind.Gps, gps.Kind);
        Assert.Equal("gpmf", gps.Format);
        Assert.Equal(["deg", "deg", "m", "m/s", "m/s"], gps.Units);
        GpsSample s = Assert.IsType<GpsSample>(gps.Samples[0]);
        Assert.Equal(GpsFixKind.Fix3D, s.Fix);
        Assert.Equal(6.06, s.Dop);
        Assert.Equal(new DateTime(2017, 4, 17, 17, 31, 3, DateTimeKind.Utc), s.GpsTime);

        Assert.IsType<Vector3Sample>(channels.Single(c => c.Name == "ACCL").Samples[0]);
        CameraSettingsSample shut = Assert.IsType<CameraSettingsSample>(channels.Single(c => c.Name == "SHUT").Samples[0]);
        Assert.True(shut.ExposureTime is { IsKnown: true });
        Assert.Contains(channels, c => c.Kind == TelemetryKind.Unknown && c.Name == "MINF");

        FactCandidate<GeoLocation> where = Assert.Single(TelemetryFacts.LocationCandidates(channels));
        Assert.Equal(33.1264968, where.Value.Latitude, 9);
        Assert.Equal(TimeZoneCertainty.Utc, where.Certainty);
        FactCandidate<MediaDateTime> when = Assert.Single(TelemetryFacts.CaptureTimeCandidates(channels));
        Assert.Equal(new DateTime(2017, 4, 17, 17, 31, 3, DateTimeKind.Utc), when.Value.Value);
        Assert.Equal(TimeZoneCertainty.Utc, when.Value.Certainty);
    }

    [Fact]
    public void CammDecodesEveryPacketType()
    {
        List<(TelemetryItem Item, string Channel, byte[] Raw)> items = ReadAll("camm-all-types.mp4", out _, out DefectLog defects);
        TelemetryItem T(int type) => items.Single(i => i.Channel == "camm type " + type).Item;

        Assert.Equal(TelemetryKind.Orientation, T(0).Kind);
        // Angle axis (0, 0.5, 0.25): angle = |v|, quaternion = (cos a/2, sin a/2 · axis).
        double angle = Math.Sqrt(0.3125);
        Assert.Equal(Math.Cos(angle / 2), T(0).Values[0], 9);
        Assert.Equal(0.5 * Math.Sin(angle / 2) / angle, T(0).Values[2], 9);
        Assert.Equal(0.5, T(0).Values[5], 9);

        Assert.Equal([8_000_000.0, 25_000_000.0], T(1).Values.ToArray());
        Assert.Equal([0.125, -0.25, 0.5], T(2).Values.ToArray());
        Assert.Equal(TelemetryKind.Gyroscope, T(2).Kind);
        Assert.Equal([0.0, 9.8125, 0.25], T(3).Values.ToArray());
        Assert.Equal([1.5, 2.5, -3.5], T(4).Values.ToArray());

        TelemetryItem g5 = T(5);
        Assert.Equal(47.6205, g5.Values[0]);
        Assert.Equal(-122.3493, g5.Values[1]);
        Assert.Equal(184.5, g5.Values[2]);
        Assert.Equal(0, g5.GpsTimeTicks);

        TelemetryItem g6 = T(6);
        Assert.Equal(GpsFixKind.Fix3D, g6.Fix);
        Assert.Equal(Math.Sqrt(5), g6.Values[3], 6);
        Assert.Equal(Math.Sqrt(5.25), g6.Values[4], 6);
        Assert.Equal(3.5, g6.Values[7]);
        // 1,272,000,000.5 s on the GPS scale is 2020-04-27 05:19:42.5 UTC (18 leap seconds by then).
        Assert.Equal(new DateTime(2020, 4, 27, 5, 19, 42, 500, DateTimeKind.Utc).Ticks, g6.GpsTimeTicks);
        Assert.Equal(1272000000.5, g6.Values[13]);

        Assert.Equal(TelemetryKind.Magnetometer, T(7).Kind);
        Assert.Equal([22.5, -41.25, 18.0], T(7).Values.ToArray());

        // A type from a later revision of the format stays as bytes; a short sample is a typed defect and is skipped.
        (TelemetryItem unknown, _, byte[] raw) = items.Single(i => i.Channel == "camm type 9");
        Assert.True(unknown.IsRaw);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], raw);
        Assert.Contains(defects, d => d.Code == "camm.short");
        Assert.Equal(new MediaTime(100 * 3, 1000), T(3).Time);
    }

    [Fact]
    public void CammConvertsGpsSecondsToUtcAcrossLeapSeconds()
    {
        // 1980-01-06 is the epoch; the first leap second came on 1981-07-01.
        Assert.Equal(new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc).Ticks, CammPacket.GpsSecondsToUtcTicks(0));
        double justBefore = (new DateTime(1981, 7, 1, 0, 0, 0, DateTimeKind.Utc) - new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        Assert.Equal(new DateTime(1981, 7, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, CammPacket.GpsSecondsToUtcTicks(justBefore));
        Assert.Equal(new DateTime(1981, 7, 1, 0, 0, 1, DateTimeKind.Utc).Ticks, CammPacket.GpsSecondsToUtcTicks(justBefore + 2));
        Assert.Equal(0, CammPacket.GpsSecondsToUtcTicks(double.NaN));
        Assert.Equal(0, CammPacket.GpsSecondsToUtcTicks(-1));
    }

    [Fact]
    public void MebxKeysAndItemsDecode()
    {
        using MediaFile media = Media.Open(Fixture("mebx-synthetic.mp4"));
        IReadOnlyList<TelemetryChannel> channels = media.Telemetry;

        IReadOnlyList<TelemetrySample> loc = channels.Single(c => c.Name == "com.apple.quicktime.location.ISO6709" && c.Kind == TelemetryKind.Gps).Samples;
        Assert.Equal(2, loc.Count);
        GpsSample first = Assert.IsType<GpsSample>(loc[0]);
        Assert.Equal(47.6205, first.Latitude, 9);
        Assert.Equal(-122.3493, first.Longitude, 9);
        Assert.Equal(184.5, first.Altitude);
        Assert.Equal(GpsFixKind.Fix3D, first.Fix);
        Assert.Equal(new MediaTime(0, 600), first.Time);
        GpsSample second = Assert.IsType<GpsSample>(loc[1]);
        Assert.Null(second.Altitude);
        Assert.Equal(GpsFixKind.Fix2D, second.Fix);
        Assert.Equal(new MediaTime(600, 600), second.Time);

        Assert.Equal([90.0], Assert.IsType<ScalarSample>(channels.Single(c => c.Name == "com.apple.quicktime.video-orientation" && c.Kind == TelemetryKind.Scalar).Samples[0]).Values.ToArray());
        Assert.Equal([-1.0], Assert.IsType<ScalarSample>(channels.Single(c => c.Name.EndsWith("still-image-time", StringComparison.Ordinal)).Samples[0]).Values.ToArray());

        // The detected-face group nests atoms typed by sub-key ids; each leaf decodes by its own declared type.
        Assert.Equal([0.25, 0.125, 0.5, 0.5], Assert.IsType<ScalarSample>(channels.Single(c => c.Name.EndsWith("detected-face.bounds", StringComparison.Ordinal)).Samples[0]).Values.ToArray());
        Assert.Equal([7.0], Assert.IsType<ScalarSample>(channels.Single(c => c.Name.EndsWith("face-id", StringComparison.Ordinal)).Samples[0]).Values.ToArray());
        Assert.Equal([0.5], Assert.IsType<ScalarSample>(channels.Single(c => c.Name.EndsWith("roll-angle", StringComparison.Ordinal)).Samples[0]).Values.ToArray());

        // Text of a key we do not interpret, an unparsable location and a value of the wrong length all stay as stored bytes.
        RawTelemetrySample note = Assert.IsType<RawTelemetrySample>(channels.Single(c => c.Name == "com.example.note").Samples[0]);
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(note.Data.Span));
        Assert.Equal("1", note.TypeCode);
        RawTelemetrySample bad = Assert.IsType<RawTelemetrySample>(channels.Single(c => c.Name == "com.apple.quicktime.location.ISO6709" && c.Kind == TelemetryKind.Unknown).Samples[0]);
        Assert.Equal("not a location", System.Text.Encoding.ASCII.GetString(bad.Data.Span));
        Assert.Single(channels.Single(c => c.Name == "com.apple.quicktime.video-orientation" && c.Kind == TelemetryKind.Unknown).Samples);
    }

    [Fact]
    public void MebxKeyTableParsesAndToleratesDamage()
    {
        byte[] entry = File.ReadAllBytes(Fixture("mebx-synthetic.mp4"));
        int at = entry.AsSpan().IndexOf("keys"u8) - 4;
        int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(entry.AsSpan(at));
        byte[] keys = entry.AsSpan(at, size).ToArray();
        Assert.True(MebxKeyTable.TryParse(keys, out MebxKeyTable table));
        Assert.Equal(9, table.Keys.Count);
        MebxKey location = table.Find(1)!;
        Assert.Equal("com.apple.quicktime.location.ISO6709", location.Key);
        Assert.Equal(MebxDataTypes.Utf8, location.DataType);
        Assert.Equal(0u, location.DataTypeNamespace);
        Assert.Null(table.Find(99));

        Assert.False(MebxKeyTable.TryParse(keys.AsSpan(0, keys.Length - 5), out MebxKeyTable damaged));
        Assert.True(damaged.Keys.Count < 9);
    }

    [Trait("Browser", "allocation")]
    [Fact]
    public void ThePullPathAllocatesNothingPerSample()
    {
        AllocationGuard.RequireOptimised();
        using MediaFile media = Media.Open(Fixture("gpmf-synthetic.mp4"));
        MediaTrack track = TelemetryChannels.TracksOf(media).Single();
        byte[] payload;
        long offset;
        using (IEnumerator<OwnedPacket> packets = media.Packets(track).GetEnumerator())
        {
            Assert.True(packets.MoveNext());
            payload = packets.Current.Data.ToArray();
            offset = packets.Current.Info.SourceRange.Offset;
            packets.Current.Dispose();
        }

        // Decode the same payload repeatedly into one collector, as the reader does packet after packet.
        DefectLog log = new();
        TelemetryCollector collector = new(log, 1);
        GpmfDecoder decoder = new(collector);
        MediaTime pts = new(0, 1000), dur = new(1001, 1000);
        collector.Clear();
        decoder.Decode(payload, offset, pts, dur); // warm-up: discovers the channels, grows buffers
        int perPacket = collector.Count;
        Assert.True(perPacket > 50);

        long allocated = AllocationGuard.Best(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                collector.Clear();
                decoder.Decode(payload, offset, pts, dur);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.Equal(0, allocated);

        byte[] camm = [0, 0, 6, 0, .. new byte[56]];
        CammDecoder cd = new(collector);
        collector.Clear();
        cd.Decode(camm, 0, pts, dur);
        long cammAllocated = AllocationGuard.Best(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                collector.Clear();
                cd.Decode(camm, 0, pts, dur);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.Equal(0, cammAllocated);
    }

    [Fact]
    public void ARawSampleInAStreamThatWasTypedStaysRaw()
    {
        // Found by fuzzing: a later payload gave the key a structure the decoder cannot read, so its sample is delivered as bytes inside a channel
        // that earlier held numbers. Materialising the channels used to read numbers that were not there.
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "telemetry-file", "gpmf-raw-item-in-typed-channel.bin"));
        using MediaFile media = MediaFile.Open(new MemoryMediaSource(input.AsSpan(1).ToArray()));
        IReadOnlyList<TelemetryChannel> channels = media.Telemetry;
        Assert.NotEmpty(channels);
        foreach (TelemetryChannel c in channels)
        {
            Assert.All(c.Samples, s => Assert.Equal(c.Kind == TelemetryKind.Unknown, s is RawTelemetrySample));
        }
    }

    [Trait("Browser", "timing")]
    [Fact]
    public void ALongChunkOfEqualSizedSamplesIsSeekedInConstantTime()
    {
        // Found by fuzzing: 655,360 one-byte samples in a single chunk. Locating a sample summed the sizes before it in the chunk, and finding the
        // sample a seek lands on walked back one sample at a time, so opening the track took about 80 seconds.
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "telemetry-file", "stts-huge-run-constant-size-chunk.bin"));
        Task opened = Task.Run(() =>
        {
            using MediaFile media = MediaFile.Open(new MemoryMediaSource(input.AsSpan(1).ToArray()));
            _ = media.Telemetry;
        });
        Assert.True(opened.Wait(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ImplausibleGpsTimesGiveNoTime()
    {
        // Found by fuzzing: a GPS9 day count far outside any calendar made the sample's time a DateTime that cannot exist.
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "telemetry-file", "gps9-time-out-of-range.bin"));
        using MediaFile media = MediaFile.Open(new MemoryMediaSource(input.AsSpan(1).ToArray()));
        Assert.NotEmpty(media.Telemetry);
    }

    [Fact]
    public void AxesRemapToTheRequestedOrderAndSign()
    {
        // HERO6 stores ACCL as Y, -X, Z (ORIN "YxZ") and asks for Z, X, Y (ORIO "ZXY").
        double[] stored = [1, 2, 3];
        double[] camera = new double[3];
        Assert.True(GpmfAxes.TryRemap(stored, "YxZ", "ZXY", camera));
        Assert.Equal([3.0, -2.0, 1.0], camera);
        Assert.True(GpmfAxes.TryRemap(stored, "ZXY", "ZXY", camera));
        Assert.Equal(stored, camera);
        Assert.False(GpmfAxes.TryRemap(stored, "YxZ", "ZXW", camera));
        Assert.False(GpmfAxes.TryRemap(stored, "YxZ", "ZX", camera));
    }

    [Fact]
    public void HostileGpmfNeverThrows()
    {
        DefectLog log = new();
        TelemetryCollector collector = new(log, 1);
        GpmfDecoder decoder = new(collector);
        MediaTime pts = new(0, 1000), dur = new(1000, 1000);
        byte[] seed = [(byte)'D', (byte)'E', (byte)'V', (byte)'C', 0, 4, 0, 6, (byte)'S', (byte)'T', (byte)'R', (byte)'M', 0, 4, 0, 4, (byte)'A', (byte)'C', (byte)'C', (byte)'L', (byte)'s', 255, 255, 255, 1, 2, 3, 4, 5, 6, 7, 8];
        Random rng = new(42);
        for (int i = 0; i < 2000; i++)
        {
            byte[] data = [.. seed, .. new byte[rng.Next(0, 64)]];
            rng.NextBytes(data.AsSpan(rng.Next(0, data.Length)));
            collector.Clear();
            decoder.Decode(data, 0, pts, dur);
            Assert.True(collector.Count <= TelemetryCollector.MaxItems);
        }

        // A structure size of 255 and a repeat of 65535 over a short payload is a defect, not an allocation.
        collector.Clear();
        decoder.Decode([(byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'B', 255, 255, 255, 1, 2, 3, 4], 0, pts, dur);
        Assert.Equal(0, collector.Count);
    }
}
