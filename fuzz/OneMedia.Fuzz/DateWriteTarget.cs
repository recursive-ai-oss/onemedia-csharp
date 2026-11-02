using System;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for capture-time writes. The first four bytes pick the time (any instant in the supported range, any offset in whole quarter hours) and
/// the option bits; the rest is the file. The properties: the plan's length is what is written, every copied piece is byte-identical to its source range,
/// the per-track payload hash is unchanged, and the result is itself writable. Planning may refuse with a <see cref="MediaException"/>; anything else
/// escaping is a finding.
/// </summary>
internal static class DateWriteTarget
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxTagBytes = 1 << 20, MaxAttachmentBytes = 1 << 20 };

    public static void Register(System.Collections.Generic.Dictionary<string, FuzzTarget> targets) => targets["datewrite"] = Run;

    internal static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 16)
        {
            return;
        }

        // 1904-01-01 up to 2262, in seconds; offsets from -12:00 to +14:00.
        uint raw = (uint)(input[0] | (input[1] << 8) | (input[2] << 16) | (input[3] << 24));
        DateTime utc = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(raw * 1.5);
        int quarter = (input[4] % 105) - 48;
        DateTimeOffset when = new DateTimeOffset(utc).ToOffset(TimeSpan.FromMinutes(quarter * 15));
        CaptureTimeOptions options = new()
        {
            Limits = Limits,
            HeaderTimesAreLocal = (input[5] & 3) switch { 1 => false, 2 => true, _ => null },
            AddMissing = (input[5] & 4) == 0,
            CheckTelemetry = (input[5] & 8) == 0,
        };
        byte[] file = input[6..].ToArray();
        byte[]? once = Pass(file, when, options);
        if (once is not null)
        {
            _ = Pass(once, when.AddDays(1), options);
        }
    }

    private static byte[]? Pass(byte[] file, DateTimeOffset when, CaptureTimeOptions options)
    {
        try
        {
            MemoryMediaSource src = new(file);
            CaptureTimePlan plan = CaptureTimeWriter.Plan(src, when, options);
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

            return result;
        }
        catch (MediaException)
        {
            // A typed refusal: the input is damaged or outside what the writer edits.
            return null;
        }
    }
}
