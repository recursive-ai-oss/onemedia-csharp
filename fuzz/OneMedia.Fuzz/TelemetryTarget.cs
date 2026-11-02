using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Metadata.Telemetry;
using RecursiveAI.OneMedia.Metadata.Telemetry.Camm;
using RecursiveAI.OneMedia.Metadata.Telemetry.Dji;
using RecursiveAI.OneMedia.Metadata.Telemetry.Gpmf;
using RecursiveAI.OneMedia.Metadata.Telemetry.Mdpm;
using RecursiveAI.OneMedia.Metadata.Telemetry.Mebx;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the telemetry decoders. <c>telemetry-gpmf</c> and <c>telemetry-camm</c> take one payload; <c>telemetry-mebx</c> takes a key
/// length byte, that many bytes of key table, then one sample; <c>telemetry-file</c> takes a selector byte (bit 0: forward-only, bit 1: materialise all
/// channels) and an ISO file. None may throw for any input, and the items they produce must respect their own limits.
/// </summary>
internal static class TelemetryTarget
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["telemetry-gpmf"] = Gpmf;
        targets["telemetry-camm"] = Camm;
        targets["telemetry-mebx"] = Mebx;
        targets["telemetry-file"] = File;
        targets["telemetry-dji-text"] = DjiText;
        targets["telemetry-djmd"] = Djmd;
        targets["telemetry-mdpm"] = MdpmGps;
    }

    private static void Check(TelemetryCollector c)
    {
        if (c.Count > TelemetryCollector.MaxItems)
        {
            throw new InvalidOperationException("more items than the cap");
        }

        for (int i = 0; i < c.Count; i++)
        {
            TelemetryItem item = c.Items[i];
            if (item.Count is < 0 or > TelemetryItem.MaxComponents || item.ChannelIndex < 0 || item.ChannelIndex >= c.Channels.Count)
            {
                throw new InvalidOperationException("item out of range");
            }
        }
    }

    private static void Gpmf(ReadOnlySpan<byte> data)
    {
        DefectLog log = new();
        TelemetryCollector c = new(log, 1);
        GpmfDecoder d = new(c);
        // Twice, so that state carried between payloads (stream lookup, device fields already seen) is exercised too.
        for (int i = 0; i < 2; i++)
        {
            c.Clear();
            d.Decode(data, 0, new MediaTime(i * 1001, 1000), new MediaTime(1001, 1000));
            Check(c);
        }

        GpmfKlvReader r = new(data);
        int n = 0;
        while (n++ < 100_000 && r.TryRead(out GpmfKlv klv))
        {
            if (klv.Data.Length > 0 && GpmfTypes.IsNumeric(klv.Type) && klv.Data.Length >= GpmfTypes.SizeOf(klv.Type))
            {
                _ = GpmfTypes.ReadNumber(klv.Type, klv.Data);
            }
        }
    }

    private static void Camm(ReadOnlySpan<byte> data)
    {
        DefectLog log = new();
        TelemetryCollector c = new(log, 1);
        CammDecoder d = new(c);
        d.Decode(data, 0, new MediaTime(0, 1000), new MediaTime(100, 1000));
        Check(c);
        if (data.Length >= 12 && CammPacket.TryReadType(data, out int type) && type == 6)
        {
            _ = CammPacket.GpsSecondsToUtcTicks(BitConverter.ToDouble(data[4..]));
        }
    }

    private static void Mebx(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return;
        }

        int keyBytes = Math.Min(data[0] * 8, data.Length - 1);
        _ = MebxKeyTable.TryParse(data.Slice(1, keyBytes), out MebxKeyTable table);
        DefectLog log = new();
        TelemetryCollector c = new(log, 1);
        MebxDecoder d = new(c, [table]);
        c.Clear();
        d.Decode(data[(1 + keyBytes)..], 0, new MediaTime(0, 600), new MediaTime(600, 600), 0);
        Check(c);
    }

    // One text sample through the decoder (twice, so carried state is exercised), and the same bytes as a whole sidecar.
    private static void DjiText(ReadOnlySpan<byte> data)
    {
        DefectLog log = new();
        TelemetryCollector c = new(log, 1);
        DjiTextDecoder d = new(c);
        for (int i = 0; i < 2; i++)
        {
            c.Clear();
            d.Decode(data, 0, new MediaTime(i * 33, 1000), new MediaTime(33, 1000));
            Check(c);
        }

        _ = DjiSrt.Read(data).Count;
    }

    // One protobuf message through the generic reader and the djmd mapper; the first byte picks the protocol so every layout is reached.
    private static void Djmd(ReadOnlySpan<byte> data)
    {
        string[] protocols = ["wm265e", "wm260", "Mavic4", "Air3", "Mini4_Pro", "AVATA2", "NEO2", "ac203", "oq101"];
        DefectLog log = new();
        TelemetryCollector c = new(log, 1);
        DjmdDecoder d = new(c);
        if (data.Length > 0)
        {
            byte[] header = System.Text.Encoding.ASCII.GetBytes("dvtm_" + protocols[data[0] % protocols.Length] + ".proto");
            byte[] prefix = [0x0A, (byte)(header.Length + 4), 0x0A, (byte)(header.Length + 2), 0x0A, (byte)header.Length, .. header];
            c.Clear();
            d.Decode([.. prefix, .. data[1..].ToArray()], 0, new MediaTime(0, 1000), new MediaTime(100, 1000));
            Check(c);
        }

        c.Clear();
        d.Decode(data, 0, new MediaTime(0, 1000), new MediaTime(100, 1000));
        Check(c);
        ProtobufReader r = new(data);
        int n = 0;
        while (n++ < 100_000 && r.TryRead(out _, out _, out _, out _, out _))
        {
        }
    }

    // A packet of video as the transport stream reader hands it over: start codes and NAL units, some of them SEI with MDPM.
    private static void MdpmGps(ReadOnlySpan<byte> data)
    {
        DefectLog log = new();
        TelemetryCollector c = new(log, 1);
        MdpmGpsDecoder d = new(c);
        c.Clear();
        d.Decode(data, 0, new MediaTime(0, 90000), new MediaTime(3600, 90000));
        Check(c);
    }

    private static void File(ReadOnlySpan<byte> input)
    {
        if (input.Length < 9)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool materialise = (input[0] & 2) != 0;
        byte[] file = input[1..].ToArray();
        MediaOpenOptions options = new()
        {
            Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxSampleTableEntries = 2_000_000, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20 },
        };
        try
        {
            using MediaFile media = forward
                ? MediaFile.Open(new StreamForwardSource(new MemoryStream(file)), options: options)
                : MediaFile.Open(new MemoryMediaSource(file), options: options);
            foreach (MediaTrack track in TelemetryChannels.TracksOf(media))
            {
                using TelemetryTrackReader reader = media.OpenTelemetry(track);
                int n = 0;
                while (n++ < 200_000 && reader.TryRead(out TelemetryItem item))
                {
                    if (item.IsRaw)
                    {
                        _ = reader.RawData(item).Length;
                    }
                }
            }

            if (materialise && !forward)
            {
                _ = media.Telemetry.Count;
            }
        }
        catch (MediaException)
        {
            // Typed failures are the contract for damaged input.
        }
    }
}
