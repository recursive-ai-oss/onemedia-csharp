using System;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the structure writers, one per family (ISO boxes, EBML, RIFF). The first input byte picks the path (bit 0: re-emit every
/// header; bit 1: also append a padding element and read the result back); the rest is the file. The property: an untouched file written
/// back is byte-identical, whatever its damage (headers are only re-emitted when asked, and then only well-formed input must come back
/// identical; EBML is exempt because re-emitting recomputes every CRC-32 element, so a stored checksum that is wrong changes, which is the intended repair). Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class WriterTarget
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24 };

    public static void Register(System.Collections.Generic.Dictionary<string, FuzzTarget> targets)
    {
        targets["writer-iso"] = span => Run(span, Family.Iso);
        targets["writer-ebml"] = span => Run(span, Family.Ebml);
        targets["writer-riff"] = span => Run(span, Family.Riff);
    }

    internal enum Family { Iso, Ebml, Riff }

    internal static void Run(ReadOnlySpan<byte> input, Family family)
    {
        if (input.Length < 9)
        {
            return;
        }

        bool strict = (input[0] & 1) != 0;
        bool append = (input[0] & 2) != 0;
        byte[] file = input[1..].ToArray();
        try
        {
            MemoryMediaSource src = new(file);
            DefectLog log = new();
            (EditPlan plan, bool clean) = Plan(src, family, strict, append, log);
            using MemoryStream output = new();
            SafeWriter.Write(plan, src, output);
            if (output.Length != plan.Length)
            {
                throw new InvalidOperationException($"the plan states {plan.Length} bytes but {output.Length} were written");
            }

            if (!append && (!strict || (clean && family != Family.Ebml)) && !output.ToArray().AsSpan().SequenceEqual(file))
            {
                throw new InvalidOperationException("an untouched file did not come back byte-identical");
            }

            if (append)
            {
                // The result must be readable by the same layer.
                MemoryMediaSource back = new(output.ToArray());
                _ = family switch
                {
                    Family.Iso => new BoxTree(back, Limits).TopLevel.Count,
                    Family.Ebml => EbmlDocument.Open(back, Limits).Elements.Count,
                    _ => ChunkFile.TryOpen(back, Limits, new DefectLog())?.Chunks.Count ?? 0,
                };
            }
        }
        catch (MediaException)
        {
        }
    }

    private static (EditPlan Plan, bool Clean) Plan(MemoryMediaSource src, Family family, bool strict, bool append, DefectLog log)
    {
        switch (family)
        {
            case Family.Iso:
                {
                    BoxTree tree = new(src, Limits, log);
                    BoxEditor e = new(tree, src, Limits) { RewriteHeaders = strict, GrowthPadding = append ? 200 : 0 };
                    if (append)
                    {
                        e.AppendChild(null, BoxBuilder.CreateFree(64));
                        e.AppendChild(null, BoxBuilder.Create(BoxTypes.Make("zzzz"), new byte[600]));
                    }

                    EditPlan plan = e.Build();
                    return (plan, log.All(d => d.Severity < DefectSeverity.Warning));
                }

            case Family.Ebml:
                {
                    EbmlDocument doc = EbmlDocument.Open(src, Limits, log);
                    EbmlEditor e = new(doc, src, Limits) { RewriteHeaders = strict, GrowthPadding = append ? 200 : 0 };
                    if (append && doc.Segment is not null)
                    {
                        e.AppendChild(doc.Segment, EbmlWriter.Void(64));
                        e.AppendChild(doc.Segment, EbmlWriter.Element(0xBF, new byte[600]));
                    }

                    EditPlan plan = e.Build();
                    return (plan, log.All(d => d.Severity < DefectSeverity.Warning));
                }

            default:
                {
                    ChunkFile cf = ChunkFile.TryOpen(src, Limits, log) ?? throw new MediaFormatException("Not a chunk file.");
                    ChunkEditor e = new(cf, src, Limits) { RewriteHeaders = strict, GrowthPadding = append ? 200 : 0 };
                    if (append)
                    {
                        e.AppendChild(null, ChunkBuilder.CreatePadding(cf.Layout, 64));
                        e.AppendChild(null, ChunkBuilder.CreatePadding(cf.Layout, 600));
                    }

                    EditPlan plan = e.Build();
                    return (plan, log.All(d => d.Severity < DefectSeverity.Warning));
                }
        }
    }
}
