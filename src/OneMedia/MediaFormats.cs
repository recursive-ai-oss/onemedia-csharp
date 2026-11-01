using RecursiveAI.OneMedia.Asf;
using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Dv;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Sniffing;

namespace RecursiveAI.OneMedia;

/// <summary>
/// The formats the happy path (<see cref="Media"/>) opens. Formats are listed here by name, so a program that never calls the happy
/// path does not carry them. Programs that want a different set build their own <see cref="MediaFormatRegistry"/> and pass it to
/// <see cref="MediaFile.Open(IMediaSource, MediaFormatRegistry?, MediaOpenOptions?, CancellationToken)"/>.
/// </summary>
public static class MediaFormats
{
    /// <summary>
    /// The shared registry: ISO BMFF, Matroska/WebM, WAVE (RIFF, RF64, Wave64), AIFF and CAF can be opened; every other format OneMedia knows is recognised by content, so
    /// opening one reports the format by name in an <see cref="UnsupportedMediaException"/>. Do not add to it; use <see cref="Create"/> for that.
    /// </summary>
    public static MediaFormatRegistry Default { get; } = Create();

    /// <summary>Builds a new registry with the same contents as <see cref="Default"/>, for callers that want to add formats.</summary>
    /// <returns>A new registry.</returns>
    public static MediaFormatRegistry Create()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new IsoBmffFormat());
        registry.Add(new MatroskaFormat());
        registry.Add(new RecursiveAI.OneMedia.Riff.WavFileFormat());
        registry.Add(new AviFormat());
        registry.Add(new AsfFormat());
        registry.Add(new RecursiveAI.OneMedia.MpegTs.MpegTsFormat());
        registry.Add(new RecursiveAI.OneMedia.MpegPs.MpegPsFormat());
        registry.Add(new RecursiveAI.OneMedia.Flv.FlvFormat());
        registry.Add(new RecursiveAI.OneMedia.Ogg.OggFormat());
        registry.Add(new RecursiveAI.OneMedia.Flac.FlacFormat());
        registry.Add(new RecursiveAI.OneMedia.Aiff.AiffFileFormat());
        registry.Add(new RecursiveAI.OneMedia.Caf.CafFileFormat());
        registry.Add(new AudioStreams.Id3AudioFormat());
        registry.Add(new AudioStreams.MpegAudioFormat());
        registry.Add(new AudioStreams.AdtsFormat());
        registry.Add(new AudioStreams.AmrFormat());
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.IvfFormat());
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.H264));
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.Hevc));
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.Vvc));
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.MpegVideo));
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind.Av1Obu));
        registry.Add(new AudioStreams.Ac3Format());
        registry.Add(new DvFormat());
        return registry;
    }
}
