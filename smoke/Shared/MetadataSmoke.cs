using System.Text;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tests.IsoBmffSupport;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Reads tags and basic facts from a tiny MP4 and WebM built in memory, through the sync and ranged readers, and renders the mapping table.
/// Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class MetadataSmoke
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        // ISO: a QuickTime user-data title and an ISO 6709 location.
        byte[] title = Encoding.UTF8.GetBytes("Smoke title");
        byte[] location = Encoding.UTF8.GetBytes("+37.7749-122.4194/");
        byte[] udta = Mp4Builder.Box("udta",
            Mp4Builder.Box("\u00a9nam", Mp4Builder.U16((ushort)title.Length), Mp4Builder.U16(0x15C7), title),
            Mp4Builder.Box("\u00a9xyz", Mp4Builder.U16((ushort)location.Length), Mp4Builder.U16(0x15C7), location));
        Mp4Builder.TrackSpec video = new() { SampleCount = 3 };
        byte[] mp4 = Mp4Builder.Build([video], moovFirst: false, extraMoovBoxes: udta).ToArray([video]);
        using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(mp4)))
        {
            MediaMetadata m = d.ReadMetadata();
            check("metadata: mp4 title", m.Tags.Title == "Smoke title");
            check("metadata: mp4 location", m.Facts.Location.HasValue && Math.Abs(m.Facts.Location.Value.Latitude - 37.7749) < 1e-6);
        }

        // The same file through the happy path: tags, facts and native atoms on MediaFile.
        using (MediaFile media = Media.Open(mp4))
        {
            check("metadata: MediaFile tags", media.Tags.Title == "Smoke title");
            check("metadata: MediaFile location", media.Location.HasValue && media.Location.Candidates.Count > 0);
            check("metadata: MediaFile facts", media.Device is not null && media.Rotation is not null && media.CaptureTime is not null);
            NativeTag first = media.Metadata.NativeTags[0];
            check("metadata: MediaFile native", media.Native[first.System, first.Key] is not null);
            check("facts: resolution reason", media.Location.Resolution is { Reason.Length: > 0 });
        }

        // Facts: a Sony XML sidecar, an Exif block and telemetry GPS time through the default resolver.
        EvidenceSet sony = Sidecars.Read("<NonRealTimeMeta><CreationDate value=\"2016-01-03T06:12:00-04:00\"/><Device manufacturer=\"Sony\" modelName=\"X\" serialNo=\"7\"/></NonRealTimeMeta>"u8.ToArray())!;
        GpsSample fix = new(MediaTime.Zero, MediaTime.Unknown, ByteRange.None, 1, 2, Fix: GpsFixKind.Fix3D, GpsTime: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        MediaFacts facts = DefaultFactResolver.Instance.Resolve(new FactEvidence { Telemetry = [new TelemetryChannel(TelemetryKind.Gps, "smoke", "gps", [fix])] }.With(sony));
        check("facts: sidecar beats telemetry", facts.CaptureTime.Best?.Source == "sidecar:sony-xml:CreationDate" && facts.CaptureTime.Candidates.Count == 2 && facts.Device.Serial.Best?.Value == "7");
        byte[] tiff = [(byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0, 1, 0, 0x0F, 0x01, 2, 0, 2, 0, 0, 0, (byte)'A', 0, 0, 0, 0, 0, 0, 0];
        check("facts: exif", Exif.ExifBlock.TryParse(tiff, 0, MediaLimits.Default, new DefectLog(), out Exif.ExifBlock exif) && exif.ToEvidence().Make.Count == 1);

        // Matroska: a global TITLE tag, read synchronously and through a ranged source.
        byte[] simple = El(0x67C8, El(0x45A3, "TITLE"u8.ToArray()), El(0x4487, "Smoke title"u8.ToArray()));
        byte[] tags = El(0x1254C367, El(0x7373, El(0x63C0, []), simple));
        byte[] mkv = Build(tags);
        using (MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(mkv)))
        {
            check("metadata: matroska title", d.ReadMetadata().Tags.Title == "Smoke title");
        }

        await using (RangedMediaSource ranged = new(mkv.Length, (offset, buffer, _) =>
        {
            int count = (int)Math.Min(buffer.Length, mkv.Length - offset);
            mkv.AsMemory((int)offset, count).CopyTo(buffer);
            return new ValueTask<int>(count);
        }))
        {
            await using MatroskaAsyncDemuxer a = await MatroskaAsyncDemuxer.OpenAsync(ranged);
            MediaMetadata m = await a.ReadMetadataAsync();
            check("metadata: matroska ranged title", m.Tags.Title == "Smoke title");
        }

        await using (MediaFile media = await Media.OpenAsync(mkv.Length, (offset, buffer, _) =>
        {
            int count = (int)Math.Min(buffer.Length, mkv.Length - offset);
            mkv.AsMemory((int)offset, count).CopyTo(buffer);
            return new ValueTask<int>(count);
        }))
        {
            MediaMetadata m = await media.ReadMetadataAsync();
            check("metadata: MediaFile async title", m.Tags.Title == "Smoke title" && media.Tags.Title == "Smoke title");
        }

        string table = TagMappingTable.RenderMarkdown();
        check("metadata: mapping table", TagMappingTable.Rows.Count > 50 && table.Contains("`TITLE`", StringComparison.Ordinal));
    }

    private static byte[] El(uint id, params byte[][] parts)
    {
        List<byte> payload = [];
        foreach (byte[] part in parts)
        {
            payload.AddRange(part);
        }

        List<byte> bytes = [];
        int length = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        for (int i = length - 1; i >= 0; i--)
        {
            bytes.Add((byte)(id >> (8 * i)));
        }

        bytes.Add((byte)(0x80 | payload.Count)); // payloads here are all under 127 bytes
        bytes.AddRange(payload);
        return [.. bytes];
    }

    private static byte[] Build(byte[] tags)
    {
        byte[] header = El(0x1A45DFA3, El(0x4282, "webm"u8.ToArray()));
        byte[] info = El(0x1549A966, El(0x2AD7B1, [0x0F, 0x42, 0x40]));
        byte[] tracks = El(0x1654AE6B, El(0xAE, El(0xD7, [1]), El(0x83, [1]), El(0x86, "V_VP8"u8.ToArray())));
        byte[] cluster = El(0x1F43B675, El(0xE7, [0]), El(0xA3, [0x81, 0, 0, 0x80, 0x11, 0x22]));
        return [.. header, .. El(0x18538067, info, tracks, tags, cluster)];
    }
}
