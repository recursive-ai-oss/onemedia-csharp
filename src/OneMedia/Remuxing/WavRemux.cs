using System.Linq;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>The WAVE side of a path-to-path remux: metadata chunks carried unchanged from a WAVE input, and the file written under a temporary name.</summary>
internal static class WavRemux
{
    // Chunks the muxer writes itself, and padding that carries nothing.
    private static readonly HashSet<string> s_skipped = ["fmt ", "data", "fact", "ds64", "JUNK", "junk", "PAD ", "RIFF", "RF64", "BW64"];

    // Room left in front of the sample data for the tags of a non-WAVE input to be written in place.
    private const int TagPadding = 16 * 1024;

    public static RemuxReport Remux(MediaFile input, string inputPath, string outputPath, RemuxFormat format, RemuxOptions? options, CancellationToken cancellationToken)
    {
        options ??= new RemuxOptions();
        HashSet<string> carried = [];
        List<RemuxLoss> notCarried = [];
        bool wavInput = false;
        if (options.Wav is null && input.Demuxer is WavDemuxer wav && wav.ChunkFile.Layout != ChunkLayout.Wave64)
        {
            options = options with { Wav = CarryChunks(inputPath, wav, options.Limits, carried, notCarried, cancellationToken) };
            wavInput = true;
        }
        else if (options.Wav is null && options.CarryTags)
        {
            options = options with { Wav = new WavMuxOptions { PaddingBytes = TagPadding } };
        }

        using AtomicOutput atomic = AtomicOutput.Create(outputPath, options.Overwrite);
        RemuxReport report = Remuxer.Remux(input, atomic.Stream, format, options, cancellationToken);
        atomic.CloseStream();
        if (wavInput)
        {
            report = Account(report, carried, notCarried);
        }
        else if (options.CarryTags)
        {
            // Done on the temporary file, so the target name still never shows a half-edited file.
            report = Remuxer.CarryTags(input, atomic.TempPath, report, inPlaceFirst: true, cancellationToken);
        }

        atomic.Commit();
        return report;
    }

    // Every top-level chunk of a RIFF/RF64 input that is not about the sample data travels as it is, before the data.
    private static WavMuxOptions CarryChunks(string inputPath, WavDemuxer wav, MediaLimits limits, HashSet<string> systems, List<RemuxLoss> notCarried, CancellationToken cancellationToken)
    {
        List<WavChunk> chunks = [];
        using FileMediaSource source = FileMediaSource.Open(inputPath);
        foreach (ChunkNode c in wav.Chunks)
        {
            if (s_skipped.Contains(c.Id))
            {
                continue;
            }

            if (c.PayloadSize > limits.MaxTagBytes)
            {
                notCarried.Add(new RemuxLoss(LossKind.Structure, LossReason.NotWritten, -1, c.Id, $"The '{c.Id}' chunk has {c.PayloadSize} bytes, more than the {limits.MaxTagBytes} the limits allow to carry."));
                continue;
            }

            byte[] payload = new byte[c.PayloadSize];
            source.ReadExactly(c.Payload.Offset, payload, cancellationToken);
            chunks.Add(new WavChunk(c.Id, payload));
            string? system = c.Id switch
            {
                "bext" => TagSystems.Bext,
                "iXML" => TagSystems.IXml,
                "axml" => TagSystems.AXml,
                "cart" => TagSystems.Cart,
                "LIST" when c.FormType == "INFO" => TagSystems.RiffInfo,
                "id3 " or "ID3 " => TagSystems.Id3Chunk,
                "cue " => "cue",
                _ => null,
            };
            if (system is not null)
            {
                systems.Add(system);
            }
        }

        return new WavMuxOptions { Chunks = chunks, Limits = limits };
    }

    private static RemuxReport Account(RemuxReport report, HashSet<string> carried, List<RemuxLoss> notCarried)
    {
        // The id3 chunk's frames are listed as their own system; a chunk carried raw carries them too.
        if (carried.Contains(TagSystems.Id3Chunk))
        {
            carried.Add(TagSystems.Id3v2);
        }

        List<RemuxLoss> losses = [];
        foreach (RemuxLoss l in report.Losses)
        {
            bool gone = (l.Kind == LossKind.Tag && l.Reason == LossReason.NotWritten && carried.Contains(l.Subject))
                || (l.Kind == LossKind.Chapter && l.Reason == LossReason.NotWritten && carried.Contains("cue"));
            if (!gone)
            {
                losses.Add(l);
            }
        }

        losses.AddRange(notCarried);
        List<string> changes = [.. report.Changes];
        if (carried.Count > 0)
        {
            changes.Add($"WAVE chunks carried over unchanged: {string.Join(", ", carried.OrderBy(x => x, StringComparer.Ordinal))}.");
        }

        return report with { Losses = losses, Changes = changes };
    }
}
