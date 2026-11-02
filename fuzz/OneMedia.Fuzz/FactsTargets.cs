using System;
using System.Collections.Generic;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Exif;
using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the fact sources outside the container tag layers. <c>exif</c>: the TIFF/Exif block reader, every entry accessor and the fact mapping.
/// <c>sidecars</c>: content sniffing and the XMP, Sony XML and DJI SRT parsers (the first byte picks one parser, or auto-detection), the file-name date
/// parser, and the default resolver over whatever came out. Anything other than a clean return is a finding.
/// </summary>
internal static class FactsTargets
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["exif"] = Exif;
        targets["sidecars"] = Sidecar;
    }

    private static MediaLimits SmallLimits() => new() { MaxTagBytes = 1 << 18, MaxDefects = 200 };

    private static void Exif(ReadOnlySpan<byte> input)
    {
        byte[] data = input.ToArray();
        DefectLog defects = new(SmallLimits());
        if (!ExifBlock.TryParse(data, 0, SmallLimits(), defects, out ExifBlock block, searchBytes: data.Length > 0 ? data[0] % 40 : 16))
        {
            return;
        }

        foreach (ExifEntry e in block.Entries)
        {
            _ = e.AsText();
            for (int i = 0; i < 4; i++)
            {
                _ = e.TryGetUInt(i, out _);
                _ = e.TryGetRational(i, out _);
            }
        }

        EvidenceSet set = block.ToEvidence();
        Resolve(set);
    }

    private static void Sidecar(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        byte pick = input[0];
        ReadOnlyMemory<byte> data = input[1..].ToArray();
        MediaLimits limits = SmallLimits();
        EvidenceSet? set = (pick % 4) switch
        {
            0 => Sidecars.ReadXmp(data, limits),
            1 => Sidecars.ReadSonyXml(data, limits),
            2 => Sidecars.ReadDjiSrt(data, limits),
            _ => Sidecars.Read(data, limits),
        };
        _ = Sidecars.Sniff(data.Span);
        _ = FileNameDates.TryParse(Encoding.Latin1.GetString(data.Span), out _, out _);
        if (set is not null)
        {
            Resolve(set);
        }
    }

    private static void Resolve(EvidenceSet set)
    {
        FactEvidence evidence = new FactEvidence { FileName = "VID_20230405_101112.mp4", FileSystemTimeUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) }.With(set);
        MediaFacts facts = DefaultFactResolver.Instance.Resolve(evidence);
        _ = facts.CaptureTime.Resolution;
        _ = facts.Device.Serial;
    }
}
