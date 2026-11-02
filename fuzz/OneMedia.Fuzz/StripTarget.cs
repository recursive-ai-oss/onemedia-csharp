using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Metadata.Telemetry.Mebx;
using RecursiveAI.OneMedia.Privacy;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the privacy strip. <c>strip-bytes</c> takes a selector byte (bit 0: location, bit 1: device ids; both when neither is set), a key length
/// byte and that many bytes of mebx key table, then one block that every block scrubber (GPMF, CAMM, mebx, Exif, XMP, GoPro settings, secrets, djmd, text) is run on: the
/// patches they make must stay inside the block and keep its length. <c>strip-file</c> takes the selector byte and a whole file: the plan must have the file's length
/// when the layout is kept, and applying it must not throw anything but the library's own exceptions.
/// </summary>
internal static class StripTarget
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["strip-bytes"] = Bytes;
        targets["strip-file"] = StripFile;
    }

    private static StripCategories Categories(byte selector) => (selector & 3) == 0 ? StripCategories.All : (StripCategories)(selector & 3);

    private static void Bytes(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        StripCategories cats = Categories(input[0]);
        int keyBytes = Math.Min(input[1] * 8, input.Length - 2);
        _ = MebxKeyTable.TryParse(input.Slice(2, keyBytes), out MebxKeyTable table);
        byte[] block = input[(2 + keyBytes)..].ToArray();
        const long Base = 1000;
        PatchSet patches = new();
        StripSink sink = new(patches);
        TelemetryScrub.Gpmf(block, Base, "gpmd", 1, cats, sink);
        TelemetryScrub.Camm(block, Base, 1, cats, sink);
        TelemetryScrub.Mebx(block, Base, 1, table, cats, sink);
        TiffScrub.Scrub(block, Base, "exif", 1, cats, sink);
        _ = DjiScrub.Djmd(block, Base, 1, cats, sink);
        TextScrub.Sample(block, Base, "text", 1, cats, sink);
        byte[] firmware = block.Length >= 8 ? block[..6] : [];
        TelemetryScrub.GoProSettings(block, Base, firmware, "fdsc", 1, cats, sink);
        if (block.Length >= 12)
        {
            TelemetryScrub.BySecrets(block, Base, "fdsc", 1, cats, [new Secret(StripCategories.All, "s", block[..6])], sink);
        }

        List<XmpScrub.Removed> removed = [];
        byte[]? xmp = XmpScrub.Scrub(block, cats, removed, out _);
        if (xmp is not null && xmp.Length != block.Length)
        {
            throw new InvalidOperationException("the XMP rewrite changed the length");
        }

        long length = Base + block.Length + 16;
        EditPlan plan = patches.ToPlan(length);
        if (plan.Length != length || !plan.IsSameLayout)
        {
            throw new InvalidOperationException("the plan changed the layout");
        }

        foreach (StripEntry e in sink.Entries())
        {
            if (e.Range.Offset < Base - 2 || e.Range.End > length || e.Bytes < 0)
            {
                throw new InvalidOperationException("an entry points outside the block");
            }
        }
    }

    private static void StripFile(ReadOnlySpan<byte> input)
    {
        if (input.Length < 9)
        {
            return;
        }

        StripOptions options = new()
        {
            Categories = Categories(input[0]),
            Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxSampleTableEntries = 2_000_000, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20, MaxTagBytes = 1 << 20 },
        };
        byte[] file = input[1..].ToArray();
        try
        {
            using MemoryMediaSource source = new(file);
            StripPlan plan = PrivacyStrip.Plan(source, options);
            if (plan.Plan.IsSameLayout && plan.Plan.Length != file.Length)
            {
                throw new InvalidOperationException("same layout, different length");
            }

            using MemoryStream output = new();
            SafeWriter.Write(plan.Plan, source, output);
            if (output.Length != plan.Plan.Length)
            {
                throw new InvalidOperationException("the output is not as long as the plan");
            }
        }
        catch (MediaException)
        {
            // Not a file the strip can read, or a damaged one: a typed refusal is the right answer.
        }
        catch (IOException)
        {
            // Writers refuse inputs they cannot reproduce.
        }
    }
}
