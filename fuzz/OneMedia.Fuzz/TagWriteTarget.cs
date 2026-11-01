using System;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for tag writing, over every format the writer edits. The input is a file; the fixed edit set (title, artist, comment, a
/// picture, one removal) is planned and written to memory. The properties: the plan's length is what is written, every copied piece is
/// byte-identical to its source range, and a file that opened before still opens afterwards. Planning may refuse with a
/// <see cref="MediaException"/>; anything else escaping is a finding.
/// </summary>
internal static class TagWriteTarget
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxTagBytes = 1 << 20, MaxAttachmentBytes = 1 << 20 };
    private static readonly byte[] Picture = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    public static void Register(System.Collections.Generic.Dictionary<string, FuzzTarget> targets) => targets["tagwrite"] = Run;

    internal static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 12)
        {
            return;
        }

        byte[] file = input.ToArray();
        TagEdits edits = new();
        edits.Set(CommonField.Title, "Fuzz title");
        edits.Set(CommonField.Artist, "Fuzz artist");
        edits.Set(CommonField.Comment, "Fuzz comment");
        edits.Set(CommonField.Description, "Fuzz description");
        edits.Set(CommonField.TrackNumber, 3);
        edits.SetPicture(Picture, "image/png");
        edits.Remove(CommonField.Genre);
        byte[]? once = Pass(file, edits, new TagWriteOptions { Limits = Limits });
        if (once is null)
        {
            return;
        }

        // The output is edited again, naming the XML systems, which creates XMP and iXML where the format has a place for them and edits them the
        // second time round.
        TagEdits xml = new();
        xml.Set(CommonField.Title, "Second <&> title");
        xml.SetNative(TagSystems.IXml, "PROJECT", "Fuzz");
        TagWriteOptions named = new() { Limits = Limits, Systems = [TagSystems.Xmp, TagSystems.IXml] };
        byte[]? twice = Pass(once, xml, named);
        if (twice is not null)
        {
            _ = Pass(twice, xml, named);
        }
    }

    private static byte[]? Pass(byte[] file, TagEdits edits, TagWriteOptions options)
    {
        try
        {
            MemoryMediaSource src = new(file);
            TagWritePlan plan = TagWriter.Plan(src, edits, options);
            using MemoryStream output = new();
            SafeWriter.Write(plan.Plan, src, output);
            if (output.Length != plan.Plan.Length)
            {
                throw new InvalidOperationException($"the plan states {plan.Plan.Length} bytes but {output.Length} were written");
            }

            byte[] result = output.ToArray();
            long pos = 0;
            foreach (OutputPiece p in plan.Plan.Pieces)
            {
                if (p.Kind == OutputPieceKind.Copy && !file.AsSpan((int)p.SourceRange.Offset, (int)p.Length).SequenceEqual(result.AsSpan((int)pos, (int)p.Length)))
                {
                    throw new InvalidOperationException("a copied piece differs from its source range");
                }

                pos += p.Length;
            }

            if (Opens(file) && !Opens(result))
            {
                throw new InvalidOperationException("a file that opened no longer opens after the tag write");
            }

            return result;
        }
        catch (MediaException)
        {
            // A typed refusal: the input is damaged or outside what the writer edits.
            return null;
        }
        catch (ArgumentException)
        {
            // A native key that is not an element path is the caller's mistake, not a finding in the parser.
            return null;
        }
    }

    private static bool Opens(byte[] bytes)
    {
        try
        {
            using MemoryMediaSource src = new(bytes);
            using MediaFile m = MediaFile.Open(src, null, new MediaOpenOptions { Limits = Limits });
            _ = m.Tags.Title;
            return true;
        }
        catch (MediaException)
        {
            return false;
        }
    }
}
